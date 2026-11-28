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
					(ref FileSystemEntry directory) => rules.IsExcluded(directory.FileName) || Levels(WorkspaceWalker.RelativeParent(ref directory)) >= Depth)
				.Where(WorkspaceManager.IsSolutionPath)
				.Where(path => !SecurityBoundary.PathComparer.Equals(Path.GetDirectoryName(path), root))
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
	public static string? Above(string path, string root, out string? severalIn)
	{
		severalIn = null;
		
		for(var directory = Path.GetDirectoryName(path); directory is not null && IsBelow(directory, root); directory = Path.GetDirectoryName(directory)) {
			
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
	public static string? ProjectAbove(string path, string root)
	{
		for(var directory = Path.GetDirectoryName(path); directory is not null && IsBelow(directory, root); directory = Path.GetDirectoryName(directory)) {
			
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
			if(relativeDirectory.IsEmpty)
				
				return rootRules.IsExcludedOrWorktree(relativeDirectory, name);
			
			var cut   = relativeDirectory.IndexOfAny(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
			var first = (cut < 0 ? relativeDirectory : relativeDirectory[..cut]).ToString();
			
			if(!below.TryGetValue(first, out var rules))
				below[first] = rules = IgnoreRules.ForRoot(Path.Combine(root, first), logger);
			
			return rules.IsPrunedFromListing(cut < 0 ? [] : relativeDirectory[(cut + 1)..], name);
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
	
	/// <summary>Forgets both, before the next file of a call that names several.</summary>
	public void Clear() => (Solution, ReportRoot) = (null, null);
}
