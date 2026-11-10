using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace RoslynMcp;

/// <summary>
///     The types of an assembly that carry a code fix or code refactoring export attribute, read
///     from its metadata without loading one of them (#421). <see cref="CodeActionHost"/> asks only
///     when some type of the assembly did not load: reflection then says how many failed, not
///     which, and most often they are helper types no provider needs — the 27 of
///     Microsoft.CodeAnalysis.Analyzers that want Microsoft.Bcl.AsyncInterfaces, which no host on
///     .NET has. The compiler never sees those fail, since it loads the analyzer types its metadata
///     names and nothing else; with the names from here the providers are loaded the same way.
///     A file of its own, not members of the host: System.Reflection.Metadata has a
///     <c>Document</c> that would clash with Roslyn's there.
/// </summary>
internal static class ExportedProviderTypes
{
	// A larger assembly than any analyzer package ships; past it the caller reports the failed types by count.
	private const long SizeLimit = 256L * 1024 * 1024;
	
	/// <summary>
	///     Full names as <see cref="Type.FullName"/> spells them (a nested type as
	///     <c>Outer+Inner</c>). Throws what reading the file or its metadata throws; the caller
	///     falls back to what it knows without the names.
	/// </summary>
	internal static ImmutableArray<string> Read(Assembly assembly)
	{
		using var image = new PEReader(ImmutableArray.Create(FileReader.ReadAllBytes(assembly.Location, SizeLimit)));
		
		var reader = image.GetMetadataReader();
		var names  = ImmutableArray.CreateBuilder<string>();
		
		foreach(var handle in reader.TypeDefinitions) {
			
			var type = reader.GetTypeDefinition(handle);
			
			foreach(var attribute in type.GetCustomAttributes()) {
				
				if(!IsProviderExport(reader, reader.GetCustomAttribute(attribute)))
					continue;
				
				names.Add(FullName(reader, type));
				
				break;
			}
		}
		
		return names.ToImmutable();
	}
	
	// By the attribute's own name, as a reference to another assembly's type. One derived from an
// export attribute is not recognised; its type is then a provider only if it loaded, as before.
private static bool IsProviderExport(MetadataReader reader, CustomAttribute attribute)
{
	if(attribute.Constructor.Kind != HandleKind.MemberReference)
		return false;
	
	if(reader.GetMemberReference((MemberReferenceHandle) attribute.Constructor).Parent is not { Kind: HandleKind.TypeReference } parent)
		return false;
	
	var type = reader.GetTypeReference((TypeReferenceHandle) parent);
	
	return reader.StringComparer.Equals(type.Name, "ExportCodeFixProviderAttribute") && reader.StringComparer.Equals(type.Namespace, "Microsoft.CodeAnalysis.CodeFixes")
		|| reader.StringComparer.Equals(type.Name, "ExportCodeRefactoringProviderAttribute") && reader.StringComparer.Equals(type.Namespace, "Microsoft.CodeAnalysis.CodeRefactorings");
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
