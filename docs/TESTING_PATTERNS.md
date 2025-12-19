# Testing Patterns & Best Practices

This document captures common patterns and best practices discovered during test development for PrintFarmer.

## SignalR Hub Testing

### Required Mocks
```csharp
private readonly Mock<IHubCallerClients> _clientsMock;
private readonly Mock<ISingleClientProxy> _callerMock;  // NOTE: ISingleClientProxy, not IClientProxy
private readonly Mock<IClientProxy> _groupMock;
private readonly Mock<IGroupManager> _groupsMock;
private readonly Mock<HubCallerContext> _contextMock;
```

### Setup Pattern
```csharp
// Hub context
_contextMock.Setup(c => c.ConnectionId).Returns("test-connection-id");
_contextMock.Setup(c => c.ConnectionAborted).Returns(CancellationToken.None);

// Clients
_clientsMock.Setup(c => c.Caller).Returns(_callerMock.Object);
_clientsMock.Setup(c => c.Group(It.IsAny<string>())).Returns(_groupMock.Object);

// Instantiate hub with properties
_hub = new YourHub(dependencies...)
{
    Clients = _clientsMock.Object,
    Groups = _groupsMock.Object,
    Context = _contextMock.Object
};
```

### Verification Pattern
```csharp
// Verify SendCoreAsync (not SendAsync)
_callerMock.Verify(c => c.SendCoreAsync(
    "eventname",
    It.Is<object[]>(args => args.Length == 1 && ReferenceEquals(args[0], expectedData)),
    It.IsAny<CancellationToken>()), 
    Times.Once);

// Verify group operations
_groupsMock.Verify(g => g.AddToGroupAsync(
    "connection-id",
    "group-name",
    It.IsAny<CancellationToken>()), 
    Times.Once);
```

## Record Type DTOs

### Instantiation with Positional Parameters
```csharp
// ✅ CORRECT - Use named parameters
var dto = new DiscoveryProgressDto(
    SessionId: "test-session",
    CurrentNetwork: "192.168.1.0/24",
    CurrentIp: "192.168.1.100",
    TotalIps: 100,
    ScannedIps: 50,
    PrintersFound: 2,
    PrintersExcluded: 0,
    ProgressPercentage: 50,
    Status: DiscoveryStatus.Scanning
);

// ❌ WRONG - Object initializer syntax doesn't work with positional records
var dto = new DiscoveryProgressDto
{
    SessionId = "test-session",  // Compilation error!
    ProgressPercentage = 50
};
```

### Verification in Mocks
```csharp
// ✅ Use ReferenceEquals for record comparison
It.Is<object[]>(args => args.Length == 1 && ReferenceEquals(args[0], expectedDto))

// ⚠️ Avoid == operator (causes warnings about reference comparison)
It.Is<object[]>(args => args.Length == 1 && args[0] == expectedDto)  // Warning CS0252
```

## Factory Pattern Testing

### Constructor Null Validation
```csharp
[Fact]
public void Constructor_WithNullDependency_ThrowsArgumentNullException()
{
    // Test EACH constructor parameter individually
    Assert.Throws<ArgumentNullException>(() => new Factory(
        null!,  // Test this parameter
        mock2.Object,
        mock3.Object
    ));
}
```

### GetClient Pattern
```csharp
[Fact]
public void GetClient_WithValidBackend_ReturnsCorrectClient()
{
    // Arrange
    var factory = CreateFactory();

    // Act
    var client = factory.GetClient(PrinterBackend.Moonraker);

    // Assert
    Assert.NotNull(client);
    Assert.IsAssignableFrom<IBackendClient>(client);  // Marker interface
    Assert.IsAssignableFrom<IMoonrakerClient>(client);  // Specific interface
    Assert.Same(_moonMock.Object, client);  // Reference equality
}
```

### Error Handling
```csharp
[Fact]
public void GetClient_WithInvalidBackend_ThrowsArgumentException()
{
    // Arrange
    var factory = CreateFactory();

    // Act & Assert
    var ex = Assert.Throws<ArgumentException>(() => 
        factory.GetClient((PrinterBackend)999));
    Assert.Contains("Unsupported printer backend", ex.Message);
}
```

## Logger Mock Verification

### ILogger Extension Method Pattern
```csharp
// Verify logging calls using ILogger extension method patterns
_loggerMock.Verify(
    x => x.Log(
        LogLevel.Information,
        It.IsAny<EventId>(),
        It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("expected text")),
        It.IsAny<Exception>(),
        It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
    Times.Once);
```

## Test Organization

### File Structure
```
/tests/Farm.Web.Api.Tests/
├── Controllers/
│   └── PrintersControllerTests.cs
├── Services/
│   ├── BackendClientFactoryTests.cs
│   └── Printers/
│       └── PrintersServiceTests.cs
├── Hubs/
│   └── PrinterHubTests.cs
└── Integration/
    └── PrinterIntegrationTests.cs
```

### Test Naming Convention
```csharp
// Pattern: MethodName_Scenario_ExpectedBehavior
[Fact]
public void GetClient_WithMoonrakerBackend_ReturnsMoonrakerClient()
{
    // ...
}

[Fact]
public void Constructor_WithNullLogger_ThrowsArgumentNullException()
{
    // ...
}
```

### Test Categories
```csharp
// Use Arrange-Act-Assert pattern
[Fact]
public void SomeTest()
{
    // Arrange - Setup test data and mocks
    var expected = "value";
    var mock = new Mock<IDependency>();
    
    // Act - Execute the method under test
    var result = _sut.Method(expected);
    
    // Assert - Verify expected behavior
    Assert.Equal(expected, result);
}
```

## Common Pitfalls

### ❌ Wrong ClientProxy Type
```csharp
// WRONG - IClientProxy doesn't work for Caller
private readonly Mock<IClientProxy> _callerMock;

// CORRECT - Use ISingleClientProxy
private readonly Mock<ISingleClientProxy> _callerMock;
```

### ❌ Missing Hub Property Assignment
```csharp
// WRONG - Hub properties not set
var hub = new PrinterHub(deps...);

// CORRECT - Assign mock properties
var hub = new PrinterHub(deps...)
{
    Clients = _clientsMock.Object,
    Groups = _groupsMock.Object,
    Context = _contextMock.Object
};
```

### ❌ Record Equality Comparison
```csharp
// WRONG - == causes reference comparison warning
args[0] == expectedDto  // Warning CS0252

// CORRECT - Use ReferenceEquals explicitly
ReferenceEquals(args[0], expectedDto)
```

## Coverage Goals

### Target Metrics
- **Overall Method Coverage**: 50% (current: 34.3%)
- **Critical Components**: 80%+ (controllers, core services)
- **Utility Classes**: 60%+ (helpers, extensions)
- **Integration Tests**: Key user workflows covered

### High-Impact Areas
1. **Controllers** - REST endpoints (highest coverage per test)
2. **SignalR Hubs** - Real-time communication
3. **Core Services** - Business logic (PrintersService, SlicerService, etc.)
4. **Repository Layer** - Data access patterns
5. **Integration Tests** - End-to-end workflows

### Coverage Calculation
- **Approximate ratio**: ~175 tests per 1% method coverage increase
- **Session efficiency**: ~35 tests in 45 minutes = ~0.2% coverage gain
- **Target gap**: 15.7 percentage points = ~2,750 additional tests
- **Realistic timeline**: 6-8 sessions of similar scope to reach 50%

## Testing Checklist

Before committing new tests:
- [ ] All tests pass: `dotnet test ./farm-web.sln -c Debug`
- [ ] Build succeeds: `dotnet build ./farm-web.sln -c Debug`
- [ ] No new warnings introduced
- [ ] Coverage report shows improvement
- [ ] Tests follow naming conventions
- [ ] Arrange-Act-Assert pattern used consistently
- [ ] Null validation tests for all constructor parameters
- [ ] Error cases tested (exceptions, invalid inputs)
- [ ] Edge cases covered (empty collections, boundary values)
- [ ] Integration with existing test suite verified

## Useful Commands

```bash
# Run all tests
dotnet test ./farm-web.sln -c Debug

# Run specific test class
dotnet test --filter "FullyQualifiedName~BackendClientFactoryTests"

# Run with coverage
dotnet test --collect:"XPlat Code Coverage" --results-directory ./tests/coverage

# Count tests
find tests -name "*Tests.cs" | wc -l

# Check coverage
dotnet test ./farm-web.sln -c Debug 2>&1 | grep -A 15 "Module"
```

## References

- [xUnit Documentation](https://xunit.net/)
- [Moq Documentation](https://github.com/moq/moq4)
- [ASP.NET Core Testing](https://docs.microsoft.com/en-us/aspnet/core/test/)
- [SignalR Testing](https://docs.microsoft.com/en-us/aspnet/core/signalr/testing)
- [FluentAssertions](https://fluentassertions.com/) (if used in project)
