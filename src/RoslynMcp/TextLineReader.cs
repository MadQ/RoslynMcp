using System.Buffers;

namespace RoslynMcp;

/// <summary>
///     A line of a streamed file that holds more characters than <see cref="TextLineReader.LineLimit"/>.
///     An <see cref="IOException"/>, so every guard that already catches a failed read catches this
///     one too.
/// </summary>
internal sealed class LineTooLongException : IOException
{
	internal LineTooLongException(string path, int line)
		: base($"Line {line} of '{Path.GetFileName(path)}' is longer than {TextLineReader.LineLimit:N0} characters, the limit for one line.")
	{
		FilePath = path;
		Line     = line;
	}
	
	internal string FilePath { get; }
	internal int    Line     { get; }
}

/// <summary>
///     The lines of a file on disk, one at a time, holding only the line it is on (#327, #401): a
///     search through files the workspace does not track, and a line range of a file too large
///     to read whole. Opened with <see cref="FileReader.OpenLines"/>.
/// </summary>
/// <remarks>
///     <para>
///         Lines are numbered as <c>SourceText</c> numbers them, because that is how every other
///         tool numbers them and an edit is anchored on the number a search or a read reported:
///         a line ends at <c>\r\n</c>, <c>\r</c>, <c>\n</c>, U+0085, U+2028 or U+2029, and what
///         follows the last break is a line too, also when it is empty. A file of N breaks has
///         N + 1 lines; an empty file has one. <c>StreamReader.ReadLine</c> knows the first three
///         breaks only and drops an empty last line.
///     </para>
///     <para>
///         Two bounds, since neither alone ends every read. The line limit is what stops a file
///         without a line break (a minified bundle, <c>/dev/zero</c>) from becoming one endless
///         string. The byte limit of the stream beneath is what stops a file without an end
///         whose bytes do hold breaks, a link to <c>/dev/urandom</c>.
///     </para>
/// </remarks>
internal sealed class TextLineReader : IDisposable
{
	/// <summary>The most characters one line may hold; a longer one ends the read with <see cref="LineTooLongException"/>.</summary>
	internal const int LineLimit = 1_000_000;
	
	// How much of the start of the file is looked at for a NUL character.
	const int BinaryProbe = 8192;
	
	static readonly SearchValues<char> Breaks = SearchValues.Create("\r\n\u0085\u2028\u2029");
	
	readonly string       path;
	readonly StreamReader reader;
	readonly char[]       chunk = ArrayPool<char>.Shared.Rent(32 * 1024);
	
	// A line that does not lie within one chunk is put together here.
	char[] carry = [];
	int    held;
	int    position;
	int    length;
	bool   started;
	bool   ended;
	
	internal TextLineReader(string path, StreamReader reader)
	{
		this.path   = path;
		this.reader = reader;
	}
	
	/// <summary>The number of the line the last <see cref="Read"/> returned, from 1; 0 before the first.</summary>
	internal int LineNumber { get; private set; }
	
	/// <summary>
	///     Whether the file starts like one that is not text: a NUL character in its first
	///     characters, which a compiled or compressed file has and decoded text does not (a
	///     UTF-16 file with a byte order mark is decoded, and so has none). Reads the first chunk
	///     when nothing was read yet.
	/// </summary>
	internal bool LooksBinary()
	{
		if(!started)
			Fill();
		
		return chunk.AsSpan(0, Math.Min(length, BinaryProbe)).Contains('\0');
	}
	
	/// <summary>
	///     The next line, without its break, or false after the last. The span is valid until the
	///     next call.
	/// </summary>
	internal bool Read(out ReadOnlySpan<char> line)
	{
		line = default;
		
		if(ended)
			
			return false;
		
		if(!started)
			Fill();
		
		held = 0;
		
		while(true) {
			
			if(position == length && !Fill()) {
				
				// The end of the file ends the last line, which may be empty.
				ended = true;
				line  = carry.AsSpan(0, held);
				
				LineNumber++;
				
				return true;
			}
			
			var rest = chunk.AsSpan(position, length - position);
			var at   = rest.IndexOfAny(Breaks);
			
			if(at < 0) {
				
				Keep(rest);
				position = length;
				
				continue;
			}
			
			var found = rest[at];
			
			position += at + 1;
			
			// A '\r' at the very end of the chunk: whether a '\n' follows is in the next chunk,
			// and reading that overwrites this one, so the line is kept first.
			var split = found == '\r' && position == length;
			
			if(held > 0 || split) {
				
				Keep(rest[..at]);
				
				if(split)
					Fill();
				
				line = carry.AsSpan(0, held);
			}
			else
				line = rest[..at];
			
			if(found == '\r' && position < length && chunk[position] == '\n')
				position++;
			
			LineNumber++;
			
			return true;
		}
	}
	
	void Keep(ReadOnlySpan<char> part)
	{
		if(held + part.Length > LineLimit)
			
			throw new LineTooLongException(path, LineNumber + 1);
		
		if(held + part.Length > carry.Length)
			Array.Resize(ref carry, Math.Min(LineLimit, Math.Max(carry.Length * 2, held + part.Length)));
		
		part.CopyTo(carry.AsSpan(held));
		
		held += part.Length;
	}
	
	// The next chunk; false at the end of the file.
	bool Fill()
	{
		started  = true;
		position = 0;
		length   = reader.Read(chunk, 0, chunk.Length);
		
		return length > 0;
	}
	
	public void Dispose()
	{
		reader.Dispose();
		
		ArrayPool<char>.Shared.Return(chunk);
	}
}
