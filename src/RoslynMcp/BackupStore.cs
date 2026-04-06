using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;


namespace RoslynMcp;

/// <summary>
///     Crash-safe pre-write backup store — analogous to VS Code's Local History.
///     Before any destructive file write, callers save a backup here and receive a token.
///     Tokens survive process restarts because backups are re-discoverable on disk.
/// </summary>
internal sealed class BackupStore
{
	const int    MaxPerFile   = 10;
	const string MetaFileName = "meta.json";

	static readonly JsonSerializerOptions JsonOptions = RoslynMcpJson.Backup;

	readonly string?     backupRoot;
	readonly object      syncRoot = new();
	readonly FileLogger  logger;

	public bool IsEnabled => backupRoot is not null;

	public BackupStore(FileLogger logger)
	{
		this.logger = logger;

		var configured = ServerArgs.Current.BackupPath;

		// Explicitly empty → disabled.
		if(configured is not null && configured.Length == 0) {
			backupRoot = null;
			return;
		}

		backupRoot = configured
			?? Path.Combine(
				Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
				"RoslynMcp", "backups"
			);

		try {
			Directory.CreateDirectory(backupRoot);
		}
		catch {
			// Can't create backup dir — silently disable rather than crashing.
			backupRoot = null;
		}
	}

	/// <summary>
	///     Creates a backup of <paramref name="absolutePath"/> before it is overwritten.
	///     Returns a token of the form <c>{8-char-path-hash}_{unix-ms}</c> that can be
	///     passed to <see cref="TryRestore"/> or <see cref="List"/>.
	///     Returns null if backups are disabled or the file does not exist.
	/// </summary>
	public string? Save(
		string   absolutePath,
		string   projectPath,
		string   operation,
		byte[]   newContent,
		int[]?   changedLineHint = null)
	{
		if(backupRoot is null)
			return null;

		byte[] current;

		try {
			current = File.ReadAllBytes(absolutePath);
		}
		catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) {

			logger.LogInfo("backup_save", $"could not read pre-write content for \"{Path.GetFileName(absolutePath)}\": {ex.GetType().Name}: {ex.Message}");

			return null;
		}

		var preHash        = ComputeContentHash(current);
		var newContentHash = ComputeContentHash(newContent);

		// Skip backup when writing identical bytes — nothing to restore.
		if(preHash == newContentHash)
			return null;

		var pathHash = ComputePathHash(absolutePath);
		var dir      = Path.Combine(backupRoot, pathHash);
		var unixMs   = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
		// Append a short random suffix to guarantee uniqueness even when two backups
		// of the same file land within the same millisecond (Windows timer resolution ~15ms).
		var nonce    = Guid.NewGuid().ToString("N")[..4];
		var token    = $"{pathHash}_{unixMs}_{nonce}";

		// Read git context outside the lock — avoids holding it during file I/O.
		var (gitBranch, gitCommit) = ReadGitContext(absolutePath);

		lock(syncRoot)
			try {

				Directory.CreateDirectory(dir);

				var bakFile  = Path.Combine(dir, $"{Path.GetFileName(absolutePath)}_{unixMs}_{nonce}.bak");
				var metaFile = Path.Combine(dir, MetaFileName);

				FileWriter.WriteAllBytes(bakFile, current);

				var meta = new BackupMeta {
					AbsolutePath    = absolutePath,
					ProjectPath     = projectPath,
					Operation       = operation,
					Timestamp       = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
					FileSizeBytes   = current.Length,
					PreWriteHash    = preHash,
					PostWriteHash   = newContentHash,
					ChangedLineHint = changedLineHint,
					GitBranch       = gitBranch,
					GitCommit       = gitCommit
				};

				WriteMetaEntry(metaFile, token, meta);
				PruneOldBackups(dir, absolutePath);

				return token;
			}
			catch(Exception ex) {
				logger.LogError("backup_save", $"failed to save backup for \"{Path.GetFileName(absolutePath)}\": {ex.Message}");
				return null;
			}

	}


	/// <summary>
	///     Lists all backup entries, optionally filtered to a specific file by <paramref name="absolutePath"/>.
	/// </summary>
	public IReadOnlyList<BackupEntry> List(string? absolutePath = null)
	{
		if(backupRoot is null || !Directory.Exists(backupRoot))
			return [];

		var results = new List<BackupEntry>();

		foreach(var dir in Directory.EnumerateDirectories(backupRoot)) {

			var metaFile = Path.Combine(dir, MetaFileName);
			var entries  = ReadAllMetaEntries(metaFile);

			foreach(var (token, meta) in entries) {

				if(absolutePath is not null &&
					!string.Equals(meta.AbsolutePath, absolutePath, StringComparison.OrdinalIgnoreCase))
					continue;

				var conflictRisk = false;

				try {
					if(meta.PostWriteHash is not null) {
						var current = File.ReadAllBytes(meta.AbsolutePath);
						conflictRisk = ComputeContentHash(current) != meta.PostWriteHash;
					}
				}
				catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) {
					logger.LogInfo("backup_list", $"could not read current content to check conflict risk for \"{Path.GetFileName(meta.AbsolutePath)}\": {ex.Message}");
				}

				results.Add(new BackupEntry(token, meta, conflictRisk));
			}
		}

		results.Sort((a, b) => string.Compare(b.Meta.Timestamp, a.Meta.Timestamp, StringComparison.Ordinal));

		return results;
	}

	// Validates the token, runs the conflict check, and reads the .bak content —
	// all inside the lock. Returns either a failure result or a CheckedRestore
	// that the caller can pass to WriteAndInvalidate + CompleteRestore.
	public (RestoreResult? Failure, CheckedRestore? Checked) TryCheck(string token, bool force = false)
	{
		if(backupRoot is null)
			return (RestoreResult.Disabled(), null);

		var parts = token.Split('_');

		// Token format: {pathHash}_{unixMs} (legacy) or {pathHash}_{unixMs}_{nonce}.
		if(parts.Length < 2)
			return (RestoreResult.InvalidToken(token), null);

		var dir      = Path.Combine(backupRoot, parts[0]);
		var metaFile = Path.Combine(dir, MetaFileName);

		lock(syncRoot) {

			var entries = ReadAllMetaEntries(metaFile);

			if(!entries.TryGetValue(token, out var meta))
				return (RestoreResult.NotFound(token), null);

			try {

				// Extract the suffix after the path hash (covers both legacy "ms" and new "ms_nonce" formats).
				var suffix  = token[(parts[0].Length + 1)..];
				var bakFile = Directory.EnumerateFiles(dir, $"*_{suffix}.bak").FirstOrDefault();

				if(bakFile is null)
					return (RestoreResult.NotFound(token), null);

				// Conflict check: has the file changed since backup?
				// Any I/O failure here means we cannot verify — proceed and let the write surface the real error.
				if(!force && meta.PostWriteHash is not null)
					try {
						var current     = File.ReadAllBytes(meta.AbsolutePath);
						var currentHash = ComputeContentHash(current);

						if(currentHash != meta.PostWriteHash)
							return (RestoreResult.Conflict(meta.AbsolutePath, currentHash, meta.PostWriteHash), null);
					}
					catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) { }

				var content = File.ReadAllBytes(bakFile);

				return (null, new CheckedRestore(meta.AbsolutePath, content, bakFile, metaFile, token));
			}
			catch(Exception ex) {
				return (RestoreResult.Failed(ex.Message), null);
			}
		}
	}

	// Removes the consumed backup entry and deletes the .bak file.
	// Call after a successful WriteAndInvalidate.
	public void CompleteRestore(CheckedRestore checkedRestore)
	{
		lock(syncRoot) {

			RemoveMetaEntry(checkedRestore.MetaFile, checkedRestore.Token);

			try { File.Delete(checkedRestore.BakFile); }
			catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or NotSupportedException) {
				logger?.LogInfo("backup_delete", $"failed to delete backup file: {ex.Message}");
			}
		}
	}


	/// <summary>
	///     Attempts to restore a file from the backup identified by <paramref name="token"/>.
	///     Returns a <see cref="RestoreResult"/> describing success, conflict, or failure.
	/// </summary>
	/// <remarks>
	///     Bypasses WorkspaceManager — the workspace will be out of sync until FSW fires.
	///     Prefer the TryCheck / WriteAndInvalidate / CompleteRestore split used by LocalHistoryTool.
	/// </remarks>
	[Obsolete("Use TryCheck + WriteAndInvalidate + CompleteRestore instead — this method bypasses WorkspaceManager.")]
	public RestoreResult TryRestore(string token, bool force = false)
	{
		var (failure, checkedRestore) = TryCheck(token, force);

		if(failure is not null)
			return failure;

		var absPath = checkedRestore!.AbsolutePath;
		var dir     = Path.GetDirectoryName(absPath)!;

		Directory.CreateDirectory(dir);

		var tmp = Path.Combine(dir, $".roslynmcp_restore_{Guid.NewGuid():N}.tmp");

		FileWriter.WriteAllBytes(tmp, checkedRestore.Content);
		FileWriter.Move(tmp, absPath, overwrite: true);

		CompleteRestore(checkedRestore);

		return RestoreResult.Success(absPath);
	}


	/// <summary>
	///     Returns the current git branch for the repo containing <paramref name="startPath"/>,
	///     or null if not in a git repo or in detached HEAD state.
	/// </summary>
	public string? GetCurrentBranch(string startPath) => ReadGitContext(startPath).Branch;

	// ── Git context ──────────────────────────────────────────────────────────

	static (string? Branch, string? Commit) ReadGitContext(string startPath)
	{
		var gitDir = FindGitDir(startPath);

		if(gitDir is null)
			return (null, null);

		try {
			var headPath = Path.Combine(gitDir, "HEAD");
			var head     = File.ReadAllText(headPath).Trim();

			if(head.StartsWith("ref: refs/heads/", StringComparison.Ordinal)) {

				var branch = head["ref: refs/heads/".Length..];
				var sha    = ReadRef(gitDir, branch);

				return (branch, sha is not null ? sha[..Math.Min(7, sha.Length)] : null);
			}

			// Detached HEAD — bare SHA, no branch name.
			return (null, head.Length >= 7 ? head[..7] : head);
		}
		catch {
			return (null, null);
		}
	}

	static string? FindGitDir(string startPath)
	{
		var dir = File.Exists(startPath) ? Path.GetDirectoryName(startPath) : startPath;

		while(dir is not null) {

			var gitPath = Path.Combine(dir, ".git");

			if(Directory.Exists(gitPath))
				return gitPath;

			// Worktree: .git is a file pointing to the real gitdir.
			if(File.Exists(gitPath))
				try {

					var content = File.ReadAllText(gitPath).Trim();

					if(content.StartsWith("gitdir: ", StringComparison.Ordinal))
						return content["gitdir: ".Length..].Trim();
				}
				catch { }

			dir = Path.GetDirectoryName(dir);
		}

		return null;
	}

	static string? ReadRef(string gitDir, string branchName)
	{
		// Try loose ref first.
		var refPath = Path.Combine(gitDir, "refs", "heads", branchName.Replace('/', Path.DirectorySeparatorChar));

		if(File.Exists(refPath))
			return File.ReadAllText(refPath).Trim();

		// Fall back to packed-refs.
		var packedRefs = Path.Combine(gitDir, "packed-refs");

		if(!File.Exists(packedRefs))
			return null;

		var needle = $" refs/heads/{branchName}";

		foreach(var line in File.ReadLines(packedRefs)) {

			if(line.StartsWith('#'))
				continue;

			if(line.EndsWith(needle, StringComparison.Ordinal))
				return line[..line.IndexOf(' ')];
		}

		return null;
	}


	// ── Internal helpers ────────────────────────────────────────────────────

	// 8-char hex prefix of SHA256(normalized-lowercase-path).
	static string ComputePathHash(string absolutePath)
	{
		var normalized = absolutePath.ToLowerInvariant().Replace('/', '\\');
		var bytes      = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));

		return Convert.ToHexString(bytes)[..8].ToLowerInvariant();
	}

	static string ComputeContentHash(byte[] content)
	{
		var bytes = SHA256.HashData(content);

		return Convert.ToHexString(bytes).ToLowerInvariant();
	}


	// meta.json is a JSON object: { "token": BackupMeta, ... }
	Dictionary<string, BackupMeta> ReadAllMetaEntries(string metaFile)
	{
		try {
			var json = File.ReadAllText(metaFile);

			return JsonSerializer.Deserialize<Dictionary<string, BackupMeta>>(json, JsonOptions)
				?? new Dictionary<string, BackupMeta>();
		}
		catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or JsonException) {
			logger.LogInfo("backup_meta", $"could not read meta file \"{Path.GetFileName(metaFile)}\": {ex.Message}");
			return new Dictionary<string, BackupMeta>();
		}
	}

	void WriteMetaEntry(string metaFile, string token, BackupMeta meta)
	{
		var entries = ReadAllMetaEntries(metaFile);

		entries[token] = meta;

		WriteMetaAtomic(metaFile, entries);
	}


	void RemoveMetaEntry(string metaFile, string token)
	{
		var entries = ReadAllMetaEntries(metaFile);

		if(entries.Remove(token))
			WriteMetaAtomic(metaFile, entries);
	}

	void PruneOldBackups(string dir, string absolutePath)
	{
		var metaFile = Path.Combine(dir, MetaFileName);
		var entries  = ReadAllMetaEntries(metaFile);
		var fileName = Path.GetFileName(absolutePath);

		// Sort ascending by token (timestamp suffix) — oldest first.
		var ordered = entries.OrderBy(kv => kv.Key).ToList();

		while(ordered.Count > MaxPerFile) {

			var (oldToken, _) = ordered[0];
			ordered.RemoveAt(0);
			entries.Remove(oldToken);

			// Delete matching .bak file — extract suffix after path hash.
			var hashEnd = oldToken.IndexOf('_');
			var suffix  = hashEnd >= 0 ? oldToken[(hashEnd + 1)..] : oldToken;
			var bakFile = Directory.EnumerateFiles(dir, $"{fileName}_{suffix}.bak").FirstOrDefault();

			if(bakFile is not null)
				File.Delete(bakFile);
		}

		WriteMetaAtomic(metaFile, entries);
	}

	// Writes meta.json atomically via a temp file + rename, eliminating partial-write corruption
	// on crash or power loss.
	void WriteMetaAtomic(string metaFile, Dictionary<string, BackupMeta> entries)
	{
		var json    = JsonSerializer.Serialize(entries, JsonOptions);
		var metaTmp = metaFile + $".{Guid.NewGuid():N}.tmp";

		FileWriter.WriteAllText(metaTmp, json);
		FileWriter.Move(metaTmp, metaFile, overwrite: true);
	}
}

// ── Data model ──────────────────────────────────────────────────────────────

internal sealed record BackupMeta
{
	public required string   AbsolutePath    { get; init; }
	public required string   ProjectPath     { get; init; }
	public required string   Operation       { get; init; }
	public required string   Timestamp       { get; init; }
	public required long     FileSizeBytes   { get; init; }
	public required string   PreWriteHash    { get; init; }
	public          string?  PostWriteHash   { get; init; }
	public          int[]?   ChangedLineHint { get; init; }
	public          string?  GitBranch       { get; init; }
	public          string?  GitCommit       { get; init; }
}

internal sealed record BackupEntry(string Token, BackupMeta Meta, bool ConflictRisk);

internal sealed class RestoreResult
{
	public bool    Restored      { get; private init; }
	public bool    IsConflict    { get; private init; }
	public bool    IsDisabled    { get; private init; }
	public string? AbsolutePath  { get; private init; }
	public string? ErrorMessage  { get; private init; }
	public string? CurrentHash   { get; private init; }
	public string? BackupHash    { get; private init; }

	public static RestoreResult Success(string path)
		=> new() { Restored = true, AbsolutePath = path };

	public static RestoreResult Conflict(string path, string currentHash, string backupHash)
		=> new() { IsConflict = true, AbsolutePath = path, CurrentHash = currentHash, BackupHash = backupHash };

	public static RestoreResult Disabled()
		=> new() { ErrorMessage = "Backup store is disabled (ROSLYNMCP_BACKUP_PATH is empty)." };

	public static RestoreResult NotFound(string token)
		=> new() { ErrorMessage = $"No backup found for token '{token}'." };

	public static RestoreResult InvalidToken(string token)
		=> new() { ErrorMessage = $"Invalid token format: '{token}'. Expected {{path-hash}}_{{unix-ms}}_{{nonce}}." };

	public static RestoreResult Failed(string message)
		=> new() { ErrorMessage = message };
}

// Pre-validated restore state produced by BackupStore.TryCheck.
// Holds everything needed to execute the write and cleanup phases separately.
internal sealed record CheckedRestore(
	string AbsolutePath,
	byte[] Content,
	string BakFile,
	string MetaFile,
	string Token
);

