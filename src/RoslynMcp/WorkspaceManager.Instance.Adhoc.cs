using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using System.Diagnostics;
using System.IO.Enumeration;

namespace RoslynMcp;

internal sealed partial class WorkspaceManager
{
	sealed partial class WorkspaceInstance
	{
		// ── AdhocWorkspace file management ───────────────────────────────────
		
		/// <summary>
		///     Loads every source file under the root as a document — one document per file on
		///     disk — and builds <see cref="physicalIndex"/> from the same look at the paths.
		///     <para>
		///         A source file that is a symbolic link is the same file as the one it leads
		///         to. Loading both puts the file into the compilation twice, with every type
		///         in it declared twice (#343). So a file is represented once: by its own path
		///         when that is among the files found, and otherwise — the file lies outside
		///         the root, or in a directory the walk stays out of — by the first link to it.
		///         <see cref="SyncAdhocDocument"/> applies the same rule to a path that turns up
		///         after the load, so a link is treated alike before and after a restart.
		///     </para>
		/// </summary>
		void LoadAllFiles(AdhocWorkspace adhocWorkspace, ProjectId projectId)
		{
			var files   = EnumerateFilesWithErrorHandling(rootPath, "*.cs").ToArray();
			var watch   = Stopwatch.StartNew();
			var index   = links.BuildIndex(files);
			var elapsed = watch.ElapsedMilliseconds;
			
			// The usual case: no links, nothing to decide.
			if(index.Count == 0) {
				
				foreach(var path in files)
					AddOrUpdateDocument(adhocWorkspace, projectId, path);
			}
			else {
				
				var found       = new HashSet<string>(files, SecurityBoundary.PathComparer);
				var represented = new HashSet<string>(SecurityBoundary.PathComparer);
				var leftOut     = new List<string>();
				
				foreach(var path in files) {
					
					if(index.PhysicalOf(path) is { } physical && (found.Contains(physical) || !represented.Add(physical))) {
						
						leftOut.Add(path);
						continue;
					}
					
					AddOrUpdateDocument(adhocWorkspace, projectId, path);
				}
				
				// The index holds documents only.
				index = index.With(leftOut, _ => null);
			}
			
			physicalIndex = index;
			
			logger.LogInfo("Links", $"{index.Count} of {files.Length} document path(s) lead to another file ({elapsed}ms)");
		}
		
		IEnumerable<string> EnumerateFilesWithErrorHandling(string path, string searchPattern)
		{
			var pathInfo = new DirectoryInfo(path);
			
			if(pathInfo.Attributes.HasFlag(FileAttributes.System) || pathInfo.Parent is null)
				
				return [];
			
			return WorkspaceWalker.EnumerateFiles(
				path,
				searchPattern,
				recursive: true,
				(ref FileSystemEntry directory) =>
					(directory.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0
					|| ignoreRules.IsExcluded(directory.FileName)
			);
		}
		
		void AddOrUpdateDocument(AdhocWorkspace adhocWorkspace, ProjectId projectId, string path)
		{
			if(!path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
				
				return;
			
			using var stream = File.OpenRead(path);
			
			var text = SourceText.From(stream, FileWriter.Utf8NoBom);
			var name = Path.GetRelativePath(rootPath, path)
			;
			
			var project  = adhocWorkspace.CurrentSolution.GetProject(projectId)!;
			var existing = project.Documents.FirstOrDefault(d => d.Name == name);
			
			Solution newSolution;
			
			if(existing is not null)
				newSolution = adhocWorkspace.CurrentSolution.WithDocumentText(existing.Id, text);
			else
				newSolution = adhocWorkspace.CurrentSolution.AddDocument(
					DocumentId.CreateNewId(projectId), name, text, filePath: path
				);
			
			adhocWorkspace.TryApplyChanges(newSolution);
			InvalidateCompilation();
		}
		
		/// <summary>
		///     <see cref="AddOrUpdateDocument"/> for a path that was written or reported after
		///     the load, when it may be another name for a file (#341). The walk that loads an
		///     adhoc workspace does not enter a linked directory, so a file under one is not a
		///     document under that name — but it may well be one under its own. Adding the
		///     written path would put the file into the compilation twice, with every type in
		///     it declared twice. The document the file already is gets the new text instead.
		///     A link that becomes a document here is entered in <see cref="physicalIndex"/>.
		/// </summary>
		void SyncAdhocDocument(AdhocWorkspace adhocWorkspace, ProjectId projectId, string path)
		{
			lock(adhocSyncLock) {
				
				// The file itself, when the documents it has are links to it: it lies outside the
				// tree the load walks (under obj, say) but inside what the watcher covers, so
				// a change to it is reported under its own path. Those documents are the file
				// and get the new text; the path does not become a second document.
				if(path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
					&& adhocWorkspace.CurrentSolution.GetDocumentIdsWithFilePath(path).IsEmpty
					&& physicalIndex.NamesOf(path) is { Length: > 0 } documents) {
					
					foreach(var name in documents)
						AddOrUpdateDocument(adhocWorkspace, projectId, name);
					
					return;
				}
				
				if(path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
					&& adhocWorkspace.CurrentSolution.GetDocumentIdsWithFilePath(path).IsEmpty
					&& links.TargetOf(path) is { } target) {
					
					if(!adhocWorkspace.CurrentSolution.GetDocumentIdsWithFilePath(target).IsEmpty) {
						
						AddOrUpdateDocument(adhocWorkspace, projectId, target);
						
						return;
					}
					
					// The file is not a document under its own name — it lies outside the root,
					// or in a directory the workspace stays out of — but it may be one under
					// another link. Those documents are the file, and get the new text.
					if(physicalIndex.NamesOf(target) is { Length: > 0 } known) {
						
						foreach(var name in known)
							AddOrUpdateDocument(adhocWorkspace, projectId, name);
						
						return;
					}
					
					AddOrUpdateDocument(adhocWorkspace, projectId, path);
					ReindexNames([path]);
					
					return;
				}
				
				AddOrUpdateDocument(adhocWorkspace, projectId, path);
			}
		}
		
		void RemoveDocument(AdhocWorkspace adhocWorkspace, ProjectId projectId, string path)
		{
			// Under the same lock as SyncAdhocDocument: both take the current solution, change
			// it and apply it, and the index is changed along with it.
			lock(adhocSyncLock) {
				
				if(physicalIndex.IsName(path))
					ForgetNames([path]);
				
				var name     = Path.GetRelativePath(rootPath, path);
				var project  = adhocWorkspace.CurrentSolution.GetProject(projectId)!;
				var existing = project.Documents.FirstOrDefault(d => d.Name == name);
				
				if(existing is null)
					
					return;
				
				adhocWorkspace.TryApplyChanges(adhocWorkspace.CurrentSolution.RemoveDocument(existing.Id));
				
				InvalidateCompilation();
			}
		}
	}
}
