using System.IO.Enumeration;

namespace RoslynMcp;

/// <summary>
///     The one recursive directory walk for workspace trees. <c>Directory.EnumerateFiles(…,
///     SearchOption.AllDirectories)</c> cannot skip a directory: it descends into
///     <c>node_modules</c> and <c>.git</c> and filters afterwards, which on a large or
///     network-mounted tree costs more than an MCP client waits (#309). This walk decides per
///     directory, before descending, so a pruned tree costs one directory entry.
/// </summary>
internal static class WorkspaceWalker
{
	/// <summary>
	///     Decides whether a directory is left out of a walk. Receives the directory's own entry;
	///     use <see cref="RelativeParent"/> for its location under the walk root.
	/// </summary>
	public delegate bool PrunePredicate(ref FileSystemEntry directory);
	
	/// <summary>
	///     Deepest level a walk descends to. A backstop, not a filter — pruning is what bounds the
	///     cost, and source trees do nest deeply — so it is set far beyond any real layout and only
	///     stops a runaway tree.
	/// </summary>
	public const int MaxDepth = 32;
	
	// Same name-matching case rule the platform file APIs apply.
	static readonly bool ignoreCase = !OperatingSystem.IsLinux();
	
	/// <summary>
	///     Lazily enumerates the full paths of files under <paramref name="root"/> whose name matches
	///     <paramref name="namePattern"/> (<c>*</c> and <c>?</c> wildcards), skipping every directory
	///     <paramref name="prune"/> rejects. Never follows a directory symlink or junction — a link
	///     can point back up the tree — and skips unreadable directories instead of throwing.
	/// </summary>
	public static IEnumerable<string> EnumerateFiles(string root, string namePattern, bool recursive, PrunePredicate prune) =>
		new FileSystemEnumerable<string>(root, (ref FileSystemEntry entry) => entry.ToFullPath(), Options(recursive)) {
			
			ShouldIncludePredicate = (ref FileSystemEntry entry) => !entry.IsDirectory && FileSystemName.MatchesSimpleExpression(namePattern, entry.FileName, ignoreCase),
			ShouldRecursePredicate = (ref FileSystemEntry entry) => !IsLink(ref entry) && !prune(ref entry),
		}
	;
	
	/// <summary>
	///     Walks <paramref name="root"/> and returns the full paths of the directories
	///     <paramref name="prune"/> rejected, without entering them. This is how the watcher learns
	///     where the expensive trees are, at the cost of the directories it would watch anyway.
	/// </summary>
	public static List<string> FindPrunedDirectories(string root, PrunePredicate prune)
	{
		var pruned = new List<string>();
		
		var walk = new FileSystemEnumerable<bool>(root, (ref FileSystemEntry _) => true, Options(recursive: true)) {
			
			ShouldIncludePredicate = (ref FileSystemEntry _) => false,
			ShouldRecursePredicate = (ref FileSystemEntry entry) => {
				
				if(IsLink(ref entry))
					
					return false;
				
				if(!prune(ref entry))
					
					return true;
				
				pruned.Add(entry.ToFullPath());
				
				return false;
			},
		};
		
		// Nothing is ever included; enumerating is what drives the recursion.
		foreach(var _ in walk) { }
		
		return pruned;
	}
	
	/// <summary>
	///     The path of <paramref name="entry"/>'s parent directory relative to the walk root, with
	///     no leading separator — empty for an entry directly under the root.
	/// </summary>
	public static ReadOnlySpan<char> RelativeParent(ref FileSystemEntry entry) =>
		entry.Directory[entry.RootDirectory.Length..].TrimStart(Path.DirectorySeparatorChar).TrimStart(Path.AltDirectorySeparatorChar);
	
	static bool IsLink(ref FileSystemEntry entry) =>
		(entry.Attributes & FileAttributes.ReparsePoint) != 0 && IsLink(entry.ToFileSystemInfo());
	
	/// <summary>
	///     Whether <paramref name="info"/> is a link to somewhere else — a symlink or a junction.
	///     The reparse-point attribute alone is not enough: on Windows a cloud-synced folder
	///     (OneDrive and the like) carries it too, and is an ordinary directory that must be
	///     walked. Only a reparse point that resolves to a target is a link. One that cannot be
	///     read is treated as a link, the safe side for a walk.
	/// </summary>
	public static bool IsLink(FileSystemInfo info)
	{
		if(!info.Attributes.HasFlag(FileAttributes.ReparsePoint))
			
			return false;
		
		try {
			return info.LinkTarget is not null;
		}
		catch(Exception ex) when (ex is IOException or UnauthorizedAccessException) {
			return true;
		}
	}
	
	// AttributesToSkip defaults to Hidden | System, which on Unix hides every dot-file. The walks
	// here must see .editorconfig and .globalconfig, so nothing is skipped by attribute.
	static EnumerationOptions Options(bool recursive) => new() {
		
		RecurseSubdirectories = recursive,
		MaxRecursionDepth     = MaxDepth,
		IgnoreInaccessible    = true,
		AttributesToSkip      = FileAttributes.None,
	};
}
