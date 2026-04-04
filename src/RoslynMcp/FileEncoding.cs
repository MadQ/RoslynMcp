using System.Text;


namespace RoslynMcp;

static class FileEncoding
{
	// Detects encoding from a BOM header. UTF-16 variants are preserved when
	// already present; absent or unrecognised BOM → UTF-8 without BOM.
	internal static Encoding Detect(ReadOnlySpan<byte> header)
	{
		if(header.Length >= 3 && header[0] == 0xEF && header[1] == 0xBB && header[2] == 0xBF)
			return new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);

		if(header.Length >= 2 && header[0] == 0xFF && header[1] == 0xFE)
			return Encoding.Unicode; // UTF-16 LE

		if(header.Length >= 2 && header[0] == 0xFE && header[1] == 0xFF)
			return Encoding.BigEndianUnicode; // UTF-16 BE

		return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
	}

	// Peeks at the first 4 bytes of a file on disk to detect encoding — avoids
	// reading the whole file and avoids trusting a potentially-stale in-memory
	// encoding cached by the Roslyn workspace.
	internal static Encoding Peek(string path)
	{
		Span<byte> header = stackalloc byte[4];

		try {
			using var fs = File.OpenRead(path);
			var read = fs.Read(header);

			return Detect(header[..read]);
		}
		catch {
			// Can't peek (unlikely for a file we're about to overwrite) → no-BOM default.
			return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
		}
	}
}
