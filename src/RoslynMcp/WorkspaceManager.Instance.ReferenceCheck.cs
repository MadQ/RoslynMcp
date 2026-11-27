using Microsoft.CodeAnalysis;
using System.Text.Json;

namespace RoslynMcp;

internal sealed partial class WorkspaceManager
{
	sealed partial class WorkspaceInstance
	{
		/// <summary>
		///     Finds the projects that loaded with only part of their package references (#456), by
		///     comparing what each resolved with what its restore said it would get.
		///     <para>
		///         <c>project.assets.json</c> lists, per target framework, the compile-time assemblies
		///         of every package: what the build hands the compiler after conditions, asset
		///         exclusions and package pruning. A design-time build that came up short has metadata
		///         references for fewer of them.
		///     </para>
		///     <para>
		///         Assemblies are compared by file name, not by path. The SDK's conflict resolution
		///         replaces a package's assembly with the framework's own copy of it when that is as
		///         new, so the path the assets file names is often not the one referenced, while an
		///         assembly of that name still is. Only packages are compared: a framework reference
		///         is in no package, and a project is a project reference.
		///     </para>
		///     <para>
		///         Where the comparison cannot be made, nothing is found: no assets file (a project
		///         that was never restored is reported as that), a file that cannot be read, a target
		///         framework the file does not name the way the project does. A finding makes the
		///         workspace unhealthy and is worth a reload, so a doubt must not be one.
		///     </para>
		///     <para>
		///         Known to be reported though intended: a build that takes a package's assembly out
		///         of its references itself, in a target of its own. The assets file still lists it.
		///     </para>
		///     A type of its own because it needs nothing of the workspace but the solution it is
		///     handed.
		/// </summary>
		static class ReferenceCheck
		{
			/// <summary>
			///     The projects of <paramref name="solution"/> that lack a compile-time assembly of
			///     their packages. <paramref name="compared"/> counts the projects that could be
			///     compared, <paramref name="assetsFiles"/> the files read for them: the targets of a
			///     multi-targeted project share one.
			/// </summary>
			public static PartialReferences[] Find(Solution solution, out int compared, out int assetsFiles)
			{
				var read  = new Dictionary<string, AssetsFile?>(SecurityBoundary.PathComparer);
				var found = new List<PartialReferences>();
				
				compared = 0;
				
				foreach(var project in solution.Projects) {
					
					// A project with no references at all is the finding next to this one.
					if(project.MetadataReferences.Count == 0)
						continue;
					
					if(FindAssetsFile(project).Path is not { } path)
						continue;
					
					if(!read.TryGetValue(path, out var assets))
						read[path] = assets = AssetsFile.TryRead(path);
					
					if(assets?.TargetOf(project.Name) is not { } expected)
						continue;
					
					compared++;
					
					var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
					
					foreach(var reference in project.MetadataReferences)
						if(reference is PortableExecutableReference { FilePath: { Length: > 0 } file })
							referenced.Add(Path.GetFileName(file));
					
					var missing = expected.Where(assembly => !referenced.Contains(assembly.FileName)).ToArray();
					
					if(missing.Length == 0)
						continue;
					
					string[] names = [..missing
						.Select(assembly => assembly.FileName)
						.Distinct(StringComparer.OrdinalIgnoreCase)
						.Order(StringComparer.OrdinalIgnoreCase)];
					
					var expectedNames = expected.Select(assembly => assembly.FileName).Distinct(StringComparer.OrdinalIgnoreCase).Count();
					
					found.Add(new(ProjectKey.Of(project), expectedNames, names, missing.All(assets.IsGone)));
				}
				
				assetsFiles = read.Count;
				
				return [..found];
			}
			
			/// <summary>A compile-time assembly of a package: its file name, and where the package has it.</summary>
			readonly record struct Assembly(string FileName, string PackagePath, string Asset);
			
			/// <summary>What the check takes from one assets file: the assemblies per target, and the folders packages are in.</summary>
			sealed class AssetsFile
			{
				// By the target's name in the file, for the targets without a runtime identifier.
				readonly Dictionary<string, Assembly[]> targets = new(StringComparer.OrdinalIgnoreCase);
				
				// The name a project gives a target framework (its TargetFramework value) to the
				// target's name in the file, where the file says they differ.
				readonly Dictionary<string, string> aliases = new(StringComparer.OrdinalIgnoreCase);
				
				readonly List<string> packageFolders = [];
				
				/// <summary>
				///     The file, or null when it cannot be read or is not what an assets file looks
				///     like: the comparison is then not made.
				/// </summary>
				public static AssetsFile? TryRead(string path)
				{
					try {
						
						using var document = JsonDocument.Parse(FileReader.ReadAllBytes(path, FileReader.DocumentLimit));
						
						var root = document.RootElement;
						
						if(!Object(root, "targets", out var targetList) || !Object(root, "libraries", out var libraries))
							
							return null;
						
						var file = new AssetsFile();
						
						foreach(var target in targetList.EnumerateObject()) {
							
							// "net10.0/win-x64": the same compile-time assemblies as "net10.0", for a
							// runtime. A project is matched to the target without one.
							if(target.Name.Contains('/') || target.Value.ValueKind != JsonValueKind.Object)
								continue;
							
							var assemblies = new List<Assembly>();
							
							foreach(var library in target.Value.EnumerateObject()) {
								
								if(!String(library.Value, "type", out var type) || type != "package" || !Object(library.Value, "compile", out var compile))
									continue;
								
								// Where the package is below a package folder; the library's name and
								// version in lower case where the file does not say.
								var packagePath = Object(libraries, library.Name, out var entry) && String(entry, "path", out var known)
									? known
									: library.Name.ToLowerInvariant();
								
								foreach(var asset in compile.EnumerateObject()) {
									
									var fileName = asset.Name[(asset.Name.LastIndexOf('/') + 1)..];
									
									// "_._" stands for "nothing for this framework"; anything else that
									// is no assembly is not referenced either.
									if(fileName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
										assemblies.Add(new(fileName, packagePath, asset.Name));
								}
							}
							
							file.targets[target.Name] = [..assemblies];
						}
						
						if(Object(root, "project", out var project) && Object(project, "frameworks", out var frameworks))
							foreach(var framework in frameworks.EnumerateObject())
								if(String(framework.Value, "targetAlias", out var alias) && file.targets.ContainsKey(framework.Name))
									file.aliases[alias] = framework.Name;
						
						if(Object(root, "packageFolders", out var folders))
							foreach(var folder in folders.EnumerateObject())
								file.packageFolders.Add(folder.Name);
						
						return file;
					}
					catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException) {
						
						return null;
					}
				}
				
				/// <summary>
				///     The assemblies of the target a project is built for, or null when the file does
				///     not say which that is. A multi-targeted project is one project per framework,
				///     named <c>Foo(net8.0)</c>; a project with one framework has the file's one target.
				/// </summary>
				public Assembly[]? TargetOf(string projectName)
				{
					var open = projectName.LastIndexOf('(');
					
					if(open < 0 || !projectName.EndsWith(')'))
						
						return targets.Count == 1 ? targets.Values.First() : null;
					
					var framework = projectName[(open + 1)..^1];
					
					if(targets.TryGetValue(framework, out var named))
						
						return named;
					
					return aliases.TryGetValue(framework, out var target) ? targets[target] : null;
				}
				
				/// <summary>
				///     Whether an assembly is in no package folder any more. Unknown counts as still
				///     there: "gone" changes the advice from a reload to a restore.
				/// </summary>
				public bool IsGone(Assembly assembly)
				{
					if(packageFolders.Count == 0)
						
						return false;
					
					try {
						
						foreach(var folder in packageFolders)
							if(File.Exists(Path.Combine(folder, assembly.PackagePath, assembly.Asset)))
								return false;
						
						return true;
					}
					catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or ArgumentException) {
						
						return false;
					}
				}
				
				static bool Object(JsonElement element, string name, out JsonElement value) =>
					element.TryGetProperty(name, out value) && value.ValueKind == JsonValueKind.Object;
				
				static bool String(JsonElement element, string name, out string value)
				{
					value = element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
						? property.GetString() ?? ""
						: "";
					
					return value.Length > 0;
				}
			}
		}
	}
}
