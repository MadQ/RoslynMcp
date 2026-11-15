using System.Collections.Frozen;
using System.Xml;
using Microsoft.CodeAnalysis;

namespace RoslynMcp.Tools;

/// <summary>
///     What a text edit is checked for before it is written, by the kind of file: the syntax of a
///     C# file (#476), and the well-formedness of the XML files MSBuild evaluates a project from
///     (#245). Both ask the same question, whether the edit leaves the file worse than it found it,
///     and both are answered the same way by the tools: the edit is refused, quoting the parser
///     with a line, unless the call allows it.
/// </summary>
internal static class EditChecks
{
	// The evaluation inputs that are XML. One that is malformed does not fail the edit that broke
	// it: it fails the next workspace load, which answers with load problems far from the cause.
	static readonly FrozenSet<string> XmlExtensions = new[] {
		".csproj", ".props", ".targets", ".slnx", ".ruleset", ".resx",
	}.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
	
	static readonly FrozenSet<string> XmlNames = new[] {
		"nuget.config", "packages.config",
	}.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
	
	/// <summary>Whether <paramref name="path"/> is an XML file a project is evaluated from.</summary>
	public static bool IsProjectXml(string path)
		=> XmlExtensions.Contains(Path.GetExtension(path)) || XmlNames.Contains(Path.GetFileName(path))
	;
	
	/// <summary>
	///     The errors <paramref name="changed"/> has that <paramref name="original"/> did not, each
	///     as <c>line N: message</c> with the line of the new text; empty for a file of a kind that
	///     is not checked. <paramref name="before"/> is the syntax tree of a C# file as it was, and
	///     null for any other file.
	/// </summary>
	public static string[] AddedErrors(string path, SyntaxTree? before, string original, string changed)
		=> before is not null   ? ReplaceInFileTool.AddedSyntaxErrors(before, changed)
			: IsProjectXml(path) ? AddedXmlErrors(original, changed)
			: []
	;
	
	// A file that was malformed before stays editable, as a C# file with errors does: the tool
	// that could repair it must not refuse to. XML has no second error to count; the parser stops
	// at the first.
	static string[] AddedXmlErrors(string original, string changed)
		=> XmlError(original) is null && XmlError(changed) is { } error ? [error] : []
	;
	
	// Well-formedness only: read to the end and see whether the reader objects. Nothing is
	// resolved or fetched, and a DTD is skipped, not followed.
	static string? XmlError(string text)
	{
		var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null };
		
		try {
			
			using var reader = XmlReader.Create(new StringReader(text), settings);
			
			while(reader.Read()) { }
			
			return null;
		}
		catch(XmlException ex) {
			return $"line {ex.LineNumber}: {ex.Message}";
		}
	}
}
