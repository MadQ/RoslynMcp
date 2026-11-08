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
		: base($"'{Path.GetFileName(path)}' is larger than {FileReader.Describe(limit)}, the most that is read from one file.")
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
	internal static Stream OpenRead(string path, long maxBytes) => BoundedStream.Open(path, maxBytes, FileOptions.None);
	
	internal static byte[] ReadAllBytes(string path, long maxBytes)
	{
		using var stream = BoundedStream.Open(path, maxBytes, FileOptions.None);
		using var buffer = new MemoryStream(stream.SizeHint);
		
		stream.CopyTo(buffer);
		
		return buffer.ToArray();
	}
	
	internal static Task<byte[]> ReadAllBytesAsync(string path, long maxBytes) => ReadAllBytesAsync(path, maxBytes, CancellationToken.None);
	
	internal static async Task<byte[]> ReadAllBytesAsync(string path, long maxBytes, CancellationToken cancellationToken)
	{
		await using var stream = BoundedStream.Open(path, maxBytes, FileOptions.Asynchronous);
		using var       buffer = new MemoryStream(stream.SizeHint);
		
		await stream.CopyToAsync(buffer, cancellationToken);
		
		return buffer.ToArray();
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
		new(BoundedStream.Open(path, maxBytes, options), Encoding.UTF8, detectEncodingFromByteOrderMarks: true)
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
		internal static BoundedStream Open(string path, long maxBytes, FileOptions options)
		{
			ArgumentOutOfRangeException.ThrowIfNegative(maxBytes);
			ArgumentOutOfRangeException.ThrowIfGreaterThan(maxBytes, Array.MaxLength - 1L);
			
			var inner = new FileStream(path, new FileStreamOptions {
				
				Mode    = FileMode.Open,
				Access  = FileAccess.Read,
				Share   = FileShare.Read,
				Options = options,
			});
			
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
		
		/// <summary>The capacity to start a buffer with: the file's length when it has one, plus one byte to see the end.</summary>
		internal int SizeHint => inner.CanSeek
			? (int) Math.Min(inner.Length + 1, maxBytes + 1)
			: (int) Math.Min(4096, maxBytes + 1)
		;
		
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
