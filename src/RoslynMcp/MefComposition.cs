using System.Collections.Immutable;
using System.Composition.Hosting;
using System.Diagnostics;
using System.Reflection;
using Microsoft.CodeAnalysis.Host;
using Microsoft.CodeAnalysis.Host.Mef;

namespace RoslynMcp;

/// <summary>
///     The one MEF composition of the process (#389): Roslyn's default host assemblies — Workspaces,
///     CSharp.Workspaces, Features and CSharp.Features; the VisualBasic pair is not shipped and
///     <c>MefHostServices.DefaultAssemblies</c> skips what it cannot load — composed once and handed to
///     every workspace the server creates, MSBuild and Adhoc alike. The container is kept because it is
///     also where the built-in code refactoring and code fix providers come from
///     (<see cref="CodeActionHost"/>): one composition means the providers and the workspace services
///     they run against share their <c>[Shared]</c> singletons, as they do in Visual Studio.
///     <c>MefHostServices.DefaultHost</c> would compose the same assemblies but keeps its container to
///     itself, and a second container for the providers alone would compose Features twice.
/// </summary>
internal static class MefComposition
{
	static readonly Lazy<Composed> composed = new(Compose, LazyThreadSafetyMode.ExecutionAndPublication);
	static          int            logged;
	
	/// <summary>The host services every workspace the server creates is given.</summary>
	public static HostServices HostServices => composed.Value.HostServices;
	
	/// <summary>The container behind <see cref="HostServices"/>, for the exports a workspace does not hand out itself.</summary>
	public static CompositionHost Container => composed.Value.Container;
	
	public static ImmutableArray<Assembly> Assemblies => composed.Value.Assemblies;
	
	public static TimeSpan Elapsed => composed.Value.Elapsed;
	
	public static bool IsComposed => composed.IsValueCreated;
	
	/// <summary>
	///     Logs what the composition cost, once, the first time a caller with a logger reaches it. The
	///     cost is paid by the first workspace load, so this is where it shows up next to the load time.
	/// </summary>
	public static void LogOnce(FileLogger? log)
	{
		if(log is null || Interlocked.Exchange(ref logged, 1) == 1)
			return;
		
		var value = composed.Value;
		var names = string.Join(", ", value.Assemblies.Select(assembly => assembly.GetName().Name));
		
		log.LogInfo("Composition", $"{value.Assemblies.Length} assemblies composed in {value.Elapsed.TotalMilliseconds:F0} ms: {names}");
	}
	
	static Composed Compose()
	{
		var stopwatch  = Stopwatch.StartNew();
		var assemblies = MefHostServices.DefaultAssemblies;
		var container  = new ContainerConfiguration().WithAssemblies(assemblies).CreateContainer();
		
		return new Composed(container, MefHostServices.Create(container), assemblies, stopwatch.Elapsed);
	}
	
	sealed record Composed(CompositionHost Container, MefHostServices HostServices, ImmutableArray<Assembly> Assemblies, TimeSpan Elapsed);
}
