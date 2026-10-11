namespace RoslynMcp;

/// <summary>
///     The tracked documents of a workspace that are another file's name: a symbolic link, or
///     a path under a linked directory (#341). One file on disk can be several documents, and
///     when it changes — whoever changed it, under whichever name — every one of them has new
///     content. The index answers "which documents are this file" without looking at the
///     file system.
///     <para>
///         Only such documents are held. A workspace without links has an empty index, and a
///         lookup in it costs nothing. Paths are spelled the way the workspace spells them
///         (see <c>WorkspaceInstance.LinkTargetOf</c>) and compared the way the file system
///         compares names.
///     </para>
///     <para>
///         An instance never changes. A change produces a new instance, which the workspace
///         swaps in as a whole, so readers need no lock.
///     </para>
/// </summary>
internal sealed class PhysicalFileIndex
{
	public static readonly PhysicalFileIndex Empty = new(new Dictionary<string, string>(SecurityBoundary.PathComparer));
	
	readonly Dictionary<string, string>   physicalByName;
	readonly Dictionary<string, string[]> namesByPhysical;
	
	PhysicalFileIndex(Dictionary<string, string> physicalByName)
	{
		this.physicalByName = physicalByName;
		
		namesByPhysical = physicalByName
			.GroupBy(entry => entry.Value, entry => entry.Key, SecurityBoundary.PathComparer)
			.ToDictionary(group => group.Key, group => group.ToArray(), SecurityBoundary.PathComparer)
		;
	}
	
	/// <summary>The number of documents that are another file's name.</summary>
	public int Count => physicalByName.Count;
	
	/// <summary>Whether <paramref name="path"/> is a document that leads to another file.</summary>
	public bool IsName(string path) => physicalByName.ContainsKey(path);
	
	/// <summary>The file the document at <paramref name="name"/> leads to, or null when it leads nowhere else.</summary>
	public string? PhysicalOf(string name) => physicalByName.GetValueOrDefault(name);
	
	/// <summary>
	///     The documents that lead to the file at <paramref name="physical"/>. The file's own
	///     path is not among them: it leads nowhere else.
	/// </summary>
	public string[] NamesOf(string physical) => namesByPhysical.GetValueOrDefault(physical) ?? [];
	
	/// <summary>
	///     Builds the index for <paramref name="documentPaths"/>. <paramref name="physicalOf"/>
	///     gives the file a path leads to, and null for a path that leads nowhere else.
	/// </summary>
	public static PhysicalFileIndex Build(IEnumerable<string> documentPaths, Func<string, string?> physicalOf)
		=> Empty.With(documentPaths, physicalOf)
	;
	
	/// <summary>
	///     The index after looking at <paramref name="names"/> again: each is entered under the
	///     file it leads to now, and dropped when <paramref name="physicalOf"/> gives null.
	/// </summary>
	public PhysicalFileIndex With(IEnumerable<string> names, Func<string, string?> physicalOf)
	{
		var updated = new Dictionary<string, string>(physicalByName, SecurityBoundary.PathComparer);
		var changed = false;
		
		foreach(var name in names) {
			
			if(physicalOf(name) is { } physical) {
				
				if(updated.TryGetValue(name, out var known) && SecurityBoundary.PathComparer.Equals(known, physical))
					continue;
				
				updated[name] = physical;
				changed       = true;
			}
			else
				changed |= updated.Remove(name);
		}
		
		return changed ? new(updated) : this;
	}
}
