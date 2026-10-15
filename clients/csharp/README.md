# Sendway.Client

.NET client for [Sendway](https://github.com/iyulab/Sendway): send email, SMS and app push through one API, read a
message's outcome with a machine-readable failure reason, and verify the signed outcome webhooks.

It covers the API a calling service uses — the same contract the service describes in OpenAPI 3.1 at
`/openapi/v1.json` — and nothing else: no dependencies beyond the .NET runtime, trimming- and AOT-compatible.

```
dotnet add package Sendway.Client
```

## Send

```csharp
using Sendway.Client;

var sendway = new SendwayClient(
    new HttpClient { BaseAddress = new Uri("https://sendway.example.com") },
    apiKey: Environment.GetEnvironmentVariable("SENDWAY_API_KEY")!);

var accepted = await sendway.SendSmsAsync(
    new SmsMessage("+821012345678", "Your code is 482913") { ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(3) },
    idempotencyKey: $"login-code-{attemptId}");
```

A send returns once Sendway has stored the message (`202`); delivery happens afterwards, with retries. Pass an
`idempotencyKey` you derive from your own operation: repeating the call with the same key and the same message, within
24 hours, answers with the original message instead of sending a second one — so a call whose answer was lost can be
repeated safely. `ExpiresAt` keeps a message that could not go out in time from going out late.

`SendEmailAsync(new EmailMessage(to, subject, body) { HtmlBody = …, Cc = …, Bcc = … })` and
`SendPushAsync(new PushMessage(deviceToken, title, body))` work the same way.

## Read the outcome

```csharp
var message = await sendway.GetMessageAsync(accepted.Id);

if (message.Status == MessageState.Failed)
{
    switch (message.Reason)
    {
        case FailureReason.RecipientRefused:     // unknown mailbox, dead device token, opted-out number: stop using it
        case FailureReason.MessageRefused:       // content, size or form refused: sending it again fails the same way
        case FailureReason.RecipientRateLimited: // too many to that destination in a short window
        case FailureReason.Expired:              // ExpiresAt passed before it could be sent
        case FailureReason.AttemptsExhausted:    // a failure on the sending side lasted through every retry
            break;
    }
}
```

Branch on `Reason`, not on the human-readable `Error`. A value this version does not know reads as `Unknown`, with the
text kept in `ReasonText` / `StatusText`.

## Testing without sending

A sandbox tenant (its key starts with `sw_test_`) goes through the whole path — acceptance, status, signed
webhooks — but nothing leaves Sendway, and the destination picks the outcome: an address local part or device token
starting with `refused`, `rejected`, `ratelimited` or `exhausted` (SMS: a number ending in `0001`–`0004`) gets the
matching `FailureReason`; anything else is `Sent`. Point a development environment at one.

## Errors

A refused call throws `SendwayException` with the HTTP `Status`, a `Code` to branch on (`InvalidRequest`,
`InvalidRecipient`, `IdempotencyKeyReused`, `ChannelNotConfigured`, `RateLimited`, `Unauthorized`, `NotFound`,
`InternalError`) and, when rate limited, `RetryAfter`.

## Webhooks

Instead of polling, a service can receive each final outcome as a webhook signed per
[Standard Webhooks](https://www.standardwebhooks.com/). Verify it on the raw body:

```csharp
var webhook = new SendwayWebhook(secret); // whsec_…, given when the endpoint was registered

app.MapPost("/sendway-events", async (HttpRequest request) =>
{
    var body = await new StreamReader(request.Body).ReadToEndAsync();
    try
    {
        var received = webhook.Verify(
            request.Headers[SendwayWebhook.IdHeader]!,
            request.Headers[SendwayWebhook.TimestampHeader]!,
            request.Headers[SendwayWebhook.SignatureHeader]!,
            body);
        // received.Data is the same document GetMessageAsync returns. Delivery is at least once:
        // ignore an IdHeader value you have already handled.
        return Results.Ok();
    }
    catch (WebhookVerificationException)
    {
        return Results.Unauthorized();
    }
});
```

## Versions

See [CHANGELOG.md](CHANGELOG.md). Before 1.0, a minor version may change the API.
