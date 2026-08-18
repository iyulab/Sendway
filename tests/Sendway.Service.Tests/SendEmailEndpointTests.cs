using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sendway.Core;
using Sendway.Service.Endpoints;
using Xunit;

namespace Sendway.Service.Tests;

public class SendEmailEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public SendEmailEndpointTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IEmailSender>();
                services.AddSingleton<IEmailSender>(new FakeEmailSender());
            });
        });
    }

    [Fact]
    public async Task Post_WithValidRequest_Returns200()
    {
        var client = _factory.CreateClient();
        var request = new SendEmailRequest("user@example.com", "Subject", "Body");

        var response = await client.PostAsJsonAsync("/messages/email", request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Post_WithMissingTo_Returns400()
    {
        var client = _factory.CreateClient();
        var request = new SendEmailRequest(null, "Subject", "Body");

        var response = await client.PostAsJsonAsync("/messages/email", request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Post_WhenSenderThrows_Returns502()
    {
        var factory = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IEmailSender>();
                services.AddSingleton<IEmailSender>(new FakeEmailSender(throwOnSend: true));
            });
        });
        var client = factory.CreateClient();
        var request = new SendEmailRequest("user@example.com", "Subject", "Body");

        var response = await client.PostAsJsonAsync("/messages/email", request);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
    }

    private sealed class FakeEmailSender : IEmailSender
    {
        private readonly bool _throwOnSend;

        public FakeEmailSender(bool throwOnSend = false)
        {
            _throwOnSend = throwOnSend;
        }

        public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
        {
            if (_throwOnSend)
            {
                throw new InvalidOperationException("simulated SMTP failure");
            }
            return Task.CompletedTask;
        }
    }
}
