using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Build.Locator;
using Microsoft.Win32;

namespace RoslynMcp;

/// <summary>
///     Discovers, configures, and registers MSBuild for the process.
///     Tries MSBuildLocator first, then falls back to vswhere.exe to find
///     Visual Studio installations. Thread-safe — blocks concurrent callers
///     via semaphore until discovery is complete.
/// </summary>
internal static class MSBuildBootstrap
{
	static readonly SemaphoreSlim gate = new(1, 1);
	static bool    completed;
	static string? failureReason;
	static string discoveryMethod = "not attempted";

	/// <summary>How MSBuild was discovered. Always non-null — describes the method or the failure.</summary>
	public static string DiscoveryMethod => discoveryMethod;
	static WorkspaceMode resolvedMode;

	/// <summary>The workspace mode that was resolved and applied.</summary>
	public static WorkspaceMode ResolvedMode => resolvedMode;

	/// <summary>
	///     Peeks at a .csproj to determine if it's SDK-style or old-style (.NET Framework).
	///     SDK-style projects have <c>Sdk="Microsoft.NET.Sdk"</c> in the Project element.
	///     Old-style projects have <c>ToolsVersion</c> or <c>TargetFrameworkVersion</c>.
	///     Reads only the first few lines — fast, no XML parsing.
	/// </summary>
	public static WorkspaceMode DetectProjectStyle(string csprojPath)
	{
		if(!File.Exists(csprojPath))
			return WorkspaceMode.Sdk;

		try {

			using var reader = new StreamReader(csprojPath);

			for(var i = 0; i < 5 && !reader.EndOfStream; i++) {

				var line = reader.ReadLine();

				if(line is null)
					break;

				if(line.Contains("Sdk=", StringComparison.OrdinalIgnoreCase))
					return WorkspaceMode.Sdk;

				if(line.Contains("ToolsVersion=", StringComparison.OrdinalIgnoreCase) ||
				   line.Contains("TargetFrameworkVersion", StringComparison.OrdinalIgnoreCase))
					return WorkspaceMode.Vs;
			}
		}
		catch { }

		return WorkspaceMode.Sdk;
	}

	/// <summary>
	///     Finds the first .csproj under a directory for project style detection.
	/// </summary>
	public static string? FindFirstCsproj(string directory)
	{
		try {
			return Directory.EnumerateFiles(directory, "*.csproj", SearchOption.AllDirectories)
				.FirstOrDefault();
		}
		catch { return null; }
	}



	/// <summary>
	///     Ensures MSBuild is registered for the process. Blocks until discovery
	///     is complete. Subsequent calls return immediately. If discovery failed,
	///     returns the failure reason (does not throw — callers decide how to handle).
	/// </summary>
	public static string? EnsureReady(WorkspaceMode mode = WorkspaceMode.Auto)
	{
		if(completed)
			return failureReason;

		gate.Wait();

		try {

			if(completed)
				return failureReason;


			resolvedMode = mode;

			// Adhoc mode: skip MSBuild entirely.
			if(mode == WorkspaceMode.Adhoc) {
				discoveryMethod = "adhoc mode (MSBuild skipped)";
				return null;
			}

			// VS mode: skip SDK, go straight to vswhere.
			if(mode == WorkspaceMode.Vs) {

				if(!OperatingSystem.IsWindows()) {
					failureReason = "VS workspace mode requires Windows (Visual Studio MSBuild).";
					discoveryMethod = "not found — " + failureReason;
					return failureReason;
				}

				var msbuildDir = TryVsWhere();

				if(msbuildDir is not null) {
					PrependToPath(msbuildDir);
					if(TryRegister()) {
						discoveryMethod = $"resolved via vswhere — VS mode ({msbuildDir})";
						return null;
					}
				}

				failureReason = "Visual Studio MSBuild not found. Install Visual Studio or Build Tools.";
				discoveryMethod = "not found — " + failureReason;
				return failureReason;
			}

			// SDK mode (or Auto): standard discovery chain.

			// 1. Try MSBuildLocator directly — works when .NET SDK is on PATH.
			if(TryRegister()) {

				discoveryMethod = "resolved via PATH (.NET SDK found on PATH)";
				return null;
			}

			// 2. dotnet not on PATH — discover via env vars, registry, well-known paths.
			if(TryDiscoverDotnet(out var dotnetDir, out var source)) {

				PrependToPath(dotnetDir);

				if(TryRegister()) {

					discoveryMethod = source;
					return null;
				}
			}

			// 3. Windows only: try vswhere.exe to find Visual Studio MSBuild.
			if(OperatingSystem.IsWindows()) {

				var msbuildDir = TryVsWhere();

				if(msbuildDir is not null) {

					PrependToPath(msbuildDir);

					if(TryRegister()) {

						discoveryMethod = $"resolved via vswhere ({msbuildDir})";
						return null;
					}
				}
			}

			failureReason = OperatingSystem.IsWindows()
				? "MSBuild not found. Install .NET SDK or Visual Studio Build Tools. "
					+ "If installed in a non-standard location, set DOTNET_ROOT or ROSLYNMCP_MSBUILD_PATH."
				: "MSBuild not found. Install the .NET SDK (https://dot.net). "
					+ "If installed in a non-standard location, set DOTNET_ROOT."
			;

			discoveryMethod = "not found — " + failureReason;
			return failureReason;
		}
		finally {

			completed = true;
			gate.Release();
		}
	}

	/// <summary>
	///     Discovers the dotnet SDK directory via env vars, registry, or
	///     well-known platform-specific install locations.
	///     Returns (directory, source description) or (null, null).
	/// </summary>
	static bool TryDiscoverDotnet(out string dotnetDir, out string source)
	{
		// DOTNET_ROOT is the official cross-platform override.
		var dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");

		if(dotnetRoot is not null && Directory.Exists(dotnetRoot)) {

			dotnetDir = dotnetRoot;
			source    = "resolved via DOTNET_ROOT env var";
			return true;
		}

		// DOTNET_ROOT(x86) — official env var for x86 SDK on Windows (yes, parens in the name).
		if(OperatingSystem.IsWindows()) {

			var dotnetRootX86 = Environment.GetEnvironmentVariable("DOTNET_ROOT(x86)");

			if(dotnetRootX86 is not null && Directory.Exists(dotnetRootX86)) {

				dotnetDir = dotnetRootX86;
				source    = "resolved via DOTNET_ROOT(x86) env var";
				return true;
			}
		}

		// DOTNET_HOST_PATH — set by some .NET hosting scenarios.
		var hostPath = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");

		if(hostPath is not null && File.Exists(hostPath)) {

			var hostDir = Path.GetDirectoryName(hostPath);

			if(hostDir is not null) {

				dotnetDir = hostDir;
				source    = "resolved via DOTNET_HOST_PATH env var";
				return true;
			}
		}

		// Windows: check registry for SDK install location.
		if(OperatingSystem.IsWindows()) {

			var regPath = TryDotnetFromRegistry();

			if(regPath is not null) {

				dotnetDir = regPath;
				source    = "resolved via Windows registry";
				return true;
			}
		}

		// Well-known install paths per platform.
		var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

		string[] candidates = OperatingSystem.IsWindows()
			? [
				Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet"),
				Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "dotnet"),
				Path.Combine(home, ".dotnet")
			]
			: [
				"/usr/share/dotnet",
				"/usr/lib/dotnet",
				"/usr/local/share/dotnet",
				"/opt/homebrew/share/dotnet",
				"/snap/dotnet-sdk/current",
				Path.Combine(home, ".dotnet")
			]
		;

		var exeName = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";

		foreach(var dir in candidates) {

			if(!Directory.Exists(dir))
				continue;

			if(File.Exists(Path.Combine(dir, exeName))) {

				dotnetDir = dir;
				source    = $"resolved via well-known path ({dir})";
				return true;
			}
		}

		dotnetDir = source = "";
		return false;
	}

	static bool TryRegister()
	{
		try {

			if(!MSBuildLocator.CanRegister)
				return false;

			MSBuildLocator.RegisterDefaults();
			return true;
		}
		catch {

			return false;
		}
	}

	/// <summary>
	///     Runs vswhere.exe to find the latest Visual Studio installation with MSBuild.
	///     Windows-only — returns null on Linux/macOS (vswhere doesn't exist; .NET SDK
	///     installs are discovered by MSBuildLocator.RegisterDefaults directly).
	/// </summary>
	static string? TryVsWhere()
	{
		// vswhere.exe lives at a well-known path under the VS Installer.
		// No registry key for vswhere itself — the installer path is predictable.
		var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
		var vsWherePath  = Path.Combine(programFiles, "Microsoft Visual Studio", "Installer", "vswhere.exe");

		if(!File.Exists(vsWherePath))
			return null;

		try {

			var psi = new ProcessStartInfo(vsWherePath) {

				Arguments              = "-latest -requires Microsoft.Component.MSBuild -find MSBuild\\**\\Bin\\MSBuild.exe",
				RedirectStandardOutput = true,
				RedirectStandardError  = true,
				UseShellExecute        = false,
				CreateNoWindow         = true
			};

			using var proc = Process.Start(psi);

			if(proc is null)
				return null;

			var output = proc.StandardOutput.ReadToEnd().Trim();
			proc.WaitForExit(5_000);

			if(proc.ExitCode != 0 || string.IsNullOrEmpty(output))
				return null;

			// vswhere returns the full path to MSBuild.exe — we need its directory.
			var firstLine = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();

			if(firstLine is null || !File.Exists(firstLine))
				return null;

			return Path.GetDirectoryName(firstLine);
		}
		catch {

			return null;
		}
	}

	/// <summary>
	///     Checks the Windows registry for the .NET SDK install location.
	///     Key: HKLM\SOFTWARE\dotnet\Setup\InstalledVersions\{arch}\InstallLocation
	/// </summary>
	[SupportedOSPlatform("windows")]
	static string? TryDotnetFromRegistry()
	{
		try {

			// Try current architecture first, then fall back to x64.
			string[] archKeys = RuntimeInformation.ProcessArchitecture == Architecture.Arm64
				? ["arm64", "x64"]
				: ["x64", "x86"]
			;

			foreach(var arch in archKeys) {

				var value = Registry.GetValue(
					@$"HKEY_LOCAL_MACHINE\SOFTWARE\dotnet\Setup\InstalledVersions\{arch}",
					"InstallLocation",
					null
				) as string;

				if(value is not null && Directory.Exists(value))
					return value;
			}
		}
		catch {

			// Registry access can fail for permissions or platform reasons.
		}

		return null;
	}

	static void PrependToPath(string directory)
	{
		var current = Environment.GetEnvironmentVariable("PATH") ?? "";
		Environment.SetEnvironmentVariable("PATH", directory + Path.PathSeparator + current);
	}
}
