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
	const string EnvVar         = "ROSLYNMCP_BACKUP_PATH";
	const int    MaxPerFile     = 10;
	const string MetaFileName   = "meta.json";

	static readonly JsonSerializerOptions JsonOptions = RoslynMcpJson.Backup;

	readonly string? backupRoot;
	readonly object  syncRoot = new();

	public bool IsEnabled => backupRoot is not null;

	public BackupStore()
	{
		var envValue = Environment.GetEnvironmentVariable(EnvVar);

		// Explicitly empty → disabled.
		if(envValue is not null && envValue.Length == 0) {

			backupRoot = null;
			return;
		}

		backupRoot = envValue is not null
			? envValue
			: Path.Combine(
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

		if(!File.Exists(absolutePath))
			return null;

		byte[] current;

		try {
			current = File.ReadAllBytes(absolutePath);
		}
		catch {
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
		var token    = $"{pathHash}_{unixMs}";

		// Read git context outside the lock — avoids holding it during file I/O.
		var (gitBranch, gitCommit) = ReadGitContext(absolutePath);

		lock(syncRoot) {

			try {

				Directory.CreateDirectory(dir);

				var bakFile  = Path.Combine(dir, $"{Path.GetFileName(absolutePath)}_{unixMs}.bak");
				var metaFile = Path.Combine(dir, MetaFileName);

				File.WriteAllBytes(bakFile, current);

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
			catch {
				return null;
			}
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

			if(!File.Exists(metaFile))
				continue;

			var entries = ReadAllMetaEntries(metaFile);

			foreach(var (token, meta) in entries) {

				if(absolutePath is not null &&
					!string.Equals(meta.AbsolutePath, absolutePath, StringComparison.OrdinalIgnoreCase))
					continue;

				var conflictRisk = false;

				if(meta.PostWriteHash is not null && File.Exists(meta.AbsolutePath)) {

					try {
						var current = File.ReadAllBytes(meta.AbsolutePath);
						conflictRisk = ComputeContentHash(current) != meta.PostWriteHash;
					}
					catch { }
				}

				results.Add(new BackupEntry(token, meta, conflictRisk));
			}
		}

		results.Sort((a, b) => string.Compare(b.Meta.Timestamp, a.Meta.Timestamp, StringComparison.Ordinal));

		return results;
	}

	/// <summary>
	///     Attempts to restore a file from the backup identified by <paramref name="token"/>.
	///     Returns a <see cref="RestoreResult"/> describing success, conflict, or failure.
	/// </summary>
	public RestoreResult TryRestore(string token, bool force = false)
	{
		if(backupRoot is null)
			return RestoreResult.Disabled();

		var parts = token.Split('_');

		if(parts.Length != 2)
			return RestoreResult.InvalidToken(token);

		var dir      = Path.Combine(backupRoot, parts[0]);
		var metaFile = Path.Combine(dir, MetaFileName);

		if(!File.Exists(metaFile))
			return RestoreResult.NotFound(token);

		lock(syncRoot) {

			var entries = ReadAllMetaEntries(metaFile);

			if(!entries.TryGetValue(token, out var meta))
				return RestoreResult.NotFound(token);

			// Find the .bak file — suffix match on the unix_ms portion.
			var ms      = parts[1];
			var bakFile = Directory.EnumerateFiles(dir, $"*_{ms}.bak").FirstOrDefault();

			if(bakFile is null)
				return RestoreResult.NotFound(token);

			// Conflict check: has the file changed since backup?
			if(!force && meta.PostWriteHash is not null && File.Exists(meta.AbsolutePath)) {

				try {

					var current     = File.ReadAllBytes(meta.AbsolutePath);
					var currentHash = ComputeContentHash(current);

					if(currentHash != meta.PostWriteHash)
						return RestoreResult.Conflict(meta.AbsolutePath, currentHash, meta.PostWriteHash);
				}
				catch { }
			}

			try {

				var content = File.ReadAllBytes(bakFile);
				var dir2    = Path.GetDirectoryName(meta.AbsolutePath)!;

				Directory.CreateDirectory(dir2);

				var tmp = Path.Combine(dir2, $".roslynmcp_restore_{Guid.NewGuid():N}.tmp");

				File.WriteAllBytes(tmp, content);
				File.Move(tmp, meta.AbsolutePath, overwrite: true);

				// Remove the consumed backup entry.
				RemoveMetaEntry(metaFile, token);
				File.Delete(bakFile);

				return RestoreResult.Success(meta.AbsolutePath);
			}
			catch(Exception ex) {
				return RestoreResult.Failed(ex.Message);
			}
		}
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

			if(!File.Exists(headPath))
				return (null, null);

			var head = File.ReadAllText(headPath).Trim();

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
			if(File.Exists(gitPath)) {

				try {

					var content = File.ReadAllText(gitPath).Trim();

					if(content.StartsWith("gitdir: ", StringComparison.Ordinal))
						return content["gitdir: ".Length..].Trim();
				}
				catch { }
			}

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
	static Dictionary<string, BackupMeta> ReadAllMetaEntries(string metaFile)
	{
		try {

			var json = File.ReadAllText(metaFile);

			return JsonSerializer.Deserialize<Dictionary<string, BackupMeta>>(json, JsonOptions)
				?? new Dictionary<string, BackupMeta>();
		}
		catch {
			return new Dictionary<string, BackupMeta>();
		}
	}

	static void WriteMetaEntry(string metaFile, string token, BackupMeta meta)
	{
		var entries = ReadAllMetaEntries(metaFile);

		entries[token] = meta;
		File.WriteAllText(metaFile, JsonSerializer.Serialize(entries, JsonOptions));
	}


	static void RemoveMetaEntry(string metaFile, string token)
	{
		var entries = ReadAllMetaEntries(metaFile);

		if(entries.Remove(token))
			File.WriteAllText(metaFile, JsonSerializer.Serialize(entries, JsonOptions));
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

			// Delete matching .bak file.
			var ms      = oldToken.Split('_').LastOrDefault() ?? "";
			var bakFile = Directory.EnumerateFiles(dir, $"{fileName}_{ms}.bak").FirstOrDefault();

			if(bakFile is not null)
				File.Delete(bakFile);
		}

		File.WriteAllText(metaFile, JsonSerializer.Serialize(entries, JsonOptions));
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
		=> new() { ErrorMessage = $"Invalid token format: '{token}'. Expected {{path-hash}}_{{unix-ms}}." };

	public static RestoreResult Failed(string message)
		=> new() { ErrorMessage = message };
}
