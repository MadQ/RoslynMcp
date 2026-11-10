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
	public BuildPointer? ReadPointer()
	{
		try {
			
			var written = File.GetLastWriteTimeUtc(PointerPath);
			var value   = File.ReadAllText(PointerPath).Trim();
			
			return value.Length == 0 ? null : new BuildPointer(value, written);
		}
		catch(Exception failure) when(failure is IOException or UnauthorizedAccessException) {
			return null;
		}
	}
	
	public string? ReadLastGood()
	{
		try {
			
			var value = File.ReadAllText(LastGoodPath).Trim();
			
			return value.Length == 0 ? null : value;
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
