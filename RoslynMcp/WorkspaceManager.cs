using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Text;

namespace RoslynMcp;

/// <summary>
///     Loads a C# project into Roslyn and keeps the Compilation warm.
///     Detects .csproj files and uses MSBuildWorkspace (full type resolution)
///     when available, falling back to AdhocWorkspace (.cs files only) otherwise.
///     Thread-safe via a reader-writer lock.
/// </summary>
internal sealed class WorkspaceManager : IDisposable
{
    private readonly Workspace            workspace;
    private readonly ProjectId            projectId;
    private readonly string               rootPath;
    private readonly string?              csprojPath;
    private readonly bool                 isMSBuild;
    private readonly ReaderWriterLockSlim rwLock = new();
    private readonly FileSystemWatcher?   watcher;

    // The current compilation — replaced atomically on each file change.
    private Compilation? compilation;

    static WorkspaceManager()
    {
        // Register MSBuild instance once per process — required for MSBuildWorkspace.
        if(MSBuildLocator.CanRegister)
            MSBuildLocator.RegisterDefaults();
    }

    public WorkspaceManager(string rootPath)
    {
        this.rootPath = Path.GetFullPath(rootPath);

        // Detect .csproj file: if found, use MSBuildWorkspace; otherwise AdhocWorkspace.
        var csprojFiles = Directory.GetFiles(this.rootPath, "*.csproj", SearchOption.AllDirectories);

		if(csprojFiles.Length > 0) {
			(workspace, projectId) = LoadMSBuildWorkspace(csprojFiles[0]);
			csprojPath = csprojFiles[0];
			isMSBuild  = true;
			// MSBuildWorkspace watches files via Roslyn's internal mechanisms — no manual watcher needed.
		}
		else {
			(workspace, projectId) = LoadAdhocWorkspace();
            isMSBuild = false;

            // AdhocWorkspace requires manual file watching.
            watcher = new FileSystemWatcher(this.rootPath, "*.cs") {
                IncludeSubdirectories = true,
                NotifyFilter          = NotifyFilters.LastWrite | NotifyFilters.FileName
            };

            watcher.Changed += OnFileChanged;
            watcher.Created += OnFileChanged;
            watcher.Deleted += OnFileDeleted;
            watcher.Renamed += OnFileRenamed;
            watcher.EnableRaisingEvents = true;
		}
	}

	public string  RootPath   => rootPath;
	public bool    IsMSBuild  => isMSBuild;
	public string? CsprojPath => csprojPath;

	public Solution GetSolution() => workspace.CurrentSolution;

	/// <summary>Returns the primary project loaded by this manager.</summary>
	public Project GetProject() => workspace.CurrentSolution.GetProject(projectId)!;

    /// <summary>
    ///     Returns the current Compilation, building it if not yet warm.
    ///     Never returns null after construction.
    /// </summary>
    public Compilation GetCompilation()
    {
        rwLock.EnterReadLock();

        try {
			if(compilation is not null)
                return compilation;
		}
		finally {
            rwLock.ExitReadLock();
        }

        return RebuildCompilation();
	}

	public void Dispose()
    {
        watcher?.Dispose();
        rwLock.Dispose();
        workspace.Dispose();
    }

    // ── Private ───────────────────────────────────────────────────────────────

    private (Workspace workspace, ProjectId projectId) LoadMSBuildWorkspace(string csprojPath)
    {
        var msbuildWorkspace = MSBuildWorkspace.Create();
        var project = msbuildWorkspace.OpenProjectAsync(csprojPath).GetAwaiter().GetResult();

        return (msbuildWorkspace, project.Id);
    }

    private (Workspace workspace, ProjectId projectId) LoadAdhocWorkspace()
    {
        var adhocWorkspace = new AdhocWorkspace();

        var projectInfo = ProjectInfo.Create(
            id:             ProjectId.CreateNewId(),
            version:        VersionStamp.Create(),
            name:           "Target",
            assemblyName:   "Target",
            language:       LanguageNames.CSharp,
            compilationOptions: new CSharpCompilationOptions(OutputKind.ConsoleApplication)
                .WithNullableContextOptions(NullableContextOptions.Enable),
            parseOptions: new CSharpParseOptions(
                languageVersion: LanguageVersion.Preview,
                preprocessorSymbols: ["DEBUG"]
            )
        );

        adhocWorkspace.AddProject(projectInfo);

        LoadAllFiles(adhocWorkspace, projectInfo.Id);

        return (adhocWorkspace, projectInfo.Id);
    }

    private void LoadAllFiles(AdhocWorkspace adhocWorkspace, ProjectId pid)
    {
        foreach(var path in Directory.EnumerateFiles(rootPath, "*.cs", SearchOption.AllDirectories))
            AddOrUpdateDocument(adhocWorkspace, pid, path);
    }

    private void AddOrUpdateDocument(AdhocWorkspace adhocWorkspace, ProjectId pid, string path)
    {
        var text = SourceText.From(File.ReadAllText(path));
        var name = Path.GetRelativePath(rootPath, path);

        var project  = adhocWorkspace.CurrentSolution.GetProject(pid)!;
        var existing = project.Documents.FirstOrDefault(d => d.Name == name);

        Solution newSolution;

        if(existing is not null)
            newSolution = adhocWorkspace.CurrentSolution.WithDocumentText(existing.Id, text);
        else
            newSolution = adhocWorkspace.CurrentSolution.AddDocument(
                DocumentId.CreateNewId(pid), name, text, filePath: path
            );

        adhocWorkspace.TryApplyChanges(newSolution);
        InvalidateCompilation();
    }

    private void RemoveDocument(AdhocWorkspace adhocWorkspace, ProjectId pid, string path)
    {
        var name     = Path.GetRelativePath(rootPath, path);
        var project  = adhocWorkspace.CurrentSolution.GetProject(pid)!;
        var existing = project.Documents.FirstOrDefault(d => d.Name == name);

        if(existing is null)
            return;

        adhocWorkspace.TryApplyChanges(adhocWorkspace.CurrentSolution.RemoveDocument(existing.Id));
        InvalidateCompilation();
    }

    private Compilation RebuildCompilation()
    {
        rwLock.EnterWriteLock();

        try {
			// Double-checked: another thread may have rebuilt while we waited.
			if(compilation is not null)
                return compilation;

            var project = workspace.CurrentSolution.GetProject(projectId)!;

            // GetCompilationAsync is the correct async path; block here because
            // tool calls arrive on a thread pool thread without a live SynchronizationContext.
            compilation = project.GetCompilationAsync().GetAwaiter().GetResult()
                ?? CSharpCompilation.Create("empty");

            return compilation;
		}
		finally {
            rwLock.ExitWriteLock();
        }
	}

	private void InvalidateCompilation()
    {
        rwLock.EnterWriteLock();

        try {
			compilation = null;
		}
		finally {
            rwLock.ExitWriteLock();
        }
	}

	private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        if(isMSBuild || workspace is not AdhocWorkspace adhoc)
            return;

        try {
			AddOrUpdateDocument(adhoc, projectId, e.FullPath);
		}
		catch {
            // File may be locked mid-write; the next change event will catch it.
        }
	}

	private void OnFileDeleted(object sender, FileSystemEventArgs e)
    {
        if(isMSBuild || workspace is not AdhocWorkspace adhoc)
            return;

        RemoveDocument(adhoc, projectId, e.FullPath);
    }

    private void OnFileRenamed(object sender, RenamedEventArgs e)
    {
        if(isMSBuild || workspace is not AdhocWorkspace adhoc)
            return;

        RemoveDocument(adhoc, projectId, e.OldFullPath);

        try {
			AddOrUpdateDocument(adhoc, projectId, e.FullPath);
		}
		catch {
        }
	}
}
