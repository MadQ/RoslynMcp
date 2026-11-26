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
///         stay out of, and a wrong one costs a directory not listed.
///     </para>
/// </summary>
internal static class GitWorktrees
{
	// A repository with more worktrees than this was not made by hand; the rest are walked.
	const int maxWorktrees = 256;
	
	// How far above the root the repository is looked for. A backstop against a path that
	// never ends, not a limit a real tree meets.
	const int maxAncestors = 64;
	
	/// <summary>
	///     The directory that holds the worktree records of the repository
	///     <paramref name="rootPath"/> is in, or null when it is in none. The directory need not
	///     exist: a repository without linked worktrees has none.
	/// </summary>
	public static string? RecordsDirectory(string rootPath)
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
				
				return Path.Combine(common, "worktrees");
			}
		}
		catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) {
			// Unreadable is as good as absent: nothing is pruned.
		}
		
		return null;
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
		
		return target.Length == 0
			? null
			: Path.GetFullPath(Path.Combine(Path.GetDirectoryName(gitFile)!, target));
	}
	
	/// <summary>
	///     A value that changes when the records under <paramref name="recordsDirectory"/> do: a
	///     worktree added or removed writes the directory, one moved rewrites its record. 0 when
	///     there are none. A stat per worktree, so that a worktree made during a session is
	///     seen without a restart.
	/// </summary>
	public static long Stamp(string? recordsDirectory)
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
		catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) {
			
			return 0;
		}
	}
	
	/// <summary>
	///     The roots of the linked worktrees below <paramref name="rootPath"/>, relative to it,
	///     as the records under <paramref name="recordsDirectory"/> name them. Never throws.
	/// </summary>
	public static string[] Below(string rootPath, string recordsDirectory)
	{
		var found = new List<string>();
		
		try {
			
			// Both sides resolved, so that a root reached through a link (on macOS the
			// temporary directory is one) is still seen to contain what git recorded by its
			// real path.
			var root  = FileIdentity.FinalPath(rootPath) ?? Path.GetFullPath(rootPath);
			var count = 0;
			
			foreach(var record in new DirectoryInfo(recordsDirectory).EnumerateDirectories()) {
				
				if(++count > maxWorktrees)
					break;
				
				if(RootOf(record) is not { } worktree)
					continue;
				
				var relative = Path.GetRelativePath(root, FileIdentity.FinalPath(worktree) ?? worktree);
				
				// Below the root, and not the root: a workspace that is a worktree is walked.
				if(relative is "." or ".." || Path.IsPathRooted(relative) || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
					continue;
				
				found.Add(relative);
			}
		}
		catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) {
			// What was read so far stands: each entry is a worktree on its own.
		}
		
		return [..found];
	}
	
	// The root of the worktree one record is for, or null when the record is stale. A record
	// outlives its worktree until git prunes it, and the directory may since be an ordinary one:
	// only a directory that still holds the ".git" file the record names is a worktree.
	static string? RootOf(DirectoryInfo record)
	{
		try {
			
			var file = Path.Combine(record.FullName, "gitdir");
			
			if(!File.Exists(file))
				
				return null;
			
			var recorded = FileReader.ReadAllText(file, FileReader.ConfigLimit).Trim();
			
			if(recorded.Length == 0)
				
				return null;
			
			// Absolute, or relative to the record's directory (git's worktree.useRelativePaths).
			var marker = Path.GetFullPath(Path.Combine(record.FullName, recorded));
			
			return File.Exists(marker) ? Path.GetDirectoryName(marker) : null;
		}
		catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) {
			
			return null;
		}
	}
}
