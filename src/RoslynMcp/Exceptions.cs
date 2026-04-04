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
