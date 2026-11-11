namespace RoslynMcp.DevHost;

/// <summary>
///     What the pointer file said at one moment. The write time is part of it, so writing the same build
///     again is a request to start it again.
/// </summary>
readonly record struct BuildPointer(string Value, DateTime WrittenUtc);

/// <summary>
///     The directory the host and <c>scripts/Publish-DevServer.ps1</c> share: the published server builds,
///     the pointer to the one to run, and the build that last answered a handshake.
/// </summary>
sealed class DevHome
{
	public const string HomeVariable = "ROSLYNMCP_DEV_HOME";
	
	public string Root         { get; }
	public string PointerPath  => Path.Combine(Root, "current");
	public string LastGoodPath => Path.Combine(Root, "last-good");
	public string Logs         => Path.Combine(Root, "logs");
	
	public DevHome()
	{
		var named = Environment.GetEnvironmentVariable(HomeVariable);
		
		Root = string.IsNullOrWhiteSpace(named)
			? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RoslynMcp", "dev")
			: Path.GetFullPath(named)
		;
	}
	
	/// <summary>
	///     The pointer as it is now, or null while there is none to read: no file, an empty one, or one
	///     that is being replaced this moment. The caller asks again later.
	/// </summary>
	public BuildPointer? ReadPointer() => ReadShared(PointerPath)
	;
	
	public string? ReadLastGood() => ReadShared(LastGoodPath)?.Value
	;
	
	/// <summary>
	///     Content and write time of one file, both taken from the one open handle: read apart, a file
	///     replaced in between gives the new content with the old time, which reads as a pointer of its
	///     own and costs a second swap. The file is opened sharing deletes, so the rename that replaces
	///     it never fails because a host happened to be reading.
	/// </summary>
	static BuildPointer? ReadShared(string path)
	{
		try {
			
			using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
			using var reader = new StreamReader(stream);
			
			var written = File.GetLastWriteTimeUtc(stream.SafeFileHandle);
			var value   = reader.ReadToEnd().Trim();
			
			return value.Length == 0 ? null : new BuildPointer(value, written);
		}
		catch(Exception failure) when(failure is IOException or UnauthorizedAccessException) {
			return null;
		}
	}
	
	/// <summary>
	///     Records a build that answered a handshake. Every host of the machine writes this file, so it is
	///     replaced whole, and a failure is not worth more than the next host's attempt.
	/// </summary>
	public void WriteLastGood(string value)
	{
		if(ReadLastGood() == value)
			
			return;
		
		var temp = $"{LastGoodPath}.{Environment.ProcessId}.tmp";
		
		try {
			
			File.WriteAllText(temp, value + "\n");
			File.Move(temp, LastGoodPath, overwrite: true);
		}
		catch(Exception failure) when(failure is IOException or UnauthorizedAccessException) {
			
			try {
				File.Delete(temp);
			}
			catch(Exception cleanup) when(cleanup is IOException or UnauthorizedAccessException) { }
		}
	}
	
	// A pointer names a directory under builds, or any directory by its full path.
	public string Resolve(string value) => Path.IsPathRooted(value) ? value : Path.Combine(Root, "builds", value)
	;
}
