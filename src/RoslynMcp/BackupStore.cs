using System.Diagnostics;
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
	
	// How long the backup prune waits for another process that is pruning the same store.
	static readonly TimeSpan PruneMutexWait = TimeSpan.FromMinutes(5);
	
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
			backupRoot = null
			;
			
			return;
		}
		
		// Not here, on the caller's time: this constructor runs inside the first call of a tool that
		// writes, with every other such call waiting for the singleton behind it, and the pass reads
		// one meta file per backed-up file path — thousands of them on a machine that has been in use
		// for a while. Two calls waited 76 and 16 seconds for it before their tools ran (#445).
		if(FilePruner.IsPruningRun)
			new Thread(PruneExpiredGlobal) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "RoslynMcp backup prune" }.Start();
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
			current = FileReader.ReadAllBytes(absolutePath, FileReader.DocumentLimit);
		}
		catch(FileNotFoundException) {
			// File does not yet exist — this is a new-file creation, no pre-snapshot to save.
			logger.LogInfo("backup_save_pre", $"skipping pre-snapshot for \"{Path.GetFileName(absolutePath)}\": file not found")
			;
			
			return null;
		}
		catch(DirectoryNotFoundException) {
			// Same rationale as FileNotFoundException.
			logger.LogInfo("backup_save_pre", $"skipping pre-snapshot for \"{Path.GetFileName(absolutePath)}\": directory not found")
			;
			
			return null;
		}
		// Other IOException / UnauthorizedAccessException propagate — callers must abort on failure.
		
		var pathHash = ComputePathHash(absolutePath);
		var dir      = Path.Combine(backupRoot, pathHash);
		var unixMs   = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
		// Short random suffix guarantees uniqueness within the same millisecond.
		var nonce    = Guid.NewGuid().ToString("N")[..4]
		;
		var token    = $"{pathHash}_{unixMs}_{nonce}_pre";
		
		var (gitBranch, gitCommit) = ReadGitContext(absolutePath);
		
		await asyncLock.WaitAsync();
		
		try {
			
			Directory.CreateDirectory(dir);
			
			var bakFile  = Path.Combine(dir, $"{Path.GetFileName(absolutePath)}_{unixMs}_{nonce}.pre.bak");
			var metaFile = Path.Combine(dir, MetaFileName);
			
			// A snapshot must not be readable by more people than the file it was taken from —
			// from its first byte on, so it is created with that file's permissions.
			FileWriter.WriteAllBytes(bakFile, current, FileWriter.UnixModeOf(absolutePath));
			
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
			
			// Created with the permissions of the file this content is about to be written to. A
			// file that does not exist yet has none, and the snapshot gets the default.
			FileWriter.WriteAllBytes(bakFile, content, FileWriter.UnixModeOf(absolutePath));
			
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
					!SecurityBoundary.PathComparer.Equals(meta.AbsolutePath, absolutePath))
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
			var isV2    = parts.Length >= 4 && (parts[^1] == "pre" || parts[^1] == "post")
			;
			var phase   = isV2 ? parts[^1] : null;
			string bakFile;
			
			if(isV2) {
				// Core = everything between pathHash and the phase suffix.
				var core = token[(parts[0].Length + 1)..token.LastIndexOf('_')]
				;
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
						
						var currentHash = ComputeFileHash(meta.AbsolutePath);
						
						if(currentHash != meta.PostWriteHash)
							
							return (RestoreResult.Conflict(meta.AbsolutePath, currentHash, meta.PostWriteHash), null);
					}
					catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) {
						// Cannot verify conflict — let the write proceed; the caller will surface any real error.
						warning = $"Conflict check skipped: {ex.GetType().Name}: {ex.Message}"
						;
					}
			}
			
			var content = FileReader.ReadAllBytes(bakFile, FileReader.DocumentLimit);
			
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
			var head     = FileReader.ReadAllText(headPath, FileReader.ConfigLimit).Trim();
			
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
					
					var content = FileReader.ReadAllText(gitPath, FileReader.ConfigLimit).Trim();
					
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
		var refPath = Path.Combine(gitDir, "refs", "heads", branchName.Replace('/', Path.DirectorySeparatorChar))
		;
		
		if(File.Exists(refPath))
			
			return FileReader.ReadAllText(refPath, FileReader.ConfigLimit).Trim();
		
		// Fall back to packed-refs.
		var packedRefs = Path.Combine(gitDir, "packed-refs")
		;
		
		if(!File.Exists(packedRefs))
			
			return null;
		
		var needle = $" refs/heads/{branchName}";
		
		foreach(var line in FileReader.ReadLines(packedRefs, FileReader.DocumentLimit)) {
			
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
		var normalized = Path.GetFullPath(absolutePath).Replace('/', Path.DirectorySeparatorChar)
		;
		
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
	
	// Streamed, with no limit: a hash holds no copy of the file, so it needs none. A limit here
	// would turn a changed file past it into a skipped conflict check, and the restore would
	// overwrite it.
	static string ComputeFileHash(string path)
	{
		using var stream = FileReader.OpenUnbounded(path);
		
		return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
	}
	
	
	// meta.json is a JSON object: { "token": BackupMeta, ... }
	Dictionary<string, BackupMeta> ReadAllMetaEntries(string metaFile)
	{
		if(!File.Exists(metaFile))
			
			return new Dictionary<string, BackupMeta>();
		
		try {
			
			var json = FileReader.ReadAllText(metaFile, FileReader.DocumentLimit);
			
			return JsonSerializer.Deserialize<Dictionary<string, BackupMeta>>(json, JsonOptions)
				?? new Dictionary<string, BackupMeta>();
		}
		catch(FileNotFoundException) {
			
			// TOCTOU race: file existed at File.Exists time but was deleted (e.g. by pruner) before read.
			
			return new Dictionary<string, BackupMeta>();
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
	// atomically, and removes empty directories. Only runs in a process that is a pruning run
	// (FilePruner), on a thread of its own. A named mutex per store lets one process prune a store
	// at a time; taken and released on this one thread, as a mutex must be.
	//
	// Each directory is pruned under the store's lock and the lock is let go between two of them:
	// a snapshot being saved reads and rewrites the same meta file, and it waits for one
	// directory's pass at most, never for the whole store's.
	void PruneExpiredGlobal()
	{
		if(backupRoot is null)
			
			return;
		
		var directories = 0;
		var removed     = 0;
		var owned       = false;
		Mutex? mutex    = null;
		
		try {
			
			// Created in here: opening a named mutex another account made throws, and an exception
			// that leaves this method leaves the thread, which ends the process.
			//
			// A mutex of this store's own, by its path. The directory pruners' mutex was shared
			// before, and they give up at once when it is held: this pass, which holds its mutex for
			// as long as it takes, would have cost every other process its log pruning, and a pass
			// over one store has nothing to keep from a pass over another.
			var storeId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(backupRoot).ToUpperInvariant())))[..16];
			
			mutex = new Mutex(false, $@"Global\RoslynMcp_BackupPruner_{storeId}");
			
			try {
				
				// Waited for: nothing waits for this thread, and another server pruning the same store
				// is done in the time one pass takes.
				owned = mutex.WaitOne(PruneMutexWait);
				
				if(!owned) {
					
					logger.LogInfo("Backups", $"prune skipped: another process pruned this store for {PruneMutexWait.TotalMinutes:0} minutes");
					
					return;
				}
			}
			catch(AbandonedMutexException) {
				owned = true;
			}
			catch {
				return;
			}
			
			// The pass's own time, without the wait for another process.
			var started = Stopwatch.GetTimestamp();
			var cutoff  = DateTime.UtcNow - TimeSpan.FromDays(ServerArgs.Current.BackupMaxAgeDays);
			
			try {
				
				foreach(var dir in Directory.EnumerateDirectories(backupRoot)) {
					
					directories++;
					asyncLock.Wait();
					
					try {
						removed += PruneExpiredInDir(dir, cutoff);
					}
					catch(Exception ex) {
						FilePruner.RecordPruneError(ex);
					}
					finally {
						asyncLock.Release();
					}
				}
			}
			catch(Exception ex) {
				// Enumeration of backup root failed — the rest of the prune pass is skipped.
				FilePruner.RecordPruneError(ex);
			}
			
			logger.LogInfo("Backups", $"pruned in {(long) Stopwatch.GetElapsedTime(started).TotalMilliseconds} ms: {directories} directories read, {removed} expired snapshot(s) removed");
		}
		catch(Exception ex) {
			
			// The last frame of a thread: an exception that leaves it ends the process.
			FilePruner.RecordPruneError(ex);
		}
		finally {
			
			if(owned)
				try { mutex?.ReleaseMutex(); }
				catch { }
			
			mutex?.Dispose();
		}
	}
	
	// The number of expired snapshots removed from the directory's meta file.
	int PruneExpiredInDir(string dir, DateTime cutoff)
	{
		var metaFile = Path.Combine(dir, MetaFileName);
		var entries  = ReadAllMetaEntries(metaFile);
		
		if(entries.Count == 0)
			
			return 0;
		
		var removed = 0;
		
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
			removed++;
			
			var bakFile = FindBakFile(dir, token, meta.AbsolutePath);
			
			if(bakFile is not null)
				try { File.Delete(bakFile); }
				catch { }
		}
		
		if(removed == 0)
			
			return 0;
		
		if(entries.Count == 0) {
			
			try { File.Delete(metaFile); } catch { }
			try { Directory.Delete(dir); } catch { }
			
			return removed;
		}
		
		WriteMetaAtomic(metaFile, entries);
		
		return removed;
	}
	
	void PruneOldBackups(string dir, string absolutePath)
	{
		var metaFile = Path.Combine(dir, MetaFileName);
		var entries  = ReadAllMetaEntries(metaFile);
		
		// Sort ascending by token (timestamp suffix) — oldest first.
		var ordered = entries.OrderBy(kv => kv.Key).ToList()
		;
		
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
	public string ContentHash { get; init; } = string.Empty;
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

