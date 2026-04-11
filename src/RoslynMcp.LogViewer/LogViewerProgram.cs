using System.Net;
using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using RoslynMcp.LogViewer;

class Program
{
	// Readable Unicode in SSE stream — em dashes, non-ASCII etc. pass through as-is.
	static readonly JsonSerializerOptions SseOptions = new() {
		Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
	};
	
	static string GetVersion() =>
		typeof(Program).Assembly
			.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()
			?.InformationalVersion ?? "?"
	;

	// Returns the path of the most recently modified per-PID log file in the log directory,
	// or null if none exist. Matches roslynmcp.*.log (base name only — not rotation siblings).
	static string? DiscoverLatestLog(string logDir)
	{
		if(!Directory.Exists(logDir))
			return null;

		try {

			return Directory
				.EnumerateFiles(logDir, "roslynmcp.*.log")
				.OrderByDescending(f => File.GetLastWriteTimeUtc(f))
				.FirstOrDefault();
		}
		catch {
			return null;
		}
	}
	
	static async Task Main(string[] args)
	{
		var envLogPath = Environment.GetEnvironmentVariable("ROSLYNMCP_LOG_PATH");
		
		string logPath;
		
		if(args.Length > 0) {
			logPath = args[0];
		}
		else if(envLogPath is not null) {
			
			if(envLogPath.Length == 0) {
				
				Console.Error.WriteLine("RoslynMcp Log Viewer");
				Console.Error.WriteLine("ROSLYNMCP_LOG_PATH is set to an empty string, so logging is disabled.");
				Console.Error.WriteLine("Provide a log file path as an argument to view logs, e.g.:");
				Console.Error.WriteLine("    RoslynMcp.LogViewer.exe <path-to-log-file>");
				
				return;
			}
			
			logPath = envLogPath;
		}
		else {

			// Discover the most recently active per-PID log file (e.g. roslynmcp.1234.log).
			// If no file exists yet, poll until one appears or the user cancels.
			var logDir  = Path.Combine(
				Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
				"RoslynMcp", "logs"
			);

			logPath = DiscoverLatestLog(logDir);

			if(logPath is null) {

				Console.Error.WriteLine("No active RoslynMcp log found — waiting for a server to start...");

				using var cts2 = new CancellationTokenSource();

				Console.CancelKeyPress += (_, e2) => {
					e2.Cancel = true;
					cts2.Cancel();
				};

				while(logPath is null && !cts2.IsCancellationRequested) {

					try {
						await Task.Delay(1000, cts2.Token);
					}
					catch(OperationCanceledException) {
						return;
					}

					logPath = DiscoverLatestLog(logDir);
				}

				if(logPath is null)
					return;
			}
		}
		
		Console.Error.WriteLine($"RoslynMcp Log Viewer {GetVersion()}");
		Console.Error.WriteLine($"Watching : {logPath}");
		Console.Error.WriteLine($"Open     : http://localhost:5123");
		
		var cts = new CancellationTokenSource();
		
		Console.CancelKeyPress += (_, e) => {
			
			e.Cancel = true;
			cts.Cancel();
		};
		
		var builder = WebApplication.CreateBuilder();
		
		builder.WebHost.UseUrls("http://localhost:5123");
		builder.Logging.ClearProviders();
		builder.Services.AddSingleton(new LogTailer(logPath));
		
		var app = builder.Build();
		
		app.MapGet("/", (HttpContext ctx) => {
			
			ctx.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
			ctx.Response.Headers.Pragma       = "no-cache";
			ctx.Response.Headers.Expires      = "0";
			
			return Results.Content(ViewerHtml.Page, "text/html; charset=utf-8");
		});
		
		app.MapPost("/shutdown", (HttpContext ctx, IHostApplicationLifetime lifetime) => {
			
			var origin = ctx.Request.Headers.Origin.FirstOrDefault();
			
			if(origin is not null) {
				
				var isLoopback = false;
				
				if(Uri.TryCreate(origin, UriKind.Absolute, out var uri)) {
					
					var host = uri.Host;
					isLoopback = host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || (IPAddress.TryParse(host, out var ip) && IPAddress.IsLoopback(ip));
				}
				
				if(!isLoopback)
					return Results.Forbid();
			}
			
			lifetime.StopApplication();
			
			return Results.Ok("Shutting down…");
		});
		
		app.MapGet("/logs/stream", async (HttpContext ctx, LogTailer tailer, IHostApplicationLifetime lifetime, CancellationToken ct) => {
			
			ctx.Response.Headers.Append("Content-Type",      "text/event-stream; charset=utf-8");
			ctx.Response.Headers.Append("Cache-Control",     "no-cache");
			ctx.Response.Headers.Append("X-Accel-Buffering", "no");
			
			using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.ApplicationStopping, cts.Token);
			
			await foreach(var entry in tailer.TailAsync(linked.Token)) {
				
				var json = JsonSerializer.Serialize(entry, SseOptions);
				
				await ctx.Response.WriteAsync($"data: {json}\n\n", linked.Token);
				await ctx.Response.Body.FlushAsync(linked.Token);
			}
		});
		
		await app.RunAsync(cts.Token);
	}
}
