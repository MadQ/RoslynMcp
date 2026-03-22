using System.ComponentModel;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using ModelContextProtocol.Server;

namespace RoslynMcp.Tools;

[McpServerToolType]
internal sealed class ProjectInfoTool(WorkspaceManager workspace)
{
    // Matches a TFM segment in a path like ...\net8.0\... or .../net11.0/...
    private static readonly Regex TfmPattern = new(@"[/\\](net\d+\.\d+(?:-\w+)?)[/\\]", RegexOptions.Compiled);

    // Matches NuGet package paths: ...\.nuget\packages\<name>\<version>\...
    private static readonly Regex NuGetPattern = new(@"[/\\]packages[/\\]([^/\\]+)[/\\]([^/\\]+)[/\\]", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    [McpServerTool, Description(
        "Returns metadata about the loaded project: name, assembly name, target framework, language version, " +
        "output kind, nullable setting, NuGet package references, and additional files. " +
        "Use this to understand project configuration without reading the .csproj directly.")]
    public object GetProjectInfo()
    {
        var project = workspace.GetProject();

        var compOpts  = project.CompilationOptions  as CSharpCompilationOptions;
        var parseOpts = project.ParseOptions         as CSharpParseOptions;

        var tfm          = InferTfm(project);
        var outputKind   = compOpts?.OutputKind.ToString() ?? "Unknown";
        var nullable     = compOpts?.NullableContextOptions.ToString() ?? "Unknown";
        var langVersion  = parseOpts?.LanguageVersion.ToDisplayString() ?? "Unknown";
        var packages     = ExtractPackages(project.MetadataReferences);
        var extraFiles   = project.AdditionalDocuments
            .Select(d => Path.GetRelativePath(workspace.RootPath, d.FilePath ?? d.Name))
            .Order()
            .ToArray()
        ;

        return new {
            name            = project.Name,
            assembly_name   = project.AssemblyName,
            file_path       = project.FilePath is not null
                ? Path.GetRelativePath(workspace.RootPath, project.FilePath)
                : null,
            target_framework    = tfm,
            language_version    = langVersion,
            output_kind         = outputKind,
            nullable            = nullable,
            is_msbuild_workspace = workspace.IsMSBuild,
            package_references  = packages,
            additional_files    = extraFiles
        };
    }

    private static string? InferTfm(Project project)
    {
        // Try output path first — most reliable, e.g. bin\Debug\net11.0\RoslynMcp.exe
        var candidate = project.OutputFilePath ?? project.FilePath;

        if(candidate is not null) {
            var m = TfmPattern.Match(candidate);

            if(m.Success)
                return m.Groups[1].Value;
        }

        // Fall back: scan metadata reference paths for a consistent TFM segment.
        foreach(var r in project.MetadataReferences) {
            if(r.Display is null)
                continue;

            var m = TfmPattern.Match(r.Display);

            if(m.Success)
                return m.Groups[1].Value;
        }

        return null;
    }

    private static PackageRef[] ExtractPackages(IReadOnlyList<MetadataReference> references)
    {
        // NuGet packages end up in the global packages cache — parse name+version from the path.
        // Framework assemblies (from dotnet/shared/...) are not NuGet packages and are skipped.
        return references
            .Select(r => r.Display)
            .Where(p => p is not null)
            .Select(p => {
                var m = NuGetPattern.Match(p!);

                return m.Success
                    ? new PackageRef(m.Groups[1].Value, m.Groups[2].Value)
                    : null;
            })
            .Where(p => p is not null)
            .Select(p => p!)
            .DistinctBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray()
        ;
    }

    private sealed record PackageRef(string Name, string Version);
}
