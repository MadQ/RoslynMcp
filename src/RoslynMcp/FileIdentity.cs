using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace RoslynMcp;

/// <summary>
///     Whether two paths name the same file (compare their <see cref="FinalPath"/>s), decided by the operating system rather than by the text of the
///     paths. <see cref="Path.GetFullPath"/> normalizes text only: a substituted drive, an 8.3 short name or a
///     symbolic link names a file under another path, and a file name alone cannot tell a second name of one
///     file from a different file that happens to share it (#384). The OS's final path can:
///     <c>GetFinalPathNameByHandle</c> on Windows, <c>realpath</c> elsewhere.
/// </summary>
internal static class FileIdentity
{
	/// <summary>
	///     The operating system's canonical path of an existing file — every link, substituted drive and short
	///     name resolved — or <c>null</c> when it cannot be determined (a missing file, no access): never a
	///     guess. Two paths are the same file when their final paths are equal under
	///     <see cref="SecurityBoundary.PathComparer"/>. On Windows the result keeps the <c>\\?\</c> prefix; it
	///     is meant for comparison, not for display.
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
	
	static string? WindowsFinalPath(string path)
	{
		// Read access with every share mode: the file is only identified, never read, and a project file
		// open in an editor or a build must not make it unidentifiable.
		using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
		
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
	
	[DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
	static extern uint GetFinalPathNameByHandle(SafeFileHandle file, [Out] char[] buffer, uint bufferLength, uint flags);
	
	[DllImport("libc", EntryPoint = "realpath", SetLastError = true)]
	static extern nint RealPath([MarshalAs(UnmanagedType.LPUTF8Str)] string path, [Out] byte[] resolved);
}
