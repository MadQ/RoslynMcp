namespace RoslynMcp.DevHost;

/// <summary>
///     Reads a stream one line at a time as the bytes it holds, so a forwarded message is the message that
///     was sent: no decoding, no change of line ending. A <c>StreamReader</c> would also end a line at a
///     lone carriage return, which JSON allows between tokens.
/// </summary>
sealed class LineReader(Stream stream)
{
	byte[] buffer = new byte[64 * 1024];
	int    start;
	int    end;
	
	/// <summary>
	///     The next line with its line feed, or what is left when the stream ends without one. Empty at the
	///     end of the stream. The memory is the reader's own buffer and holds the line only until the next
	///     call.
	/// </summary>
	public async ValueTask<ReadOnlyMemory<byte>> ReadLineAsync()
	{
		var scanned = start;
		
		while(true) {
			
			var newline = buffer.AsSpan(scanned, end - scanned).IndexOf((byte) '\n');
			
			if(newline >= 0) {
				
				var line = buffer.AsMemory(start, scanned + newline + 1 - start);
				start = scanned + newline + 1;
				
				return line;
			}
			
			// The line continues past what was read: make room for more behind it.
			if(start > 0) {
				
				Buffer.BlockCopy(buffer, start, buffer, 0, end - start);
				end   -= start;
				start  = 0;
			}
			
			scanned = end;
			
			if(end == buffer.Length)
				Array.Resize(ref buffer, buffer.Length * 2);
			
			var read = await stream.ReadAsync(buffer.AsMemory(end));
			
			if(read == 0) {
				
				var rest = buffer.AsMemory(start, end - start);
				start = end;
				
				return rest;
			}
			
			end += read;
		}
	}
}
