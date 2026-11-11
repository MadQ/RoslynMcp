using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace RoslynMcp;

/// <summary>
///     Gives a document of a workspace a new text without writing it to disk (#403).
///     <para>
///         The only public way to change a workspace's solution is <c>TryApplyChanges</c>, and an
///         <c>MSBuildWorkspace</c> persists what it applies: it writes every changed document to
///         its file. That is wanted for an edit and wrong for a text that was just read from that
///         file. Written back, it replaces whatever someone else has saved there since the read.
///     </para>
///     <para>
///         <c>Workspace.OnDocumentTextChanged</c> and <c>OnAdditionalDocumentTextChanged</c> are
///         what a workspace's own host calls when a file changed outside: they move the current
///         solution on and write nothing. They are <c>protected internal</c>, for subclasses, and
///         <c>MSBuildWorkspace</c> is sealed, so they are reached with an unsafe accessor. An
///         accessor binds by exact name and signature when it is first called. Should a Roslyn
///         version rename or reshape the methods, that first call fails, the failure is
///         remembered, and the caller goes back to <c>TryApplyChanges</c>: what was true before
///         #403, not a broken sync.
///     </para>
/// </summary>
internal static class WorkspaceTextSetter
{
	[UnsafeAccessor(UnsafeAccessorKind.Method, Name = "OnDocumentTextChanged")]
	static extern void OnDocumentTextChanged(Workspace workspace, DocumentId documentId, SourceText newText, PreservationMode mode);
	
	[UnsafeAccessor(UnsafeAccessorKind.Method, Name = "OnAdditionalDocumentTextChanged")]
	static extern void OnAdditionalDocumentTextChanged(Workspace workspace, DocumentId documentId, SourceText newText, PreservationMode mode);
	
	static volatile bool unavailable;
	
	/// <summary>False once a call has found that this Roslyn does not have the methods.</summary>
	public static bool Available => !unavailable;
	
	/// <summary>
	///     Sets the text of a source document, or of an additional document when
	///     <paramref name="additional"/> says so. Returns false, and changes nothing, when the
	///     methods are not there; any other failure is the workspace's own and comes out.
	/// </summary>
	public static bool TrySet(Workspace workspace, DocumentId documentId, SourceText text, bool additional)
	{
		if(unavailable)
			
			return false;
		
		try {
			
			Set(workspace, documentId, text, additional);
			
			return true;
		}
		catch(Exception ex) when(ex is MissingMethodException or MissingMemberException) {
			
			unavailable = true;
			
			return false;
		}
	}
	
	// Its own method, never inlined: the accessors are bound when the method that calls them is
	// compiled, and the failure of that must come out inside the try above.
	[MethodImpl(MethodImplOptions.NoInlining)]
	static void Set(Workspace workspace, DocumentId documentId, SourceText text, bool additional)
	{
		if(additional)
			OnAdditionalDocumentTextChanged(workspace, documentId, text, PreservationMode.PreserveValue);
		else
			OnDocumentTextChanged(workspace, documentId, text, PreservationMode.PreserveValue);
	}
}
