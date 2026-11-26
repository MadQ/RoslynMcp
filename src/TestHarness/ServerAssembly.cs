using System.Reflection;

/// <summary>
///     The server assembly the harness built, for the tests that call one of its internal methods
///     by reflection instead of going through a tool call. Loaded once per run.
/// </summary>
static class ServerAssembly
{
	static Assembly? loaded;
	
	internal static Assembly Load(TestContext ctx)
	{
		if(loaded is not null)
			return loaded;
		
		var projectDir   = Path.GetDirectoryName(ctx.ServerProj)!;
		var assemblyPath = Path.Combine(projectDir, "bin", "Debug", "net10.0", "RoslynMcp.dll");
		
		loaded = Assembly.LoadFrom(assemblyPath);
		
		return loaded;
	}
}
