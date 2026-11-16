using System.Collections.Concurrent;

namespace RoslynMcp;

/// <summary>A directory that should be watched and is not, with what the watcher said when it failed to start.</summary>
internal sealed record UnwatchedRoot(string Path, string Reason);

/// <summary>
///     What a workspace's file watching amounts to (#495): how many roots are watched, and the ones
///     that are not. Under an unwatched root a change made outside the server is not seen: an
///     existing file still shows as drift when <c>roslyn_check_drift</c> is asked, a new file does
///     not show anywhere until a reload.
/// </summary>
internal sealed record WatchStatus(int Live, UnwatchedRoot[] Unwatched)
{
	/// <summary>What is not watched, for a sentence: all of the workspace, or part of it.</summary>
	public string Extent => Live == 0 ? "the workspace" : "part of the workspace";
	
	/// <summary>
	///     The state as sentences for an agent, empty when every root is watched: what failed, what
	///     that costs, and what helps. On Linux the cause is nearly always the inotify limit, which a
	///     reload does not lift, so the limit is named before the reload.
	/// </summary>
	public string Problem {
		get {
			if(Unwatched.Length == 0)
				
				return "";
			
			var remedy = OperatingSystem.IsLinux()
				? "On Linux this is usually the limit on inotify instances: raise fs.inotify.max_user_instances (sysctl), then call roslyn_respawn."
				: "Call roslyn_respawn once the cause is gone.";
			
			return $"{Unwatched.Length} of {Live + Unwatched.Length} watch root(s) could not start ({Unwatched[0].Reason}). "
				+ "Files created or changed there outside the server are not noticed; a new file is not seen at all until the workspace reloads. "
				+ remedy;
		}
	}
}

/// <summary>
///     Watch problems waiting to be told (#495). A watcher fails to start on a background thread,
///     after the call that loaded its workspace has answered, so there is no call to put the news
///     in: it is posted here and the next tool answer carries it as a caution. Process-wide, like
///     the session note; the text names the workspace it is about.
/// </summary>
internal static class WatchNews
{
	static readonly ConcurrentQueue<string> pending = new();
	
	// More than a few are one cause seen from several workspaces; the rest is in roslyn_check_drift.
	const int Limit = 4;
	
	// False when the queue is full: the caller then has not said it, and says it again later.
	internal static bool Post(string news)
	{
		if(pending.Count >= Limit)
			
			return false;
		
		pending.Enqueue(news);
		
		return true;
	}
	
	internal static bool TryTake(out string news) => pending.TryDequeue(out news!);
}
