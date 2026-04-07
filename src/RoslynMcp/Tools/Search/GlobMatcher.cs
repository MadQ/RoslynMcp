using System.Text;
using System.Text.RegularExpressions;

namespace RoslynMcp.Tools;

// Single * does not cross path-segment boundaries; ** does.
internal static class GlobMatcher
{
	public static bool Matches(string fileName, string pattern)
	{
		if(pattern is "*" or "*.*")
			return true;

		// Fast-path for the common *.ext form.
		if(pattern.StartsWith("*.") && !pattern.AsSpan(2).Contains('*') && !pattern.AsSpan(2).Contains('?'))
			return fileName.EndsWith(pattern.AsSpan(1), StringComparison.OrdinalIgnoreCase);

		return Regex.IsMatch(fileName, BuildRegex(pattern), RegexOptions.IgnoreCase);
	}

	static string BuildRegex(string pattern)
	{
		var sb = new StringBuilder("^");
		var i  = 0;

		while(i < pattern.Length) {

			if(pattern[i] == '*' && i + 1 < pattern.Length && pattern[i + 1] == '*') {

				// ** matches any characters including path separators.
				sb.Append(".*");
				i += 2;

				// Consume the trailing separator after ** (e.g., **/).
				if(i < pattern.Length && (pattern[i] == '/' || pattern[i] == '\\'))
					i++;
			}
			else if(pattern[i] == '*') {

				// Single * stays within one path segment.
				sb.Append(@"[^/\\]*");
				i++;
			}
			else if(pattern[i] == '?') {
				sb.Append('.');
				i++;
			}
			else if(pattern[i] == '.') {
				sb.Append("\\.");
				i++;
			}
			else {
				sb.Append(Regex.Escape(pattern[i].ToString()));
				i++;
			}
		}

		sb.Append('$');

		return sb.ToString();
	}
}
