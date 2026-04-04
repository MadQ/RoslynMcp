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

static class WorkspaceModeParser
{
	const string EnvVar = "ROSLYNMCP_WORKSPACE";
	
	/// <summary>
	///     Resolves the workspace mode from CLI args and env var.
	///     Priority: CLI arg → env var → <see cref="WorkspaceMode.Auto"/>.
	/// </summary>
	public static WorkspaceMode Resolve(string[] args)
	{
		// CLI: --workspace sdk|vs|adhoc|auto
		for(var i = 0; i < args.Length - 1; i++)
			if(string.Equals(args[i], "--workspace", StringComparison.OrdinalIgnoreCase))
				return Parse(args[i + 1]) ?? WorkspaceMode.Auto;
		
		// Env var: ROSLYNMCP_WORKSPACE=sdk|vs|adhoc|auto
		var env = Environment.GetEnvironmentVariable(EnvVar);
		
		if(env is not null)
			return Parse(env) ?? WorkspaceMode.Auto;
		
		return WorkspaceMode.Auto;
	}
	
	/// <summary>Strips the <c>--workspace</c> arg pair from the args array so it doesn't confuse project preloading.</summary>
	public static string[] StripWorkspaceArg(string[] args)
	{
		for(var i = 0; i < args.Length - 1; i++) {
			
			if(string.Equals(args[i], "--workspace", StringComparison.OrdinalIgnoreCase)) {
				
				var list = new List<string>(args);
				list.RemoveAt(i + 1);
				list.RemoveAt(i);
				
				return list.ToArray();
			}
		}
		
		return args;
	}
	
	static WorkspaceMode? Parse(string value) => value.ToLowerInvariant() switch {
		
		"auto"  => WorkspaceMode.Auto,
		"sdk"   => WorkspaceMode.Sdk,
		"vs"    => WorkspaceMode.Vs,
		"adhoc" => WorkspaceMode.Adhoc,
		_       => null,
	};
}
