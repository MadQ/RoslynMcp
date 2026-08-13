using BenchmarkDotNet.Running;
using RoslynMcp.Benchmarks;

BenchmarkSwitcher.FromAssembly(typeof(WarmBenchmarks).Assembly).Run(args);

namespace RoslynMcp.Benchmarks
{
	/// <summary>
	///     Shared environment for all benchmarks. ServerArgs must be initialized exactly once per
	///     process before any workspace type is used; logging is disabled so log I/O does not skew
	///     numbers. MSBuildLocator registration happens inside the workspace load path
	///     (MSBuildBootstrap), in a method that references no Microsoft.Build types.
	/// </summary>
	internal static class BenchEnv
	{
		internal static readonly FileLogger Logger;

		static BenchEnv()
		{
			// Field initializers would run before this body — Logger must be constructed after
			// ServerArgs.Initialize, so both happen here in order.
			ServerArgs.Initialize(["--log-path", ""]);
			Logger = new FileLogger();
		}

		internal static string RepoRoot { get; } = FindRepoRoot();

		internal static string ProjectPath => Path.Combine(RepoRoot, "src", "RoslynMcp", "RoslynMcp.csproj");

		static string FindRepoRoot()
		{
			for(var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
				if(File.Exists(Path.Combine(dir.FullName, "RoslynMcp.slnx")))
					return dir.FullName;

			throw new InvalidOperationException($"RoslynMcp.slnx not found above {AppContext.BaseDirectory}");
		}
	}
}
