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
		"Workspace health probe — the tool to reach for when symbol results look wrong or stale, or when " +
		"roslyn_get_diagnostics reports errors you do not believe. Checks three independent axes. " +
		"(1) Source drift: files whose on-disk state changed without the workspace noticing — a " +
		"FileSystemWatcher miss (network drives, event buffer overflow, excluded directories). A healthy " +
		"workspace reports drifted: false; changes made within the last ~2 seconds may still be syncing and " +
		"are not reported. (2) Reference health: workspace_healthy is false when a project loaded with zero " +
		"metadata references, which happens when a contended MSBuild design-time build silently drops them. " +
		"That state makes symbol results wrong-but-plausible and makes roslyn_get_diagnostics report phantom " +
		"CS0246/CS0234 errors for code that builds fine — source can be perfectly in sync while this is broken. " +
		"(3) Pending reload: reload_pending is true when a change could not be applied incrementally and the " +
		"reload servicing it has not finished — the workspace is behind disk. This is the only axis that can " +
		"account for a newly added file, because drifted_files is built from documents the workspace already " +
		"has and a brand-new file is not one of them. " +
		"Any of these is fixed by roslyn_respawn; drift alone can also be cleared by re-saving the files " +
		"through roslyn editing tools. last_unhealthy_load is present when a dropped-reference load happened " +
		"at any point, even if the workspace has since recovered.")]
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
		
		// Second axis: a workspace can be perfectly in sync with disk and still be useless if its
		// design-time build dropped every metadata reference. Report both.
		var health        = TryGetHealth(projectPath);
		var unhealthy     = health is { IsHealthy: false };
		var reloadPending = health is { ReloadPending: true };

		var result = new CheckDriftResult(
			driftedFiles.Length > 0,
			driftedFiles.Length,
			driftedFiles[..Math.Min(driftedFiles.Length, MaxReportedFiles)],
			checkedCount,
			lastSynced.ToString("O"),
			isMSBuild,
			WorkspaceHealthy:          !unhealthy,
			ProjectsWithoutReferences: unhealthy ? health!.ProjectsWithoutReferences : null,
			LastUnhealthyLoad:         health?.LastUnhealthyLoad,
			ReloadPending:             reloadPending)
		{
			// reload_pending is reported first when set: it is the only signal that can account
			// for a file the workspace has never seen, which drifted_files structurally cannot.
			Hint = reloadPending
				? "A file change is waiting on a workspace reload that has not completed, so the workspace is "
					+ "behind disk — a newly added file may not appear in symbol results at all. This usually "
					+ "clears on its own within seconds; if it persists, something is holding a file the MSBuild "
					+ "design-time build needs. Call roslyn_respawn to force it."
				: (driftedFiles.Length > 0, unhealthy) switch {

				(true, true) =>
					"Two separate problems. Files changed on disk without the workspace noticing, AND the workspace "
					+ $"loaded without metadata references on: {string.Join(", ", health!.ProjectsWithoutReferences)}. "
					+ "Call roslyn_respawn — it fixes both.",

				(false, true) =>
					$"Source is in sync, but the workspace loaded without metadata references on: "
					+ $"{string.Join(", ", health!.ProjectsWithoutReferences)}. Symbol results are unreliable and "
					+ "roslyn_get_diagnostics will report phantom errors — a real build would succeed. "
					+ "Call roslyn_respawn to reload.",

				(true, false) =>
					"Files changed on disk without the workspace noticing. Symbol results may be stale — call "
					+ "roslyn_respawn to reload, or re-save the files through roslyn editing tools.",

				_ => null,
			}
		};

		var outcome = (driftedFiles.Length > 0, unhealthy) switch {

			(true, true)  => $"{driftedFiles.Length} drifted, no metadata references",
			(false, true) => "in sync, no metadata references",
			(true, false) => $"{driftedFiles.Length} drifted",
			_             => "in sync",
		};

		if(reloadPending)
			outcome += ", reload pending";

		return scope.Outcome(outcome, result);
	}
}
