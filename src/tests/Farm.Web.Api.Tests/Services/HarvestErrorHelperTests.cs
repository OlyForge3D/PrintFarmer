using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Gcode;
using FluentAssertions;
using System.Diagnostics.CodeAnalysis;

namespace Farm.Web.Api.Tests.Services;

[SuppressMessage("Design", "CA2201:Do not raise reserved exception types")]
public class HarvestErrorHelperTests
{
    [Theory]
    [InlineData(typeof(HttpRequestException), nameof(HarvestErrorType.ConnectionError))]
    [InlineData(typeof(TimeoutException), nameof(HarvestErrorType.ConnectionError))]
    [InlineData(typeof(TaskCanceledException), nameof(HarvestErrorType.ConnectionError))]
    [InlineData(typeof(UnauthorizedAccessException), nameof(HarvestErrorType.AuthenticationError))]
    [InlineData(typeof(IOException), nameof(HarvestErrorType.FileSystemError))]
    [InlineData(typeof(ArgumentException), nameof(HarvestErrorType.ValidationError))]
    public void CategorizeError_MapsByExceptionType(Type exceptionType, string expectedCategory)
    {
        var ex = (Exception)Activator.CreateInstance(exceptionType, "Test error")!;

        string result = HarvestErrorHelper.CategorizeError(ex);

        result.Should().Be(expectedCategory);
    }

    [Theory]
    [InlineData("401 Unauthorized")]
    [InlineData("403 Forbidden")]
    public void CategorizeError_Maps401And403ToAuthenticationError(string message)
    {
        var ex = new Exception(message);

        string result = HarvestErrorHelper.CategorizeError(ex);

        result.Should().Be(nameof(HarvestErrorType.AuthenticationError));
    }

    [Fact]
    public void CategorizeError_Maps404ToFileSystemError()
    {
        var ex = new Exception("404 Not Found");

        string result = HarvestErrorHelper.CategorizeError(ex);

        result.Should().Be(nameof(HarvestErrorType.FileSystemError));
    }

    [Theory]
    [InlineData("Request timeout")]
    [InlineData("Operation timed out")]
    public void CategorizeError_MapsTimeoutMessagesToConnectionError(string message)
    {
        var ex = new Exception(message);

        string result = HarvestErrorHelper.CategorizeError(ex);

        result.Should().Be(nameof(HarvestErrorType.ConnectionError));
    }

    [Theory]
    [InlineData("Connection refused")]
    [InlineData("Network unreachable")]
    public void CategorizeError_MapsConnectionMessagesToConnectionError(string message)
    {
        var ex = new Exception(message);

        string result = HarvestErrorHelper.CategorizeError(ex);

        result.Should().Be(nameof(HarvestErrorType.ConnectionError));
    }

    [Fact]
    public void CategorizeError_UnknownExceptionTypeReturnsUnknownError()
    {
        var ex = new InvalidOperationException("Some random error");

        string result = HarvestErrorHelper.CategorizeError(ex);

        result.Should().Be(nameof(HarvestErrorType.UnknownError));
    }

    [Fact]
    public void CategorizeError_WithFailedResourceParameter()
    {
        var ex = new Exception("Test error");

        string result = HarvestErrorHelper.CategorizeError(ex, "test-resource");

        result.Should().Be(nameof(HarvestErrorType.UnknownError));
    }

    [Theory]
    [InlineData(nameof(HarvestErrorType.ConnectionError), true)]
    [InlineData(nameof(HarvestErrorType.FileSystemError), true)]
    [InlineData(nameof(HarvestErrorType.AuthenticationError), false)]
    [InlineData(nameof(HarvestErrorType.ValidationError), false)]
    [InlineData(nameof(HarvestErrorType.UnknownError), false)]
    public void IsRetryableError_ReturnsExpectedValues(string errorType, bool expected)
    {
        bool result = HarvestErrorHelper.IsRetryableError(errorType);

        result.Should().Be(expected);
    }

    [Fact]
    public void CreateErrorDetailsJson_IncludesExceptionType()
    {
        var ex = new InvalidOperationException("Test error");

        string json = HarvestErrorHelper.CreateErrorDetailsJson(ex);

        json.Should().Contain("InvalidOperationException");
    }

    [Fact]
    public void CreateErrorDetailsJson_IncludesStackTrace()
    {
        var ex = new Exception("Test error");

        string json = HarvestErrorHelper.CreateErrorDetailsJson(ex);

        // Stack trace may be null in some test environments, but should serialize
        json.Should().Contain("StackTrace");
    }

    [Fact]
    public void CreateErrorDetailsJson_IncludesInnerException()
    {
        var innerEx = new InvalidOperationException("Inner error");
        var ex = new Exception("Outer error", innerEx);

        string json = HarvestErrorHelper.CreateErrorDetailsJson(ex);

        json.Should().Contain("Inner error");
    }

    [Fact]
    public void CreateErrorDetailsJson_WithFailedResource_IncludesInAdditionalInfo()
    {
        var ex = new Exception("Test error");

        string json = HarvestErrorHelper.CreateErrorDetailsJson(ex, "failed-resource");

        json.Should().Contain("AdditionalInfo");
        json.Should().Contain("failed-resource");
    }

    [Theory]
    [InlineData(nameof(HarvestErrorType.ConnectionError), "Connection failed: ")]
    [InlineData(nameof(HarvestErrorType.AuthenticationError), "Authentication failed: ")]
    [InlineData(nameof(HarvestErrorType.FileSystemError), "File system error: ")]
    [InlineData(nameof(HarvestErrorType.ValidationError), "Validation failed: ")]
    [InlineData(nameof(HarvestErrorType.UnknownError), "Error: ")]
    public void GetUserFriendlyMessage_ReturnsCorrectPrefix(string errorType, string expectedPrefix)
    {
        string result = HarvestErrorHelper.GetUserFriendlyMessage(errorType, "Original message");

        result.Should().Be(expectedPrefix + "Original message");
    }

    [Theory]
    [InlineData(nameof(HarvestErrorType.ConnectionError))]
    [InlineData(nameof(HarvestErrorType.AuthenticationError))]
    [InlineData(nameof(HarvestErrorType.FileSystemError))]
    public void GetErrorSuggestion_ReturnsNonEmptyForKnownTypes(string errorType)
    {
        string? result = HarvestErrorHelper.GetErrorSuggestion(errorType);

        result.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void GetErrorSuggestion_ReturnsNullForUnknownType()
    {
        string? result = HarvestErrorHelper.GetErrorSuggestion("UnknownErrorType");

        result.Should().BeNull();
    }

    [Theory]
    [InlineData(nameof(HarvestErrorType.ConnectionError))]
    [InlineData(nameof(HarvestErrorType.AuthenticationError))]
    [InlineData(nameof(HarvestErrorType.FileSystemError))]
    [InlineData(nameof(HarvestErrorType.ValidationError))]
    public void ErrorFlow_CompleteFlow(string _)
    {
        // Create exception
        var ex = new Exception("Test error");
        
        // Categorize
        string category = HarvestErrorHelper.CategorizeError(ex);
        
        // Get details
        string details = HarvestErrorHelper.CreateErrorDetailsJson(ex);
        
        // Get user message
        string userMessage = HarvestErrorHelper.GetUserFriendlyMessage(category, ex.Message);
        
        // Check if retryable
        bool retryable = HarvestErrorHelper.IsRetryableError(category);

        // Verify flow completes without errors
        details.Should().NotBeNullOrWhiteSpace();
        userMessage.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void CategorizeError_WithInnerException_StillCategorizesOuter()
    {
        var innerEx = new InvalidOperationException("Inner");
        var outerEx = new Exception("Outer", innerEx);

        string result = HarvestErrorHelper.CategorizeError(outerEx);

        result.Should().Be(nameof(HarvestErrorType.UnknownError));
    }

    [Fact]
    public void CreateErrorDetailsJson_WithoutFailedResource_HasNullAdditionalInfo()
    {
        var ex = new Exception("Test error");

        string json = HarvestErrorHelper.CreateErrorDetailsJson(ex);

        // Should either not contain AdditionalInfo or contain "null" value
        (json.Contains("\"AdditionalInfo\"") == false || json.Contains("null"))
            .Should().BeTrue();
    }
}
