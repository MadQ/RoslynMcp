using System.ComponentModel;
using ModelContextProtocol.Server;

namespace RoslynMcp.Tools;

[McpServerToolType]
internal sealed class CheckDriftTool : RoslynMcpTool
{
	public CheckDriftTool(WorkspaceResolver workspace, FileLogger logger, PaginationCache paginationCache) : base(workspace, logger, paginationCache) { }
	
	// FS timestamp granularity plus the 300ms FSW debounce window — changes younger than this
	// relative to the last sync may still be syncing and are not reported as drift.
	private static readonly TimeSpan DriftTolerance = TimeSpan.FromSeconds(2);
	
	private const int MaxReportedFiles = 200;
	
	[McpServerTool(Name = "roslyn_check_drift", ReadOnly = true, Title = "Check Drift", OpenWorld = false, Idempotent = true)]
	[Description(
		"Diagnostic probe: reports files whose on-disk state changed without the workspace noticing — " +
		"a FileSystemWatcher miss (network drives, event buffer overflow, excluded directories). " +
		"The watcher normally keeps the workspace in sync automatically, so a healthy workspace reports " +
		"drifted: false; use this when symbol results look inexplicably stale. " +
		"A drifted file means results may be based on outdated text — call roslyn_respawn to reload, " +
		"or re-save the files through roslyn editing tools. " +
		"Changes made within the last ~2 seconds may still be syncing and are not reported.")]
	public object CheckDrift([Description(ProjectPathDescription)] string projectPath)
	{
		using var scope = BeginTool("roslyn_check_drift", null);
		
		// PeekSolution, not GetSolution: forcing the pending reload would re-sync the workspace
		// and hide exactly the staleness this probe exists to measure.
		Microsoft.CodeAnalysis.Solution solution;
		
		try {
			solution = workspace.PeekSolution(projectPath);
		}
		catch(Exception ex) when(ex is InvalidOperationException or IOException or DirectoryNotFoundException) {
			
			return scope.Error(new ErrorResult(ex.Message));
		}
		
		var rootPath          = workspace.GetRootPath(projectPath);
		var (_, isMSBuild, _) = workspace.GetWorkspaceInfo(projectPath);
		var lastSynced        = workspace.GetLastSyncedUtc(projectPath);
		var cutoff            = lastSynced + DriftTolerance;
		
		var drifted      = new List<string>();
		var checkedCount = 0;
		
		foreach(var document in solution.Projects.SelectMany(p => p.Documents)) {
			
			// In-memory / source-generated documents have nothing on disk to drift against.
			if(document.FilePath is not { Length: > 0 } path)
				continue;
			
			checkedCount++;
			
			// Missing counts as drift too — the workspace still serves the deleted file's text.
			if(!File.Exists(path) || File.GetLastWriteTimeUtc(path) > cutoff)
				drifted.Add(TryMakeRelative(path, rootPath) ?? path);
		}
		
		// Dedupe (multi-TFM projects surface the same file per framework) and sort for
		// deterministic output.
		string[] driftedFiles = [..drifted
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.Order(StringComparer.Ordinal)];
		
		var result = new CheckDriftResult(
			driftedFiles.Length > 0,
			driftedFiles.Length,
			driftedFiles[..Math.Min(driftedFiles.Length, MaxReportedFiles)],
			checkedCount,
			lastSynced.ToString("O"),
			isMSBuild)
		{
			Hint = driftedFiles.Length > 0
				? "Files changed on disk without the workspace noticing. Symbol results may be stale — call roslyn_respawn to reload, or re-save the files through roslyn editing tools."
				: null
		};
		
		return scope.Outcome(driftedFiles.Length > 0 ? $"{driftedFiles.Length} drifted" : "in sync", result);
	}
}
