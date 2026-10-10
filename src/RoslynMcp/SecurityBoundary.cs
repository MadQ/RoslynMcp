namespace RoslynMcp;

/// <summary>
///     Enforces filesystem access boundaries for a workspace.
///     All path validation normalizes with <see cref="Path.GetFullPath"/> before comparison
///     to prevent directory traversal via <c>..</c> components or alternate path representations.
///     A symbolic link inside a trusted root is followed when it leads to somewhere inside a trusted
///     root, and denied when it leads out — escape via symlink redirection is what this prevents.
/// </summary>
internal sealed class SecurityBoundary
{
	readonly string[] allowedRoots;
	
	// How two paths are compared. On Linux names that differ only in case are different files,
	// and ignoring case there would count /work/ROOT/secrets as inside /work/root — a way out
	// for a link that points at it, or for a path that simply names it. Windows and macOS
	// file systems ignore case as a rule, and there the same file can arrive in either spelling.
	static readonly StringComparison pathComparison = OperatingSystem.IsLinux()
		? StringComparison.Ordinal
		: StringComparison.OrdinalIgnoreCase
	;
	
	/// <summary>The same comparison, for a set or dictionary keyed by path.</summary>
	internal static readonly StringComparer PathComparer = StringComparer.FromComparison(pathComparison)
	;
	
	// Computed once at startup — SpecialFolder lookups involve platform invocation and filesystem access.
	static readonly string[] systemDirectories = BuildSystemDirectories()
	;
	
	
	/// <param name="workspaceRoot">The resolved root directory of the workspace (project or solution directory).</param>
	/// <param name="solutionRoot">The solution directory, if any, which adds an additional trusted root.</param>
	/// <param name="referencedProjectRoots">Roots of directly referenced projects; each becomes a trusted root.</param>
	public SecurityBoundary(string workspaceRoot, string? solutionRoot = null, IEnumerable<string>? referencedProjectRoots = null)
	{
		var roots = new HashSet<string>(StringComparer.FromComparison(pathComparison)) { workspaceRoot };
		
		if(solutionRoot is not null)
			roots.Add(solutionRoot);
		
		if(referencedProjectRoots is not null)
			foreach(var r in referencedProjectRoots)
				roots.Add(r);
		
		allowedRoots = [..roots];
	}
	
	/// <summary>
	///     Returns true if <paramref name="requestedPath"/> is accessible within this workspace's
	///     trusted roots. Normalizes the path with <see cref="Path.GetFullPath"/>. A path that
	///     passes through a symbolic link inside a trusted root is allowed when the link leads to
	///     somewhere inside a trusted root, and denied when it leads out.
	/// </summary>
	public bool IsPathAllowed(string requestedPath)
	{
		string normalized;
		
		try {
			normalized = Path.GetFullPath(requestedPath);
		}
		catch(Exception ex) when(ex is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException) {
			
			return false;
		}
		
		foreach(var root in allowedRoots.AsSpan()) {
			
			if(!IsUnderDirectory(normalized, root))
				continue;
			
			// The common case, and the cheap one: no link on the way, nothing to resolve.
			if(!HasLinkBelowRoot(normalized, root) || ResolvesInsideARoot(normalized))
				
				return true;
		}
		
		return false;
	}
	
	/// <summary>
	///     Whether <paramref name="path"/>, with every symbolic link in it followed, is still
	///     inside one of the trusted roots — compared against the roots' own resolved locations,
	///     because a root can itself sit under a link (#330).
	///     <para>
	///         A link inside a workspace is ordinary: a shared folder linked into two projects, a
	///         file linked from another directory of the same repository. It is followed as long
	///         as it stays in the workspace (#322). What the boundary exists to stop is a link
	///         that leads out — <c>secrets -> /home/user/.ssh</c> — and that one still resolves to
	///         a path under no root. A link that cannot be resolved (a loop, an unreadable
	///         directory) is treated as leading out.
	///     </para>
	/// </summary>
	bool ResolvesInsideARoot(string path)
		=> ResolveLinks(path) is { } resolved && IsInsideAResolvedRoot(resolved)
	;
	
	// Whether a path that has already been resolved is inside a trusted root, the root's own
	// location resolved the same way.
	bool IsInsideAResolvedRoot(string resolved)
	{
		foreach(var root in allowedRoots.AsSpan())
			if(ResolveLinks(root) is { } resolvedRoot && IsUnderDirectory(resolved, resolvedRoot))
				
				return true;
		
		return false;
	}
	
	/// <summary>
	///     Where <paramref name="requestedPath"/> really is — every symbolic link along it
	///     followed — when that place is inside a trusted root; null when it is not, or when it
	///     cannot be worked out.
	///     <para>
	///         For a caller that goes on to write: <see cref="IsPathAllowed"/> answers yes or no
	///         about a path that still has its links in it, and a link can be pointed somewhere
	///         else between the answer and the write. The path returned here has no links left,
	///         and it is the very path the answer was given for — write to it, and the two
	///         cannot come apart (#334).
	///     </para>
	/// </summary>
	public string? ResolveAllowed(string requestedPath)
	{
		string normalized;
		
		try {
			normalized = Path.GetFullPath(requestedPath);
		}
		catch(Exception ex) when(ex is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException) {
			
			return null;
		}
		
		// The same first condition as IsPathAllowed: the path as written is under a root. Only
		// then does where it leads matter.
		foreach(var root in allowedRoots.AsSpan())
			if(IsUnderDirectory(normalized, root))
				
				return ResolveLinks(normalized) is { } resolved && IsInsideAResolvedRoot(resolved) ? resolved : null;
		
		return null;
	}
	
	/// <summary>
	///     <paramref name="fullPath"/> with every symbolic link or junction along it replaced by
	///     what it points at, or null when that cannot be worked out — a chain that loops, an
	///     unreadable directory, a link target with a <c>..</c> after a name. A part of the path
	///     that does not exist yet is kept as written: a file about to be created has no links
	///     to follow.
	/// </summary>
	internal static string? ResolveLinks(string fullPath)
	{
		try {
			
			// The path is walked one component at a time, a link replaced by its own target
			// the moment it is met, so that ".." is only ever applied to a place that has
			// been resolved. Asking for a link's final target and tidying the result as text
			// gets ".." wrong — in "d/../x" with d a link out of the workspace, ".." is the
			// parent of where d leads, not of where d sits — and that mistake reported a path
			// outside the workspace as one inside it.
			var pathRoot = Path.GetPathRoot(fullPath) ?? "";
			var pending  = new Stack<string>();
			var current  = pathRoot;
			var hops     = 0;
			
			if(!TryPushSegments(pending, fullPath[pathRoot.Length..]))
				
				return null;
			
			while(pending.TryPop(out var segment)) {
				
				if(segment == ".")
					continue;
				
				if(segment == "..") {
					
					// Everything in current has been resolved, so its parent is its real parent.
					current = Path.GetDirectoryName(current) ?? current;
					
					continue;
				}
				
				var candidate = Path.Combine(current, segment);
				
				FileSystemInfo info = Directory.Exists(candidate) ? new DirectoryInfo(candidate) : new FileInfo(candidate);
				
				// LinkTarget is null for everything that is not a link, and for what does not
				// exist: a part of the path that is still to be created is kept as written.
				if(info.LinkTarget is not { } target) {
					
					current = candidate;
					
					continue;
				}
				
				// The cap is what ends a chain of links that loops.
				if(++hops > 40)
					
					return null;
				
				if(Path.IsPathFullyQualified(target)) {
					
					var targetRoot = Path.GetPathRoot(target) ?? "";
					
					current = targetRoot;
					target  = target[targetRoot.Length..];
				}
				else if(Path.IsPathRooted(target))
					current = Path.GetPathRoot(current) ?? current;
				
				// A relative target continues from the directory that holds the link, which is
				// current as it stands.
				if(!TryPushSegments(pending, target))
					
					return null;
			}
			
			return current;
		}
		catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
		
		return null;
	}
	
	// Queues the segments of a path or link target, last segment first so that the first one is
	// the next to be popped. False for a ".." that comes after a name, as in "d/../x": where
	// that leads depends on whether d is a link and on whether the system applies ".." to the
	// text or to the place d leads to — Unix does the latter, Windows the former. Nothing is
	// lost by refusing to guess. Leading ".." segments are fine: they start from the directory
	// that holds the link, which is already resolved.
	static bool TryPushSegments(Stack<string> pending, string path)
	{
		var segments = path.Split(
			[Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
			StringSplitOptions.RemoveEmptyEntries);
		
		var nameSeen = false;
		
		foreach(var segment in segments.AsSpan()) {
			
			if(segment == "..") {
				
				if(nameSeen)
					
					return false;
			}
			else if(segment != ".")
				nameSeen = true;
		}
		
		for(var i = segments.Length - 1; i >= 0; i--)
			pending.Push(segments[i]);
		
		return true;
	}
	
	/// <summary>
	///     Whether <paramref name="path"/> passes through a symbolic link or junction somewhere
	///     below <paramref name="root"/>, the path's own last component included. Such a link can
	///     point anywhere, so the path has to be resolved before it can be trusted.
	///     <para>
	///         The root itself and the directories above it are deliberately not checked. They are
	///         where the user put the workspace: a link among them moves the whole workspace, it
	///         is not a way out of it. Checking the entire chain denied every file of a workspace
	///         that merely lives under a link — which on macOS is every workspace under the temp
	///         directory, since <c>/var</c> is a link to <c>/private/var</c> (#330).
	///     </para>
	/// </summary>
	internal static bool HasLinkBelowRoot(string path, string root)
	{
		var rootLength = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Length;
		
		try {
			
			var current = path;
			
			// Strictly longer than the root: the walk stops when it reaches the root itself.
			while(current.Length > rootLength) {
				
				if(IsLink(current))
					
					return true;
				
				var parent = Path.GetDirectoryName(current);
				
				if(parent is null || parent.Length >= current.Length)
					break;
				
				current = parent;
			}
		}
		catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) {
			
			// A component that cannot be examined may be a link. Saying so sends the path
			// through resolution instead of letting it pass unlooked-at.
			return true;
		}
		
		return false;
	}
	
	// Asks the entry itself and never what it points at, so a link whose target does not exist
	// is still a link: a write through it would create that target, wherever it is. A path
	// that does not exist is not a link — a file about to be created.
	static bool IsLink(string path)
	{
		try {
			return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);
		}
		catch(Exception ex) when(ex is FileNotFoundException or DirectoryNotFoundException) {
			
			return false;
		}
	}
	
	/// <summary>
	///     Returns true if <paramref name="fullPath"/> is a drive root or known system directory
	///     that must not be opened as a project. Used by <see cref="WorkspaceManager.ResolveProjectPath"/>
	///     before workspace creation to prevent arbitrary directory scanning.
	/// </summary>
	internal static bool IsDangerousProjectPath(string fullPath)
	{
		if(IsDriveRoot(fullPath))
			
			return true;
		
		foreach(var dir in systemDirectories.AsSpan()) {
			
			if(IsUnderDirectory(fullPath, dir))
				
				return true;
		}
		
		return false;
	}
	
	/// <summary>
	///     Returns true if <paramref name="path"/> is contained within <paramref name="root"/>,
	///     including the root directory itself. Uses separator-aware comparison to prevent prefix
	///     collisions (e.g. root <c>D:\Foo</c> incorrectly matching <c>D:\FooBar\file.cs</c>).
	/// </summary>
	internal static bool IsUnderDirectory(string path, string root)
	{
		if(string.IsNullOrEmpty(root))
			
			return false;
		
		var rootNorm = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		
		if(string.Equals(path, rootNorm, pathComparison))
			
			return true;
		
		// Check both separator styles for cross-platform compatibility.
		
		return path.StartsWith(rootNorm + Path.DirectorySeparatorChar, pathComparison)
			|| path.StartsWith(rootNorm + Path.AltDirectorySeparatorChar, pathComparison);
	}
	
	static bool IsDriveRoot(string fullPath)
	{
		var root = Path.GetPathRoot(fullPath);
		
		if(root is null)
			
			return false;
		
		// Strip trailing separators on both sides for consistent comparison.
		var normalizedFull = fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
		;
		var normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		
		return string.Equals(normalizedFull, normalizedRoot, pathComparison);
	}
	
	static string[] BuildSystemDirectories()
	{
		var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		
		if(OperatingSystem.IsWindows()) {
			
			AddIfNotEmpty(dirs, Environment.GetFolderPath(Environment.SpecialFolder.Windows));
			AddIfNotEmpty(dirs, Environment.GetFolderPath(Environment.SpecialFolder.System));
			AddIfNotEmpty(dirs, Environment.GetFolderPath(Environment.SpecialFolder.SystemX86));
		}
		else {
			
			dirs.Add("/etc");
			dirs.Add("/sys");
			dirs.Add("/proc");
			dirs.Add("/dev");
			dirs.Add("/bin");
			dirs.Add("/sbin");
			dirs.Add("/boot");
		}
		
		// User-sensitive directories common to both platforms.
		var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
		;
		
		if(!string.IsNullOrEmpty(home)) {
			
			AddIfNotEmpty(dirs, Path.Combine(home, ".ssh"));
			AddIfNotEmpty(dirs, Path.Combine(home, ".gnupg"));
			
			if(OperatingSystem.IsWindows()) {
				
				var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
				
				if(!string.IsNullOrEmpty(appData)) {
					
					AddIfNotEmpty(dirs, Path.Combine(appData, "Microsoft", "Credentials"));
					AddIfNotEmpty(dirs, Path.Combine(appData, "Microsoft", "Protect"));
				}
			}
		}
		
		return [..dirs];
	}
	
	static void AddIfNotEmpty(HashSet<string> set, string value)
	{
		if(!string.IsNullOrEmpty(value))
			set.Add(value);
	}
}
