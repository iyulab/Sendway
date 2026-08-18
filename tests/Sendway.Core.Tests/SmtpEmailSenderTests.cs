using Microsoft.Extensions.Options;
using Sendway.Core;
using Xunit;

namespace Sendway.Core.Tests;

[Trait("Category", "Integration")]
public class SmtpEmailSenderTests
{
    [Fact]
    public async Task SendAsync_ToLocalSmtpCatcher_CompletesWithoutException()
    {
        var options = Options.Create(new SmtpOptions
        {
            Host = "localhost",
            Port = 2525,
            FromAddress = "sendway@localhost"
        });
        var sender = new SmtpEmailSender(options);
        var message = new EmailMessage("recipient@localhost", "L0 test", "walking skeleton");

        await sender.SendAsync(message);
    }
}
