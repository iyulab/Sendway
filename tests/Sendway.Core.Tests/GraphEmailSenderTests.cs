using Microsoft.Graph.Models;
using Microsoft.Kiota.Abstractions.Serialization;
using Microsoft.Kiota.Serialization.Json;
using Sendway.Core;
using Xunit;

namespace Sendway.Core.Tests;

public class GraphEmailSenderTests
{
    // GraphServiceClient normally registers the JSON writer factory as a side effect of its own
    // construction. These tests serialize a Message directly without ever constructing a client,
    // so the registration has to happen here instead.
    static GraphEmailSenderTests()
    {
        Microsoft.Kiota.Abstractions.ApiClientBuilder.RegisterDefaultSerializer<JsonSerializationWriterFactory>();
    }

    // Regression: real Graph sends with no Cc/Bcc failed
    // with "A null value was found for the property named 'bccRecipients' ... cannot be null but
    // it can have null values." GraphEmailSender used to assign `CcRecipients`/`BccRecipients` to a
    // C# null for empty Cc/Bcc, which Kiota's backing store serializes as an explicit JSON null —
    // Graph's OData layer rejects that for these collection properties. Serializing here (not just
    // inspecting the built Message object) is what actually proves the wire format Graph accepts.
    [Fact]
    public async Task BuildGraphMessage_WithoutCcOrBcc_OmitsCcAndBccFromSerializedBody()
    {
        var message = new EmailMessage(Guid.NewGuid(), ["user@example.com"], "Subject", "Body");

        var graphMessage = GraphEmailSender.BuildGraphMessage(message);
        var json = await KiotaJsonSerializer.SerializeAsStringAsync(graphMessage);

        Assert.DoesNotContain("ccRecipients", json);
        Assert.DoesNotContain("bccRecipients", json);
    }

    [Fact]
    public async Task BuildGraphMessage_WithCcAndBcc_IncludesThemInSerializedBody()
    {
        var message = new EmailMessage(
            Guid.NewGuid(), ["user@example.com"], "Subject", "Body",
            cc: ["cc@example.com"], bcc: ["bcc@example.com"]);

        var graphMessage = GraphEmailSender.BuildGraphMessage(message);
        var json = await KiotaJsonSerializer.SerializeAsStringAsync(graphMessage);

        Assert.Contains("cc@example.com", json);
        Assert.Contains("bcc@example.com", json);
    }
}
