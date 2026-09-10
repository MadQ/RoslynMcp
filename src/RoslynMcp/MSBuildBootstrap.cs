using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Build.Locator;
using Microsoft.Win32;

namespace RoslynMcp;

/// <summary>
///     Discovers, configures, and registers MSBuild for the process.
///     Tries MSBuildLocator first, then falls back to vswhere.exe to find
///     Visual Studio installations. Thread-safe — blocks concurrent callers
///     via semaphore until discovery is complete.
/// </summary>
internal static partial class MSBuildBootstrap
{
	static readonly SemaphoreSlim gate = new(1, 1);
	// volatile: the fast-path DCL check (if(completed) return failureReason) runs without the
	// gate, so the JIT/CPU must not cache completed or reorder reads of the other fields past it.
	static volatile bool    completed
	;
	static volatile string? failureReason;
	static volatile string  discoveryMethod = "not attempted";
	static volatile string? resolvedVsVersionRange;
	
	/// <summary>How MSBuild was discovered. Always non-null — describes the method or the failure.</summary>
	public static string DiscoveryMethod => discoveryMethod;
	
	static volatile WorkspaceMode resolvedMode;
	
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
	///     Determines the workspace mode for an auto-mode load of <paramref name="path"/> — a
	///     .csproj, .sln, or .slnx. A solution is judged by the projects it actually references:
	///     one legacy project is enough for <see cref="WorkspaceMode.Vs"/>, because Roslyn loads it
	///     through the .NET Framework BuildHost regardless of how modern its siblings are. Peeking
	///     at whichever .csproj the file system enumerates first is what #266 fixed — that file may
	///     be a stale copy the solution never references. Returns the mode plus a short description
	///     of what decided it, for the log.
	/// </summary>
	public static (WorkspaceMode Mode, string Detail) DetectLoadStyle(string path)
	{
		if(path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)) {
			
			var style = DetectProjectStyle(path);
			
			return (style, $"{Path.GetFileName(path)} is {(style == WorkspaceMode.Vs ? "legacy" : "SDK")}-style");
		}
		
		var isSolution = path.EndsWith(".sln",  StringComparison.OrdinalIgnoreCase)
		              || path.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase);
		
		var projects = isSolution ? ReadSolutionProjects(path) : [];
		var source   = Path.GetFileName(path);
		
		if(projects.Length == 0) {
			
			// Not a solution, or one that references nothing readable — scan the directory instead,
			// nearest files first and skipping build output, where stale project copies tend to live.
			projects = FindCsprojCandidates(Path.GetDirectoryName(path) ?? path);
			source   = "directory scan";
		}
		
		if(projects.Length == 0)
			
			return (WorkspaceMode.Sdk, "no .csproj found — defaulting to Sdk");
		
		foreach(var csproj in projects) {
			
			if(DetectProjectStyle(csproj) == WorkspaceMode.Vs)
				
				return (WorkspaceMode.Vs, $"{Path.GetFileName(csproj)} is legacy-style ({source}, {projects.Length} projects)");
		}
		
		return (WorkspaceMode.Sdk, $"all {projects.Length} projects SDK-style ({source})");
	}
	
	/// <summary>
	///     Lists the .csproj files a .sln or .slnx references, as full paths, limited to those that
	///     exist on disk. Text-level parsing on purpose: this runs before MSBuild is registered, so
	///     Microsoft.Build.Construction.SolutionFile is not available yet. Non-C# projects and
	///     solution folders are skipped. Returns empty on any read or parse failure.
	/// </summary>
	public static string[] ReadSolutionProjects(string solutionPath)
	{
		try {
			
			var solutionDir = Path.GetDirectoryName(solutionPath) ?? "";
			
			var relativePaths = solutionPath.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase)
				? XDocument.Load(solutionPath).Descendants("Project").Select(p => (string?) p.Attribute("Path")).OfType<string>()
				: File.ReadLines(solutionPath).Select(line => SlnProjectLine().Match(line)).Where(m => m.Success).Select(m => m.Groups[1].Value);
			
			var result = new List<string>();
			
			foreach(var relative in relativePaths) {
				
				if(!relative.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
					continue;
				
				var full = Path.GetFullPath(Path.Combine(solutionDir, relative.Replace('\\', Path.DirectorySeparatorChar)));
				
				if(File.Exists(full))
					result.Add(full);
			}
			
			return [..result];
		}
		catch(Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException or ArgumentException) {
			return [];
		}
	}
	
	// Project("{GUID}") = "Name", "rel\path.csproj", "{GUID}" — the second quoted value is the path.
	[GeneratedRegex(@"^\s*Project\(""\{[0-9A-Fa-f-]+\}""\)\s*=\s*""[^""]*""\s*,\s*""([^""]+)""", RegexOptions.CultureInvariant)]
	private static partial Regex SlnProjectLine();
	
	/// <summary>
	///     Finds .csproj files under <paramref name="directory"/> for style detection when no
	///     solution lists them: files in the directory itself first, then one level at a time,
	///     skipping build output and tooling folders (bin, obj, packages, node_modules, dot-folders)
	///     where stale or copied project files live. Bounded so a huge tree cannot stall a load.
	/// </summary>
	public static string[] FindCsprojCandidates(string directory)
	{
		const int maxCandidates = 32;
		
		var result = new List<string>();
		var queue  = new Queue<string>();
		
		queue.Enqueue(directory);
		
		while(queue.Count > 0 && result.Count < maxCandidates) {
			
			var dir = queue.Dequeue();
			
			try {
				
				result.AddRange(Directory.EnumerateFiles(dir, "*.csproj"));
				
				foreach(var sub in Directory.EnumerateDirectories(dir)) {
					
					if(!IsSkippedFolder(Path.GetFileName(sub)))
						queue.Enqueue(sub);
				}
			}
			catch(Exception ex) when (ex is IOException or UnauthorizedAccessException) {
				// Unreadable directory — skip it; detection is best-effort.
			}
		}
		
		return [..result.Take(maxCandidates)];
	}
	
	static bool IsSkippedFolder(string name) =>
		name.StartsWith('.')
		|| name.Equals("bin",          StringComparison.OrdinalIgnoreCase)
		|| name.Equals("obj",          StringComparison.OrdinalIgnoreCase)
		|| name.Equals("packages",     StringComparison.OrdinalIgnoreCase)
		|| name.Equals("node_modules", StringComparison.OrdinalIgnoreCase)
	;
	
	/// <summary>
	///     Ensures MSBuild is registered for the process. Blocks until discovery
	///     is complete. Subsequent calls return immediately. If discovery failed,
	///     returns the failure reason (does not throw — callers decide how to handle).
	/// </summary>
	/// <param name="mode">The workspace mode to resolve MSBuild for; adhoc skips MSBuild entirely.</param>
	/// <param name="vsVersionRange">
	///     Optional Visual Studio version pin in vswhere <c>-version</c> syntax (see
	///     <see cref="VsVersionPin"/>). In VS mode it narrows which instance is registered; in
	///     SDK/auto mode it only pins the out-of-process .NET Framework BuildHost, which otherwise
	///     picks the newest installed Visual Studio on its own. Ignored in adhoc mode.
	/// </param>
	public static string? EnsureReady(WorkspaceMode mode = WorkspaceMode.Auto, string? vsVersionRange = null)
	{
		if(completed)
			
			return failureReason ?? ReportVsVersionConflict(mode, vsVersionRange);
		
		gate.Wait();
		
		try {
			
			if(completed)
				
				return failureReason ?? ReportVsVersionConflict(mode, vsVersionRange);
			
			resolvedMode = mode;
			resolvedVsVersionRange = vsVersionRange;
			
			try {
				
				// Adhoc mode: skip MSBuild entirely.
				if(mode == WorkspaceMode.Adhoc) {
					
					discoveryMethod = "adhoc mode (MSBuild skipped)";
					
					return null;
				}
				
				// Direct path override — CLI --msbuild-path or ROSLYNMCP_MSBUILD_PATH, already
				// merged by ServerArgs (CLI wins). Point it at a directory containing MSBuild:
				// a dotnet SDK directory, or a VS install's MSBuild\Current\Bin. Bypasses
				// MSBuildLocator auto-discovery entirely — intended for self-contained exe
				// scenarios where hostfxr cannot enumerate SDKs.
				//
				// Applies to sdk, vs, and auto: an explicit user-supplied path wins regardless of
				// mode. Adhoc returns above — it skips MSBuild entirely, and an override must not
				// re-enable it.
				var overridePath = ServerArgs.Current.MsBuildPath
				;

				if(!string.IsNullOrEmpty(overridePath)) {

					if(TryRegisterPath(overridePath)) {

						// No-op unless the override points at a VS MSBuild\...\Bin dir (SDK dirs return null).
						// A VS path is the more specific pin and wins; an SDK path leaves the BuildHost to
						// the version pin, if one is configured.
						var pinnedRoot = TryPinVisualStudioInstance(overridePath);

						discoveryMethod = $"resolved via --msbuild-path / ROSLYNMCP_MSBUILD_PATH ({overridePath})"
							+ (pinnedRoot is not null ? PinSuffix(pinnedRoot) : BuildHostPinSuffix(vsVersionRange));

						return null;
					}

					// Override was set but invalid — fall through to this mode's normal discovery.
				}

				// VS mode: skip SDK, go straight to vswhere.
				if(mode == WorkspaceMode.Vs) {
					
					if(!OperatingSystem.IsWindows()) {
						
						failureReason = "VS workspace mode requires Windows (Visual Studio MSBuild).";
						discoveryMethod = "not found — " + failureReason;
						
						return failureReason;
					}
					
					var msbuildDir = TryVsWhere(vsVersionRange);
					
					if(msbuildDir is not null) {
						
						PrependToPath(msbuildDir);
						if(TryRegister()) {
							
							var pinnedRoot = TryPinVisualStudioInstance(msbuildDir);
							
							discoveryMethod = $"resolved via vswhere — VS mode ({msbuildDir})"
								+ VersionPinNote(vsVersionRange)
								+ PinSuffix(pinnedRoot);
							
							return null;
						}
					}
					
					// A pinned range that matches nothing is a configuration problem, not a missing
					// install — say so, rather than pointing the user at the installer.
					failureReason = vsVersionRange is null
						? "Visual Studio MSBuild not found. Install Visual Studio or Build Tools, "
							+ "or pass --msbuild-path (or set ROSLYNMCP_MSBUILD_PATH) to a VS MSBuild\\Current\\Bin directory."
						: $"No Visual Studio MSBuild matches the version pin {vsVersionRange} (--vs-version, "
							+ $"ROSLYNMCP_VS_VERSION, or vsVersion in {ProjectConfig.FileName}). Install a matching "
							+ "Visual Studio or Build Tools, widen the pin, or pass --msbuild-path (or set "
							+ "ROSLYNMCP_MSBUILD_PATH) to a VS MSBuild\\Current\\Bin directory."
					;
					discoveryMethod = "not found — " + failureReason;
					
					return failureReason;
				}
				
				// SDK mode (or Auto): standard discovery chain. Every success path also applies the
				// BuildHost version pin, if one is configured — a legacy project's .NET Framework
				// BuildHost never consults the SDK registered here and would otherwise pick the
				// newest Visual Studio on its own.

				// 1. Try MSBuildLocator directly — works when .NET SDK is on PATH.
				if(TryRegister()) {
					
					discoveryMethod = "resolved via PATH (.NET SDK found on PATH)" + BuildHostPinSuffix(vsVersionRange);
					
					return null;
				}
				
				// 2. dotnet not on PATH — discover via env vars, registry, well-known paths.
				if(TryDiscoverDotnet(out var dotnetDir, out var source)) {
					
					PrependToPath(dotnetDir);
					
					if(TryRegister()) {
						
						discoveryMethod = source + BuildHostPinSuffix(vsVersionRange);
						
						return null;
					}
					
					// RegisterDefaults() can fail from self-contained executables — the bundled hostfxr
					// cannot enumerate system-installed SDKs. Fall back to direct SDK enumeration,
					// which calls RegisterMSBuildPath() and bypasses hostfxr entirely.
					if(TryRegisterFromDotnetSDK(dotnetDir, out var sdkPath)) {
						
						discoveryMethod = $"{source} (direct SDK path: {sdkPath})" + BuildHostPinSuffix(vsVersionRange);
						
						return null;
					}
				}
				
				// 3. Windows only: try vswhere.exe to find Visual Studio MSBuild.
				if(OperatingSystem.IsWindows()) {
					
					var msbuildDir = TryVsWhere(vsVersionRange);
					
					if(msbuildDir is not null) {
						
						PrependToPath(msbuildDir);
						
						if(TryRegister()) {
							
							var pinnedRoot = TryPinVisualStudioInstance(msbuildDir);
							
							discoveryMethod = $"resolved via vswhere ({msbuildDir})"
								+ VersionPinNote(vsVersionRange)
								+ PinSuffix(pinnedRoot);
							
							return null;
						}
					}
				}
				
				failureReason = OperatingSystem.IsWindows()
					? "MSBuild not found. Install .NET SDK or Visual Studio Build Tools. "
						+ "If installed in a non-standard location, pass --msbuild-path (or set "
						+ "ROSLYNMCP_MSBUILD_PATH or DOTNET_ROOT)."
					: "MSBuild not found. Install the .NET SDK (https://dot.net). "
						+ "If installed in a non-standard location, pass --msbuild-path (or set "
						+ "ROSLYNMCP_MSBUILD_PATH or DOTNET_ROOT)."
				;
				
				discoveryMethod = "not found — " + failureReason;
			}
			catch(Exception ex) when (failureReason is null) {
				
				// Unexpected exception before any known failure was recorded. Without this catch,
				// the finally block would set completed=true with failureReason=null, which callers
				// interpret as successful initialization.
				failureReason   = $"MSBuild initialization failed unexpectedly: {ex.GetType().Name}: {ex.Message}"
				;
				discoveryMethod = "not found — unexpected error";
			}
			
			return failureReason;
		}
		finally {
			
			completed = true;
			gate.Release();
		}
	}

	static string? ReportVsVersionConflict(WorkspaceMode requestedMode, string? requestedVsVersionRange)
	{
		if(requestedMode == WorkspaceMode.Adhoc
			|| string.Equals(requestedVsVersionRange, resolvedVsVersionRange, StringComparison.Ordinal))
			
			return null;

		return "MSBuild is already initialized for this server process with "
			+ $"{DescribeVsVersionRange(resolvedVsVersionRange)}, so the later request for "
			+ $"{DescribeVsVersionRange(requestedVsVersionRange)} from a different project cannot apply. "
			+ $"Restart the server, set --vs-version / ROSLYNMCP_VS_VERSION for the whole process, or keep {ProjectConfig.FileName} "
			+ "vsVersion consistent across projects loaded by this server."
		;
	}

	static string DescribeVsVersionRange(string? vsVersionRange) =>
		vsVersionRange is null ? "no Visual Studio version pin" : $"Visual Studio version pin {vsVersionRange}";
	
	/// <summary>
	///     Discovers the dotnet SDK directory via env vars, registry, or
	///     well-known platform-specific install locations.
	///     Returns (directory, source description) or (null, null).
	/// </summary>
	static bool TryDiscoverDotnet(out string dotnetDir, out string source)
	{
		// DOTNET_ROOT is the official cross-platform override.
		var dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT")
		;
		
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
		var hostPath = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH")
		;
		
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
		var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
		;
		
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
	///     Registers MSBuild from a specific directory path — a dotnet SDK directory or a
	///     Visual Studio <c>MSBuild\Current\Bin</c> directory — bypassing
	///     MSBuildLocator.RegisterDefaults() and the hostfxr P/Invoke it relies on.
	///     Use when running as a self-contained executable, where the bundled hostfxr
	///     cannot enumerate system-installed SDKs.
	/// </summary>
	static bool TryRegisterPath(string msbuildDir)
	{
		try {
			
			if(!MSBuildLocator.CanRegister)
				
				return false;
			
			// A dotnet SDK directory ships MSBuild.dll; a VS install's MSBuild\Current\Bin ships
			// only MSBuild.exe. Accept either — RegisterMSBuildPath itself validates the rest
			// (it looks for the Microsoft.Build.* assemblies, present in both layouts).
			if(!File.Exists(Path.Combine(msbuildDir, "MSBuild.dll")) &&
			   !File.Exists(Path.Combine(msbuildDir, "MSBuild.exe")))

				return false;
			
			MSBuildLocator.RegisterMSBuildPath(msbuildDir);
			
			return true;
		}
		catch {
			return false;
		}
	}
	
	/// <summary>
	///     Enumerates dotnet SDK directories under <paramref name="dotnetDir"/>, selects the
	///     latest version whose major version does not exceed the current runtime version,
	///     and registers it via <see cref="TryRegisterPath"/>. This mirrors MSBuildLocator's
	///     <c>allowQueryAllRuntimeVersions=false</c> policy without invoking hostfxr.
	/// </summary>
	static bool TryRegisterFromDotnetSDK(string dotnetDir, out string sdkPath)
	{
		sdkPath = "";
		
		try {
			
			var sdkBaseDir = Path.Combine(dotnetDir, "sdk")
			;
			
			if(!Directory.Exists(sdkBaseDir))
				
				return false;
			
			var currentMajor = Environment.Version.Major
			;
			
			Version? best    = null;
			string?  bestDir = null;
			
			foreach(var dir in Directory.EnumerateDirectories(sdkBaseDir)) {
				
				var candidate = TryParseVersionPrefix(Path.GetFileName(dir))
				;
				
				if(candidate is null || candidate.Major > currentMajor)
					
					continue;
				
				if(!File.Exists(Path.Combine(dir, "MSBuild.dll")))
					
					continue;
				
				if(best is null || candidate > best) {
					
					best    = candidate;
					bestDir = dir;
				}
			}
			
			if(bestDir is null)
				
				return false;
			
			if(!TryRegisterPath(bestDir))
				
				return false;
			
			sdkPath = bestDir;
			
			return true;
		}
		catch {
			return false;
		}
	}
	
	/// <summary>
	///     Parses the version prefix from an SDK directory name, stripping pre-release
	///     suffixes (e.g. "10.0.100-rc.2.25502.107" becomes <c>10.0.100</c>). Returns
	///     <see langword="null"/> when the name cannot be parsed as a <see cref="Version"/>.
	/// </summary>
	static Version? TryParseVersionPrefix(string dirName)
	{
		var dash = dirName.IndexOf('-')
		;
		var versionStr = dash >= 0 ? dirName[..dash] : dirName
		;
		
		return Version.TryParse(versionStr, out var v) ? v : null;
	}
	
	/// <summary>
	///     Runs vswhere.exe to find the latest Visual Studio installation with MSBuild — within
	///     <paramref name="versionRange"/> (vswhere <c>-version</c> syntax) when one is given.
	///     Windows-only — returns null on Linux/macOS (vswhere doesn't exist; .NET SDK
	///     installs are discovered by MSBuildLocator.RegisterDefaults directly).
	/// </summary>
	static string? TryVsWhere(string? versionRange = null)
	{
		// vswhere.exe lives at a well-known path under the VS Installer.
		// No registry key for vswhere itself — the installer path is predictable.
		var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
		;
		var vsWherePath  = Path.Combine(programFiles, "Microsoft Visual Studio", "Installer", "vswhere.exe");
		
		if(!File.Exists(vsWherePath))
			
			return null;
		
		try {
			
			// -version narrows -latest to the pinned range. Without it "latest" is the newest install
			// on the machine — the same pick the BuildHost makes on its own, which defeats the pin
			// when that newest install is the one that crashes. The range is validated by
			// VsVersionPin (digits, dots, brackets, comma only), so splicing it verbatim is safe.
			var versionFilter = versionRange is not null ? $" -version \"{versionRange}\"" : ""
			;
			
			var psi = new ProcessStartInfo(vsWherePath) {
				// -products * is required to discover standalone Build Tools installs (not just IDE editions).
				Arguments              = "-latest -products * -requires Microsoft.Component.MSBuild -find MSBuild\\**\\Bin\\MSBuild.exe" + versionFilter,
				RedirectStandardOutput = true,
				RedirectStandardError  = true,
				UseShellExecute        = false,
				CreateNoWindow         = true
			};
			
			using var process = Process.Start(psi);
			
			if(process is null)
				
				return null;
			
			// Read both streams concurrently on thread pool — sequential reads deadlock if either
			// pipe buffer fills. Both tasks unblock when the process exits (pipes close).
			var stdoutTask = Task.Run(() => { try { return process.StandardOutput.ReadToEnd(); } catch { return ""; } })
			;
			Task.Run(() => { try { process.StandardError.ReadToEnd(); } catch { } });
			
			// Enforce a hard timeout. WaitForExit(ms) returns false if the process hasn't exited,
			// so ExitCode is only valid after a true return.
			var exited = process.WaitForExit(5_000)
			;
			
			if(!exited) {
				
				// Kill the child so it doesn't keep running after we return. Killing closes the
				// pipes, which unblocks the stdoutTask thread pool task.
				try { process.Kill(entireProcessTree: true); } catch { }
				
				return null;
			}
			
			if(process.ExitCode != 0)
				
				return null;
			
			// After a normal exit the pipes are closed; stdoutTask should drain near-instantly.
			var output = stdoutTask.Wait(1_000) ? stdoutTask.Result.Trim() : ""
			;
			
			if(string.IsNullOrEmpty(output))
				
				return null;
			
			// vswhere returns the full path to MSBuild.exe — we need its directory.
			var firstLine = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim()
			;
			
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
	
	/// <summary>
	///     Pins the out-of-process Roslyn BuildHost to a specific Visual Studio MSBuild instance.
	///     The .NET Framework BuildHost runs its own discovery and picks the highest-versioned VS
	///     install (prerelease included), ignoring the path this process resolved — so on a machine
	///     with a newer/preview VS it can load incompatible MSBuild assemblies that crash
	///     (TypeInitializationException in Microsoft.Build.Shared.XMakeElements). The BuildHost is a
	///     child process that inherits this process's environment, and MSBuildLocator synthesizes a
	///     "Developer Console" instance from VSINSTALLDIR + VSCMD_VER. Setting those to the VS root
	///     derived from <paramref name="msbuildBinDir"/> — with a deliberately high synthetic version
	///     so it outranks the BuildHost's OrderByDescending(Version) pick — forces the child onto the
	///     instance this process already chose. No supported API pins the BuildHost's MSBuild path;
	///     this is the only mechanism (MSBUILD_EXE_PATH is stripped from the child, and PATH is not
	///     consulted for VS instance selection).
	///     Returns the pinned VS root, or null when pinning is disabled, the path is not a VS bin
	///     layout (e.g. a dotnet SDK directory), or the root cannot be derived.
	/// </summary>
	static string? TryPinVisualStudioInstance(string msbuildBinDir)
	{
		if(ServerArgs.Current.NoBuildHostPin)
			
			return null;
		
		// Expect a VS layout: <root>\MSBuild\<Current|x.y>\Bin. Walk three levels up to the root
		// and confirm the "MSBuild" segment so this never fires for a dotnet SDK directory.
		var bin        = msbuildBinDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		var versionDir = Path.GetDirectoryName(bin);        // ...\MSBuild\Current
		var msbuildDir = Path.GetDirectoryName(versionDir); // ...\MSBuild
		var vsRoot     = Path.GetDirectoryName(msbuildDir); // ...\<root>
		
		if(vsRoot is null || msbuildDir is null ||
		   !string.Equals(Path.GetFileName(msbuildDir), "MSBuild", StringComparison.OrdinalIgnoreCase))
			
			return null;
		
		// VSINSTALLDIR is the VS root — MSBuildLocator appends MSBuild\Current\Bin itself. The
		// synthetic version needs major >= 16 (for the Current\Bin layout) and must outrank any
		// real install, including an 18.x preview — 9999.0 satisfies both.
		Environment.SetEnvironmentVariable("VSINSTALLDIR", vsRoot + Path.DirectorySeparatorChar);
		Environment.SetEnvironmentVariable("VSCMD_VER",    "9999.0");
		
		return vsRoot;
	}
	
	/// <summary>
	///     Applies the Visual Studio version pin on an SDK-resolution path and returns the
	///     discovery-method suffix describing the outcome. This process keeps the SDK MSBuild it
	///     registered — only the out-of-process .NET Framework BuildHost, which legacy projects load
	///     through, is steered. Empty when no pin is configured or not on Windows; the "unpinned"
	///     note when nothing matches the range, since an SDK-mode load may never need Visual Studio.
	/// </summary>
	static string BuildHostPinSuffix(string? vsVersionRange)
	{
		if(vsVersionRange is null || !OperatingSystem.IsWindows())
			
			return "";
		
		var msbuildDir = TryVsWhere(vsVersionRange);
		
		if(msbuildDir is null)
			
			return $" (VS version pin {vsVersionRange}: no matching Visual Studio — BuildHost unpinned)";
		
		var pinnedRoot = TryPinVisualStudioInstance(msbuildDir);
		
		return pinnedRoot is null
			? $" (VS version pin {vsVersionRange}: BuildHost pinning disabled)"
			: VersionPinNote(vsVersionRange) + PinSuffix(pinnedRoot)
		;
	}
	
	/// <summary>Formats the discovery-method note naming an active version pin, or empty when none.</summary>
	static string VersionPinNote(string? vsVersionRange) =>
		vsVersionRange is not null ? $" (VS version pin {vsVersionRange})" : ""
	;
	

	/// <summary>Formats the discovery-method suffix noting a BuildHost pin, or empty when none.</summary>
	static string PinSuffix(string? pinnedRoot) =>
		pinnedRoot is not null ? $" (BuildHost pinned to {pinnedRoot})" : ""
	;
	
	

	static void PrependToPath(string directory)
	{
		var current = Environment.GetEnvironmentVariable("PATH") ?? "";
		Environment.SetEnvironmentVariable("PATH", directory + Path.PathSeparator + current);
	}
}
