using Microsoft.CodeAnalysis;

namespace RoslynMcp;

internal sealed partial class WorkspaceManager
{
	sealed partial class WorkspaceInstance
	{
		/// <summary>
		///     Answers where a path leads when the symbolic links on the way to it are followed,
		///     spelled the way this workspace spells paths, and builds the
		///     <see cref="PhysicalFileIndex"/> of a solution from those answers.
		///     <para>
		///         A type of its own because the answer needs no state of the workspace: only
		///         where its root is, which is fixed when the workspace is constructed, and the
		///         file system. It takes no lock and changes nothing. It is given what it needs
		///         and has no way back to the <see cref="WorkspaceInstance"/> that owns it.
		///     </para>
		/// </summary>
		sealed class LinkResolver(string rootPath)
		{
			/// <summary>
			///     The file a write to <paramref name="fullPath"/> lands in when that is a different
			///     file from the one the path names — the path is a symbolic link, or lies under a
			///     linked directory — and null when it is not, or when it cannot be worked out.
			///     <para>
			///         The result is spelled the way the watcher and the workspace's documents spell
			///         paths, not the way the file system resolves them. Resolving links gives the
			///         real location, and that differs from the path as it was opened whenever a
			///         directory above sits under a link — on macOS every temp directory does
			///         (<c>/var</c> is <c>/private/var</c>). A path in that spelling matches neither a
			///         watcher event nor a document. So the resolved path is re-spelled through the
			///         workspace root, or the nearest directory above the root that contains it: the
			///         part up to that directory is kept as written, the rest is the real location.
			///         For a path with no link below that directory this gives the path back, and the
			///         answer is null.
			///     </para>
			/// </summary>
			public string? TargetOf(string fullPath)
				=> TryTargetOf(fullPath, out var target) ? target : null
			;
			
			/// <summary>
			///     <see cref="TargetOf"/> for a caller that has to tell "leads nowhere else"
			///     from "could not be worked out": false for the second, and
			///     <paramref name="target"/> is then null without meaning anything. A caller
			///     that acts on a <em>change</em> in where a path leads needs the difference —
			///     a path that cannot be examined for a moment has not changed.
			/// </summary>
			public bool TryTargetOf(string fullPath, out string? target)
			{
				target = null;
				
				try {
					
					// The usual case by far, and it has to be cheap: this is asked for every
					// document of every change the workspace applies, a thousand at a time when a
					// branch is switched. A path under the root with no link below the root leads
					// nowhere else, and finding that out is one attribute query per directory
					// level — no resolution of the path or of the root.
					if(SecurityBoundary.IsUnderDirectory(fullPath, rootPath) && !SecurityBoundary.HasLinkBelowRoot(fullPath, rootPath))
						
						return true;
					
					if(SecurityBoundary.ResolveLinks(fullPath) is not { } resolved)
						
						return false;
					
					// From the workspace root upwards — not from the written path. A link at or above
					// the root is where the user put the workspace and stays as written; a link
					// below it is what is being followed. Starting at the written path's own
					// directory would find a linked directory first and spell the target straight
					// back through it, as the path that was written.
					for(var above = (string?) rootPath; above is not null; above = Path.GetDirectoryName(above)) {
						
						if(SecurityBoundary.ResolveLinks(above) is not { } resolvedAbove
							|| !SecurityBoundary.IsUnderDirectory(resolved, resolvedAbove))
							continue;
						
						var respelled = Path.Combine(above, Path.GetRelativePath(resolvedAbove, resolved));
						
						// The same name the way the file system compares names, which on Linux is
						// to the letter: Foo.cs leading to foo.cs leads somewhere else.
						target = SecurityBoundary.PathComparer.Equals(respelled, fullPath) ? null : respelled;
						
						return true;
					}
					
					target = SecurityBoundary.PathComparer.Equals(resolved, fullPath) ? null : resolved;
					
					return true;
				}
				catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) {
					
					return false;
				}
			}
			
			/// <summary>
			///     Builds the index for a solution, and says in <paramref name="documentPaths"/>
			///     how many document paths were looked at.
			/// </summary>
			public PhysicalFileIndex BuildIndex(Solution solution, out int documentPaths)
			{
				var paths = solution.Projects
					.SelectMany(project => project.Documents
						.Concat<TextDocument>(project.AdditionalDocuments)
						.Concat(project.AnalyzerConfigDocuments))
					.Select(document => document.FilePath)
					.OfType<string>()
					.Distinct(SecurityBoundary.PathComparer)
					.ToArray()
				;
				
				documentPaths = paths.Length;
				
				return BuildIndex(paths);
			}
			
			/// <summary>
			///     Builds the index for a set of paths. Looks at the file system once per
			///     directory that holds one of them and once per path; a path is resolved only
			///     when one of those turned up a link.
			/// </summary>
			public PhysicalFileIndex BuildIndex(string[] paths)
			{
				var directories = new Dictionary<string, bool>(SecurityBoundary.PathComparer);
				
				// Null without resolving anything for a document under the root with no link on
				// the way to it — nearly every document there is. HasLinkBelowRoot answers true
				// for what it cannot examine, and TargetOf then has the last word.
				string? PhysicalOf(string path)
				{
					if(Path.GetDirectoryName(path) is { } directory) {
						
						// Asked once per directory. Under the root that is a look at each level
						// down to it. A directory elsewhere — a referenced project, when the root
						// is one project's directory — is resolved in full, which is why it must
						// not be done per document.
						if(!directories.TryGetValue(directory, out var linked)) {
							
							linked = SecurityBoundary.IsUnderDirectory(directory, rootPath)
								? SecurityBoundary.HasLinkBelowRoot(directory, rootPath)
								: TargetOf(directory) is not null
							;
							directories.Add(directory, linked);
						}
						
						// With the directory as the root, only the file itself is looked at.
						if(!linked && !SecurityBoundary.HasLinkBelowRoot(path, directory))
							
							return null;
					}
					
					return TargetOf(path);
				}
				
				return PhysicalFileIndex.Build(paths, PhysicalOf);
			}
		}
	}
}
