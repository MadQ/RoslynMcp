using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RoslynMcp;

/// <summary>
///     Shared <see cref="JsonSerializerOptions"/> instances used throughout RoslynMcp.
///     All options use <see cref="JavaScriptEncoder.UnsafeRelaxedJsonEscaping"/> so that
///     printable Unicode (em dashes, quotes, angle brackets, etc.) is never needlessly
///     escaped to <c>\uXXXX</c> sequences — output is readable as-is in any text viewer.
/// </summary>
internal static class RoslynMcpJson
{
	/// <summary>
	///     For <see cref="FileLogger"/> — NDJSON log lines. Omits null properties; no indent.
	/// </summary>
	internal static readonly JsonSerializerOptions Log = new() {
		
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
		Encoder                = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
	};
	
	/// <summary>
	///     For compact single-line serialization: tool args, response peeks, diagnostic
	///     previews. No indent; includes null properties for faithful value representation.
	/// </summary>
	internal static readonly JsonSerializerOptions Compact = new() {
		Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
	};
	
	/// <summary>
	///     For session note injection — matches the SDK's WithToolsFromAssembly serializer
	///     options: camelCase property names and relaxed Unicode encoding.
	/// </summary>
	internal static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web) {
		Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
	};
	
	/// <summary>
	///     For <see cref="BackupStore"/> meta.json files — indented, camelCase, omits nulls.
	/// </summary>
	internal static readonly JsonSerializerOptions Backup = new() {
		
		WriteIndented          = true,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
		PropertyNamingPolicy   = JsonNamingPolicy.CamelCase,
		Encoder                = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
	};
}
