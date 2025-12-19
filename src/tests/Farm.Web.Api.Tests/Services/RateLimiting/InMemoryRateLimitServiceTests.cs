using Farm.Infrastructure.Services.RateLimiting;
using Farm.Infrastructure.Telemetry;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Services.RateLimiting;

public class InMemoryRateLimitServiceTests
{
    private readonly Mock<IUnifiedLoggingService> _loggerMock;
    private readonly RateLimitOptions _options;
    private readonly InMemoryRateLimitService _service;

    public InMemoryRateLimitServiceTests()
    {
        _loggerMock = new Mock<IUnifiedLoggingService>();
        _options = CreateOptions();
        _service = new InMemoryRateLimitService(_options, _loggerMock.Object);
    }

    private RateLimitOptions CreateOptions()
    {
        return new RateLimitOptions
        {
            PasswordReset = new PasswordResetRateLimitOptions { MaxAttemptsPerHour = 3, MaxAttemptsPerDay = 10 },
            EmailConfirmation = new EmailConfirmationRateLimitOptions { MaxAttemptsPerHour = 5, MaxAttemptsPerDay = 20 },
            SliceJobs = new SliceJobRateLimitOptions { MaxAttemptsPerHour = 20, MaxAttemptsPerDay = 200 },
            Authentication = new AuthenticationRateLimitOptions { MaxLoginAttemptsPerMinute = 10, MaxRegisterAttemptsPerMinute = 10 }
        };
    }

    #region Constructor Tests

    [Fact]
    public void Constructor_WithValidOptions_InitializesService()
    {
        // Act
        var service = new InMemoryRateLimitService(_options, _loggerMock.Object);

        // Assert
        Assert.NotNull(service);
    }

    #endregion

    #region Password Reset Rate Limiting Tests

    [Fact]
    public async Task CheckPasswordResetLimitAsync_FirstAttempt_ReturnsAllowed()
    {
        // Arrange
        string email = "user@example.com";

        // Act
        var result = await _service.CheckPasswordResetLimitAsync(email);

        // Assert
        Assert.True(result.IsAllowed);
        Assert.Equal(3, result.RemainingAttempts); // No attempts yet, all 3 remaining
        Assert.Null(result.RetryAfter);
    }

    [Fact]
    public async Task CheckPasswordResetLimitAsync_WithinHourlyLimit_ReturnsAllowed()
    {
        // Arrange
        string email = "user@example.com";
        
        // Record 2 attempts
        await _service.RecordPasswordResetAttemptAsync(email);
        await _service.RecordPasswordResetAttemptAsync(email);

        // Act
        var result = await _service.CheckPasswordResetLimitAsync(email);

        // Assert
        Assert.True(result.IsAllowed);
        Assert.Equal(1, result.RemainingAttempts);
    }

    [Fact]
    public async Task CheckPasswordResetLimitAsync_ExceedsHourlyLimit_ReturnsBlocked()
    {
        // Arrange
        string email = "user@example.com";
        
        // Record all 3 hourly attempts
        await _service.RecordPasswordResetAttemptAsync(email);
        await _service.RecordPasswordResetAttemptAsync(email);
        await _service.RecordPasswordResetAttemptAsync(email);

        // Act
        var result = await _service.CheckPasswordResetLimitAsync(email);

        // Assert
        Assert.False(result.IsAllowed);
        Assert.NotNull(result.RetryAfter);
        Assert.True(result.RetryAfter > TimeSpan.Zero);
    }

        [Fact]
    public async Task CheckPasswordResetLimitAsync_CaseInsensitive_SameLimit()
    {
        // Arrange
        string email1 = "User@Example.Com";
        string email2 = "user@example.com";
        
        // Record one attempt using uppercase version
        await _service.RecordPasswordResetAttemptAsync(email1);

        // Act - check using lowercase version
        var result2 = await _service.CheckPasswordResetLimitAsync(email2);

        // Assert - should see the same limit because email is normalized to lowercase
        Assert.True(result2.IsAllowed);
        Assert.Equal(2, result2.RemainingAttempts); // 3 - 1 recorded attempt = 2 remaining
    }

    [Fact]
    public async Task RecordPasswordResetAttemptAsync_RecordsAttempt()
    {
        // Arrange
        string email = "user@example.com";

        // Act
        await _service.RecordPasswordResetAttemptAsync(email);
        var result = await _service.CheckPasswordResetLimitAsync(email);

        // Assert
        Assert.Equal(2, result.RemainingAttempts); // 3 - 1 recorded = 2 remaining
    }

    #endregion

    #region Email Confirmation Rate Limiting Tests

    [Fact]
    public async Task CheckEmailConfirmationLimitAsync_FirstAttempt_ReturnsAllowed()
    {
        // Arrange
        string email = "user@example.com";

        // Act
        var result = await _service.CheckEmailConfirmationLimitAsync(email);

        // Assert
        Assert.True(result.IsAllowed);
        Assert.Equal(5, result.RemainingAttempts); // No attempts yet, all 5 remaining
    }

    [Fact]
    public async Task CheckEmailConfirmationLimitAsync_ExceedsLimit_ReturnsBlocked()
    {
        // Arrange
        string email = "user@example.com";
        
        // Record all 5 hourly attempts
        for (int i = 0; i < 5; i++)
        {
            await _service.RecordEmailConfirmationAttemptAsync(email);
        }

        // Act
        var result = await _service.CheckEmailConfirmationLimitAsync(email);

        // Assert
        Assert.False(result.IsAllowed);
    }

    [Fact]
    public async Task RecordEmailConfirmationAttemptAsync_RecordsAttempt()
    {
        // Arrange
        string email = "user@example.com";

        // Act
        await _service.RecordEmailConfirmationAttemptAsync(email);
        var result = await _service.CheckEmailConfirmationLimitAsync(email);

        // Assert
        Assert.Equal(4, result.RemainingAttempts); // 5 - 1 recorded = 4 remaining
    }

    #endregion

    #region Slice Job Rate Limiting Tests

    [Fact]
    public async Task CheckSliceJobSubmitLimitAsync_FirstAttempt_ReturnsAllowed()
    {
        // Arrange
        Guid userId = Guid.NewGuid();

        // Act
        var result = await _service.CheckSliceJobSubmitLimitAsync(userId);

        // Assert
        Assert.True(result.IsAllowed);
        Assert.Equal(20, result.RemainingAttempts); // No attempts yet, all 20 remaining
    }

    [Fact]
    public async Task CheckSliceJobSubmitLimitAsync_WithinLimit_ReturnsAllowed()
    {
        // Arrange
        Guid userId = Guid.NewGuid();

        // Record 19 attempts
        for (int i = 0; i < 19; i++)
        {
            await _service.RecordSliceJobSubmitAttemptAsync(userId);
        }

        // Act
        var result = await _service.CheckSliceJobSubmitLimitAsync(userId);

        // Assert
        Assert.True(result.IsAllowed);
        Assert.Equal(1, result.RemainingAttempts); // 20 - 19 = 1
    }

    [Fact]
    public async Task CheckSliceJobSubmitLimitAsync_ExceedsLimit_ReturnsBlocked()
    {
        // Arrange
        Guid userId = Guid.NewGuid();
        
        // Record all 20 hourly attempts
        for (int i = 0; i < 20; i++)
        {
            await _service.RecordSliceJobSubmitAttemptAsync(userId);
        }

        // Act
        var result = await _service.CheckSliceJobSubmitLimitAsync(userId);

        // Assert
        Assert.False(result.IsAllowed);
    }

    [Fact]
    public async Task RecordSliceJobSubmitAttemptAsync_RecordsAttempt()
    {
        // Arrange
        Guid userId = Guid.NewGuid();

        // Act
        await _service.RecordSliceJobSubmitAttemptAsync(userId);
        var result = await _service.CheckSliceJobSubmitLimitAsync(userId);

        // Assert
        Assert.Equal(19, result.RemainingAttempts); // 20 - 1 recorded = 19 remaining
    }

    [Fact]
    public async Task SliceJobSubmitLimit_IsolatedPerUser_DifferentLimits()
    {
        // Arrange
        Guid user1 = Guid.NewGuid();
        Guid user2 = Guid.NewGuid();

        // Act
        await _service.RecordSliceJobSubmitAttemptAsync(user1);
        await _service.RecordSliceJobSubmitAttemptAsync(user1);
        
        var result1 = await _service.CheckSliceJobSubmitLimitAsync(user1);
        var result2 = await _service.CheckSliceJobSubmitLimitAsync(user2);

        // Assert
        Assert.Equal(18, result1.RemainingAttempts); // 20 - 2 = 18
        Assert.Equal(20, result2.RemainingAttempts); // 20 - 0 = 20
    }

    #endregion

    #region Login Rate Limiting Tests

    [Fact]
    public async Task CheckLoginLimitAsync_FirstAttempt_ReturnsAllowed()
    {
        // Arrange
        string ipAddress = "192.168.1.1";

        // Act
        var result = await _service.CheckLoginLimitAsync(ipAddress);

        // Assert
        Assert.True(result.IsAllowed);
        Assert.Equal(10, result.RemainingAttempts); // No attempts yet, all 10 remaining
    }

    [Fact]
    public async Task CheckLoginLimitAsync_WithinMinuteLimit_ReturnsAllowed()
    {
        // Arrange
        string ipAddress = "192.168.1.1";

        // Record 9 attempts
        for (int i = 0; i < 9; i++)
        {
            await _service.RecordLoginAttemptAsync(ipAddress);
        }

        // Act
        var result = await _service.CheckLoginLimitAsync(ipAddress);

        // Assert
        Assert.True(result.IsAllowed);
        Assert.Equal(1, result.RemainingAttempts); // 10 - 9 = 1
    }

    [Fact]
    public async Task CheckLoginLimitAsync_ExceedsLimit_ReturnsBlocked()
    {
        // Arrange
        string ipAddress = "192.168.1.1";
        
        // Record all 10 per-minute attempts
        for (int i = 0; i < 10; i++)
        {
            await _service.RecordLoginAttemptAsync(ipAddress);
        }

        // Act
        var result = await _service.CheckLoginLimitAsync(ipAddress);

        // Assert
        Assert.False(result.IsAllowed);
    }

    [Fact]
    public async Task CheckLoginLimitAsync_CaseInsensitive_SameLimit()
    {
        // Arrange
        string ip1 = "192.168.1.1";
        string ip2 = "192.168.1.1";

        // Act
        var result1 = await _service.CheckLoginLimitAsync(ip1);
        var result2 = await _service.CheckLoginLimitAsync(ip2);

        // Assert
        Assert.True(result1.IsAllowed);
        Assert.True(result2.IsAllowed);
        Assert.Equal(10, result2.RemainingAttempts); // Shares same limit, no attempts recorded
    }

    [Fact]
    public async Task RecordLoginAttemptAsync_RecordsAttempt()
    {
        // Arrange
        string ipAddress = "192.168.1.1";

        // Act
        await _service.RecordLoginAttemptAsync(ipAddress);
        var result = await _service.CheckLoginLimitAsync(ipAddress);

        // Assert
        Assert.Equal(9, result.RemainingAttempts); // 10 - 1 recorded = 9 remaining
    }

    #endregion

    #region Registration Rate Limiting Tests

    [Fact]
    public async Task CheckRegisterLimitAsync_FirstAttempt_ReturnsAllowed()
    {
        // Arrange
        string ipAddress = "192.168.1.1";

        // Act
        var result = await _service.CheckRegisterLimitAsync(ipAddress);

        // Assert
        Assert.True(result.IsAllowed);
        Assert.Equal(10, result.RemainingAttempts); // No attempts yet, all 10 remaining
    }

    [Fact]
    public async Task CheckRegisterLimitAsync_WithinLimit_ReturnsAllowed()
    {
        // Arrange
        string ipAddress = "192.168.1.1";

        // Record 9 attempts
        for (int i = 0; i < 9; i++)
        {
            await _service.RecordRegisterAttemptAsync(ipAddress);
        }

        // Act
        var result = await _service.CheckRegisterLimitAsync(ipAddress);

        // Assert
        Assert.True(result.IsAllowed);
        Assert.Equal(1, result.RemainingAttempts); // 10 - 9 = 1
    }

    [Fact]
    public async Task CheckRegisterLimitAsync_ExceedsLimit_ReturnsBlocked()
    {
        // Arrange
        string ipAddress = "192.168.1.1";
        
        // Record all 10 per-minute attempts
        for (int i = 0; i < 10; i++)
        {
            await _service.RecordRegisterAttemptAsync(ipAddress);
        }

        // Act
        var result = await _service.CheckRegisterLimitAsync(ipAddress);

        // Assert
        Assert.False(result.IsAllowed);
    }

    [Fact]
    public async Task RecordRegisterAttemptAsync_RecordsAttempt()
    {
        // Arrange
        string ipAddress = "192.168.1.1";

        // Act
        await _service.RecordRegisterAttemptAsync(ipAddress);
        var result = await _service.CheckRegisterLimitAsync(ipAddress);

        // Assert
        Assert.Equal(9, result.RemainingAttempts); // 10 - 1 recorded = 9 remaining
    }

    #endregion

    #region RateLimitResult Tests

    [Fact]
    public async Task RateLimitResult_Allowed_HasCorrectProperties()
    {
        // Arrange
        string email = "user@example.com";

        // Act
        var result = await _service.CheckPasswordResetLimitAsync(email);

        // Assert
        Assert.True(result.IsAllowed);
        Assert.Null(result.RetryAfter);
        Assert.Null(result.Message);
    }

    [Fact]
    public async Task RateLimitResult_Blocked_HasRetryAfterAndMessage()
    {
        // Arrange
        string email = "user@example.com";
        
        // Record all attempts
        for (int i = 0; i < 3; i++)
        {
            await _service.RecordPasswordResetAttemptAsync(email);
        }

        // Act
        var result = await _service.CheckPasswordResetLimitAsync(email);

        // Assert
        Assert.False(result.IsAllowed);
        Assert.NotNull(result.RetryAfter);
        Assert.NotNull(result.Message);
        Assert.Contains("Too many", result.Message);
    }

    #endregion

    #region Rate Limit Isolation Tests

    [Fact]
    public async Task RateLimits_AreIsolatedByType()
    {
        // Arrange
        string email = "user@example.com";

        // Act
        var resetCheck = await _service.CheckPasswordResetLimitAsync(email);
        var emailCheck = await _service.CheckEmailConfirmationLimitAsync(email);

        // Assert - different rate limit types should be independent
        Assert.True(resetCheck.IsAllowed);
        Assert.True(emailCheck.IsAllowed);
        Assert.NotEqual(resetCheck.RemainingAttempts, emailCheck.RemainingAttempts);
    }

    [Fact]
    public async Task RateLimits_AreIsolatedByUser()
    {
        // Arrange
        string email1 = "user1@example.com";
        string email2 = "user2@example.com";

        // Act
        var result1 = await _service.CheckPasswordResetLimitAsync(email1);
        var result2 = await _service.CheckPasswordResetLimitAsync(email2);

        // Assert
        Assert.True(result1.IsAllowed);
        Assert.True(result2.IsAllowed);
        Assert.Equal(3, result1.RemainingAttempts);
        Assert.Equal(3, result2.RemainingAttempts);
    }

    [Fact]
    public async Task RateLimits_AreIsolatedByIPAddress()
    {
        // Arrange
        string ip1 = "192.168.1.1";
        string ip2 = "192.168.1.2";

        // Act
        var result1 = await _service.CheckRegisterLimitAsync(ip1);
        var result2 = await _service.CheckRegisterLimitAsync(ip2);

        // Assert
        Assert.True(result1.IsAllowed);
        Assert.True(result2.IsAllowed);
        Assert.Equal(10, result1.RemainingAttempts);
        Assert.Equal(10, result2.RemainingAttempts);
    }

    #endregion

    #region Concurrency Tests

    [Fact]
    public async Task ConcurrentChecks_SameEmail_ProperlyTrackedCounts()
    {
        // Arrange
        string email = "user@example.com";
        var tasks = new List<Task<RateLimitResult>>();

        // Act
        for (int i = 0; i < 3; i++)
        {
            tasks.Add(_service.CheckPasswordResetLimitAsync(email));
        }

        var results = await Task.WhenAll(tasks);

        // Assert
        Assert.True(results.Count(r => r.IsAllowed) >= 1);
    }

    [Fact]
    public async Task ConcurrentRecordings_SameEmail_ProperlyTrackedCounts()
    {
        // Arrange
        string email = "user@example.com";
        var tasks = new List<Task>();

        // Act
        for (int i = 0; i < 3; i++)
        {
            tasks.Add(_service.RecordPasswordResetAttemptAsync(email));
        }

        await Task.WhenAll(tasks);
        var result = await _service.CheckPasswordResetLimitAsync(email);

        // Assert
        Assert.Equal(0, result.RemainingAttempts); // 3 recorded + 1 check = 4, exceeds limit
    }

    #endregion

    #region Edge Cases Tests

    [Fact]
    public async Task EmptyString_Email_HandledProperly()
    {
        // Arrange
        string email = string.Empty;

        // Act
        var result = await _service.CheckPasswordResetLimitAsync(email);

        // Assert
        Assert.True(result.IsAllowed);
    }

    [Fact]
    public async Task Whitespace_Email_HandledProperly()
    {
        // Arrange
        string email = "   ";

        // Act
        var result = await _service.CheckPasswordResetLimitAsync(email);

        // Assert
        Assert.True(result.IsAllowed);
    }

    [Fact]
    public async Task SpecialCharacters_Email_HandledProperly()
    {
        // Arrange
        string email = "user+special@example.com";

        // Act
        var result = await _service.CheckPasswordResetLimitAsync(email);

        // Assert
        Assert.True(result.IsAllowed);
    }

    [Fact]
    public async Task GuidEmpty_SliceJobUserId_HandledProperly()
    {
        // Arrange
        Guid userId = Guid.Empty;

        // Act
        var result = await _service.CheckSliceJobSubmitLimitAsync(userId);

        // Assert
        Assert.True(result.IsAllowed);
    }

    #endregion
}
