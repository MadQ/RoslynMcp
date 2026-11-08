using System.Text.Json;

namespace RoslynMcp;

/// <summary>
///     The user's own server configuration: <c>config.json</c> in the server's per-user data directory
///     (<c>%LOCALAPPDATA%\RoslynMcp</c>, beside the logs and backups), for settings wanted in every
///     workspace. <c>ROSLYNMCP_USER_CONFIG</c> names another file, which the harness uses so a
///     developer's own file never reaches a test server. Read once per process, like
///     <see cref="ProjectConfig"/>: editing it takes a restart. A missing file is <see cref="Empty"/>.
///     It is the user's own file, so unlike the committed project file it may name assembly paths
///     without a trust gate. Only the <c>codeActions</c> section is read today.
/// </summary>
internal sealed class UserConfig
{
	public static string FilePath { get; } = ServerArgs.Current.UserConfigPath
		?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RoslynMcp", "config.json");
	
	public static readonly UserConfig Empty = new(CodeActionConfig.Empty, null);
	
	static UserConfig? loaded;
	
	public CodeActionConfig CodeActions { get; }
	
	/// <summary>The file this was read from; null for <see cref="Empty"/>.</summary>
	public string? SourcePath { get; }
	
	UserConfig(CodeActionConfig codeActions, string? sourcePath)
	{
		CodeActions = codeActions;
		SourcePath  = sourcePath;
	}
	
	public static UserConfig Load(FileLogger logger)
	{
		if(loaded is { } current)
			return current;
		
		var config = Parse(logger);
		
		return Interlocked.CompareExchange(ref loaded, config, null) ?? config;
	}
	
	// Never throws: any IO or parse failure degrades to Empty, as ProjectConfig does.
	static UserConfig Parse(FileLogger logger)
	{
		try {
			
			if(!File.Exists(FilePath))
				return Empty;
			
			using var doc = JsonDocument.Parse(FileReader.ReadAllText(FilePath, FileReader.ConfigLimit), new JsonDocumentOptions {
				
				AllowTrailingCommas = true,
				CommentHandling     = JsonCommentHandling.Skip,
			});
			
			if(doc.RootElement.ValueKind != JsonValueKind.Object) {
				
				logger.LogInfo("UserConfig", $"WARN: '{FilePath}' root is not a JSON object — ignored");
				
				return Empty;
			}
			
			var codeActions = doc.RootElement.TryGetProperty(CodeActionConfig.SectionName, out var section)
				? CodeActionConfig.Parse(section, FilePath, committed: false, logger)
				: CodeActionConfig.Empty
			;
			
			logger.LogInfo("UserConfig", $"loaded '{FilePath}': codeActions enabled={codeActions.Enabled?.ToString() ?? "unset"}, providers={codeActions.Providers.Length}, deny={codeActions.Deny.Length}");
			
			return new UserConfig(codeActions, FilePath);
		}
		catch(Exception ex) {
			
			logger.LogInfo("UserConfig", $"WARN: failed to read '{FilePath}': {ex.Message} — ignored");
			
			return Empty;
		}
	}
}
