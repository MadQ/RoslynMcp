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
	///     either spelling. Every answer is kept for the life of the process and never dropped — a set keyed
	///     by path must not see two paths change from equal to unequal — so a flag changed while the server
	///     runs is seen after a restart. A directory that does not exist answers as its nearest existing
	///     ancestor does (a new directory takes its parent's flag on Windows, the volume's rule on macOS), and
	///     that answer is kept for it too. Only directories in which a case-only difference was compared are
	///     asked at all, so the cache stays small.
	///     <para>
	///         The first comparison in a directory asks the file system, possibly under a caller's lock; on an
	///         unreachable network share that waits as long as opening the directory does. Every later one is
	///         a lookup.
	///     </para>
	/// </remarks>
	sealed class FileSystemPathComparer : StringComparer
	{
		// Each directory's answer, by its normal form; and each spelling's normal form, worked out once.
		readonly ConcurrentDictionary<string, bool>   caseSensitive = new(StringComparer.Ordinal);
		readonly ConcurrentDictionary<string, string> normalForms   = new(StringComparer.Ordinal);
		
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
		
		// An order for sorting, not a second equality: case-insensitive, then ordinal, which is total and
		// transitive. Two spellings of one path compare unequal here, adjacent; asking the file system here
		// would make the order depend on it and break transitivity (#391 review). Never key a sorted
		// collection by it — key a dictionary or set by this comparer's Equals instead.
		public override int Compare(string? x, string? y)
		{
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
			var key = NormalForm(directory);
			
			if(caseSensitive.TryGetValue(key, out var known))
				
				return known;
			
			// A missing directory answers as its parent; with no existing ancestor at all, strictly. The
			// first answer stored wins, so concurrent callers all see the same one.
			var answer = FileIdentity.IsCaseSensitiveDirectory(directory)
				?? (Parent(directory) is { } parent ? IsCaseSensitive(parent) : true)
			;
			
			return caseSensitive.GetOrAdd(key, answer);
		}
		
		// What a directory's answer is kept under: its spelling with the root, and every component whose
		// parent matches names regardless of case, in upper case. Every spelling of one directory has the
		// same normal form, so a probe that failed for one spelling cannot leave it with an answer another
		// spelling contradicts (#392 review); siblings a case-sensitive parent tells apart keep two.
		string NormalForm(string directory)
		{
			if(normalForms.TryGetValue(directory, out var known))
				
				return known;
			
			var normal = Parent(directory) is { } parent
				? Path.Join(NormalForm(parent), IsCaseSensitive(parent) ? Path.GetFileName(directory) : Path.GetFileName(directory).ToUpperInvariant())
				: directory.ToUpperInvariant()
			;
			
			return normalForms.GetOrAdd(directory, normal);
		}
		
		static string? Parent(string directory)
			=> Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(directory));
	}
}
