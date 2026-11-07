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
/// </summary>
internal static class GlobBraces
{
	/// <summary>
	///     The most patterns one glob may expand to. Each group multiplies the count, so a short pattern can ask for
	///     thousands of matchers; past this a call fails instead of matching a silent part of what was asked.
	/// </summary>
	internal const int MaxExpansions = 256;
	
	/// <summary>
	///     Expands every brace group of <paramref name="pattern"/>. A pattern without one comes back alone and
	///     unchanged; empty expansions are dropped, so the result can be empty (<c>{,}</c>), which matches nothing.
	///     Returns false, with no patterns, when the expansion would exceed <see cref="MaxExpansions"/>.
	/// </summary>
	internal static bool TryExpand(string pattern, out string[] patterns)
	{
		if(pattern.IndexOf('{') < 0) {
			
			patterns = [pattern];
			
			return true;
		}
		
		var results = new List<string>();
		
		if(!Expand(pattern, "", results)) {
			
			patterns = [];
			
			return false;
		}
		
		// '{,x}' expands to '' and 'x'. An empty pattern names no file, and Matcher is not asked what it makes of one.
		patterns = [.. results.Where(expanded => expanded.Length > 0).Distinct(StringComparer.Ordinal)];
		
		return true;
	}
	
	/// <summary>The error for a pattern past <see cref="MaxExpansions"/>, naming the parameter it came in.</summary>
	internal static string TooManyExpansions(string parameter, string pattern)
		=> $"{parameter} '{pattern}' expands to more than {MaxExpansions} patterns. Use fewer or smaller {{a,b}} groups, or split it into several calls.";
	
	// Depth-first: the first group's alternatives, each followed by the rest of the pattern, which may hold more
	// groups. Every leaf is one pattern, so the cap bounds the work as well as the result.
	static bool Expand(string rest, string prefix, List<string> results)
	{
		if(!TryFindGroup(rest, out var open, out var close, out var alternatives)) {
			
			if(results.Count == MaxExpansions)
				
				return false;
			
			results.Add(prefix + rest);
			
			return true;
		}
		
		var head = prefix + rest[..open];
		var tail = rest[(close + 1)..];
		
		foreach(var alternative in alternatives)
			if(!Expand(alternative + tail, head, results))
				
				return false;
		
		return true;
	}
	
	// The first brace that opens a group: it has a matching close and a comma at its own depth. A brace that fails
	// either test is literal, and the search goes on after it — so '{x}{a,b}' still expands its second group, and
	// '{x{a,b}}' the group inside the literal one.
	static bool TryFindGroup(string pattern, out int open, out int close, out List<string> alternatives)
	{
		alternatives = [];
		
		for(open = pattern.IndexOf('{'); open >= 0; open = pattern.IndexOf('{', open + 1)) {
			
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
