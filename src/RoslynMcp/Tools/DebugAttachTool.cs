using System.ComponentModel;
using System.Diagnostics;
using ModelContextProtocol.Server;

namespace RoslynMcp.Tools;

#if DEBUG
[McpServerToolType]
internal sealed class DebugAttachTool
{
	readonly FileLogger logger;

	public DebugAttachTool(FileLogger logger) { this.logger = logger; }

	[McpServerTool(Name = "roslyn_debug_attach", Destructive = false)]
	[Description(
		"DEBUG ONLY — do NOT call unless the user explicitly asks to attach a debugger. " +
		"Launches the JIT debugger dialog so Visual Studio can attach to the running server process. " +
		"The server pauses until a debugger attaches or the dialog is dismissed.")]
	public object DebugAttach()
	{
		var pid = Environment.ProcessId;
		logger.LogTool("roslyn_debug_attach", 0, true, detail: $"launching debugger for PID {pid}");

		if(Debugger.IsAttached)
			return new DebugAlreadyAttachedResult(true, pid, "A debugger is already attached.");

		Debugger.Launch();

		return new DebugAttachResult(
			Debugger.IsAttached,
			pid,
			Debugger.IsAttached
				? "Debugger attached. Set breakpoints and invoke the next tool."
				: "Debugger dialog was dismissed without attaching."
		);
	}
}
#endif
