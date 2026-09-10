using System.Text.Json;

namespace RoslynMcp;

/// <summary>
///     Project-local server configuration loaded from a committed <c>.madq_roslynmcp.json</c>
///     file at (or above) the directory a request resolves to. Fills in settings where neither
///     a CLI arg nor an env var was given — precedence: CLI arg &gt; env var &gt; project file
///     &gt; built-in default.
/// </summary>
/// <remarks>
///     <para>
///         Unlike <see cref="ServerArgs"/> (parsed once at startup), the project file can only
///         be found after a request supplies a project path — MCP clients rarely start the
///         server in the project directory, so startup-CWD discovery would miss most real
///         deployments. Discovery therefore runs lazily at the consuming call sites and is
///         cached per start directory for the process lifetime: editing the file requires a
///         server restart to take effect.
///     </para>
///     <para>
///         Security: this is a committed, potentially untrusted repo file. Only the
///         commit-worthy keys <c>elicit</c>, <c>workspace</c>, and <c>vsVersion</c> are honored.
///         Machine-specific settings (log, MSBuild, and backup paths) and <c>preload</c> are never
///         read from it, so a hostile repo cannot point the server at arbitrary local paths. A
///         Visual Studio <em>version</em> is the exception that proves the rule: it selects among
///         installs vswhere already knows about and never names a path, so it is portable and safe.
///     </para>
/// </remarks>
internal sealed class ProjectConfig
{
	/// <summary>File name written by <c>setup-project</c> and discovered by the server.</summary>
	public const string FileName = ".madq_roslynmcp.json";
	
	// Committed config files should be tiny; anything bigger is suspect and skipped.
	const long maxFileSizeBytes = 64 * 1024;
	
	// Entries evicted above this to prevent unbounded growth in long-running server sessions.
	const int cacheMaxSize = 500;
	
	// Parsed configs keyed by the normalized directory the walk started from.
	static readonly Dictionary<string, ProjectConfig> cache = new(StringComparer.OrdinalIgnoreCase);
	
	// Insertion order for approximate oldest-first eviction — avoids the full-cache
	// thrash a Clear() at the cap would cause in long-running sessions.
	static readonly Queue<string> cacheOrder = new();
	
	// Static — guards the static cache across all consumers, same pattern as
	// RoslynMcpTool.pathCacheLock.
#if NET9_0_OR_GREATER
	static readonly Lock   cacheLock = new();
#else
	static readonly object cacheLock = new();
#endif
	
	/// <summary>Sentinel for "no usable project file found" — all <c>*Specified</c> flags false.</summary>
	public static readonly ProjectConfig Empty = new(false, false, false, WorkspaceMode.Auto, null, "");
	
	/// <summary>Whether the file supplied a valid boolean <c>elicit</c> value.</summary>
	public bool ElicitSpecified { get; }
	
	/// <summary>The file's <c>elicit</c> value; meaningful only when <see cref="ElicitSpecified"/> is true.</summary>
	public bool Elicit { get; }
	
	/// <summary>Whether the file supplied a valid <c>workspace</c> value (<c>sdk</c>/<c>vs</c>/<c>adhoc</c>).</summary>
	public bool WorkspaceSpecified { get; }
	
	/// <summary>The file's <c>workspace</c> mode; meaningful only when <see cref="WorkspaceSpecified"/> is true.</summary>
	public WorkspaceMode Workspace { get; }
	
	/// <summary>
	///     The file's <c>vsVersion</c> pin, normalized to a vswhere <c>-version</c> range (see
	///     <see cref="VsVersionPin"/>); <c>null</c> when absent or invalid.
	/// </summary>
	public string? VsVersion { get; }
	
	/// <summary>Full path of the file this config was loaded from; empty for <see cref="Empty"/>.</summary>
	public string SourcePath { get; }
	
	ProjectConfig(bool elicitSpecified, bool elicit, bool workspaceSpecified, WorkspaceMode workspace, string? vsVersion, string sourcePath)
	{
		ElicitSpecified    = elicitSpecified;
		Elicit             = elicit;
		WorkspaceSpecified = workspaceSpecified;
		Workspace          = workspace;
		VsVersion          = vsVersion;
		SourcePath         = sourcePath;
	}
	
	/// <summary>
	///     The effective elicitation setting for a request touching <paramref name="pathInProject"/>:
	///     an explicit CLI arg or env var (<see cref="ServerArgs.ElicitSpecified"/>) always wins;
	///     otherwise the project file's <c>elicit</c> value; otherwise false.
	/// </summary>
	public static bool EffectiveElicit(string pathInProject, FileLogger logger)
	{
		if(ServerArgs.Current.ElicitSpecified)
			
			return ServerArgs.Current.Elicit;
		
		var config = ForPath(pathInProject, logger);
		
		return config.ElicitSpecified && config.Elicit;
	}
	
	/// <summary>
	///     The effective workspace mode for a load of <paramref name="pathInProject"/>:
	///     an explicit CLI arg or env var (<see cref="ServerArgs.WorkspaceModeSpecified"/>) always
	///     wins; otherwise the project file's <c>workspace</c> value; otherwise
	///     <see cref="WorkspaceMode.Auto"/>. Note an explicit <c>--workspace auto</c> parses to
	///     Auto and therefore does not pin auto-detection over a project file's mode.
	/// </summary>
	public static WorkspaceMode EffectiveWorkspaceMode(string pathInProject, FileLogger logger)
	{
		if(ServerArgs.Current.WorkspaceModeSpecified)
			
			return ServerArgs.Current.WorkspaceMode;
		
		var config = ForPath(pathInProject, logger);
		
		return config.WorkspaceSpecified ? config.Workspace : WorkspaceMode.Auto;
	}
	
	/// <summary>
	///     The effective Visual Studio version pin for a load of <paramref name="pathInProject"/>,
	///     as a vswhere <c>-version</c> range: an explicit CLI arg or env var
	///     (<see cref="ServerArgs.VsVersionSpecified"/>) always wins; otherwise the project file's
	///     <c>vsVersion</c>; otherwise <c>null</c> (newest installed Visual Studio). Honored from
	///     the committed file because a version — unlike an MSBuild path — is portable across
	///     machines and cannot point the server at an arbitrary local directory.
	/// </summary>
	public static string? EffectiveVsVersion(string pathInProject, FileLogger logger)
	{
		if(ServerArgs.Current.VsVersionSpecified)
			
			return ServerArgs.Current.VsVersion;
		
		return ForPath(pathInProject, logger).VsVersion;
	}
	
	/// <summary>
	///     Loads (or returns the cached) config for the directory containing
	///     <paramref name="pathInProject"/> — the path itself when it is a directory. Walks up
	///     the directory tree until a <see cref="FileName"/> file or the filesystem root is hit.
	///     Never throws: any IO or parse failure degrades to <see cref="Empty"/>.
	/// </summary>
	public static ProjectConfig ForPath(string pathInProject, FileLogger logger)
	{
		try {
			
			var fullPath = Path.GetFullPath(pathInProject);
			
			var startDir = Directory.Exists(fullPath)
				? fullPath
				: Path.GetDirectoryName(fullPath);
			
			if(startDir is null)
			
				return Empty;
			
			// Normalize to avoid cache misses from trailing-separator variants.
			startDir = Path.TrimEndingDirectorySeparator(startDir);
			
			lock(cacheLock)
				if(cache.TryGetValue(startDir, out var cached))
					
					return cached;
			
			var config = LoadByWalkingUp(startDir, logger);
			
			lock(cacheLock) {
				
				// Another thread may have loaded the same dir while we walked; don't double-track
				// it in cacheOrder, which would desync the queue from the dictionary.
				if(!cache.ContainsKey(startDir)) {
					
					// Approximate oldest-first eviction: drop the oldest 20% when the cap is hit,
					// avoiding the periodic full-cache thrash a Clear() would cause.
					if(cache.Count >= cacheMaxSize) {
						
						var evictCount = Math.Max(1, cacheMaxSize / 5);
						
						for(int i = 0; i < evictCount && cacheOrder.Count > 0; i++)
							cache.Remove(cacheOrder.Dequeue());
					}
					
					cacheOrder.Enqueue(startDir);
				}
				
				cache[startDir] = config;
			}
			
			return config;
		}
		catch(Exception ex) {
			
			logger.LogInfo("ProjectConfig", $"WARN: lookup failed for '{pathInProject}': {ex.Message}");
			
			return Empty;
		}
	}
	
	// Walks up from startDir looking for FileName; same loop shape as RoslynMcpTool.HooksInstalled.
	static ProjectConfig LoadByWalkingUp(string startDir, FileLogger logger)
	{
		var dir = startDir;
		
		while(true) {
			
			var candidate = Path.Combine(dir, FileName);
			
			if(File.Exists(candidate))
				
				return Parse(candidate, logger);
			
			var parent = Directory.GetParent(dir);
			
			if(parent is null || parent.FullName == dir)
				
				return Empty;
			
			dir = parent.FullName;
		}
	}
	
	// Parses one config file with strict per-key validation. Unknown keys are ignored;
	// machine-specific key names get a logged warning to aid debugging. Any failure → Empty.
	static ProjectConfig Parse(string filePath, FileLogger logger)
	{
		try {
			
			var info = new FileInfo(filePath);
			
			// Reject symlinks and other reparse points — a hostile repo can commit a symlink
			// named .madq_roslynmcp.json pointing outside the repo, which would contradict
			// the security goal of never reading arbitrary local paths.
			if(info.Attributes.HasFlag(FileAttributes.ReparsePoint)) {
				
				logger.LogInfo("ProjectConfig", $"WARN: '{filePath}' is a symlink or reparse point — ignored");
				
				return Empty;
			}
			
			var fileSize = info.Length;
			
			if(fileSize > maxFileSizeBytes) {
				
				logger.LogInfo("ProjectConfig", $"WARN: '{filePath}' is {fileSize} bytes (limit {maxFileSizeBytes}) — ignored");
				
				return Empty;
			}
			
			using var doc = JsonDocument.Parse(File.ReadAllText(filePath), new JsonDocumentOptions {
				
				AllowTrailingCommas = true,
				CommentHandling     = JsonCommentHandling.Skip,
			});
			
			if(doc.RootElement.ValueKind != JsonValueKind.Object) {
				
				logger.LogInfo("ProjectConfig", $"WARN: '{filePath}' root is not a JSON object — ignored");
				
				return Empty;
			}
			
			var elicitSpecified    = false;
			var elicit             = false;
			var workspaceSpecified = false;
			var workspace          = WorkspaceMode.Auto;
			var vsVersion          = (string?) null;
			
			foreach(var property in doc.RootElement.EnumerateObject()) {
				
				switch(property.Name) {
					
					case "elicit":
						
						if(property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False) {
							
							elicitSpecified = true;
							elicit          = property.Value.GetBoolean();
						}
						else
							logger.LogInfo("ProjectConfig", $"WARN: 'elicit' in '{filePath}' must be true or false — ignored");
						
						break;
					
					case "workspace":
						
						var value = property.Value.ValueKind == JsonValueKind.String
							? property.Value.GetString()
							: null;
						
						switch(value?.ToLowerInvariant()) {
							
							case "sdk":   workspaceSpecified = true; workspace = WorkspaceMode.Sdk;   break;
							case "vs":    workspaceSpecified = true; workspace = WorkspaceMode.Vs;    break;
							case "adhoc": workspaceSpecified = true; workspace = WorkspaceMode.Adhoc; break;
							
							default:
								logger.LogInfo("ProjectConfig", $"WARN: 'workspace' in '{filePath}' must be sdk, vs, or adhoc — ignored");
								break;
						}
						
						break;
					
					case "vsVersion":
						
						// A bare number (17) is the natural JSON spelling of a major version — accept
						// it alongside the string forms. Normalized here so no consumer ever sees raw
						// user input; the range is passed to vswhere verbatim.
						var vsVersionText = property.Value.ValueKind switch {
							
							JsonValueKind.String => property.Value.GetString(),
							JsonValueKind.Number => property.Value.GetRawText(),
							_                    => null,
						};
						
						if(VsVersionPin.TryNormalize(vsVersionText, out var vsVersionRange))
							vsVersion = vsVersionRange;
						else
							logger.LogInfo("ProjectConfig", $"WARN: 'vsVersion' in '{filePath}' must be a major (17), major.minor (17.14), or vswhere range ([17.0,18.0)) — ignored");
						
						break;
					
					case "version":
						// Schema version — tolerated but not interpreted in v1.
						break;
					
					case "logPath":
					case "msbuildPath":
					case "backupPath":
					case "preload":
						// Machine-specific or startup-only settings are deliberately not honored
						// from a committed repo file — see the class remarks.
						logger.LogInfo("ProjectConfig", $"WARN: '{property.Name}' is not supported in {FileName} — ignored");
						break;
					
					// Unknown keys: forward-tolerant, silently ignored.
				}
			}
			
			var config = new ProjectConfig(elicitSpecified, elicit, workspaceSpecified, workspace, vsVersion, filePath);
			
			logger.LogInfo("ProjectConfig",
				$"loaded '{filePath}': elicit={(elicitSpecified ? elicit.ToString() : "unset")}, " +
				$"workspace={(workspaceSpecified ? workspace.ToString() : "unset")}, " +
				$"vsVersion={vsVersion ?? "unset"}");
			
			return config;
		}
		catch(Exception ex) {
			
			logger.LogInfo("ProjectConfig", $"WARN: failed to read '{filePath}': {ex.Message} — ignored");
			
			return Empty;
		}
	}
}
