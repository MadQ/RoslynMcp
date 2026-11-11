using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;

namespace RoslynMcp;

/// <summary>
///     The types of an assembly that a host loads by name: those that carry a code fix or code
///     refactoring export attribute, and the diagnostic analyzers. Read from the assembly's metadata
///     without loading one of them (#421). <see cref="CodeActionHost"/> asks only when some type of
///     the assembly did not load: reflection then says how many failed, not which, and most often
///     they are helper types nothing needs — the 27 of Microsoft.CodeAnalysis.Analyzers that want
///     Microsoft.Bcl.AsyncInterfaces, which no host on .NET has. The compiler never sees those fail,
///     since it loads the analyzer types its metadata names and nothing else; with the names from
///     here the providers are loaded the same way, and a provider or an analyzer that is really lost
///     is named. The analyzers are here for that last part only: Roslyn swallows an analyzer that
///     does not load, so this report is the one place it can be said.
///     A file of its own, not members of the host: System.Reflection.Metadata has a
///     <c>Document</c> that would clash with Roslyn's there.
/// </summary>
internal static class ExportedProviderTypes
{
	// A larger assembly than any analyzer package ships; past it the caller reports the failed types by count.
	private const long SizeLimit = 256L * 1024 * 1024;
	
	private static readonly (string Namespace, string Name)[] HostedAttributes = [
		("Microsoft.CodeAnalysis.CodeFixes",        "ExportCodeFixProviderAttribute"),
		("Microsoft.CodeAnalysis.CodeRefactorings", "ExportCodeRefactoringProviderAttribute"),
		("Microsoft.CodeAnalysis.Diagnostics",      "DiagnosticAnalyzerAttribute"),
	];
	
	/// <summary>
	///     Full names as <see cref="Type.FullName"/> spells them (a nested type as
	///     <c>Outer+Inner</c>). Throws what reading the file or its metadata throws; the caller
	///     falls back to what it knows without the names.
	/// </summary>
	internal static ImmutableArray<string> Read(Assembly assembly)
	{
		// The reader takes the bytes as they are; wrapping the array does not copy it.
		var bytes = FileReader.ReadAllBytes(assembly.Location, SizeLimit);
		
		using var image = new PEReader(ImmutableCollectionsMarshal.AsImmutableArray(bytes));
		
		var reader = image.GetMetadataReader();
		var names  = ImmutableArray.CreateBuilder<string>();
		
		foreach(var handle in reader.TypeDefinitions) {
			
			var type = reader.GetTypeDefinition(handle);
			
			foreach(var attribute in type.GetCustomAttributes()) {
				
				if(!IsHostedAttribute(reader, reader.GetCustomAttribute(attribute)))
					continue;
				
				names.Add(FullName(reader, type));
				
				break;
			}
		}
		
		return names.ToImmutable();
	}
	
	// By the attribute's own name, as a reference to another assembly's type: the three attributes
	// are Roslyn's and sealed, so an analyzer assembly's use of one is always a member reference.
	private static bool IsHostedAttribute(MetadataReader reader, CustomAttribute attribute)
	{
		if(attribute.Constructor.Kind != HandleKind.MemberReference)
			return false;
		
		if(reader.GetMemberReference((MemberReferenceHandle) attribute.Constructor).Parent is not { Kind: HandleKind.TypeReference } parent)
			return false;
		
		var type = reader.GetTypeReference((TypeReferenceHandle) parent);
		
		foreach(var (containing, name) in HostedAttributes)
			if(reader.StringComparer.Equals(type.Name, name) && reader.StringComparer.Equals(type.Namespace, containing))
				return true;
		
		return false;
	}
	
	private static string FullName(MetadataReader reader, TypeDefinition type)
	{
		var name      = reader.GetString(type.Name);
		var declaring = type.GetDeclaringType();
		
		if(!declaring.IsNil)
			return $"{FullName(reader, reader.GetTypeDefinition(declaring))}+{name}";
		
		var containing = reader.GetString(type.Namespace);
		
		return containing.Length == 0 ? name : $"{containing}.{name}";
	}
}
