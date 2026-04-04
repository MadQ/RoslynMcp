using System.ComponentModel;
using System.Diagnostics;
using ModelContextProtocol.Server;

namespace RoslynMcp.Tools;

#if DEBUG
[McpServerToolType]
internal sealed class DebugAttachTool : RoslynMcpTool
{
	public DebugAttachTool(WorkspaceResolver workspace, FileLogger logger, PaginationCache paginationCache)
		: base(workspace, logger, paginationCache) { }
	
	[McpServerTool(Name = "roslyn_debug_attach", Title = "Debug Attach", OpenWorld = false, Destructive = false)]
	[Description(
		"DEBUG ONLY — do NOT call unless the user explicitly asks to attach a debugger. " +
		"Launches the JIT debugger dialog so Visual Studio can attach to the running server process. " +
		"The server BLOCKS until a debugger attaches or the dialog is dismissed — " +
		"calling this unexpectedly will freeze the server for all subsequent tool calls.")]
	public object DebugAttach()
	{
		using var scope = BeginTool("roslyn_debug_attach");
		
		var pid = Environment.ProcessId;
		
		if(Debugger.IsAttached)
			return scope.Outcome("already attached", new DebugAlreadyAttachedResult(true, pid, "A debugger is already attached."));
		
		Debugger.Launch();
		
		return scope.Outcome(Debugger.IsAttached ? "attached" : "dismissed", new DebugAttachResult(
			Debugger.IsAttached,
			pid,
			Debugger.IsAttached
				? "Debugger attached. Set breakpoints and invoke the next tool."
				: "Debugger dialog was dismissed without attaching."
		));
	}
}
#endif
