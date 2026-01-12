# Primary Constructor Migration Guide

## Overview

This guide documents the standardization of constructor patterns across the PrintFarmer codebase to use C# 11+ primary constructors consistently. This refactoring improves code clarity, reduces boilerplate, and modernizes the codebase for .NET 9.0.

## What Are Primary Constructors?

Primary constructors (introduced in C# 11, enhanced in C# 12) allow you to define constructor parameters directly in the class declaration:

### Before (Traditional Constructor)
```csharp
public class MyService : IMyService
{
    private readonly IDependency _dependency;
    private readonly ILogger<MyService> _logger;

    public MyService(IDependency dependency, ILogger<MyService> logger)
    {
        _dependency = dependency ?? throw new ArgumentNullException(nameof(dependency));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }
}
```

### After (Primary Constructor)
```csharp
public class MyService(IDependency dependency, ILogger<MyService> logger) : IMyService
{
    private readonly IDependency _dependency = dependency ?? throw new ArgumentNullException(nameof(dependency));
    private readonly ILogger<MyService> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
}
```

## Benefits

1. **Reduced Boilerplate**: Eliminates explicit constructor method definition
2. **Improved Readability**: Constructor parameters are immediately visible in class declaration
3. **Consistency**: Standardized pattern across entire codebase
4. **Modern C#**: Leverages latest language features (C# 11+)
5. **Maintainability**: Fewer lines of code to maintain

## Conversion Patterns

### Pattern 1: Basic Conversion

**Before:**
```csharp
public class LocationsController : ControllerBase
{
    private readonly ILocationService _locationService;
    private readonly ILogger<LocationsController> _logger;

    public LocationsController(ILocationService locationService, ILogger<LocationsController> logger)
    {
        _locationService = locationService ?? throw new ArgumentNullException(nameof(locationService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }
}
```

**After:**
```csharp
public class LocationsController(
    ILocationService locationService,
    ILogger<LocationsController> logger) : ControllerBase
{
    private readonly ILocationService _locationService = locationService ?? throw new ArgumentNullException(nameof(locationService));
    private readonly ILogger<LocationsController> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
}
```

### Pattern 2: With Initialization Logic

When the constructor performs side effects (like creating directories), move logic to a static helper method:

**Before:**
```csharp
public class Model3DFilesController : ControllerBase
{
    private readonly IFileSystem _fileSystem;
    private readonly string _modelsPath;

    public Model3DFilesController(IConfiguration configuration, IFileSystem fileSystem)
    {
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        
        string configPath = configuration["ModelStorage:Path"] ?? "models";
        _modelsPath = Path.IsPathRooted(configPath)
            ? configPath
            : Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), configPath));

        if (!_fileSystem.DirectoryExists(_modelsPath))
        {
            _fileSystem.CreateDirectory(_modelsPath);
        }
    }
}
```

**After:**
```csharp
public class Model3DFilesController(
    IConfiguration configuration,
    IFileSystem fileSystem) : ControllerBase
{
    private readonly IFileSystem _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
    private readonly string _modelsPath = InitializeModelsPath(configuration, fileSystem);

    private static string InitializeModelsPath(IConfiguration configuration, IFileSystem fileSystem)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        string configPath = configuration["ModelStorage:Path"] ?? "models";
        string modelsPath = Path.IsPathRooted(configPath)
            ? configPath
            : Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), configPath));

        if (!fileSystem.DirectoryExists(modelsPath))
        {
            fileSystem.CreateDirectory(modelsPath);
        }
        
        return modelsPath;
    }
}
```

### Pattern 3: Side-Effect Initialization

For initialization that's only needed for side effects (not for storing a value), use a helper with an unused field:

**Before:**
```csharp
public SlicingSubmissionController(IConfiguration cfg, ITempPathProvider tempPathProvider)
{
    ArgumentNullException.ThrowIfNull(cfg);
    ArgumentNullException.ThrowIfNull(tempPathProvider);
    string tempRoot = Path.GetFullPath(tempPathProvider.GetTempRoot());
    _ = Directory.CreateDirectory(tempRoot);
}
```

**After:**
```csharp
public class SlicingSubmissionController(
    IConfiguration cfg,
    ITempPathProvider tempPathProvider) : ControllerBase
{
    // Initialize temp directory (executed during construction via field initializer)
    private readonly string _unusedTempInitializer = InitializeTempRoot(cfg, tempPathProvider);
    
    private static string InitializeTempRoot(IConfiguration cfg, ITempPathProvider tempPathProvider)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        ArgumentNullException.ThrowIfNull(tempPathProvider);
        string tempRoot = Path.GetFullPath(tempPathProvider.GetTempRoot());
        _ = Directory.CreateDirectory(tempRoot);
        return string.Empty; // Return empty string since we only care about side effect
    }
}
```

**Note:** This pattern will generate analyzer warnings (CA1823, S1144) about unused fields. This is acceptable as the field is intentionally used only for its initialization side effect.

### Pattern 4: Lazy Initialization

When a field depends on another field or service instance and can't be initialized inline, use lazy initialization:

**Before:**
```csharp
public class UnifiedSettingsController : ControllerBase
{
    private readonly ISettingsService _modularSettingsService;
    private readonly Dictionary<string, string> _keyNameToClassNameMap;

    public UnifiedSettingsController(ISettingsService modularSettingsService)
    {
        _modularSettingsService = modularSettingsService;
        _keyNameToClassNameMap = BuildKeyNameToClassNameMap();
    }

    private Dictionary<string, string> BuildKeyNameToClassNameMap()
    {
        // Uses _modularSettingsService to build the map
        ...
    }
}
```

**After:**
```csharp
public class UnifiedSettingsController(ISettingsService modularSettingsService) : ControllerBase
{
    private readonly ISettingsService _modularSettingsService = modularSettingsService;
    
    // Lazy-initialize this since it depends on _modularSettingsService
    private Dictionary<string, string>? _keyNameToClassNameMap;
    private Dictionary<string, string> KeyNameToClassNameMap => _keyNameToClassNameMap ??= BuildKeyNameToClassNameMap();

    private Dictionary<string, string> BuildKeyNameToClassNameMap()
    {
        // Uses _modularSettingsService to build the map
        ...
    }
}
```

### Pattern 5: Optional Parameters

Primary constructors support default parameter values:

```csharp
public class SliceJobController(
    ISliceJobRepository jobRepository,
    ILogger<SliceJobController> logger,
    IWorkerCircuitBreakerService? circuitBreaker = null) : ControllerBase
{
    private readonly ISliceJobRepository _jobRepository = jobRepository ?? throw new ArgumentNullException(nameof(jobRepository));
    private readonly ILogger<SliceJobController> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly IWorkerCircuitBreakerService? _circuitBreaker = circuitBreaker; // Nullable, no throw
}
```

## Step-by-Step Migration Process

### 1. Identify Candidates

Find files with traditional constructors:
```bash
# Find classes with traditional constructors
grep -r "public.*Controller(" src/api/Controllers --include="*.cs" | grep -v "public class"
```

### 2. Analyze Constructor Logic

Check if the constructor has:
- ✅ Simple field assignments → Use Pattern 1
- ✅ Initialization with configuration → Use Pattern 2
- ✅ Side effects only (no field storage) → Use Pattern 3
- ✅ Dependent field initialization → Use Pattern 4
- ✅ Optional parameters → Use Pattern 5

### 3. Convert the Class

1. Move parameters from constructor method to class declaration
2. Keep parameter names as-is (camelCase)
3. Assign parameters to private readonly fields with null checks
4. Move complex initialization to static helper methods
5. Add base class specification after closing parenthesis

### 4. Test the Changes

```bash
# Build the project
cd /home/runner/work/PrintFarmer/PrintFarmer/src
dotnet build api/Farm.Web.Api.csproj -c Release

# Run tests
dotnet test farm-web.sln -c Release
```

### 5. Verify No Regressions

- ✅ Build succeeds with no new errors
- ✅ All tests pass
- ✅ Only expected warnings (unused fields for side effects)
- ✅ No behavioral changes

## Common Pitfalls

### ❌ DON'T: Use primary constructor parameters directly

Primary constructor parameters are available throughout the class, but for consistency, always assign them to private readonly fields.

**Bad:**
```csharp
public class MyController(IService service) : ControllerBase
{
    public void MyMethod()
    {
        service.DoSomething(); // Using parameter directly
    }
}
```

**Good:**
```csharp
public class MyController(IService service) : ControllerBase
{
    private readonly IService _service = service;
    
    public void MyMethod()
    {
        _service.DoSomething(); // Using private field
    }
}
```

### ❌ DON'T: Try to add logic in constructor body

Primary constructors don't have a body. Use static helper methods instead.

**Bad:**
```csharp
public class MyController(IConfig config) : ControllerBase
{
    // ERROR: Can't add constructor body with primary constructor
    {
        var path = config.GetValue("Path");
        Directory.CreateDirectory(path);
    }
}
```

**Good:**
```csharp
public class MyController(IConfig config) : ControllerBase
{
    private readonly string _path = InitializePath(config);
    
    private static string InitializePath(IConfig config)
    {
        var path = config.GetValue("Path");
        Directory.CreateDirectory(path);
        return path;
    }
}
```

### ❌ DON'T: Make initialization methods non-static unnecessarily

If the initialization method doesn't need instance state, make it static for clarity and potential performance benefits.

## Code Review Checklist

When reviewing primary constructor conversions:

- [ ] Parameters are in the class declaration (not separate constructor method)
- [ ] All parameters are assigned to private readonly fields
- [ ] Null checks are preserved using `?? throw new ArgumentNullException()`
- [ ] Complex initialization is in static helper methods
- [ ] Lazy initialization is used for dependent fields
- [ ] Build succeeds with no new errors
- [ ] Tests pass
- [ ] No behavioral changes
- [ ] Code is more readable than before

## Progress Tracking

### Completed (15 controllers)
- ✅ LocationsController
- ✅ SlicingJobsController
- ✅ SliceJobController
- ✅ AssetsController
- ✅ JobSchedulingController
- ✅ SlicingSubmissionController
- ✅ SlicerManagementController
- ✅ ArtifactsController
- ✅ RetriesController
- ✅ FilamentTypeController
- ✅ PrintJobQueueController
- ✅ SlicersController
- ✅ Model3dFilesController
- ✅ WorkersController
- ✅ UnifiedSettingsController

### Already Using Primary Constructors (~14 controllers)
- ✅ PrintersController
- ✅ CatalogController
- ✅ SpoolmanController
- ✅ GcodeHarvestController
- ✅ FileConsistencyController
- ✅ NotificationsController
- ✅ AuthController
- ✅ SetupController
- ✅ UsersController
- ✅ TagsController
- ✅ ProfilesController (Slicing)
- ✅ PrintQueueController
- ✅ And others...

### Pending (Services, Infrastructure, Backend Plugins)
- ⏳ **Core Services** (~20-30 files)
  - GcodeFilesService
  - SlicersService
  - ProfilesService
  - PrinterBackendCapabilitiesService
  - And more...
  
- ⏳ **Infrastructure** (~20-30 files)
  - Health checks
  - Cache implementations
  - File management services
  - Background services
  
- ⏳ **Backend Plugins & Repositories** (~240 files)
  - Discovery probes
  - Repository implementations
  - Validators
  - DTOs and utilities

## References

- [C# 12 Primary Constructors](https://learn.microsoft.com/en-us/dotnet/csharp/whats-new/csharp-12#primary-constructors)
- [.NET 9.0 Documentation](https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-9)
- [Dependency Injection in ASP.NET Core](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/dependency-injection)

## Questions?

If you encounter any issues or edge cases not covered in this guide:

1. Check existing converted files for similar patterns
2. Ask in the team chat
3. Document the pattern here for future reference
