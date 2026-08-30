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

    // Regression: docket #148 — Authway's verification found real Graph sends (no Cc/Bcc) failing
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

    [Theory]
    [InlineData("not-an-email-address")]
    [InlineData("plainaddress")]
    [InlineData("user@")]
    [InlineData("@example.com")]
    public async Task SendAsync_WithMalformedToRecipient_ThrowsInvalidRecipientException(string to)
    {
        var sender = CreateSender();
        var message = new EmailMessage(Guid.NewGuid(), [to], "Subject", "Body");

        await Assert.ThrowsAsync<InvalidRecipientException>(() => sender.SendAsync(message));
    }

    [Fact]
    public async Task SendAsync_WithMalformedCcRecipient_ThrowsInvalidRecipientException()
    {
        var sender = CreateSender();
        var message = new EmailMessage(Guid.NewGuid(), ["user@example.com"], "Subject", "Body", cc: ["not-an-email-address"]);

        await Assert.ThrowsAsync<InvalidRecipientException>(() => sender.SendAsync(message));
    }

    [Fact]
    public async Task SendAsync_WithMalformedBccRecipient_ThrowsInvalidRecipientException()
    {
        var sender = CreateSender();
        var message = new EmailMessage(Guid.NewGuid(), ["user@example.com"], "Subject", "Body", bcc: ["not-an-email-address"]);

        await Assert.ThrowsAsync<InvalidRecipientException>(() => sender.SendAsync(message));
    }

    // Constructing ClientSecretCredential/GraphServiceClient performs no I/O — the malformed-address
    // check runs (and throws) before the first actual network call, so these fake, never-valid
    // credentials are enough to exercise the pre-flight validation path without a live Azure AD app.
    private static GraphEmailSender CreateSender()
    {
        var credentialStore = new SingleCredentialStore(ChannelCredentialNames.EmailGraph, new GraphOptions
        {
            DirectoryId = "00000000-0000-0000-0000-000000000000",
            ClientId = "00000000-0000-0000-0000-000000000000",
            ClientSecret = "not-a-real-secret",
            FromAddress = "sendway@localhost"
        });

        return new GraphEmailSender(credentialStore);
    }
}
