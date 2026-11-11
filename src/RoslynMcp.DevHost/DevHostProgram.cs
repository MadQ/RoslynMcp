namespace RoslynMcp.DevHost;

/// <summary>
///     Development only (#426). The MCP client launches this in place of the server; every argument is
///     the server's and is passed on. See <c>docs/development/DEV_HOST.md</c>.
/// </summary>
static class DevHostProgram
{
	static async Task<int> Main(string[] arguments)
	{
		var home = new DevHome();
		
		return await new HostSession(home, new HostLog(home), arguments).RunAsync();
	}
}
