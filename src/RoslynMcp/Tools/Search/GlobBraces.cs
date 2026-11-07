using System.Diagnostics.CodeAnalysis;

namespace RoslynMcp.Tools;

/// <summary>
///     Brace expansion for glob patterns (#294): <c>src/*.{cs,csproj}</c> is <c>src/*.cs</c> and <c>src/*.csproj</c>.
///     Neither matcher the server uses has it — <c>FileSystemGlobbing.Matcher</c> (<c>roslyn_list_files</c>) and
///     <see cref="GlobMatcher"/> (every <c>filePattern</c>) — so a pattern is expanded first and its expansions are
///     matched as alternatives.
///     <para>
///         Groups nest (<c>{a,{b,c}}</c>) and multiply (<c>{a,b}.{x,y}</c> is four patterns). A brace with no match,
///         or a group with no comma at its own level (<c>{x}</c>), is a literal character, as in a shell, so a file
///         whose name holds braces can still be named. There is no escape character: the matchers have none either.
///     </para>
///     <para>
///         The pattern comes from an agent, so its size is bounded before any work: at most
///         <see cref="MaxPatternLength"/> characters, and at most <see cref="MaxExpansions"/> distinct patterns.
///         Expansion goes one group level at a time, without recursion, and each level at least doubles the
///         patterns a real group produces — so a pattern that asks for too many fails within a few levels.
///     </para>
/// </summary>
internal static class GlobBraces
{
	/// <summary>
	///     The most distinct patterns one glob may expand to. Each group multiplies the count, so a short pattern can
	///     ask for thousands of matchers; past this a call fails instead of matching a silent part of what was asked.
	/// </summary>
	internal const int MaxExpansions = 256;
	
	/// <summary>
	///     The longest glob accepted. Far above any real one (a path is at most a few hundred characters), and low
	///     enough that the scans below, quadratic in the length at worst, stay in milliseconds.
	/// </summary>
	internal const int MaxPatternLength = 4096;
	
	/// <summary>
	///     Expands every brace group of <paramref name="pattern"/>. A pattern without one comes back alone and
	///     unchanged. Duplicates and empty expansions are dropped, so the result can be empty (<c>{,}</c>), which
	///     matches nothing. Fails, with the error to return for <paramref name="parameter"/>, when the pattern is
	///     longer than <see cref="MaxPatternLength"/> or expands to more than <see cref="MaxExpansions"/> distinct
	///     patterns.
	/// </summary>
	internal static bool TryExpand(string pattern, string parameter, out string[] patterns, [NotNullWhen(false)] out string? error)
	{
		patterns = [];
		error    = null;
		
		if(pattern.Length > MaxPatternLength) {
			
			error = $"{parameter} is {pattern.Length} characters long; a glob may have at most {MaxPatternLength}.";
			
			return false;
		}
		
		if(pattern.IndexOf('{') < 0) {
			
			patterns = [pattern];
			
			return true;
		}
		
		// Each entry carries where its search for the next group starts: everything before the group just
		// expanded holds only literal braces, and expanding a later group cannot turn one into a group (an
		// alternative is balanced and has no comma at its own level), so it is never scanned again.
		List<(string Text, int From)> current = [(pattern, 0)];
		
		while(true) {
			
			var next     = new List<(string Text, int From)>();
			var seen     = new HashSet<string>(StringComparer.Ordinal);
			var expanded = false;
			
			foreach(var (text, from) in current) {
				
				if(!TryFindGroup(text, from, out var open, out var close, out var alternatives)) {
					
					if(!Keep(text, text.Length))
						
						return TooMany(pattern, parameter, out error);
					
					continue;
				}
				
				expanded = true;
				
				var head = text[..open];
				var tail = text[(close + 1)..];
				
				foreach(var alternative in alternatives)
					if(!Keep(head + alternative + tail, open))
						
						return TooMany(pattern, parameter, out error);
			}
			
			if(!expanded) {
				
				patterns = [.. next.Select(entry => entry.Text)];
				
				return true;
			}
			
			current = next;
			
			// '{,x}' expands to '' and 'x'. An empty pattern names no file, and Matcher is not asked what it
			// makes of one. A duplicate ('{a,a}') is one matcher, not two, and does not count toward the cap.
			bool Keep(string text, int from)
			{
				if(text.Length == 0 || !seen.Add(text))
					
					return true;
				
				next.Add((text, from));
				
				return next.Count <= MaxExpansions;
			}
		}
	}
	
	static bool TooMany(string pattern, string parameter, out string error)
	{
		error = $"{parameter} '{pattern}' expands to more than {MaxExpansions} patterns. Use fewer or smaller {{a,b}} groups, or split it into several calls.";
		
		return false;
	}
	
	// The first brace at or after 'from' that opens a group: it has a matching close and a comma at its own depth.
	// A brace that fails either test is literal, and the search goes on after it — so '{x}{a,b}' still expands its
	// second group, and '{x{a,b}}' the group inside the literal one.
	static bool TryFindGroup(string pattern, int from, out int open, out int close, out List<string> alternatives)
	{
		alternatives = [];
		
		for(open = pattern.IndexOf('{', from); open >= 0; open = pattern.IndexOf('{', open + 1)) {
			
			var depth = 0;
			var start = open + 1;
			
			alternatives.Clear();
			
			for(close = open + 1; close < pattern.Length; close++) {
				
				var c = pattern[close];
				
				if(c == '{')
					depth++;
				else if(c == '}') {
					
					if(depth == 0)
						break;
					
					depth--;
				}
				else if(c == ',' && depth == 0) {
					
					alternatives.Add(pattern[start..close]);
					start = close + 1;
				}
			}
			
			if(close < pattern.Length && alternatives.Count > 0) {
				
				alternatives.Add(pattern[start..close]);
				
				return true;
			}
		}
		
		close = -1;
		
		return false;
	}
}
