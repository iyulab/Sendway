using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sendway.Client;

// Source-generated serialization, so the client works trimmed and under native AOT.
[JsonSourceGenerationOptions(
    JsonSerializerDefaults.Web,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(EmailMessage))]
[JsonSerializable(typeof(PushMessage))]
[JsonSerializable(typeof(SmsMessage))]
[JsonSerializable(typeof(AcceptedMessage))]
[JsonSerializable(typeof(Message))]
[JsonSerializable(typeof(MessageEvent))]
internal sealed partial class ClientJsonContext : JsonSerializerContext;
