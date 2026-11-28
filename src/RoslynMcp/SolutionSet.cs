using System.IO.Enumeration;

namespace RoslynMcp;

/// <summary>
///     A root directory that holds several solutions, each below it in a directory of its own
///     (#501): the place a session is opened in when a product is many repositories side by
///     side. Such a root is no workspace. It is a place files are listed, searched and read in,
///     and each call that needs code is answered by one of its solutions: the nearest one above
///     the file the call names.
/// </summary>
internal static class SolutionSet
{
	/// <summary>
	///     How many directory levels below the root a solution is looked for in. A repository
	///     often keeps its solution in <c>src</c> or under <c>tools</c>, which is two or three
	///     levels below a root of repositories; one more is kept in hand.
	/// </summary>
	public const int Depth = 4;
	
	/// <summary>
	///     The solutions below <paramref name="root"/>, not in it, sorted. The walk enters no
	///     directory a workspace never reads (<c>node_modules</c>, <c>.git</c>, <c>bin</c>,
	///     <c>obj</c>, the configured names) and no link, so a root of twenty repositories costs a
	///     few hundred directory entries. A solution below another solution's directory counts as
	///     one of its own: which of the two a file belongs to is decided by <see cref="Above"/>.
	/// </summary>
	public static string[] Find(string root, FileLogger logger)
	{
		var rules = IgnoreRules.ForRoot(root, logger);
		
		try {
			
			return [..WorkspaceWalker.EnumerateFiles(
					root,
					["*.sln", "*.slnx"],
					recursive: true,
					(ref FileSystemEntry directory) => rules.IsExcludedAt(WorkspaceWalker.RelativeParent(ref directory), directory.FileName) || Levels(WorkspaceWalker.RelativeParent(ref directory)) >= Depth)
				.Where(WorkspaceManager.IsSolutionPath)
				.Where(path => !SecurityBoundary.PathComparer.Equals(Path.GetDirectoryName(path), root))
				
				// A solution file directly in a directory that is only walked through, on the way
				// to an exception below it, is that ignored directory's own (#543).
				.Where(path => !rules.IsHidden(Path.GetRelativePath(root, Path.GetDirectoryName(path)!)))
				.Order(StringComparer.OrdinalIgnoreCase)];
		}
		catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) {
			
			return [];
		}
	}
	
	// How many directories a relative path names: none for the root itself. The two separators
	// are one character where the platform has only one, and must not be counted twice there.
	static int Levels(ReadOnlySpan<char> relative)
		=> relative.IsEmpty ? 0 : relative.Count(Path.DirectorySeparatorChar)
			+ (Path.AltDirectorySeparatorChar == Path.DirectorySeparatorChar ? 0 : relative.Count(Path.AltDirectorySeparatorChar))
			+ 1
	;
	
	/// <summary>
	///     The solution a path under <paramref name="root"/> belongs to: the one solution in the
	///     nearest directory above the path that holds any, the root itself left out. Only the
	///     file system decides, never which solutions are loaded, so the same file lands in the
	///     same solution in every call. <see langword="null"/> when no directory between the
	///     path and the root holds a solution, or when the nearest one holds several, which name
	///     no single owner and whose directory is then <paramref name="severalIn"/>, for the caller
	///     to say. The path need not exist: a file about to be created belongs where its nearest
	///     existing directory does. Not held to <see cref="Depth"/>: that bounds the walk down from
	///     the root, and the way up from a file is as long as its path.
	/// </summary>
	public static string? Above(string path, string root, IgnoreRules rules, out string? severalIn)
	{
		severalIn = null;
		
		for(var directory = Path.GetDirectoryName(path); directory is not null && IsBelow(directory, root); directory = Path.GetDirectoryName(directory)) {
			
			// A solution in a directory the server stays out of is no member of the set (#543):
			// the way up passes it as the walk down from the root never reaches it.
			if(rules.IsHidden(Path.GetRelativePath(root, directory)))
				continue;
			
			string[] solutions;
			
			try {
				solutions = [..Directory.EnumerateFiles(directory, "*.sln*").Where(WorkspaceManager.IsSolutionPath)];
			}
			catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) {
				continue;
			}
			
			if(solutions.Length == 1)
				
				return solutions[0];
			
			if(solutions.Length > 1) {
				
				severalIn = directory;
				
				return null;
			}
		}
		
		return null;
	}
	
	/// <summary>
	///     The project file a path under <paramref name="root"/> belongs to when no solution is
	///     above it: the one project file in the nearest directory above the path that holds
	///     any. <see langword="null"/> as for <see cref="Above"/>.
	/// </summary>
	public static string? ProjectAbove(string path, string root, IgnoreRules rules)
	{
		for(var directory = Path.GetDirectoryName(path); directory is not null && IsBelow(directory, root); directory = Path.GetDirectoryName(directory)) {
			
			if(rules.IsHidden(Path.GetRelativePath(root, directory)))
				continue;
			
			string[] projects;
			
			try {
				projects = Directory.GetFiles(directory, "*.csproj");
			}
			catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) {
				continue;
			}
			
			if(projects.Length > 0)
				
				return projects.Length == 1 ? projects[0] : null;
		}
		
		return null;
	}
	
	/// <summary>Whether <paramref name="directory"/> is inside <paramref name="root"/>, and not the root itself.</summary>
	static bool IsBelow(string directory, string root)
		=> !SecurityBoundary.PathComparer.Equals(directory, root) && SecurityBoundary.StartsWithPath(directory, root)
	;
	
	/// <summary>
	///     Which directories a listing, a search or a file lookup over the whole root leaves out.
	///     At the top level only the directories no workspace reads and the root's linked
	///     worktrees, and never the root's own
	///     <c>.gitignore</c>: the root is often a repository that ignores every repository in it,
	///     by name, and those are what the set is made of. Below that, each directory directly
	///     under the root is taken as a tree of its own, with its own <c>.gitignore</c>: what a
	///     repository ignores (<c>dist</c>, <c>coverage</c>) is said in its own file.
	/// </summary>
	public sealed class Listing(string root, FileLogger logger)
	{
		readonly IgnoreRules rootRules = IgnoreRules.ForRoot(root, logger);
		
		readonly Dictionary<string, IgnoreRules> below = new(SecurityBoundary.PathComparer);
		
		/// <summary>As <see cref="IgnoreRules.IsPrunedFromListing"/>, for a directory called <paramref name="name"/> in <paramref name="relativeDirectory"/> under the root.</summary>
		public bool IsPruned(ReadOnlySpan<char> relativeDirectory, ReadOnlySpan<char> name)
		{
			// The root's configured paths at every depth (#543): the rules of a directory below
			// the root may come from a config file of its own, which knows nothing of them.
			if(rootRules.DecidedByPaths(relativeDirectory, name) is { } decided)
				
				return decided;
			
			if(relativeDirectory.IsEmpty)
				
				return rootRules.IsExcludedOrWorktree(relativeDirectory, name);
			
			var cut = relativeDirectory.IndexOfAny(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
			
			return RulesBelow(cut < 0 ? relativeDirectory : relativeDirectory[..cut]).IsPrunedFromListing(cut < 0 ? [] : relativeDirectory[(cut + 1)..], name);
		}
		
		/// <summary>
		///     <see cref="IsPruned"/> by names alone, the configured paths left aside, in the same
		///     two steps: the root's names without its <c>.gitignore</c> at the top level, the
		///     rules of the directory's own tree below it. For a directory inside one that was
		///     asked for by name though the rules hide it.
		/// </summary>
		public bool IsPrunedByName(ReadOnlySpan<char> relativeDirectory, ReadOnlySpan<char> name)
		{
			if(relativeDirectory.IsEmpty)
				
				return rootRules.IsPrunedByName(IgnoreRules.Tier.ExcludedOrWorktree, relativeDirectory, name);
			
			var cut = relativeDirectory.IndexOfAny(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
			
			return RulesBelow(cut < 0 ? relativeDirectory : relativeDirectory[..cut]).IsPrunedByName(cut < 0 ? [] : relativeDirectory[(cut + 1)..], name);
		}
		
		// The rules of one directory directly under the root, taken as a tree of its own.
		IgnoreRules RulesBelow(ReadOnlySpan<char> first)
		{
			var name = first.ToString();
			
			if(!below.TryGetValue(name, out var rules))
				below[name] = rules = IgnoreRules.ForRoot(Path.Combine(root, name), logger);
			
			return rules;
		}
		
		/// <summary>
		///     As <see cref="IgnoreRules.HidesFilesOf(string?)"/>, by the rules <see cref="IsPruned"/>
		///     walked with: the root's paths as the top level asks them, without its
		///     <c>.gitignore</c>, and then the rules of the directory's own tree. Asked any other
		///     way, a directory the walk took for visible could lose its files, and one it
		///     entered as a passage by its own tree's rules could keep them.
		/// </summary>
		public bool HidesFilesOf(string? relativeDirectory)
		{
			if(string.IsNullOrEmpty(relativeDirectory))
				
				return false;
			
			if(rootRules.HidesFilesOf(relativeDirectory, IgnoreRules.Tier.ExcludedOrWorktree))
				
				return true;
			
			var path = relativeDirectory.AsSpan();
			var cut  = path.IndexOfAny(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
			
			// A directory directly under the root is the root of its own tree, and no passage there.
			return cut >= 0 && RulesBelow(path[..cut]).HidesFilesOf(path[(cut + 1)..].ToString());
		}
	}
}

/// <summary>
///     Where the current tool call has landed, for a call that named no project in a session
///     whose root is a <see cref="SolutionSet"/>. Two things follow the call from the moment its
///     solution is picked. The solution itself: a project file can be part of several loaded
///     solutions, and a path to it alone would be answered by whichever was loaded last. And the
///     root paths are relative to: such a call takes and reports paths relative to the root of
///     the set, since a path relative to one solution could not be passed to the next call
///     without naming that solution.
/// </summary>
internal sealed class CallLanding
{
	static readonly AsyncLocal<CallLanding?> current = new();
	
	/// <summary>The landing of the call in progress, or <see langword="null"/> outside one.</summary>
	public static CallLanding? Current => current.Value;
	
	/// <summary>Starts a call with nothing decided. Called where a tool call begins.</summary>
	public static void Begin() => current.Value = new CallLanding();
	
	/// <summary>The solution file the call goes on in, as the workspace cache keys it.</summary>
	public string? Solution { get; set; }
	
	/// <summary>The root of the set, which the call's paths are relative to.</summary>
	public string? ReportRoot { get; set; }
	
	/// <summary>
	///     How many solutions of the set were not loaded, and so not searched, when the call
	///     landed by a symbol's name alone: another of them may declare the same name.
	/// </summary>
	public int NotSearched { get; set; }
	
	/// <summary>The symbol the call, or this part of it, asked for by name: for a note that names it.</summary>
	public string? Symbol { get; set; }
	
	/// <summary>Forgets all of it, before the next file or name of a call that names several.</summary>
	public void Clear() => (Solution, ReportRoot, NotSearched, Symbol) = (null, null, 0, null);
}
