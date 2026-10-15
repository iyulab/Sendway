using System.Text.Json;

namespace Sendway.Core;

// What sending an email produced, beyond going out: the recipients the mail server refused for good
// while it accepted the others. Empty when every recipient was accepted.
public sealed record EmailDelivery(IReadOnlyList<RefusedRecipient> RefusedRecipients)
{
    public static EmailDelivery ToEveryone { get; } = new([]);
}

// One recipient the mail server refused for good, with its reply.
public sealed record RefusedRecipient(string Address, string Error)
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public static string? Serialize(IReadOnlyList<RefusedRecipient> recipients) =>
        recipients.Count == 0 ? null : JsonSerializer.Serialize(recipients, SerializerOptions);

    public static IReadOnlyList<RefusedRecipient>? Parse(string? stored) =>
        stored is null ? null : JsonSerializer.Deserialize<List<RefusedRecipient>>(stored, SerializerOptions);
}
