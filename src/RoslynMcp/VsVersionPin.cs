using System.Text.RegularExpressions;

namespace RoslynMcp;

/// <summary>
///     Normalizes a Visual Studio version pin — <c>--vs-version</c>, <c>ROSLYNMCP_VS_VERSION</c>, or a
///     project file's <c>vsVersion</c> — into the range syntax vswhere accepts for <c>-version</c>.
///     Shared by <see cref="ServerArgs"/>, <see cref="ProjectConfig"/>, and the setup wizard so all
///     three agree on what counts as a valid pin.
/// </summary>
/// <remarks>
///     A version pin exists because the newest installed Visual Studio is not always the one a
///     legacy project loads under. The .NET Framework BuildHost picks the highest-versioned VS it can
///     find, and a preview or freshly released major (e.g. 18.x) can crash it on load. Pinning by
///     version — rather than by an install path — keeps the setting portable across machines, which
///     is what makes it safe to honor from a committed project file.
/// </remarks>
internal static partial class VsVersionPin
{
	// A vswhere range: [ or ( , optional version, comma, optional version, ] or ). The version
	// parts are restricted to digits and dots so the value can be passed to vswhere verbatim.
	[GeneratedRegex(@"^[\[(]\s*(\d+(\.\d+){0,3})?\s*,\s*(\d+(\.\d+){0,3})?\s*[\])]$")]
	private static partial Regex RangeSyntax();
	
	/// <summary>
	///     Converts a user-facing pin to a vswhere <c>-version</c> range. Accepts a major
	///     (<c>17</c> → <c>[17.0,18.0)</c>), a major.minor (<c>17.14</c> → <c>[17.14,17.15)</c>),
	///     a three- or four-part exact version (<c>17.14.37516.0</c> → <c>[17.14.37516.0,17.14.37516.0]</c>),
	///     or a range already in vswhere syntax (<c>[17.0,18.0)</c>), which passes through trimmed.
	///     Returns false — with <paramref name="range"/> empty — for null, blank, or anything else.
	/// </summary>
	public static bool TryNormalize(string? value, out string range)
	{
		range = "";
		
		if(string.IsNullOrWhiteSpace(value))
			
			return false;
		
		var trimmed = value.Trim();
		
		if(RangeSyntax().IsMatch(trimmed)) {
			
			// Whitespace inside a range ("[17.0, 18.0)") reads fine but travels badly as a quoted
			// process argument — drop it so vswhere always sees the canonical form.
			range = string.Concat(trimmed.Split(default(char[]), StringSplitOptions.RemoveEmptyEntries));
			
			return true;
		}
		
		var parts = trimmed.Split('.');
		
		if(parts.Length > 4)
			
			return false;
		
		var numbers = new int[parts.Length];
		
		for(var i = 0; i < parts.Length; i++) {
			
			if(!int.TryParse(parts[i], out numbers[i]) || numbers[i] < 0)
				
				return false;
		}
		
		if((parts.Length == 1 && numbers[0] == int.MaxValue)
			|| (parts.Length == 2 && numbers[1] == int.MaxValue))
			
			return false;
		
		range = parts.Length switch {
			
			1 => $"[{numbers[0]}.0,{numbers[0] + 1}.0)",
			2 => $"[{numbers[0]}.{numbers[1]},{numbers[0]}.{numbers[1] + 1})",
			_ => $"[{trimmed},{trimmed}]",
		};
		
		return true;
	}
}
