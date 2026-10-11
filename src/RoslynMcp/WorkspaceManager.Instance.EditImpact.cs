using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Text;
using System.Text;
using System.Text.RegularExpressions;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Collections.Frozen;
using System.IO.Enumeration;
using System.Xml.Linq;

namespace RoslynMcp;

internal sealed partial class WorkspaceManager
{
	sealed partial class WorkspaceInstance
	{
		/// <summary>
		///     Directories not worth walking or reloading for. Two distinct uses, and the
		///     <c>bin</c>/<c>obj</c> entries mean something different in each:
		///     <list type="bullet">
		///         <item>
		///             Adhoc enumeration skips them entirely when discovering source files.
		///         </item>
		///         <item>
		///             <see cref="RequiresReload"/> consults it first, so an <em>unknown</em> file
		///             under one of them — a <c>.cs</c> build artifact, NuGet's generated
		///             <c>.props</c>/<c>.editorconfig</c> under <c>obj</c> — never forces a full
		///             reload, from the MSBuild flush or from <see cref="InvalidateFile"/>. Generated files under <c>obj</c> that really
		///             are compilation inputs (<c>*.AssemblyInfo.cs</c>,
		///             <c>*.GlobalUsings.g.cs</c>) already have document IDs, never reach that
		///             branch, and keep receiving ordinary text updates.
		///         </item>
		///     </list>
		///     For the watcher's pre-queue filter — which must not suppress those generated
		///     documents — see <see cref="IsNeverCompilationInput"/>.
		/// </summary>
		bool IsExcludedDirectoryName(string name) =>
			ignoreRules.IsExcluded(name);

		/// <summary>
		///     Directories that can never hold a compilation document, so a change under one is
		///     safe to drop before it is even queued. Deliberately excludes <c>bin</c>/<c>obj</c>:
		///     SDK-style projects put generated documents there (<c>*.AssemblyInfo.cs</c>,
		///     <c>*.GlobalUsings.g.cs</c>) which are real compilation inputs and must still receive
		///     text updates. Build output is filtered later instead — only from the decision to
		///     force a full reload. See <see cref="IsUnderExcludedDirectory"/>.
		/// </summary>
		bool IsNeverCompilationInput(string name) =>
			ignoreRules.IsNeverInput(name);

		// Reason literals double as the log vocabulary for "Reload — Flagged (<reason>)".
		const string ReasonNewDocument     = "new document";
		const string ReasonEvaluationInput = "evaluation input";
		const string ReasonAdditionalDoc   = "additional document";
		const string ReasonAnalyzerConfig  = "analyzer config document";
		
		// Files MSBuild reads while evaluating a project — a change to any of them can alter the
		// compilation even though Roslyn never sees them as documents. Directory.Build.props,
		// Directory.Build.targets and Directory.Packages.props are covered by extension.
		static readonly FrozenSet<string> EvaluationInputExtensions = new[] {
			".csproj", ".props", ".targets", ".sln", ".slnx", ".slnf",
			".editorconfig", ".globalconfig", ".ruleset", ".resx",
		}.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
		
		static readonly FrozenSet<string> EvaluationInputNames = new[] {
			"global.json", "nuget.config", "packages.lock.json", "packages.config",
		}.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
		
		/// <summary>
		///     For a path that is <em>not</em> a tracked source document: does a change to it alter the
		///     compilation, or the MSBuild evaluation that produced it? Shared by
		///     <see cref="InvalidateFile"/> (RM-owned writes) and <see cref="FlushMSBuild"/> (watcher
		///     batches) so both apply one rule (#273). Before this predicate existed, InvalidateFile
		///     treated every untracked path as a new compilation input, so editing CHANGELOG.md cost a
		///     full MSBuild reload on the next compilation-needing call.
		///     <para>
		///         Order matters: build output is dropped first, because <c>obj/</c> holds generated
		///         <c>*.nuget.g.props</c> and <c>*.GeneratedMSBuildEditorConfig.editorconfig</c> files
		///         that would otherwise match the evaluation-input rule. Then a new <c>.cs</c>, then
		///         evaluation inputs by extension or name, then anything the loaded projects list as an
		///         additional or analyzer-config document, then any path declared by an
		///         <c>&lt;AdditionalFiles Include="..." /&gt;</c> item even if the file did not exist
		///         when the workspace loaded. Everything else — a <c>.md</c>, a <c>.txt</c>, an
		///         unrelated <c>.json</c> — is a no-op for the workspace.
		///     </para>
		///     <para>
		///         Known limitation: MSBuild <c>EmbeddedResource</c> items are not exposed by
		///         <see cref="Project"/>, and MSBuildWorkspace discards the project instance after the
		///         design-time build, so a resource of arbitrary type cannot be recognized. Resources do
		///         not affect any Roslyn-served result — only <c>dotnet build</c>, which reads disk.
		///         <c>.resx</c> is matched by extension as the common case.
		///     </para>
		///     Paths outside <see cref="rootPath"/> are still classified — Directory.Build.props above a
		///     csproj-mode root is a legitimate input; only the excluded-directory walk is root-relative.
		/// </summary>
		bool RequiresReload(Solution solution, string fullPath, out string reason)
		{
			reason = "";
			
			// A .cs under bin/ or obj/ that is not already a document is a compiler artifact, and a
			// .props there is NuGet's. Neither is source.
			if(IsUnderExcludedDirectory(fullPath, IsExcludedDirectoryName))
				
				return false;
			
			var extension = Path.GetExtension(fullPath);
			
			if(extension.Equals(".cs", StringComparison.OrdinalIgnoreCase)) {
				
				reason = ReasonNewDocument;
				
				return true;
			}
			
			if(EvaluationInputExtensions.Contains(extension) || EvaluationInputNames.Contains(Path.GetFileName(fullPath))) {
				
				reason = ReasonEvaluationInput;
				
				return true;
			}
			
			foreach(var project in solution.Projects) {
				
				if(project.AdditionalDocuments.Any(d => PathEquals(d.FilePath, fullPath))) {
					
					reason = ReasonAdditionalDoc;
					
					return true;
				}
				
				if(project.AnalyzerConfigDocuments.Any(d => PathEquals(d.FilePath, fullPath))) {
					
					reason = ReasonAnalyzerConfig;
					
					return true;
				}
				
				if(MatchesDeclaredAdditionalFile(project, fullPath)) {
					
					reason = ReasonAdditionalDoc;
					
					return true;
				}
			}
			
			return false;
		}
		
		static bool PathEquals(string? candidate, string fullPath) =>
			candidate is not null && string.Equals(candidate, fullPath, StringComparison.OrdinalIgnoreCase);
		
		bool MatchesDeclaredAdditionalFile(Project project, string fullPath)
		{
			if(project.FilePath is null)
				
				return false;
			
			try {
				
				var projectDir = Path.GetDirectoryName(project.FilePath);
				
				if(projectDir is null)
					return false;
				
				var relativePath = NormalizeProjectRelativePath(Path.GetRelativePath(projectDir, fullPath));
				var doc = XDocument.Load(project.FilePath);
				
				foreach(var include in doc
					.Descendants()
					.Where(e => e.Name.LocalName == "AdditionalFiles")
					.SelectMany(e => SplitMsbuildIncludeList((string?) e.Attribute("Include")))) {
					
					if(include.Contains("$(", StringComparison.Ordinal))
						continue;
					
					var normalizedInclude = NormalizeProjectRelativePath(include);
					
					if(normalizedInclude.IndexOfAny(['*', '?']) < 0) {
						
						var includePath = Path.GetFullPath(Path.Combine(projectDir, include));
						
						if(PathEquals(includePath, fullPath))
							return true;
						
						continue;
					}
					
					if(Regex.IsMatch(relativePath, BuildPathGlobRegex(normalizedInclude), RegexOptions.IgnoreCase))
						return true;
				}
			}
			catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or System.Xml.XmlException) { }
			
			return false;
		}
		
		static IEnumerable<string> SplitMsbuildIncludeList(string? include) =>
			(include ?? "")
				.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		
		static string NormalizeProjectRelativePath(string path) =>
			path.Replace('\\', '/');
		
		static string BuildPathGlobRegex(string pattern)
		{
			var sb = new StringBuilder("^");
			var i  = 0;
			
			while(i < pattern.Length) {
				
				if(pattern[i] == '*' && i + 1 < pattern.Length && pattern[i + 1] == '*') {
					
					sb.Append(".*");
					i += 2;
					
					if(i < pattern.Length && (pattern[i] == '/' || pattern[i] == '\\'))
						i++;
				}
				else if(pattern[i] == '*') {
					
					sb.Append(@"[^/\\]*");
					i++;
				}
				else if(pattern[i] == '?') {
					
					sb.Append('.');
					i++;
				}
				else {
					
					sb.Append(Regex.Escape(pattern[i].ToString()));
					i++;
				}
			}
			
			sb.Append('$');
			
			return sb.ToString();
		}

		/// <summary>
		///     What an edit to a given path means for the workspace (#288). The single decision point
		///     behind <see cref="InvalidateFile"/> and <see cref="FlushMSBuild"/>, which share the
		///     classification but not the flagging mechanics — InvalidateFile bumps
		///     <see cref="reloadVersion"/> unconditionally, FlushMSBuild goes through
		///     <see cref="FlagReload"/> with its <c>== 0</c> guard.
		/// </summary>
		enum WorkspaceEditImpact
		{
			/// <summary>Not a compilation or evaluation input — the workspace has nothing to do.</summary>
			None,
			
			/// <summary>Tracked as a source or additional document; apply the new text in place.</summary>
			Incremental,
			
			/// <summary>A compilation/evaluation input that cannot be updated in place — reload.</summary>
			Reload
		}
		
		/// <summary>
		///     Decides how <paramref name="fullPath"/> must be handled, and hands back the resolved
		///     <paramref name="info"/> so the caller can apply text without re-classifying.
		///     <paramref name="reason"/> is meaningful only for <see cref="WorkspaceEditImpact.Reload"/>.
		/// </summary>
		WorkspaceEditImpact ClassifyEditImpact(
			Solution                      solution,
			string                        fullPath,
			out WorkspaceTextDocumentInfo info,
			out string                    reason)
		{
			info   = WorkspaceTextDocumentInfo.Resolve(solution, fullPath);
			reason = "";
			
			if(info.SupportsIncrementalTextChange)
				
				return WorkspaceEditImpact.Incremental;
			
			return RequiresReload(solution, fullPath, out reason)
				? WorkspaceEditImpact.Reload
				: WorkspaceEditImpact.None;
		}

		/// <summary>
		///     True when any directory segment of <paramref name="fullPath"/> below
		///     <see cref="rootPath"/> matches <paramref name="excluded"/>. Name-based only — no
		///     <c>FileInfo</c> stat, because this runs on every FileSystemWatcher event.
		/// </summary>
		bool IsUnderExcludedDirectory(string fullPath, Func<string, bool> excluded)
		{
			string relative;

			try {
				relative = Path.GetRelativePath(rootPath, fullPath);
			}
			catch(ArgumentException) {
				return false;
			}

			var segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
			;

			// Outside the root entirely — not ours to filter. The first segment must equal ".."
			// exactly: a prefix test would also catch a directory legitimately named "..config",
			// which is inside the root and must still be subject to the exclusions below. A rooted
			// result means GetRelativePath could not relativize at all (different volume).
			if(Path.IsPathRooted(relative) || segments[0] == "..")

				return false;

			// Last segment is the filename, not a directory.
			for(var i = 0; i < segments.Length - 1; i++)

				if(excluded(segments[i]))

					return true;

			return false;
		}
	}
}
