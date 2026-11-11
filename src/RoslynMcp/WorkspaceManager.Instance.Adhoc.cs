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
			var walked  = EnumerateFilesWithErrorHandling(rootPath, AdhocFilePatterns).ToArray();
			var files   = walked.Where(path => !IsAnalyzerConfig(path)).ToArray();
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
			
			// The configs of the tree, and those of the directories above it: a config applies
			// to everything below its directory, and the root is often a folder of a repository
			// whose config sits further up. One that cannot be read is left out, like a config
			// that is not there; the sources do not depend on it.
			var configs = 0;
			
			foreach(var path in walked.Where(IsAnalyzerConfig).Concat(AnalyzerConfigsAbove(rootPath)))
				try {
					
					if(AddOrUpdateAnalyzerConfig(adhocWorkspace, projectId, path))
						configs++;
				}
				catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) {
					
					logger.LogInfo("Workspace", $"Analyzer config not loaded ({ex.GetType().Name}: {ex.Message}): {path}");
				}
			
			if(configs > 0)
				logger.LogInfo("Workspace", $"{configs} {AnalyzerConfigName} file(s) loaded for {rootPath}");
		}
		
		// The one analyzer config an AdhocWorkspace reads (#413). A project also takes
		// .globalconfig files, but only those its build names; without a build nothing names one.
		const string AnalyzerConfigName = ".editorconfig";
		
		// What an AdhocWorkspace tracks under its root: the sources and the configs that style them.
		static readonly string[] AdhocFilePatterns = ["*.cs", AnalyzerConfigName];
		
		// By name alone, compared as the platform's file APIs compare names.
		static bool IsAnalyzerConfig(string path)
			=> Path.GetFileName(path.AsSpan()).Equals(AnalyzerConfigName, OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase)
		;
		
		// The configs in the directories above the root, nearest first, all the way up: the same
		// files a project's build hands the compiler. Roslyn itself stops at one that says
		// "root = true", so none is left out here, and a config that gains or loses that line
		// needs no second look at the directories above. They lie outside the root, where nothing
		// is watched: a change to one of them is seen when the workspace is next loaded.
		static IEnumerable<string> AnalyzerConfigsAbove(string directory)
		{
			for(var parent = new DirectoryInfo(directory).Parent; parent is not null; parent = parent.Parent) {
				
				var candidate = Path.Combine(parent.FullName, AnalyzerConfigName);
				
				if(File.Exists(candidate))
					yield return candidate;
			}
		}
		
		// Whether the loading walk reaches a path: it lies under the root, and no directory
		// between the two is one the walk stays out of (an excluded name, a hidden or system
		// directory, or a link). A directory that cannot be asked counts as one it stays out of.
		bool IsInLoadedTree(string path)
		{
			if(!SecurityBoundary.IsUnderDirectory(path, rootPath) || edits.IsUnderExcludedDirectory(path))
				
				return false;
			
			// The directories below the root, not the root: the walk starts in it, whatever it is.
			var rootLength = rootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Length;
			
			try {
				
				for(var directory = Path.GetDirectoryName(path); directory is not null && directory.Length > rootLength; directory = Path.GetDirectoryName(directory)) {
					
					// The walk's own test for a link: a reparse point alone is not one, a
					// cloud-synced folder carries the attribute and is walked.
					var info = new DirectoryInfo(directory);
					
					if(!info.Exists || (info.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0 || WorkspaceWalker.IsLink(info))
						
						return false;
				}
			}
			catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) {
				
				return false;
			}
			
			return true;
		}
		
		/// <summary>
		///     Makes an <c>.editorconfig</c> an analyzer-config document of the project, or gives
		///     the document it already is the file's text (#413). Roslyn's formatter reads the
		///     style of the code a refactoring or a code fix writes from these documents, and the
		///     compiler reads diagnostic severities from them, as in a project. Without them an
		///     AdhocWorkspace formats in Roslyn's defaults whatever the directory's own config says.
		///     Returns whether the workspace took the change.
		///     <para>
		///         A config that is a symbolic link is read through the link, but is not entered
		///         in <see cref="physicalIndex"/>, which holds sources only. A change to the file
		///         it leads to is therefore not seen under the link's name; the config is read
		///         again when the link itself is reported, or at the next load.
		///     </para>
		/// </summary>
		bool AddOrUpdateAnalyzerConfig(AdhocWorkspace adhocWorkspace, ProjectId projectId, string path)
		{
			using var stream = FileReader.OpenUnbounded(path);
			
			var text     = SourceText.From(stream, FileWriter.Utf8NoBom);
			var solution = adhocWorkspace.CurrentSolution;
			var existing = solution.GetDocumentIdsWithFilePath(path).FirstOrDefault(id => solution.GetAnalyzerConfigDocument(id) is not null);
			
			var newSolution = existing is not null
				? solution.WithAnalyzerConfigDocumentText(existing, text)
				: solution.AddAnalyzerConfigDocument(DocumentId.CreateNewId(projectId), AnalyzerConfigName, text, filePath: path)
			;
			
			var applied = adhocWorkspace.TryApplyChanges(newSolution);
			
			if(!applied)
				logger.LogInfo("Workspace", $"Analyzer config not applied, the workspace moved on: {path}");
			
			InvalidateCompilation();
			
			return applied;
		}
		
		// A drive root or a directory marked as the system's own is never searched for sources.
		static bool IsLoadableRoot(string path)
		{
			var pathInfo = new DirectoryInfo(path);
			
			return !pathInfo.Attributes.HasFlag(FileAttributes.System) && pathInfo.Parent is not null;
		}
		
		IEnumerable<string> EnumerateFilesWithErrorHandling(string path, string[] searchPatterns)
		{
			if(!IsLoadableRoot(path))
				
				return [];
			
			return WorkspaceWalker.EnumerateFiles(
				path,
				searchPatterns,
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
			
			using var stream = FileReader.OpenUnbounded(path);
			
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
				
				// A config is a document of its own kind, and never one of the sources below.
				// One the workspace has is read again wherever it lies. A new one is taken only
				// from where the load would have found it, so that the workspace is the same
				// before and after a restart.
				if(IsAnalyzerConfig(path)) {
					
					if(!adhocWorkspace.CurrentSolution.GetDocumentIdsWithFilePath(path).IsEmpty || IsInLoadedTree(path))
						AddOrUpdateAnalyzerConfig(adhocWorkspace, projectId, path);
					
					return;
				}
				
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
				
				if(IsAnalyzerConfig(path)) {
					
					var solution = adhocWorkspace.CurrentSolution;
					var config   = solution.GetDocumentIdsWithFilePath(path).FirstOrDefault(id => solution.GetAnalyzerConfigDocument(id) is not null);
					
					if(config is null)
						
						return;
					
					adhocWorkspace.TryApplyChanges(solution.RemoveAnalyzerConfigDocument(config));
					InvalidateCompilation();
					
					return;
				}
				
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
