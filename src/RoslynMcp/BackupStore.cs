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
	const int    MaxPerFile   = 20;
	const string MetaFileName = "meta.json";

	static readonly JsonSerializerOptions JsonOptions = RoslynMcpJson.Backup;

	readonly string?     backupRoot;
	readonly SemaphoreSlim asyncLock = new(1, 1);
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
			return;
		}

		PruneExpiredGlobal();
	}

	/// <summary>
	///     Saves the current on-disk content of <paramref name="absolutePath"/> as a pre-change snapshot.
	///     Returns a token the caller can surface as <c>BackupToken</c> so agents can undo via <c>roslyn_local_history</c>.
	///     Returns null if backups are disabled or the file does not exist (new-file case — nothing to save).
	///     Throws <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/> on I/O failure.
	///     Callers must abort the write and surface the error when this throws.
	/// </summary>
	public async Task<string?> SavePreAsync(string absolutePath, string projectPath, string operation)
	{
		if(backupRoot is null)
			return null;

		byte[] current;

		try {
			current = File.ReadAllBytes(absolutePath);
		}
		catch(FileNotFoundException) {
			// File does not yet exist — this is a new-file creation, no pre-snapshot to save.
			logger.LogInfo("backup_save_pre", $"skipping pre-snapshot for \"{Path.GetFileName(absolutePath)}\": file not found");
			return null;
		}
		catch(DirectoryNotFoundException) {
			// Same rationale as FileNotFoundException.
			logger.LogInfo("backup_save_pre", $"skipping pre-snapshot for \"{Path.GetFileName(absolutePath)}\": directory not found");
			return null;
		}
		// Other IOException / UnauthorizedAccessException propagate — callers must abort on failure.

		var pathHash = ComputePathHash(absolutePath);
		var dir      = Path.Combine(backupRoot, pathHash);
		var unixMs   = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
		// Short random suffix guarantees uniqueness within the same millisecond.
		var nonce    = Guid.NewGuid().ToString("N")[..4];
		var token    = $"{pathHash}_{unixMs}_{nonce}_pre";

		var (gitBranch, gitCommit) = ReadGitContext(absolutePath);

		await asyncLock.WaitAsync();

		try {

			Directory.CreateDirectory(dir);

			var bakFile  = Path.Combine(dir, $"{Path.GetFileName(absolutePath)}_{unixMs}_{nonce}.pre.bak");
			var metaFile = Path.Combine(dir, MetaFileName);

			FileWriter.WriteAllBytes(bakFile, current);

			var meta = new BackupMeta {
				AbsolutePath  = absolutePath,
				ProjectPath   = projectPath,
				Operation     = operation,
				Timestamp     = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
				FileSizeBytes = current.Length,
				ContentHash   = ComputeContentHash(current),
				Phase         = "pre",
				GitBranch     = gitBranch,
				GitCommit     = gitCommit
			};

			WriteMetaEntry(metaFile, token, meta);
			PruneOldBackups(dir, absolutePath);

			return token;
		}
		catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) {
			throw;
		}
		catch(Exception ex) {
			logger.LogError("backup_save_pre", $"unexpected error saving pre-snapshot for \"{Path.GetFileName(absolutePath)}\": {ex.Message}");
			return null;
		}
		finally {
			asyncLock.Release();
		}
	}

	/// <summary>
	///     Saves <paramref name="content"/> (the intended post-write bytes) as a post-change snapshot.
	///     Call immediately after <see cref="SavePreAsync"/>, before the actual write.
	///     Throws <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/> on I/O failure.
	///     Callers must abort the write when this throws — the file has not been touched.
	///     No-ops when backups are disabled.
	/// </summary>
	public async Task SavePostAsync(string absolutePath, string projectPath, string operation, byte[] content)
	{
		if(backupRoot is null)
			return;

		var pathHash = ComputePathHash(absolutePath);
		var dir      = Path.Combine(backupRoot, pathHash);
		var unixMs   = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
		var nonce    = Guid.NewGuid().ToString("N")[..4];
		var token    = $"{pathHash}_{unixMs}_{nonce}_post";

		var (gitBranch, gitCommit) = ReadGitContext(absolutePath);

		await asyncLock.WaitAsync();

		try {

			Directory.CreateDirectory(dir);

			var bakFile  = Path.Combine(dir, $"{Path.GetFileName(absolutePath)}_{unixMs}_{nonce}.post.bak");
			var metaFile = Path.Combine(dir, MetaFileName);

			FileWriter.WriteAllBytes(bakFile, content);

			var meta = new BackupMeta {
				AbsolutePath  = absolutePath,
				ProjectPath   = projectPath,
				Operation     = operation,
				Timestamp     = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
				FileSizeBytes = content.Length,
				ContentHash   = ComputeContentHash(content),
				Phase         = "post",
				GitBranch     = gitBranch,
				GitCommit     = gitCommit
			};

			WriteMetaEntry(metaFile, token, meta);
			PruneOldBackups(dir, absolutePath);
		}
		catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) {
			throw;
		}
		catch(Exception ex) {
			logger.LogError("backup_save_post", $"unexpected error saving post-snapshot for \"{Path.GetFileName(absolutePath)}\": {ex.Message}");
		}
		finally {
			asyncLock.Release();
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

				results.Add(new BackupEntry(token, meta, meta.Phase));
			}
		}

		results.Sort((a, b) => string.Compare(b.Meta.Timestamp, a.Meta.Timestamp, StringComparison.Ordinal));

		return results;
	}

	// Validates the token, runs the conflict check, and reads the .bak content —
	// all inside the lock. Returns either a failure result or a CheckedRestore
	// that the caller can pass to WriteAndInvalidate + CompleteRestore.
	public async Task<(RestoreResult? Failure, CheckedRestore? Checked)> TryCheckAsync(string token, bool force = false)
	{
		if(backupRoot is null)
			return (RestoreResult.Disabled(), null);

		var parts = token.Split('_');

		// Token format: {pathHash}_{unixMs} (v0), {pathHash}_{unixMs}_{nonce} (v1),
		//               {pathHash}_{unixMs}_{nonce}_pre or _post (v2).
		if(parts.Length < 2)
			return (RestoreResult.InvalidToken(token), null);

		var dir      = Path.Combine(backupRoot, parts[0]);
		var metaFile = Path.Combine(dir, MetaFileName);

		await asyncLock.WaitAsync();

		try {

			var entries = ReadAllMetaEntries(metaFile);

			if(!entries.TryGetValue(token, out var meta))
				return (RestoreResult.NotFound(token), null);

			// Resolve the .bak file — handle v0/v1 (.bak) and v2 (.pre.bak / .post.bak).
			var isV2    = parts.Length >= 4 && (parts[^1] == "pre" || parts[^1] == "post");
			var phase   = isV2 ? parts[^1] : null;
			string bakFile;

			if(isV2) {
				// Core = everything between pathHash and the phase suffix.
				var core = token[(parts[0].Length + 1)..token.LastIndexOf('_')];
				bakFile  = Directory.EnumerateFiles(dir, $"*_{core}.{phase}.bak").FirstOrDefault()!;
			}
			else {
				var suffix = token[(parts[0].Length + 1)..];
				bakFile    = Directory.EnumerateFiles(dir, $"*_{suffix}.bak").FirstOrDefault()!;
			}

			if(bakFile is null)
				return (RestoreResult.NotFound(token), null);

			// Conflict check logic depends on phase:
			// - null (legacy): compare current hash to PostWriteHash (written after the fact — file should still match)
			// - "pre": no conflict check — the snapshot records what was there before; no "expected current" is known
			// - "post": no conflict check — the snapshot records intended content; applying it is always valid
			//           (the write may have failed and current = pre state, which is not a conflict)
			string? warning = null;

			if(!force && phase is null) {
				if(meta.PostWriteHash is not null)
					try {
						var current     = File.ReadAllBytes(meta.AbsolutePath);
						var currentHash = ComputeContentHash(current);

						if(currentHash != meta.PostWriteHash)
							return (RestoreResult.Conflict(meta.AbsolutePath, currentHash, meta.PostWriteHash), null);
					}
					catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) {
						// Cannot verify conflict — let the write proceed; the caller will surface any real error.
						warning = $"Conflict check skipped: {ex.GetType().Name}: {ex.Message}";
					}
			}

			var content = File.ReadAllBytes(bakFile);

			return (null, new CheckedRestore(meta.AbsolutePath, content, bakFile, metaFile, token, warning));
		}
		catch(Exception ex) {
			return (RestoreResult.Failed(ex.Message), null);
		}
		finally {
			asyncLock.Release();
		}
	}

	// Removes the consumed backup entry and deletes the .bak file.
	// Call after a successful WriteAndInvalidate.
	public async Task CompleteRestoreAsync(CheckedRestore checkedRestore)
	{
		await asyncLock.WaitAsync();

		try {

			RemoveMetaEntry(checkedRestore.MetaFile, checkedRestore.Token);

			try { File.Delete(checkedRestore.BakFile); }
			catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or NotSupportedException) {
				logger?.LogInfo("backup_delete", $"failed to delete backup file: {ex.Message}");
			}
		}
		finally {
			asyncLock.Release();
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
	[Obsolete("Use TryCheckAsync + WriteAndInvalidate + CompleteRestoreAsync instead — this method bypasses WorkspaceManager.")]
	public RestoreResult TryRestore(string token, bool force = false)
	{
		var (failure, checkedRestore) = TryCheckAsync(token, force).GetAwaiter().GetResult();

		if(failure is not null)
			return failure;

		var absPath = checkedRestore!.AbsolutePath;
		var dir     = Path.GetDirectoryName(absPath)!;

		Directory.CreateDirectory(dir);

		var tmp = Path.Combine(dir, $".roslynmcp_restore_{Guid.NewGuid():N}.tmp");

		FileWriter.WriteAllBytes(tmp, checkedRestore.Content);
		FileWriter.Move(tmp, absPath, overwrite: true);

		CompleteRestoreAsync(checkedRestore).GetAwaiter().GetResult();

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
		// Normalize to a canonical form. Only lowercase on Windows — Linux paths are
		// case-sensitive, so lowercasing would map distinct paths to the same hash.
		var normalized = Path.GetFullPath(absolutePath).Replace('/', Path.DirectorySeparatorChar);
		
		if(OperatingSystem.IsWindows())
			normalized = normalized.ToLowerInvariant();
		
		var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
		
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

	// Deletes backup snapshots older than ROSLYNMCP_BACKUP_MAX_AGE_DAYS across all hash
	// directories. Meta-aware: reads each meta.json, removes expired entries, rewrites it
	// atomically, and removes empty directories. Only runs when the server has started at
	// least PruneMinRuns times. Uses the same global mutex as FilePruner.Prune so only one
	// process prunes at a time.
	void PruneExpiredGlobal()
	{
		if(backupRoot is null)
			return;

		if(FilePruner.CachedRunCount < ServerArgs.Current.PruneMinRuns)
			return;

		var owned = false;

		using var mutex = new Mutex(false, @"Global\RoslynMcp_FilePruner");

		try {

			try {
				owned = mutex.WaitOne(0);

				if(!owned)
					return;  // another process is pruning — skip this run
			}
			catch(AbandonedMutexException) {
				owned = true;
			}
			catch {
				return;
			}

			var cutoff = DateTime.UtcNow - TimeSpan.FromDays(ServerArgs.Current.BackupMaxAgeDays);

			try {

				foreach(var dir in Directory.EnumerateDirectories(backupRoot)) {

					try {
						PruneExpiredInDir(dir, cutoff);
					}
					catch {
						// TODO #176: surface persistent prune failures (e.g. write a sentinel file).
					}
				}
			}
			catch {
				// Enumeration of backup root failed — entire prune pass silently skipped.
				// TODO #176: surface persistent prune failures (e.g. write a sentinel file).
			}

			FilePruner.RequestReset();
		}
		finally {

			if(owned)
				try { mutex.ReleaseMutex(); }
				catch { }
		}
	}

	void PruneExpiredInDir(string dir, DateTime cutoff)
	{
		var metaFile = Path.Combine(dir, MetaFileName);
		var entries  = ReadAllMetaEntries(metaFile);

		if(entries.Count == 0)
			return;

		var changed = false;

		foreach(var (token, meta) in entries.ToArray()) {

			if(!DateTime.TryParse(
				meta.Timestamp,
				null,
				System.Globalization.DateTimeStyles.RoundtripKind,
				out var ts))
				continue;

			if(ts >= cutoff)
				continue;

			entries.Remove(token);
			changed = true;

			var bakFile = FindBakFile(dir, token, meta.AbsolutePath);

			if(bakFile is not null)
				try { File.Delete(bakFile); }
				catch { }
		}

		if(!changed)
			return;

		if(entries.Count == 0) {

			try { File.Delete(metaFile); } catch { }
			try { Directory.Delete(dir); } catch { }

			return;
		}

		WriteMetaAtomic(metaFile, entries);
	}

	void PruneOldBackups(string dir, string absolutePath)
	{
		var metaFile = Path.Combine(dir, MetaFileName);
		var entries  = ReadAllMetaEntries(metaFile);

		// Sort ascending by token (timestamp suffix) — oldest first.
		var ordered = entries.OrderBy(kv => kv.Key).ToList();

		while(ordered.Count > MaxPerFile) {

			var (oldToken, _) = ordered[0];
			ordered.RemoveAt(0);
			entries.Remove(oldToken);

			var bakFile = FindBakFile(dir, oldToken, absolutePath);

			if(bakFile is not null)
				try { File.Delete(bakFile); }
				catch(IOException ex)            { logger.LogInfo("Backup prune failed", ex.Message); }
				catch(UnauthorizedAccessException ex) { logger.LogInfo("Backup prune failed", ex.Message); }
		}

		WriteMetaAtomic(metaFile, entries);
	}

	// Resolves the .bak file path for a token. Handles all token formats:
	//   v0: {hash}_{unixMs}
	//   v1: {hash}_{unixMs}_{nonce}
	//   v2: {hash}_{unixMs}_{nonce}_{pre|post}
	static string? FindBakFile(string dir, string token, string absolutePath)
	{
		var fileName   = Path.GetFileName(absolutePath);
		var tokenParts = token.Split('_');

		if(tokenParts.Length >= 4 && (tokenParts[^1] == "pre" || tokenParts[^1] == "post")) {

			var phase = tokenParts[^1];
			var core  = token[(tokenParts[0].Length + 1)..token.LastIndexOf('_')];

			return Directory.EnumerateFiles(dir, $"{fileName}_{core}.{phase}.bak").FirstOrDefault();
		}

		var hashEnd = token.IndexOf('_');
		var suffix  = hashEnd >= 0 ? token[(hashEnd + 1)..] : token;

		return Directory.EnumerateFiles(dir, $"{fileName}_{suffix}.bak").FirstOrDefault();
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
	public required string   ContentHash     { get; init; }
	// "pre" / "post" for v2 snapshots; null for legacy entries.
	public          string?  Phase           { get; init; }
	// Legacy fields — nullable for backward compat with old meta files.
	public          string?  PreWriteHash    { get; init; }
	public          string?  PostWriteHash   { get; init; }
	public          int[]?   ChangedLineHint { get; init; }
	public          string?  GitBranch       { get; init; }
	public          string?  GitCommit       { get; init; }
}

internal sealed record BackupEntry(string Token, BackupMeta Meta, string? Phase);

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
	string  AbsolutePath,
	byte[]  Content,
	string  BakFile,
	string  MetaFile,
	string  Token,
	string? Warning = null
);

