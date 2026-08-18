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
    private readonly FakeEmailSender _fakeEmailSender = new();

    public SendEmailEndpointTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IEmailSender>();
                services.AddSingleton<IEmailSender>(_fakeEmailSender);
            });
        });
    }

    [Fact]
    public async Task Post_WithValidRequest_Returns200()
    {
        var client = _factory.CreateClient();
        var request = new SendEmailRequest("user@example.com", "Distinct Subject", "Distinct Body");

        var response = await client.PostAsJsonAsync("/messages/email", request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(_fakeEmailSender.LastMessage);
        Assert.Equal("user@example.com", _fakeEmailSender.LastMessage!.To);
        Assert.Equal("Distinct Subject", _fakeEmailSender.LastMessage!.Subject);
        Assert.Equal("Distinct Body", _fakeEmailSender.LastMessage!.Body);
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

        public EmailMessage? LastMessage { get; private set; }

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
            LastMessage = message;
            return Task.CompletedTask;
        }
    }
}
