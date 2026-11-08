using System.Collections.Immutable;
using System.Text.Json;

namespace RoslynMcp;

/// <summary>
///     The <c>codeActions</c> section of a config file, as written: every switch is tri-state so the
///     layers can be merged (<see cref="CodeActionSettings.Resolve"/>). Parsed by <see cref="ProjectConfig"/>
///     from the committed project file and by <see cref="UserConfig"/> from the user's own.
/// </summary>
internal sealed record CodeActionConfig(
	bool?                              Enabled,
	bool?                              BuiltIn,
	bool?                              ProjectProviders,
	bool?                              TrustProjectProviders,
	ImmutableArray<ConfiguredProvider> Providers,
	ImmutableArray<string>             Deny)
{
	public const string SectionName = "codeActions";
	
	public static readonly CodeActionConfig Empty = new(null, null, null, null, [], []);
	
	/// <summary>
	///     Parses one section. Unknown keys are ignored and invalid values dropped with a logged
	///     warning, the same tolerance as the rest of the file. <paramref name="committed"/> is true
	///     for the project file, whose <c>trustProjectProviders</c> is never honoured: a checkout
	///     cannot grant itself the right to run the assemblies it names.
	/// </summary>
	public static CodeActionConfig Parse(JsonElement section, string filePath, bool committed, FileLogger logger)
	{
		if(section.ValueKind != JsonValueKind.Object) {
			
			logger.LogInfo("Config", $"WARN: '{SectionName}' in '{filePath}' must be an object — ignored");
			
			return Empty;
		}
		
		bool? enabled          = null;
		bool? builtIn          = null;
		bool? projectProviders = null;
		bool? trust            = null;
		var   providers        = ImmutableArray.CreateBuilder<ConfiguredProvider>();
		var   deny             = ImmutableArray.CreateBuilder<string>();
		var   directory        = Path.GetDirectoryName(filePath) ?? ".";
		
		foreach(var property in section.EnumerateObject()) {
			
			switch(property.Name) {
				
				case "enabled":
					enabled = Bool(property, filePath, logger);
					break;
				
				case "builtIn":
					builtIn = Bool(property, filePath, logger);
					break;
				
				case "projectProviders":
					projectProviders = Bool(property, filePath, logger);
					break;
				
				case "trustProjectProviders":
					
					if(committed)
						logger.LogInfo("Config", $"WARN: 'trustProjectProviders' in '{filePath}' is a per-user setting ('{UserConfig.FilePath}' or ROSLYNMCP_TRUST_PROJECT_PROVIDERS) — ignored in a committed file");
					else
						trust = Bool(property, filePath, logger);
					
					break;
				
				case "providers":
					
					if(property.Value.ValueKind != JsonValueKind.Array) {
						
						logger.LogInfo("Config", $"WARN: 'providers' in '{filePath}' must be an array — ignored");
						
						break;
					}
					
					foreach(var item in property.Value.EnumerateArray())
						if(ParseProvider(item, filePath, directory, committed, logger) is { } provider)
							providers.Add(provider);
					
					break;
				
				case "deny":
					
					if(property.Value.ValueKind != JsonValueKind.Array) {
						
						logger.LogInfo("Config", $"WARN: 'deny' in '{filePath}' must be an array of provider names — ignored");
						
						break;
					}
					
					foreach(var item in property.Value.EnumerateArray())
						if(item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } name)
							deny.Add(name.Trim());
					
					break;
				
				// Unknown keys: forward-tolerant, silently ignored.
			}
		}
		
		return new CodeActionConfig(enabled, builtIn, projectProviders, trust, providers.ToImmutable(), deny.ToImmutable());
	}
	
	static bool? Bool(JsonProperty property, string filePath, FileLogger logger)
	{
		if(property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
			return property.Value.GetBoolean();
		
		logger.LogInfo("Config", $"WARN: '{property.Name}' in '{filePath}' must be true or false — ignored");
		
		return null;
	}
	
	// One entry: a string is an assembly path; an object names exactly one of "path" or "package"
	// (with an optional "version"). A relative path is relative to the file declaring it.
	static ConfiguredProvider? ParseProvider(JsonElement item, string filePath, string directory, bool committed, FileLogger logger)
	{
		string? path    = null;
		string? package = null;
		string? version = null;
		
		if(item.ValueKind == JsonValueKind.String)
			path = item.GetString();
		
		else if(item.ValueKind == JsonValueKind.Object) {
			
			path    = Text(item, "path");
			package = Text(item, "package");
			version = Text(item, "version");
		}
		
		if(string.IsNullOrWhiteSpace(path) == string.IsNullOrWhiteSpace(package)) {
			
			logger.LogInfo("Config", $"WARN: a 'providers' entry in '{filePath}' must name exactly one of 'path' or 'package' — ignored");
			
			return null;
		}
		
		if(!string.IsNullOrWhiteSpace(path))
			return new ConfiguredProvider(Path.GetFullPath(Path.Combine(directory, path.Trim())), null, null, filePath, committed);
		
		// A package id is a bare name: anything that looks like a path is not one.
		if(package!.IndexOfAny(['/', '\\', ':']) >= 0 || package.Contains("..", StringComparison.Ordinal)) {
			
			logger.LogInfo("Config", $"WARN: 'package' '{package}' in '{filePath}' is not a NuGet package id — ignored");
			
			return null;
		}
		
		return new ConfiguredProvider(null, package.Trim(), string.IsNullOrWhiteSpace(version) ? null : version.Trim(), filePath, committed);
	}
	
	static string? Text(JsonElement element, string name)
		=> element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

/// <summary>
///     One configured provider source: an assembly on disk, or a NuGet package to find in the local
///     package cache. <see cref="Committed"/> says it came from the project file, whose paths run only
///     once the user trusts them (<see cref="CodeActionSettings.TrustProjectProviders"/>).
/// </summary>
internal sealed record ConfiguredProvider(string? Path, string? Package, string? Version, string DeclaredIn, bool Committed)
{
	public string Describe()
		=> Path ?? (Version is null ? Package! : $"{Package} {Version}");
}

/// <summary>
///     The effective code-action settings for a request. The switches follow the server's precedence,
///     CLI arg &gt; env var &gt; project file &gt; user file &gt; default; the provider and deny lists of
///     the two files are merged, a user's providers plus a team's, unlike <c>ignore</c>, where the winning
///     source replaces. Trust for the project file's assembly paths comes from the user side only.
/// </summary>
internal sealed record CodeActionSettings(
	bool                               Enabled,
	bool                               BuiltIn,
	bool                               ProjectProviders,
	bool                               TrustProjectProviders,
	ImmutableArray<ConfiguredProvider> Providers,
	ImmutableArray<string>             Deny)
{
	public static CodeActionSettings Resolve(string pathInProject, FileLogger logger)
	{
		var args    = ServerArgs.Current;
		var user    = UserConfig.Load(logger).CodeActions;
		var project = ProjectConfig.ForPath(pathInProject, logger).CodeActions;
		
		var enabled          = args.CodeActionsSpecified ? args.CodeActions : project.Enabled ?? user.Enabled ?? true;
		var builtIn          = project.BuiltIn ?? user.BuiltIn ?? true;
		var projectProviders = project.ProjectProviders ?? user.ProjectProviders ?? true;
		var trust            = args.TrustProjectProviders || user.TrustProjectProviders == true;
		
		return new CodeActionSettings(
			enabled,
			builtIn,
			projectProviders,
			trust,
			[..user.Providers, ..project.Providers],
			[..user.Deny.Concat(project.Deny).Distinct(StringComparer.OrdinalIgnoreCase)]);
	}
}
