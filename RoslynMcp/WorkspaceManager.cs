using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace RoslynMcp;

/// <summary>
///     Loads all .cs files under a directory into an AdhocWorkspace and keeps the
///     Compilation warm via a FileSystemWatcher. Thread-safe via a reader-writer lock.
///     AdhocWorkspace rather than MSBuildWorkspace: single-project, no NuGet type
///     resolution needed, starts in &lt;100 ms, no MSBuild assembly fragility.
/// </summary>
internal sealed class WorkspaceManager : IDisposable
{
    private readonly AdhocWorkspace   workspace;
    private readonly ProjectId        projectId;
    private readonly string           rootPath;
    private readonly ReaderWriterLockSlim rwLock = new();
    private readonly FileSystemWatcher    watcher;

    // The current compilation — replaced atomically on each file change.
    private Compilation? compilation;

    public WorkspaceManager(string rootPath)
    {
        this.rootPath = Path.GetFullPath(rootPath);

        workspace = new AdhocWorkspace();

        var projectInfo = ProjectInfo.Create(
            id:             ProjectId.CreateNewId(),
            version:        VersionStamp.Create(),
            name:           "Target",
            assemblyName:   "Target",
            language:       LanguageNames.CSharp,
            compilationOptions: new CSharpCompilationOptions(OutputKind.WindowsApplication)
                .WithNullableContextOptions(NullableContextOptions.Enable),
            parseOptions: new CSharpParseOptions(
                languageVersion: LanguageVersion.Preview,
                preprocessorSymbols: ["DEBUG"]
            )
        );

        workspace.AddProject(projectInfo);
        projectId = projectInfo.Id;

        LoadAllFiles();

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

    public string RootPath => rootPath;

    public Solution GetSolution() => workspace.CurrentSolution;

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
        watcher.Dispose();
        rwLock.Dispose();
        workspace.Dispose();
    }

    // ── Private ───────────────────────────────────────────────────────────────

    private void LoadAllFiles()
    {
        foreach(var path in Directory.EnumerateFiles(rootPath, "*.cs", SearchOption.AllDirectories))
            AddOrUpdateDocument(path);
    }

    private void AddOrUpdateDocument(string path)
    {
        var text = SourceText.From(File.ReadAllText(path));
        var name = Path.GetRelativePath(rootPath, path);

        var project  = workspace.CurrentSolution.GetProject(projectId)!;
        var existing = project.Documents.FirstOrDefault(d => d.Name == name);

        Solution newSolution;

        if(existing is not null)
            newSolution = workspace.CurrentSolution.WithDocumentText(existing.Id, text);
        else
            newSolution = workspace.CurrentSolution.AddDocument(
                DocumentId.CreateNewId(projectId), name, text, filePath: path
            );

        workspace.TryApplyChanges(newSolution);
        InvalidateCompilation();
    }

    private void RemoveDocument(string path)
    {
        var name     = Path.GetRelativePath(rootPath, path);
        var project  = workspace.CurrentSolution.GetProject(projectId)!;
        var existing = project.Documents.FirstOrDefault(d => d.Name == name);

        if(existing is null)
            return;

        workspace.TryApplyChanges(workspace.CurrentSolution.RemoveDocument(existing.Id));
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
        try {
            AddOrUpdateDocument(e.FullPath);
        }
        catch {
            // File may be locked mid-write; the next change event will catch it.
        }
    }

    private void OnFileDeleted(object sender, FileSystemEventArgs e)
        => RemoveDocument(e.FullPath);

    private void OnFileRenamed(object sender, RenamedEventArgs e)
    {
        RemoveDocument(e.OldFullPath);

        try {
            AddOrUpdateDocument(e.FullPath);
        }
        catch {
        }
    }
}
