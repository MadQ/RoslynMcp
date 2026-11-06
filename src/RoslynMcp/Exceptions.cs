namespace RoslynMcp.Tools;

/// <summary>
///     Thrown when no .csproj file can be found in or above the specified path.
/// </summary>
internal sealed class ProjectNotFoundException : Exception
{
	public string SearchPath { get; }
	
	public ProjectNotFoundException(string path)
		: base($"No .csproj file found in or above: {path}")
	{
		SearchPath = path;
	}
}

/// <summary>
///     Thrown when multiple .csproj files are found in a directory and disambiguation is required.
/// </summary>
internal sealed class MultipleProjectsFoundException : Exception
{
	public string   Directory    { get; }
	public string[] ProjectFiles { get; }
	
	public MultipleProjectsFoundException(string directory, string[] projectFiles)
		: base($"Multiple .csproj files found in {directory}. Specify which one to use: {string.Join(", ", projectFiles.Select(Path.GetFileName))}")
	{
		Directory    = directory;
		ProjectFiles = projectFiles;
	}
}

/// <summary>
///     Thrown when a tool that works on one project is given a path that names no single project of
///     the workspace it loaded: a solution, a <c>.csproj</c> the workspace does not contain, or a
///     file name that several of its projects share (#376). Answering for the workspace's first
///     project instead gave wrong results with nothing to say so — a symbol that exists was "not
///     found", because it was looked for in another project.
/// </summary>
internal sealed class ProjectNotInWorkspaceException : Exception
{
	public string   RequestedPath { get; }
	public string[] ProjectFiles  { get; }
	public bool     IsSolution    { get; }
	
	public ProjectNotInWorkspaceException(string requestedPath, string[] projectFiles, bool isSolution)
		: base(isSolution
			? $"'{System.IO.Path.GetFileName(requestedPath)}' is a solution with {projectFiles.Length} projects, and this tool works on one project. Pass one of its .csproj files."
			: $"'{requestedPath}' is not one of the {projectFiles.Length} projects of the workspace it belongs to. Pass one of its .csproj files.")
	{
		RequestedPath = requestedPath;
		ProjectFiles  = projectFiles;
		IsSolution    = isSolution;
	}
}

/// <summary>
///     Thrown when the provided project path is invalid or inaccessible.
/// </summary>
internal sealed class InvalidProjectPathException : Exception
{
	public string Path { get; }
	
	public InvalidProjectPathException(string path, string reason)
		: base($"Invalid project path '{path}': {reason}")
	{
		Path = path;
	}
}

/// <summary>
///     Thrown when <c>projectPath</c> is omitted and no session default workspace can be derived from the
///     configured root (<c>--root</c> / <c>ROSLYNMCP_ROOT</c>) or the process CWD. Derives from
///     <see cref="ArgumentException"/> so every existing resolution guard already maps it to the structured
///     <c>missing_project_path</c> error and excludes it from transient-fault retries.
/// </summary>
internal sealed class NoDefaultWorkspaceException : ArgumentException
{
	public string Root { get; }
	
	public NoDefaultWorkspaceException(string root, string source, string reason)
		: base($"projectPath was omitted and no default workspace could be determined from the {source} '{root}': {reason}.")
	{
		Root = root;
	}
}

/// <summary>
///     Thrown when a bare filename matches SyntaxTrees in more than one cached MSBuild workspace.
///     The agent must disambiguate by providing an explicit .csproj path.
/// </summary>
internal sealed class AmbiguousFileException : Exception
{
	public string   FileName     { get; }
	public string[] CsprojPaths  { get; }
	
	public AmbiguousFileException(string fileName, string[] csprojPaths)
		: base($"'{fileName}' exists in {csprojPaths.Length} loaded projects — specify which .csproj to use.")
	{
		FileName    = fileName;
		CsprojPaths = csprojPaths;
	}
}

/// <summary>
///     Thrown when a workspace load runs out of time: its MSBuild design-time build took longer than the load
///     timeout, or it waited that long for <see cref="MSBuildGate"/> behind another load or a <c>dotnet</c>
///     command (#379). Not a transient fault: another attempt straight away would only wait the whole timeout
///     again, so the tools do not retry it. Still an <see cref="InvalidOperationException"/>, as a failed load is.
/// </summary>
internal sealed class WorkspaceLoadTimeoutException(string message) : InvalidOperationException(message);
