using System.IO.Enumeration;

namespace RoslynMcp;

/// <summary>
///     The linked git worktrees that lie below a workspace root (#511). A worktree is another
///     checkout of the same repository: every file of the tree a second time. One inside the
///     checkout it belongs to (<c>.claude/worktrees/&lt;name&gt;</c> is where Claude Code puts
///     them) made a file named by the end of its path ambiguous, and every listing and search
///     answer for two trees.
///     <para>
///         Git records its worktrees in the repository: <c>.git/worktrees/&lt;name&gt;/gitdir</c>
///         holds the path of the <c>.git</c> file at that worktree's root. Reading those records
///         costs nothing per directory walked, which a test for a <c>.git</c> file in every
///         directory would. A submodule and a nested clone are not in them, and are not
///         boundaries: both are often real inputs of the solution above them.
///     </para>
///     <para>
///         The records are found by following the <c>.git</c> entry at or above the root, which
///         can lead out of the workspace (the root may itself be a worktree, whose repository is
///         elsewhere). Only files named <c>gitdir</c> and <c>commondir</c> are read there, each
///         bounded, and nothing of them is returned: they yield directories below the root to
///         stay out of, and a wrong one costs a directory not listed. A path that leads to
///         another machine is not followed.
///     </para>
///     <para>
///         Worktrees are made while a session runs, so the answer is never fixed for the life
///         of a workspace: <see cref="For"/> checks the records again, every time for a call
///         that lists and at most once a second for the questions a file event asks.
///     </para>
/// </summary>
internal static class GitWorktrees
{
	// A repository with more worktrees than this was not made by hand; the rest are walked.
	const int maxWorktrees = 256;
	
	// How far above the root the repository is looked for. A backstop against a path that
	// never ends, not a limit a real tree meets.
	const int maxAncestors = 64;
	
	// How long an answer is taken as it is by a caller that does not ask for a fresh one. A
	// burst of file events asks once per event; the records are a stat per worktree.
	const long recheckMilliseconds = 1000;
	
	static readonly Dictionary<string, (Below Worktrees, long CheckedAt)> cache = new(SecurityBoundary.PathComparer);
	static readonly Lock cacheLock = new();
	
	/// <summary>
	///     The linked worktrees below one workspace root, as the records said when they were last
	///     read. The same instance is handed out for as long as nothing changed, so a holder can
	///     tell by reference whether what it derived from one still stands.
	/// </summary>
	internal sealed class Below
	{
		/// <summary>No worktrees: the answer for a root in no repository, or in one without any.</summary>
		public static readonly Below None = new([], [], [], 0);
		
		// The roots as full paths in the workspace root's own spelling, which is the spelling
		// of every path a walk or a file event under that root has.
		readonly string[] roots;
		
		// Every record's ".git" file and whether it was there: a record whose worktree is gone
		// is no boundary, and one whose worktree came back is one again.
		readonly (string Marker, bool Existed)[] markers;
		
		internal Below(string[] roots, string[] relativePaths, (string Marker, bool Existed)[] markers, long stamp)
		{
			this.roots    = roots;
			this.markers  = markers;
			RelativePaths = relativePaths;
			Stamp         = stamp;
		}
		
		/// <summary>The roots relative to the workspace root, with forward slashes.</summary>
		public string[] RelativePaths { get; }
		
		internal long Stamp { get; }
		
		/// <summary>
		///     Whether the directory a walk has reached is the root of one of the worktrees. The
		///     full path is built only when there is a worktree to compare it with.
		/// </summary>
		public bool IsRoot(ref FileSystemEntry directory)
		{
			if(roots.Length == 0)
				
				return false;
			
			var fullPath = directory.ToFullPath();
			
			foreach(var root in roots)
				if(SecurityBoundary.PathComparer.Equals(root, fullPath))
					return true;
			
			return false;
		}
		
		/// <summary>The root of the worktree that <paramref name="fullPath"/> is, or lies in; null when it is in none.</summary>
		public string? Of(string fullPath)
		{
			foreach(var root in roots)
				if(SecurityBoundary.IsUnderDirectory(fullPath, root))
					return root;
			
			return null;
		}
		
		// Whether every record's ".git" file is as it was: the records' own stamp does not move
		// when a worktree's directory is deleted, or put back, without git.
		internal bool MarkersUnchanged()
		{
			foreach(var (marker, existed) in markers)
				if(File.Exists(marker) != existed)
					return false;
			
			return true;
		}
	}
	
	/// <summary>
	///     The linked worktrees below <paramref name="rootPath"/>. With <paramref name="fresh"/>
	///     the records are checked now, which a call that lists or looks a file up wants: a
	///     worktree made a moment ago must not be listed. Without it an answer up to a second old
	///     is taken, for the questions asked once per file event. Never throws.
	/// </summary>
	public static Below For(string rootPath, bool fresh)
	{
		var now = Environment.TickCount64;
		
		Below? known;
		
		lock(cacheLock) {
			
			known = cache.TryGetValue(rootPath, out var cached) ? cached.Worktrees : null;
			
			if(known is not null && !fresh && now - cached.CheckedAt < recheckMilliseconds)
				
				return known;
		}
		
		var records = RecordsDirectory(rootPath);
		var stamp   = Stamp(records);
		
		// Outside the lock: stats, and reads when something changed.
		var worktrees = known is not null && known.Stamp == stamp && known.MarkersUnchanged()
			? known
			: stamp == 0 ? Below.None : Read(rootPath, records!, stamp);
		
		lock(cacheLock)
			cache[rootPath] = (worktrees, now);
		
		return worktrees;
	}
	
	/// <summary>
	///     The directory that holds the worktree records of the repository
	///     <paramref name="rootPath"/> is in, or null when it is in none. The directory need not
	///     exist: a repository without linked worktrees has none.
	/// </summary>
	static string? RecordsDirectory(string rootPath)
	{
		try {
			
			var directory = new DirectoryInfo(rootPath);
			
			for(var depth = 0; directory is not null && depth < maxAncestors; directory = directory.Parent, depth++) {
				
				var entry = Path.Combine(directory.FullName, ".git");
				
				if(Directory.Exists(entry))
					
					return Path.Combine(entry, "worktrees");
				
				if(!File.Exists(entry))
					continue;
				
				// A file: this directory is a linked worktree or a submodule, and the file says
				// where its git directory is. A worktree's git directory names the repository's
				// own in "commondir"; a submodule's is a repository of its own.
				if(GitDirectoryOf(entry) is not { } gitDirectory)
					
					return null;
				
				var commonFile = Path.Combine(gitDirectory, "commondir");
				var common     = File.Exists(commonFile)
					? Path.GetFullPath(Path.Combine(gitDirectory, FileReader.ReadAllText(commonFile, FileReader.ConfigLimit).Trim()))
					: gitDirectory;
				
				return IsOnAnotherMachine(common) ? null : Path.Combine(common, "worktrees");
			}
		}
		catch(Exception ex) when(IsUnreadable(ex)) {
			// Unreadable is as good as absent: nothing is pruned.
		}
		
		return null;
	}
	
	// What a path that cannot be used, or a file that cannot be read, throws. Each of them means
	// "no answer from here", never a failed tool call.
	static bool IsUnreadable(Exception ex)
		=> ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException
	;
	
	// A UNC path. The content of a ".git" file decides where the records are looked for, and a
	// tree that was unpacked from somewhere must not make the server call a host of its choosing
	// at every lookup. The extended-length form of a local path ("\\?\C:\…") is not one.
	static bool IsOnAnotherMachine(string path)
	{
		if(path.Length < 2 || !IsSeparator(path[0]) || !IsSeparator(path[1]))
			
			return false;
		
		return !(path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal))
			|| path.AsSpan(4).StartsWith("UNC", StringComparison.OrdinalIgnoreCase);
		
		static bool IsSeparator(char c) => c is '\\' or '/';
	}
	
	// The directory a ".git" file points to: its one line is "gitdir: <path>", absolute or
	// relative to the file's directory.
	static string? GitDirectoryOf(string gitFile)
	{
		const string prefix = "gitdir:";
		
		var line = FileReader.ReadAllText(gitFile, FileReader.ConfigLimit).AsSpan().Trim();
		
		if(!line.StartsWith(prefix, StringComparison.Ordinal))
			
			return null;
		
		var target = line[prefix.Length..].Trim().ToString();
		
		if(target.Length == 0 || IsOnAnotherMachine(target))
			
			return null;
		
		return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(gitFile)!, target));
	}
	
	/// <summary>
	///     A value that changes when the records under <paramref name="recordsDirectory"/> do: a
	///     worktree added or removed writes the directory, one moved rewrites its record. 0 when
	///     there are none. A stat per worktree, so that a worktree made during a session is
	///     seen without a restart.
	/// </summary>
	static long Stamp(string? recordsDirectory)
	{
		if(recordsDirectory is null)
			
			return 0;
		
		try {
			
			var directory = new DirectoryInfo(recordsDirectory);
			
			if(!directory.Exists)
				
				return 0;
			
			var stamp = directory.LastWriteTimeUtc.Ticks;
			var count = 0;
			
			foreach(var record in directory.EnumerateDirectories()) {
				
				if(++count > maxWorktrees)
					break;
				
				var file = new FileInfo(Path.Combine(record.FullName, "gitdir"));
				
				if(file.Exists)
					stamp = unchecked(stamp * 31 + (file.LastWriteTimeUtc.Ticks ^ file.Length));
			}
			
			// 0 is "no records"; a directory that exists is never that.
			return stamp == 0 ? 1 : stamp;
		}
		catch(Exception ex) when(IsUnreadable(ex)) {
			
			return 0;
		}
	}
	
	// The worktrees the records under recordsDirectory name, of which those below rootPath are
	// kept as roots. Never throws.
	static Below Read(string rootPath, string recordsDirectory, long stamp)
	{
		var roots    = new List<string>();
		var relative = new List<string>();
		var markers  = new List<(string, bool)>();
		
		try {
			
			// Both sides resolved, so that a root reached through a link (on macOS the
			// temporary directory is one) is still seen to contain what git recorded by its
			// real path.
			var root  = FileIdentity.FinalPath(rootPath) ?? Path.GetFullPath(rootPath);
			var count = 0;
			
			foreach(var record in new DirectoryInfo(recordsDirectory).EnumerateDirectories()) {
				
				if(++count > maxWorktrees)
					break;
				
				if(MarkerOf(record) is not { } marker)
					continue;
				
				// A record outlives its worktree until git prunes it, and the directory may
				// since be an ordinary one: only a directory that still holds the ".git" file
				// the record names is a worktree.
				var exists = File.Exists(marker);
				
				markers.Add((marker, exists));
				
				if(!exists || Path.GetDirectoryName(marker) is not { } worktree)
					continue;
				
				var below = Path.GetRelativePath(root, FileIdentity.FinalPath(worktree) ?? worktree);
				
				// Below the root, and not the root: a workspace that is a worktree is walked.
				if(below is "." or ".." || Path.IsPathRooted(below) || below.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
					continue;
				
				roots.Add(Path.Combine(rootPath, below));
				relative.Add(below.Replace('\\', '/'));
			}
		}
		catch(Exception ex) when(IsUnreadable(ex)) {
			// What was read so far stands: each entry is a worktree on its own.
		}
		
		return new([..roots], [..relative], [..markers], stamp);
	}
	
	// The ".git" file one record names, at the root of its worktree; null when the record
	// cannot be read or names another machine.
	static string? MarkerOf(DirectoryInfo record)
	{
		try {
			
			var file = Path.Combine(record.FullName, "gitdir");
			
			if(!File.Exists(file))
				
				return null;
			
			var recorded = FileReader.ReadAllText(file, FileReader.ConfigLimit).Trim();
			
			if(recorded.Length == 0 || IsOnAnotherMachine(recorded))
				
				return null;
			
			// Absolute, or relative to the record's directory (git's worktree.useRelativePaths).
			return Path.GetFullPath(Path.Combine(record.FullName, recorded));
		}
		catch(Exception ex) when(IsUnreadable(ex)) {
			
			return null;
		}
	}
}
