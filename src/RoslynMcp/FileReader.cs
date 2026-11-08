using System.Text;

namespace RoslynMcp;

/// <summary>
///     A read refused because the file holds more than the caller's limit. An
///     <see cref="IOException"/>, so every guard that already catches a failed read catches this
///     one too.
/// </summary>
internal sealed class FileTooLargeException : IOException
{
	internal FileTooLargeException(string path, long limit)
		: base($"'{Path.GetFileName(path)}' is larger than {FileReader.Describe(limit)}, the limit for this read.")
	{
		FilePath = path;
		Limit    = limit;
	}
	
	internal string FilePath { get; }
	internal long   Limit    { get; }
}

/// <summary>
///     Centralised entry point for whole-file reads in RoslynMcp, the counterpart of
///     <see cref="FileWriter"/>: every read takes a limit, and a file that holds more than it is
///     refused with <see cref="FileTooLargeException"/> instead of being read into memory (#230).
/// </summary>
/// <remarks>
///     <para>
///         The limit is enforced on the bytes actually read, never on <see cref="FileInfo.Length"/>
///         alone. A device reports no length (<c>/dev/zero</c> is 0 bytes long and never ends), a
///         file can grow while it is read, and a file with no newline at all would make a line
///         reader build one endless string. The length, where the stream has one, only refuses an
///         oversized file early and sizes the buffer.
///     </para>
///     <para>
///         Symbolic links and other reparse points are followed like any other path, on purpose:
///         linked dotfiles and linked source folders are ordinary, and the workspace boundary
///         (<see cref="SecurityBoundary"/>) already decides which paths a tool may name. What is
///         not guarded against is a link to a named pipe: on Linux and macOS opening a FIFO waits
///         in <c>open()</c> for a writer, before any read could be bounded. A repository able to
///         plant one already controls what the server is run on; that is accepted.
///     </para>
///     Decoding matches <see cref="File.ReadAllText(string)"/>: a byte order mark selects UTF-8,
///     UTF-16 or UTF-32, and a file without one is read as UTF-8.
/// </remarks>
internal static class FileReader
{
	/// <summary>
	///     The limit for small files the server reads to learn something — git metadata, a run
	///     counter, a <c>.gitignore</c>. A file past it is not a config file.
	/// </summary>
	internal const long ConfigLimit = 1024 * 1024;
	
	/// <summary>
	///     The limit for files read as content: a file an edit tool changes or a tool returns, a
	///     backup snapshot, a project or solution file, an agent's own config file (Claude Code's
	///     <c>~/.claude.json</c> keeps a history and runs to several megabytes).
	/// </summary>
	internal const long DocumentLimit = 64 * 1024 * 1024;
	
	/// <summary>
	///     Opens <paramref name="path"/> for a streaming read that throws
	///     <see cref="FileTooLargeException"/> once more than <paramref name="maxBytes"/> bytes have
	///     come out of it. For readers that stop early (the first lines of a project file) or parse
	///     straight from a stream.
	/// </summary>
	internal static Stream OpenRead(string path, long maxBytes) => BoundedStream.Open(path, maxBytes, FileOptions.None, ReadSharing);
	
	/// <summary>
	///     Opens <paramref name="path"/> for a read with no limit, shared like every other read
	///     here. Only for reads that are unbounded on purpose: a text document the workspace reads
	///     back from disk (Roslyn holds it whole anyway, so a limit would save nothing) and a hash,
	///     which streams and holds no copy. Everything else takes a limit.
	/// </summary>
	internal static FileStream OpenUnbounded(string path) => OpenFile(path, FileOptions.None, ReadSharing);
	
	/// <summary>
	///     Opens <paramref name="path"/> to count its lines (<c>roslyn_get_line_count</c>): an
	///     asynchronous read with <see cref="ObservingSharing"/>, so a log another program is
	///     still writing can be counted. Its own method, with no sharing parameter, so that
	///     <see cref="ObservingSharing"/> cannot spread to a read whose content comes out.
	///     No limit: the count streams through a fixed buffer and keeps nothing. The cost is that
	///     a file without an end — a link to a device that never stops producing bytes — keeps
	///     it counting until the call is given up; it was so before #230 and #349 as well.
	/// </summary>
	internal static FileStream OpenForCount(string path) => OpenFile(path, FileOptions.Asynchronous | FileOptions.SequentialScan, ObservingSharing);
	
	/// <summary>
	///     Parses <paramref name="path"/> as XML — a project or solution file — read with
	///     <see cref="DocumentLimit"/> and <see cref="ReadSharing"/>. <c>XDocument.Load(path)</c>
	///     has no limit and opens the file without sharing it for deleting, which blocks the
	///     atomic replace of a project file while it is held (#349).
	/// </summary>
	internal static System.Xml.Linq.XDocument LoadXml(string path)
	{
		using var stream = OpenRead(path, DocumentLimit);
		
		return System.Xml.Linq.XDocument.Load(stream);
	}
	
	/// <summary>
	///     How every read in the server shares the file: with readers and deletes, not with
	///     writers (#349).
	///     <para>
	///         <see cref="FileShare.Delete"/> is what the server's own atomic replace needs: it
	///         renames a temp file over the target, and on Windows that rename fails against any
	///         open handle that does not share for deleting. A read in progress used to make the
	///         replace fail (it got through on the write retry). The read keeps the old content
	///         to its end.
	///     </para>
	///     <para>
	///         Writers are kept out on purpose. A read that shares for writing sees a file another
	///         program is saving in place half-done, and several readers write what they read
	///         back to disk — the watcher flush, the edit tools, a backup restored later — so a
	///         partial read becomes a cut-short file. Kept out, a writer that is mid-save makes
	///         the read fail instead, and one that tries to open the file during the read is
	///         refused (Windows does not make it wait), as it always was; the read takes
	///         milliseconds. Windows only: on Linux and macOS sharing is advisory, so there
	///         neither the conflicts nor this protection exist (#403).
	///     </para>
	///     <para>
	///         Sharing for deleting also lets someone else delete the file during a read. Where
	///         deletes are not POSIX-style (FAT, some network shares, Windows before 10 1903), the
	///         name stays taken until the read closes, so a delete-and-recreate can fail for those
	///         milliseconds. Accepted: without it the server's own replace fails instead.
	///     </para>
	///     A read that only counts can share with writers too: <see cref="ObservingSharing"/>.
	/// </summary>
	internal const FileShare ReadSharing = FileShare.Read | FileShare.Delete;
	
	/// <summary>
	///     How a read shares the file when nothing is ever built from what it reads — a count,
	///     not content: with readers, writers and deletes. <c>roslyn_get_line_count</c> uses it,
	///     so a log another program is still writing can be counted (see
	///     <see cref="OpenForCount"/>); a file caught mid-save gives a count that is out of date
	///     a moment later. Not for any read whose content
	///     comes out: <c>roslyn_read_file</c>'s output is copied into edits by the agent, so it
	///     is written back as surely as a workspace document, a backup or an edit's own read.
	///     Those use <see cref="ReadSharing"/>.
	/// </summary>
	internal const FileShare ObservingSharing = FileShare.ReadWrite | FileShare.Delete;
	
	/// <summary>
	///     Whether <paramref name="ex"/> is Windows' sharing violation (ERROR_SHARING_VIOLATION,
	///     32): the file is open in a handle that does not share what this open asks for —
	///     for a read here, another program writing it, or holding it without sharing at all.
	///     Windows words it as "used by another process", whoever the holder is. Elsewhere
	///     sharing is advisory, and the low bits of an HRESULT can be an errno that means
	///     something else (32 is EPIPE).
	/// </summary>
	internal static bool IsSharingViolation(Exception ex) =>
		OperatingSystem.IsWindows() && ex is IOException && (ex.HResult & 0xFFFF) == 32
	;
	
	static FileStream OpenFile(string path, FileOptions options, FileShare sharing)
	{
		return new(path, new FileStreamOptions {
			
			Mode    = FileMode.Open,
			Access  = FileAccess.Read,
			Share   = sharing,
			Options = options,
		});
	}
	
	/// <summary>
	///     The bytes of <paramref name="path"/>. A file with a length is read straight into an
	///     array of that length, so it is in memory once; one more byte is then asked for, which
	///     ends the read or shows the file grew. A file without one (a device, a pipe, a
	///     <c>/proc</c> file) and a file that grew are collected in a buffer and copied out.
	/// </summary>
	internal static byte[] ReadAllBytes(string path, long maxBytes)
	{
		using var stream = BoundedStream.Open(path, maxBytes, FileOptions.None, ReadSharing);
		using var rest   = new MemoryStream();
		
		if(stream.KnownLength is > 0 and var length) {
			
			var bytes = new byte[length];
			var count = stream.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false);
			
			// Shorter than its length: the file shrank while it was read.
			if(count < bytes.Length)
				
				return bytes[..count];
			
			var next = stream.ReadByte();
			
			if(next < 0)
				
				return bytes;
			
			rest.Write(bytes);
			rest.WriteByte((byte) next);
		}
		
		stream.CopyTo(rest);
		
		return rest.ToArray();
	}
	
	internal static Task<byte[]> ReadAllBytesAsync(string path, long maxBytes) => ReadAllBytesAsync(path, maxBytes, CancellationToken.None);
	
	/// <inheritdoc cref="ReadAllBytes"/>
	internal static async Task<byte[]> ReadAllBytesAsync(string path, long maxBytes, CancellationToken cancellationToken)
	{
		await using var stream = BoundedStream.Open(path, maxBytes, FileOptions.Asynchronous, ReadSharing);
		using var       rest   = new MemoryStream();
		
		if(stream.KnownLength is > 0 and var length) {
			
			var bytes = new byte[length];
			var count = await stream.ReadAtLeastAsync(bytes, bytes.Length, throwOnEndOfStream: false, cancellationToken);
			
			if(count < bytes.Length)
				
				return bytes[..count];
			
			var next = new byte[1];
			
			if(await stream.ReadAsync(next, cancellationToken) == 0)
				
				return bytes;
			
			rest.Write(bytes);
			rest.Write(next);
		}
		
		await stream.CopyToAsync(rest, cancellationToken);
		
		return rest.ToArray();
	}
	
	internal static string ReadAllText(string path, long maxBytes)
	{
		using var reader = OpenText(path, maxBytes, FileOptions.None);
		
		return reader.ReadToEnd();
	}
	
	internal static async Task<string> ReadAllTextAsync(string path, long maxBytes)
	{
		using var reader = OpenText(path, maxBytes, FileOptions.Asynchronous);
		
		return await reader.ReadToEndAsync();
	}
	
	internal static string[] ReadAllLines(string path, long maxBytes) => [..ReadLines(path, maxBytes)];
	
	/// <summary>
	///     The lines of <paramref name="path"/>, read as they are enumerated. The limit counts the
	///     whole file, so a file of one endless line ends in <see cref="FileTooLargeException"/>
	///     rather than an endless string. Like <see cref="File.ReadLines(string)"/>, the file is
	///     opened when enumeration starts.
	/// </summary>
	internal static IEnumerable<string> ReadLines(string path, long maxBytes)
	{
		using var reader = OpenText(path, maxBytes, FileOptions.SequentialScan);
		
		while(reader.ReadLine() is { } line)
			yield return line;
	}
	
	/// <summary>A limit as a reader would say it: <c>64 MB</c>, <c>1 MB</c>, <c>64 KB</c>, or bytes.</summary>
	internal static string Describe(long bytes) => bytes switch {
		
		>= 1024 * 1024 when bytes % (1024 * 1024) == 0 => $"{bytes / (1024 * 1024)} MB",
		>= 1024        when bytes % 1024          == 0 => $"{bytes / 1024} KB",
		_                                              => $"{bytes} bytes",
	};
	
	static StreamReader OpenText(string path, long maxBytes, FileOptions options) =>
		new(BoundedStream.Open(path, maxBytes, options, ReadSharing), Encoding.UTF8, detectEncodingFromByteOrderMarks: true)
	;
	
	/// <summary>
	///     A read-only, forward-only view of a file that counts what is read through it and throws
	///     <see cref="FileTooLargeException"/> as soon as the count passes the limit. Not seekable,
	///     so no reader can step around the count.
	/// </summary>
	sealed class BoundedStream : Stream
	{
		readonly FileStream inner;
		readonly string     path;
		readonly long       maxBytes;
		long                total;
		
		BoundedStream(FileStream inner, string path, long maxBytes)
		{
			this.inner    = inner;
			this.path     = path;
			this.maxBytes = maxBytes;
		}
		
		/// <summary>
		///     Opens the file and refuses it at once when its length is already known to be past the
		///     limit. A length of zero says nothing — a device or a <c>/proc</c> file reports it — so
		///     the count while reading stays the real check.
		/// </summary>
		internal static BoundedStream Open(string path, long maxBytes, FileOptions options, FileShare sharing)
		{
			ArgumentOutOfRangeException.ThrowIfNegative(maxBytes);
			ArgumentOutOfRangeException.ThrowIfGreaterThan(maxBytes, Array.MaxLength - 1L);
			
			var inner = OpenFile(path, options, sharing);
			
			try {
				
				if(inner.CanSeek && inner.Length > maxBytes)
					
					throw new FileTooLargeException(path, maxBytes);
				
				return new BoundedStream(inner, path, maxBytes);
			}
			catch {
				
				inner.Dispose();
				
				throw;
			}
		}
		
		/// <summary>
		///     The file's length when the stream has one, as an array length. Capped at the limit:
		///     <see cref="Open"/> checked the length, but the file can have grown since, and an array
		///     sized to that would be allocated before the count refuses the file. Null for a device
		///     or a pipe.
		/// </summary>
		internal int? KnownLength => inner.CanSeek ? (int) Math.Min(inner.Length, maxBytes) : null;
		
		public override bool CanRead  => true;
		public override bool CanSeek  => false;
		public override bool CanWrite => false;
		public override long Length   => throw new NotSupportedException();
		
		public override long Position {
			get => total;
			set => throw new NotSupportedException();
		}
		
		public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));
		
		public override int Read(Span<byte> buffer) => Count(inner.Read(buffer));
		
		public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
			Count(await inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken))
		;
		
		public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken) =>
			Count(await inner.ReadAsync(buffer, cancellationToken))
		;
		
		// Past the limit means the file holds more than the limit: a read returns only bytes that
		// are there.
		int Count(int read)
		{
			total += read;
			
			if(total > maxBytes)
				
				throw new FileTooLargeException(path, maxBytes);
			
			return read;
		}
		
		public override void Flush() { }
		
		public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
		
		public override void SetLength(long value) => throw new NotSupportedException();
		
		public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
		
		protected override void Dispose(bool disposing)
		{
			if(disposing)
				inner.Dispose();
			
			base.Dispose(disposing);
		}
		
		public override async ValueTask DisposeAsync()
		{
			await inner.DisposeAsync();
			await base.DisposeAsync();
		}
	}
}
