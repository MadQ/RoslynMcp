using System.Text;
using System.Text.RegularExpressions;

namespace RoslynMcp.Tools;

// Filename-only glob matcher — callers pass Path.GetFileName(...), never a full path.
// Supports * (any chars, no separator), ** (any chars, crosses segments), ? (single char),
// and {a,b} alternatives (GlobBraces, #294).
internal static class GlobMatcher
{
	/// <summary>
	///     Creates a reusable matcher for <paramref name="pattern"/>, a <c>filePattern</c> argument.
	///     Operates on filenames only — path separators in the input produce unspecified results.
	///     Supports * (within a segment), ** (across segments), ? (single char), and {a,b} (either): a name
	///     matches when it matches any expansion. Fails, with the error to return, when the pattern
	///     expands to more than <see cref="GlobBraces.MaxExpansions"/> patterns.
	/// </summary>
	public static bool TryCreateMatcher(string pattern, out Func<string, bool> matcher, [System.Diagnostics.CodeAnalysis.NotNullWhen(false)] out string? error)
	{
		error = null;
		
		if(!GlobBraces.TryExpand(pattern, "filePattern", out var patterns, out error)) {
			
			matcher = static _ => false;
			
			return false;
		}
		
		if(patterns.Length == 1) {
			
			matcher = CreateSingle(patterns[0]);
			
			return true;
		}
		
		if(patterns.Length == 0) {
			
			matcher = static _ => false;
			
			return true;
		}
		
		// One regex for all expansions, not one per expansion: each BuildRegex is anchored, so their
		// alternation matches exactly the names one of them matches.
		var regex = new Regex(string.Join("|", patterns.Select(BuildRegex)), RegexOptions.IgnoreCase);
		
		matcher = regex.IsMatch;
		
		return true;
	}
	
	static Func<string, bool> CreateSingle(string pattern)
	{
		if(pattern is "*" or "*.*")
			return static _ => true;

		// Fast-path for the common *.ext form.
		if(pattern.StartsWith("*.") && !pattern.AsSpan(2).Contains('*') && !pattern.AsSpan(2).Contains('?'))
			return fileName => fileName.EndsWith(pattern.AsSpan(1), StringComparison.OrdinalIgnoreCase);

		var regex = new Regex(BuildRegex(pattern), RegexOptions.IgnoreCase);

		return regex.IsMatch;
	}
	
	static string BuildRegex(string pattern)
	{
		var sb = new StringBuilder("^");
		var i  = 0;
		
		while(i < pattern.Length) {
			
			if(pattern[i] == '*' && i + 1 < pattern.Length && pattern[i + 1] == '*') {
				
				// ** matches any characters including path separators.
				sb.Append(".*")
				;
				i += 2;
				
				// Consume the trailing separator after ** (e.g., **/).
				if(i < pattern.Length && (pattern[i] == '/' || pattern[i] == '\\'))
					i++;
			}
			else if(pattern[i] == '*') {
				
				// Single * stays within one path segment.
				sb.Append(@"[^/\\]*")
				;
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
