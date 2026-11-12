namespace RoslynMcp;

/// <summary>
///     How often one workspace has taken a change: a text applied to a document, a document added
///     or removed, a reload swapped in. It moves whoever made the change, a tool of the server's or
///     the watcher reporting someone else's, so a result kept from an earlier call can be checked
///     against it: another count means the code may no longer be what the result was computed
///     from (#435).
///     <para>
///         An object of its own, and not a number on the workspace, so that whoever keeps a result
///         can hold on to the counter without holding on to the workspace. One counter per
///         workspace, and not one for the process: what happens in one workspace says nothing about
///         a result computed from another.
///     </para>
/// </summary>
internal sealed class WorkspaceChangeCounter
{
	long count;
	
	public long Value => Interlocked.Read(ref count);
	
	public void Increment() => Interlocked.Increment(ref count);
}
