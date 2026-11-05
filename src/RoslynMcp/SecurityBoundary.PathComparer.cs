using System.Collections.Concurrent;

namespace RoslynMcp;

internal sealed partial class SecurityBoundary
{
	/// <summary>
	///     <see cref="PathComparer"/> where case sensitivity is a property of the directory, not of the
	///     platform (#391): Windows, where any directory can be made case-sensitive (<c>fsutil file
	///     setCaseSensitiveInfo</c>, used with WSL), and macOS, whose volumes can be formatted so. Two
	///     spellings that differ only in case are one path when every directory in which they differ ignores
	///     case. Ordinal-equal text is always one path and text unequal ignoring case never is, so the file
	///     system is asked only about a case-only difference, once per directory
	///     (<see cref="FileIdentity.IsCaseSensitiveDirectory"/>). The hash ignores case, which keeps it
	///     consistent with every answer.
	/// </summary>
	/// <remarks>
	///     The relation is an equivalence: two paths are one when each component they spell differently lies
	///     in a directory that ignores case, and the directories before a component are the same directory in
	///     either spelling. A directory's answer is kept for the life of the process — a set keyed by path
	///     must not see two paths change from equal to unequal — so a flag changed while the server runs is
	///     seen after a restart. The answer for a directory that does not exist is its nearest existing
	///     ancestor's, and is not kept: a new directory takes its parent's flag on Windows, and the volume's
	///     rule on macOS, until it exists and can answer for itself.
	/// </remarks>
	sealed class FileSystemPathComparer : StringComparer
	{
		// Directories a session's paths pass through: a few hundred in practice. The cap only bounds a
		// pathological run; dropping every answer at once re-asks the same file system.
		const int MaxDirectories = 4096;
		
		readonly ConcurrentDictionary<string, bool> caseSensitive = new(StringComparer.Ordinal);
		
		public override bool Equals(string? x, string? y)
		{
			if(string.Equals(x, y, StringComparison.Ordinal))
				
				return true;
			
			if(x is null || y is null || !string.Equals(x, y, StringComparison.OrdinalIgnoreCase))
				
				return false;
			
			return CaseDifferencesIgnored(x, y);
		}
		
		public override int GetHashCode(string obj)
			=> OrdinalIgnoreCase.GetHashCode(obj);
		
		// Only for a stable order (a watch-set key): equal paths sort together, case variants that are
		// two paths sort apart by ordinal.
		public override int Compare(string? x, string? y)
		{
			if(Equals(x, y))
				
				return 0;
			
			var order = OrdinalIgnoreCase.Compare(x, y);
			
			return order != 0 ? order : Ordinal.Compare(x, y);
		}
		
		// x and y are equal ignoring case: one length, separators in the same places. The root — a drive
		// letter, a UNC server and share — ignores case on both platforms. A bare or relative name has no
		// directory to ask, and keeps the platform's rule: it only ever filters candidates that are then
		// identified by their full paths.
		bool CaseDifferencesIgnored(string x, string y)
		{
			var rootLength = Path.GetPathRoot(x)?.Length ?? 0;
			
			if(rootLength == 0)
				
				return true;
			
			var start = rootLength;
			
			for(var end = rootLength; end <= x.Length; end++) {
				
				if(end < x.Length && x[end] != Path.DirectorySeparatorChar && x[end] != Path.AltDirectorySeparatorChar)
					continue;
				
				// The directory the component is looked up in; a root keeps its separator ("C:\", "/").
				if(!x.AsSpan(start, end - start).SequenceEqual(y.AsSpan(start, end - start))
					&& IsCaseSensitive(Path.TrimEndingDirectorySeparator(x[..start])))
					
					return false;
				
				start = end + 1;
			}
			
			return true;
		}
		
		bool IsCaseSensitive(string directory)
		{
			if(caseSensitive.TryGetValue(directory, out var known))
				
				return known;
			
			if(FileIdentity.IsCaseSensitiveDirectory(directory) is { } answer) {
				
				if(caseSensitive.Count >= MaxDirectories)
					caseSensitive.Clear();
				
				caseSensitive[directory] = answer;
				
				return answer;
			}
			
			for(var ancestor = Parent(directory); ancestor is not null; ancestor = Parent(ancestor))
				
				if(FileIdentity.IsCaseSensitiveDirectory(ancestor) is { } inherited)
					
					return inherited;
			
			return false;
			
			static string? Parent(string path)
				=> Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(path));
		}
	}
}
