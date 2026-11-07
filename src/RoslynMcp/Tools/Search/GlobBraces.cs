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
	///     The longest glob accepted. Far above any real one (a path is at most a few hundred characters).
	/// </summary>
	internal const int MaxPatternLength = 4096;
	
	/// <summary>
	///     The most characters all levels of an expansion may produce together. The work of a level is linear in
	///     what it produces, so this bounds the whole expansion: without it, 256 patterns carried through hundreds
	///     of '{a,a}' levels (which never grow the count) cost seconds. A real pattern produces a few kilobytes.
	/// </summary>
	internal const int MaxExpansionWork = 1 << 22;
	
	/// <summary>
	///     Expands every brace group of <paramref name="pattern"/>. A pattern without one comes back alone and
	///     unchanged. Duplicates and empty expansions are dropped. A pattern that held a group also keeps itself,
	///     as written, for a literal match (<c>{,}</c> then names only a file called <c>{,}</c>). Fails, with the error to return for <paramref name="parameter"/>, when the pattern is
	///     longer than <see cref="MaxPatternLength"/>, expands to more than <see cref="MaxExpansions"/> distinct
	///     patterns, or would take more than <see cref="MaxExpansionWork"/> characters of work.
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
		var                           anyGroup = false;
		var                           work     = 0L;
		
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
				anyGroup = true;
				
				var head = text[..open];
				var tail = text[(close + 1)..];
				
				work += (long) alternatives.Count * (head.Length + tail.Length) + (close - open);
				
				if(work > MaxExpansionWork) {
					
					error = $"{parameter} '{pattern[..Math.Min(pattern.Length, 200)]}' has too many {{a,b}} groups to expand. Use fewer groups, or split it into several calls.";
					
					return false;
				}
				
				foreach(var alternative in alternatives)
					if(!Keep(head + alternative + tail, open))
						
						return TooMany(pattern, parameter, out error);
			}
			
			if(!expanded) {
				
				// The pattern as written is one more alternative, matched literally — both matchers read a
				// brace as itself — so a file actually named 'Gen{A,B}.cs' is still found by that name.
				// Without it, a name holding a comma-bearing group could not be matched at all: there is no
				// escape character. Not counted toward the cap; it is one pattern whatever the groups.
				patterns = [.. next.Select(entry => entry.Text)];
				
				if(anyGroup && !patterns.Contains(pattern, StringComparer.Ordinal))
					patterns = [.. patterns, pattern];
				
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
		error = $"{parameter} '{pattern[..Math.Min(pattern.Length, 200)]}' expands to more than {MaxExpansions} patterns. Use fewer or smaller {{a,b}} groups, or split it into several calls.";
		
		return false;
	}
	
	// The leftmost brace at or after 'from' that opens a group: it has a matching close and a comma at its own
	// depth. A brace that fails either test is literal — so '{x}{a,b}' still expands its second group, and
	// '{x{a,b}}' the group inside the literal one. One pass with a stack of open braces: a '}' closes the
	// innermost one still open, which is the same pairing as counting depth from each '{', without rescanning
	// to the end from every brace that never closes (#397). A '}' with nothing open is literal.

	static bool TryFindGroup(string pattern, int from, out int open, out int close, out List<string> alternatives)
	{
		var stack     = new List<(int Open, List<int>? Commas)>();
		var bestOpen  = -1;
		var bestClose = -1;
		List<int>? bestCommas = null;
		
		for(var i = from; i < pattern.Length; i++) {
			
			switch(pattern[i]) {
				
				case '{':
					stack.Add((i, null));
					break;
				
				case ',' when stack.Count > 0:
					
					var top = stack[^1];
					
					(top.Commas ??= []).Add(i);
					stack[^1] = top;
					
					break;
				
				case '}' when stack.Count > 0:
					
					var (opened, commas) = stack[^1];
					
					stack.RemoveAt(stack.Count - 1);
					
					if(commas is not null && (bestOpen < 0 || opened < bestOpen)) {
						
						bestOpen   = opened;
						bestClose  = i;
						bestCommas = commas;
					}
					
					break;
			}
			
			// A group found with nothing left open: every brace after this one opens further right.
			if(bestOpen >= 0 && stack.Count == 0)
				break;
		}
		
		open         = bestOpen;
		close        = bestClose;
		alternatives = [];
		
		if(bestCommas is null)
			
			return false;
		
		var start = bestOpen + 1;
		
		foreach(var comma in bestCommas) {
			
			alternatives.Add(pattern[start..comma]);
			start = comma + 1;
		}
		
		alternatives.Add(pattern[start..bestClose]);
		
		return true;
	}
}
