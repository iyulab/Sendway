using System.Text.Json.Serialization;

namespace Sendway.Core;

// Read and written by name in JSON ("Gmail"), the way it is written in configuration. Numbers are
// still accepted on read, which is how credentials stored before this were written.
[JsonConverter(typeof(JsonStringEnumConverter<SmtpProvider>))]
public enum SmtpProvider
{
    Gmail,
    Office365
}
