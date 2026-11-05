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
	///     a link to <c>Lib/</c> itself is the same project (#384). MSBuild also names the project after the
	///     path — <c>$(MSBuildProjectName)</c>, and with it the assembly and the files under <c>obj/</c> — so
	///     a link <c>Lib/Alias.csproj</c> beside <c>Lib.csproj</c> is another project too: <see cref="Name"/>
	///     is the file name of the path, not of the file it leads to (#386). An 8.3 short name counts as its
	///     long name: a matched project is built under the workspace's own path, never the short spelling.
	///     The name is compared within its directory, by that directory's case rule (#391).
	/// </summary>
	public readonly record struct ProjectIdentity(string File, string Directory, string Name)
	{
		public bool Matches(ProjectIdentity other)
			=> SecurityBoundary.PathComparer.Equals(File, other.File)
			&& SecurityBoundary.PathComparer.Equals(Directory, other.Directory)
			&& SecurityBoundary.PathComparer.Equals(Path.Join(Directory, Name), Path.Join(Directory, other.Name))
		;
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
			&& Path.GetFullPath(projectFile) is var fullPath
			&& Path.GetDirectoryName(fullPath) is { } directory
			&& FinalPath(directory) is { } finalDirectory
			? new ProjectIdentity(file, finalDirectory, EntryName(fullPath))
			: null
	;
	
	/// <summary>
	///     The file name <paramref name="fullPath"/> gives its own entry — the name of a link, not of the
	///     file it leads to — as <see cref="LongPath"/> spells it. On macOS in Unicode normalization form C,
	///     because its file systems treat an NFC and an NFD spelling as one entry. Nowhere else: on Linux
	///     <c>Caf&#233;.csproj</c> and <c>Cafe&#769;.csproj</c> are two entries, and two links to one file
	///     beside each other are two projects (#386), so normalizing there would join them (#390 review).
	///     Costs a system call only for a Windows path holding <c>~</c>; cheap enough to filter candidates by.
	/// </summary>
	public static string EntryName(string fullPath)
	{
		var name = Path.GetFileName(LongPath(fullPath));
		
		return OperatingSystem.IsMacOS() ? name.Normalize(NormalizationForm.FormC) : name;
	}
	
	/// <summary>
	///     <paramref name="fullPath"/> with every 8.3 short name expanded, on Windows — <c>SAME~1.CSP</c>
	///     routes by its extension as <c>Same.csproj</c>. Unchanged elsewhere, when the path holds no
	///     <c>~</c> (no short name can), and when the expansion fails (a missing entry): never a guess.
	///     <c>GetLongPathNameW</c> expands each component from its own directory entry and does not follow
	///     a link, so a link keeps its own name (#386).
	/// </summary>
	public static string LongPath(string fullPath)
	{
		if(!OperatingSystem.IsWindows() || !fullPath.Contains('~'))
			
			return fullPath;
		
		var path   = ExtendedLength(fullPath);
		var buffer = new char[512];
		
		for(var attempt = 0; attempt < 2; attempt++) {
			
			var length = GetLongPathName(path, buffer, (uint) buffer.Length);
			
			if(length == 0)
				
				return fullPath;
			
			if(length < buffer.Length) {
				
				var expanded = new string(buffer, 0, (int) length);
				
				// Back to the caller's form when the extended-length prefix was only added for the call.
				return ReferenceEquals(path, fullPath) ? expanded
					: expanded.StartsWith(@"\\?\UNC\", StringComparison.Ordinal) ? @"\\" + expanded[8..]
					: expanded.StartsWith(@"\\?\", StringComparison.Ordinal) ? expanded[4..]
					: expanded
				;
			}
			
			buffer = new char[length];
		}
		
		return fullPath;
	}
	
	// CreateFileW and GetLongPathNameW take a path as given: past MAX_PATH it needs the extended-length prefix.
	static string ExtendedLength(string fullPath)
		=> fullPath.Length < 260 || fullPath.StartsWith(@"\\?\", StringComparison.Ordinal) ? fullPath
		: fullPath.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + fullPath[2..]
		: @"\\?\" + fullPath
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
	
	/// <summary>
	///     Whether the file system matches names in the directory at <paramref name="directory"/> by case:
	///     the directory's own flag on Windows, where any directory can be made case-sensitive
	///     (<c>fsutil file setCaseSensitiveInfo</c>, used with WSL), and the volume's format on macOS
	///     (<c>pathconf(_PC_CASE_SENSITIVE)</c>). <c>null</c> when the directory does not exist. Only a
	///     confirmed answer is ever "ignores case": a Windows file system without the flag (FAT, Windows
	///     before 1803) is one, because there names never differ by case. Anything uncertain — no access,
	///     a sharing violation, a network error, an answer macOS will not give — is "case-sensitive": this
	///     answer decides the workspace boundary, and a wrong "ignores case" lets a sibling directory in.
	/// </summary>
	public static bool? IsCaseSensitiveDirectory(string directory)
	{
		try {
			
			return OperatingSystem.IsWindows() ? WindowsCaseSensitive(directory)
				: OperatingSystem.IsMacOS() ? MacCaseSensitive(directory)
				: true
			;
		}
		catch(Exception ex) when(ex is ArgumentException or NotSupportedException or DllNotFoundException or EntryPointNotFoundException) {
			
			return true;
		}
	}
	
	static bool? WindowsCaseSensitive(string directory)
	{
		using var handle = CreateFile(ExtendedLength(Path.GetFullPath(directory)), FileReadAttributes, FileShare.ReadWrite | FileShare.Delete, 0, FileMode.Open, FileFlagBackupSemantics, 0);
		
		if(handle.IsInvalid)
			
			return Marshal.GetLastPInvokeError() is ErrorFileNotFound or ErrorPathNotFound ? null : true;
		
		if(GetFileInformationByHandleEx(handle, FileCaseSensitiveInfo, out var flags, sizeof(uint)))
			
			return (flags & CaseSensitiveDirectoryFlag) != 0;
		
		// The file system or the Windows version has no such flag: its names never differ by case.
		return Marshal.GetLastPInvokeError() is not (ErrorInvalidFunction or ErrorNotSupported or ErrorInvalidParameter);
	}
	
	static bool? MacCaseSensitive(string directory)
	{
		var answer = PathConf(directory, PcCaseSensitive);
		
		if(answer >= 0)
			
			return answer != 0;
		
		return Marshal.GetLastPInvokeError() is ErrorNoEntry or ErrorNotDirectory ? null : true;
	}
	
	static string? WindowsFinalPath(string path)
	{
		var full = ExtendedLength(Path.GetFullPath(path));
		
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
	
	// FILE_INFO_BY_HANDLE_CLASS.FileCaseSensitiveInfo; its FILE_CASE_SENSITIVE_INFO is one ULONG of flags.
	const int  FileCaseSensitiveInfo      = 23;
	const uint CaseSensitiveDirectoryFlag = 0x1;
	const int  ErrorInvalidFunction       = 1;
	const int  ErrorFileNotFound          = 2;
	const int  ErrorPathNotFound          = 3;
	const int  ErrorNotSupported          = 50;
	const int  ErrorInvalidParameter      = 87;
	
	// Darwin's <unistd.h> and <errno.h>.
	const int PcCaseSensitive   = 11;
	const int ErrorNoEntry      = 2;
	const int ErrorNotDirectory = 20;
	
	// FileShare and FileMode.Open (3, OPEN_EXISTING) have the Win32 values.
	[DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
	static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess, FileShare shareMode, nint securityAttributes, FileMode creationDisposition, uint flagsAndAttributes, nint templateFile);
	
	[DllImport("kernel32.dll", EntryPoint = "GetLongPathNameW", CharSet = CharSet.Unicode, SetLastError = true)]
	static extern uint GetLongPathName(string shortPath, [Out] char[] buffer, uint bufferLength);
	
	[DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
	static extern uint GetFinalPathNameByHandle(SafeFileHandle file, [Out] char[] buffer, uint bufferLength, uint flags);
	
	[DllImport("kernel32.dll", SetLastError = true)]
	static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int informationClass, out uint information, uint bufferSize);
	
	[DllImport("libc", EntryPoint = "pathconf", SetLastError = true)]
	static extern nint PathConf([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int name);
	
	[DllImport("libc", EntryPoint = "realpath", SetLastError = true)]
	static extern nint RealPath([MarshalAs(UnmanagedType.LPUTF8Str)] string path, [Out] byte[] resolved);
}
