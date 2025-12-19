using Farm.Infrastructure.Services.Email;
using FluentAssertions;

namespace Farm.Web.Api.Tests.Services;

public class EmailMessageTests
{
    [Fact]
    public void Constructor_WithRequiredFields_Succeeds()
    {
        var message = new EmailMessage(
            To: "user@example.com",
            Subject: "Test Subject"
        );

        message.Should().NotBeNull();
        message.To.Should().Be("user@example.com");
        message.Subject.Should().Be("Test Subject");
    }

    [Fact]
    public void Constructor_WithAllFields_Succeeds()
    {
        var metadata = new Dictionary<string, string> { { "key", "value" } };

        var message = new EmailMessage(
            To: "user@example.com",
            Subject: "Test Subject",
            PlainBody: "Plain text body",
            HtmlBody: "<html>HTML body</html>",
            TemplateKey: "template-key",
            Metadata: metadata
        );

        message.To.Should().Be("user@example.com");
        message.Subject.Should().Be("Test Subject");
        message.PlainBody.Should().Be("Plain text body");
        message.HtmlBody.Should().Be("<html>HTML body</html>");
        message.TemplateKey.Should().Be("template-key");
        message.Metadata.Should().Equal(metadata);
    }

    [Fact]
    public void PlainBody_DefaultsToNull()
    {
        var message = new EmailMessage("user@example.com", "Subject");

        message.PlainBody.Should().BeNull();
    }

    [Fact]
    public void HtmlBody_DefaultsToNull()
    {
        var message = new EmailMessage("user@example.com", "Subject");

        message.HtmlBody.Should().BeNull();
    }

    [Fact]
    public void TemplateKey_DefaultsToEmpty()
    {
        var message = new EmailMessage("user@example.com", "Subject");

        message.TemplateKey.Should().Be("");
    }

    [Fact]
    public void Metadata_DefaultsToNull()
    {
        var message = new EmailMessage("user@example.com", "Subject");

        message.Metadata.Should().BeNull();
    }

    [Fact]
    public void EmailMessage_IsValueType()
    {
        var message1 = new EmailMessage("user@example.com", "Subject");
        var message2 = new EmailMessage("user@example.com", "Subject");

        message1.Equals(message2).Should().BeTrue();
    }

    [Fact]
    public void EmailMessage_WithDifferentTo_AreNotEqual()
    {
        var message1 = new EmailMessage("user1@example.com", "Subject");
        var message2 = new EmailMessage("user2@example.com", "Subject");

        message1.Equals(message2).Should().BeFalse();
    }

    [Fact]
    public void EmailMessage_WithDifferentSubject_AreNotEqual()
    {
        var message1 = new EmailMessage("user@example.com", "Subject 1");
        var message2 = new EmailMessage("user@example.com", "Subject 2");

        message1.Equals(message2).Should().BeFalse();
    }

    [Fact]
    public void EmailMessage_CanBeDeconstructed()
    {
        var message = new EmailMessage("user@example.com", "Subject", "Body");

        var (to, subject, plainBody, htmlBody, templateKey, metadata) = message;

        to.Should().Be("user@example.com");
        subject.Should().Be("Subject");
        plainBody.Should().Be("Body");
        htmlBody.Should().BeNull();
        templateKey.Should().Be("");
        metadata.Should().BeNull();
    }

    [Fact]
    public void EmailDispatchResult_WithSuccess_Succeeds()
    {
        var result = new EmailDispatchResult(Success: true);

        result.Success.Should().BeTrue();
        result.ProviderMessage.Should().BeNull();
        result.Error.Should().BeNull();
    }

    [Fact]
    public void EmailDispatchResult_WithFailure_Succeeds()
    {
        var result = new EmailDispatchResult(
            Success: false,
            Error: "Failed to send email"
        );

        result.Success.Should().BeFalse();
        result.Error.Should().Be("Failed to send email");
    }

    [Fact]
    public void EmailDispatchResult_WithProviderMessage_Succeeds()
    {
        var result = new EmailDispatchResult(
            Success: true,
            ProviderMessage: "Accepted"
        );

        result.Success.Should().BeTrue();
        result.ProviderMessage.Should().Be("Accepted");
    }

    [Fact]
    public void EmailDispatchResult_IsValueType()
    {
        var result1 = new EmailDispatchResult(Success: true);
        var result2 = new EmailDispatchResult(Success: true);

        result1.Equals(result2).Should().BeTrue();
    }

    [Fact]
    public void EmailDispatchResult_CanBeDeconstructed()
    {
        var result = new EmailDispatchResult(true, "Message", null);

        var (success, providerMessage, error) = result;

        success.Should().BeTrue();
        providerMessage.Should().Be("Message");
        error.Should().BeNull();
    }

    [Fact]
    public void EmailMessage_ToString_Includes_To()
    {
        var message = new EmailMessage("user@example.com", "Subject");

        message.ToString().Should().Contain("user@example.com");
    }
}
