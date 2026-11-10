using System.Text;
using System.Text.Json;

namespace RoslynMcp.DevHost;

/// <summary>
///     The two top-level members of a JSON-RPC message the host needs: who is waiting for an answer.
///     Everything else in a message is skipped unread. A line that is no JSON object has neither, and is
///     forwarded like any other.
/// </summary>
readonly record struct MessageHead(string? Id, string? Method)
{
	public const string Initialize  = "initialize";
	public const string Initialized = "notifications/initialized";
	public const string Cancelled   = "notifications/cancelled";
	
	// A message nested deeper than the reader's default of 64 would otherwise read as no message at all,
	// and its request would not count as waiting.
	static readonly JsonReaderOptions Deep = new() { MaxDepth = 4096 };
	
	public bool IsRequest  => Id is not null && Method is not null;
	public bool IsResponse => Id is not null && Method is null;
	
	public static MessageHead Read(ReadOnlySpan<byte> line)
	{
		try {
			
			var reader = new Utf8JsonReader(line, Deep);
			
			if(!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
				
				return default;
			
			string? id     = null;
			string? method = null;
			
			while(reader.Read() && reader.TokenType == JsonTokenType.PropertyName) {
				
				var isId     = reader.ValueTextEquals("id"u8);
				var isMethod = reader.ValueTextEquals("method"u8);
				
				reader.Read();
				
				if(isId)
					id = IdKey(ref reader);
				else if(isMethod && reader.TokenType == JsonTokenType.String)
					method = reader.GetString();
				
				// Past the value whatever it is: nothing for a scalar, the whole of an object or array.
				reader.Skip();
			}
			
			return new(id, method);
		}
		catch(JsonException) {
			return default;
		}
	}
	
	/// <summary>
	///     The id of the request a <c>notifications/cancelled</c> message gives up on. A cancelled request
	///     may never be answered, so it must stop counting as waiting.
	/// </summary>
	public static string? CancelledId(ReadOnlySpan<byte> line)
	{
		try {
			
			var reader = new Utf8JsonReader(line, Deep);
			
			if(!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
				
				return null;
			
			while(reader.Read() && reader.TokenType == JsonTokenType.PropertyName) {
				
				var isParams = reader.ValueTextEquals("params"u8);
				
				reader.Read();
				
				if(!isParams || reader.TokenType != JsonTokenType.StartObject) {
					
					reader.Skip();
					
					continue;
				}
				
				while(reader.Read() && reader.TokenType == JsonTokenType.PropertyName) {
					
					var isRequestId = reader.ValueTextEquals("requestId"u8);
					
					reader.Read();
					
					if(isRequestId)
						
						return IdKey(ref reader);
					
					reader.Skip();
				}
				
				return null;
			}
			
			return null;
		}
		catch(JsonException) {
			return null;
		}
	}
	
	// "1" and 1 are different ids, hence the prefix. A number keeps its text: 1 and 1.0 are not told apart
	// by anyone who sends ids, and parsing would only add a way to get it wrong.
	static string? IdKey(ref Utf8JsonReader reader) => reader.TokenType switch {
		JsonTokenType.String => "s:" + reader.GetString(),
		JsonTokenType.Number => "n:" + Encoding.UTF8.GetString(reader.ValueSpan),
		_                    => null,
	};
}
