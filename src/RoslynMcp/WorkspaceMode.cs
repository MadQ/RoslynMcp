namespace RoslynMcp;

/// <summary>
///     Controls which MSBuild engine is used for workspace loading.
///     Set via <c>--workspace</c> CLI arg or <c>ROSLYNMCP_WORKSPACE</c> env var.
///     CLI arg takes precedence over env var; both override auto-detection.
/// </summary>
enum WorkspaceMode
{
	/// <summary>Auto-detect: peek at .csproj to choose SDK or VS MSBuild. Falls back to adhoc.</summary>
	Auto,
	
	/// <summary>Use .NET SDK MSBuild. Best for modern SDK-style projects (.NET 6+).</summary>
	Sdk,
	
	/// <summary>Use Visual Studio MSBuild via vswhere. Required for .NET Framework projects.</summary>
	Vs,
	
	/// <summary>Skip MSBuild entirely. Fast startup, reduced semantics (no NuGet, no project refs).</summary>
	Adhoc,
}

