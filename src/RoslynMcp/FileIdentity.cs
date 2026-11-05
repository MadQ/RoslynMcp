using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace RoslynMcp;

/// <summary>
///     Whether two paths name the same file, directory or project, decided by the operating system rather
///     than by the text of the paths. <see cref="Path.GetFullPath"/> normalizes text only: a substituted
///     drive, an 8.3 short name or a symbolic link names an entry under another path, and a file name alone
///     cannot tell a second name of one file from a different file that happens to share it (#384). The OS's
///     final path can: <c>GetFinalPathNameByHandle</c> on Windows, <c>realpath</c> elsewhere.
/// </summary>
internal static class FileIdentity
{
	/// <summary>
	///     What makes a project the same project: its file, and the directory MSBuild evaluates it from —
	///     both as final paths. MSBuild evaluates a project from the directory of the path it is given, not
	///     of the file a link leads to, so a link to <c>Lib/Lib.csproj</c> placed in <c>Other/</c> is another
	///     project (its globs, imports and outputs are <c>Other/</c>'s), while the same file reached through
	///     a link to <c>Lib/</c> itself is the same project (#384).
	/// </summary>
	public readonly record struct ProjectIdentity(string File, string Directory)
	{
		public bool Matches(ProjectIdentity other)
			=> SecurityBoundary.PathComparer.Equals(File, other.File) && SecurityBoundary.PathComparer.Equals(Directory, other.Directory);
	}
	
	/// <summary>
	///     The operating system's canonical path of an existing file or directory — every link, substituted
	///     drive and short name resolved — or <c>null</c> when it cannot be determined (a missing entry, no
	///     access): never a guess. Compare final paths under <see cref="SecurityBoundary.PathComparer"/>. On
	///     Windows the result keeps the <c>\\?\</c> prefix; it is meant for comparison, not for display.
	/// </summary>
	public static string? FinalPath(string path)
	{
		try {
			
			return OperatingSystem.IsWindows() ? WindowsFinalPath(path) : UnixFinalPath(path);
		}
		catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or DllNotFoundException or EntryPointNotFoundException) {
			
			return null;
		}
	}
	
	/// <summary>
	///     The <see cref="ProjectIdentity"/> of the project file at <paramref name="projectFile"/>, or
	///     <c>null</c> when either final path cannot be determined. Costs two opens — compare the text of
	///     the paths first.
	/// </summary>
	public static ProjectIdentity? OfProject(string projectFile)
		=> FinalPath(projectFile) is { } file
			&& Path.GetDirectoryName(Path.GetFullPath(projectFile)) is { } directory
			&& FinalPath(directory) is { } finalDirectory
			? new ProjectIdentity(file, finalDirectory)
			: null
	;
	
	/// <summary>
	///     Whether two project file paths name the same project (<see cref="ProjectIdentity"/>).
	///     <c>false</c> when either cannot be identified: an unknown is never the same project.
	/// </summary>
	public static bool SameProject(string first, string second)
		=> OfProject(first) is { } firstIdentity && OfProject(second) is { } secondIdentity && firstIdentity.Matches(secondIdentity);
	
	/// <summary>
	///     Whether two paths name one existing file or directory. <c>false</c> when either final path
	///     cannot be determined.
	/// </summary>
	public static bool SamePath(string first, string second)
		=> FinalPath(first) is { } firstFinal && FinalPath(second) is { } secondFinal && SecurityBoundary.PathComparer.Equals(firstFinal, secondFinal);
	
	static string? WindowsFinalPath(string path)
	{
		var full = Path.GetFullPath(path);
		
		// CreateFileW takes the path as given: past MAX_PATH it needs the extended-length prefix.
		if(full.Length >= 260 && !full.StartsWith(@"\\?\", StringComparison.Ordinal))
			
			full = full.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + full[2..] : @"\\?\" + full;
		
		// Attributes only, with every share mode: the entry is identified, never read, so a project file
		// open in an editor or a build stays identifiable. Backup semantics open a directory as well as a
		// file, which File.OpenHandle cannot.
		using var handle = CreateFile(full, FileReadAttributes, FileShare.ReadWrite | FileShare.Delete, 0, FileMode.Open, FileFlagBackupSemantics, 0);
		
		if(handle.IsInvalid)
			
			return null;
		
		var buffer = new char[512];
		
		for(var attempt = 0; attempt < 2; attempt++) {
			
			var length = GetFinalPathNameByHandle(handle, buffer, (uint) buffer.Length, 0);
			
			if(length == 0)
				
				return null;
			
			// A result that does not fit returns the size it needs, terminator included.
			if(length < buffer.Length)
				
				return new string(buffer, 0, (int) length);
			
			buffer = new char[length];
		}
		
		return null;
	}
	
	static string? UnixFinalPath(string path)
	{
		// PATH_MAX is 4096 on Linux and 1024 on macOS; realpath never writes more than PATH_MAX bytes.
		var buffer = new byte[4096];
		
		if(RealPath(path, buffer) == 0)
			
			return null;
		
		var end = Array.IndexOf(buffer, (byte) 0);
		
		return Encoding.UTF8.GetString(buffer, 0, end < 0 ? buffer.Length : end);
	}
	
	const uint FileReadAttributes      = 0x80;
	const uint FileFlagBackupSemantics = 0x02000000;
	
	// FileShare and FileMode.Open (3, OPEN_EXISTING) have the Win32 values.
	[DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
	static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess, FileShare shareMode, nint securityAttributes, FileMode creationDisposition, uint flagsAndAttributes, nint templateFile);
	
	[DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
	static extern uint GetFinalPathNameByHandle(SafeFileHandle file, [Out] char[] buffer, uint bufferLength, uint flags);
	
	[DllImport("libc", EntryPoint = "realpath", SetLastError = true)]
	static extern nint RealPath([MarshalAs(UnmanagedType.LPUTF8Str)] string path, [Out] byte[] resolved);
}
