using MimeKit;
using Sendway.Core;
using Xunit;

namespace Sendway.Core.Tests;

public class SmtpEmailSenderBodyTests
{
    [Fact]
    public void BuildBody_WithoutHtmlBody_ReturnsPlainTextPart()
    {
        var entity = SmtpEmailSender.BuildBody("plain text", null);

        var textPart = Assert.IsType<TextPart>(entity);
        Assert.Equal("plain", textPart.ContentType.MediaSubtype);
        Assert.Equal("plain text", textPart.Text);
    }

    [Fact]
    public void BuildBody_WithBlankHtmlBody_ReturnsPlainTextPart()
    {
        var entity = SmtpEmailSender.BuildBody("plain text", "");

        Assert.IsType<TextPart>(entity);
    }

    [Fact]
    public void BuildBody_WithHtmlBody_ReturnsMultipartAlternativeWithTextFallback()
    {
        var entity = SmtpEmailSender.BuildBody("plain text", "<p>plain text</p>");

        var multipart = Assert.IsType<MultipartAlternative>(entity);
        Assert.Equal(2, multipart.Count);

        var text = Assert.IsType<TextPart>(multipart[0]);
        Assert.Equal("plain", text.ContentType.MediaSubtype);
        Assert.Equal("plain text", text.Text);

        var html = Assert.IsType<TextPart>(multipart[1]);
        Assert.Equal("html", html.ContentType.MediaSubtype);
        Assert.Equal("<p>plain text</p>", html.Text);
    }
}
