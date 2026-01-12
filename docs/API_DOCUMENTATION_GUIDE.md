# API Documentation Guide

This guide provides templates and standards for documenting all classes in the Farm.Web.Api project.

## Documentation Priority

### Tier 1: Core Services (MUST document)
- `GcodeFilesService` ✅ COMPLETED
- `Model3DFileService` - 3D model management
- `FolderManagementService` - Virtual folder organization  
- `TagService` - Tag management
- `ChunkedUploadService` - Large file uploads
- `PrintersService` - Printer management
- `DiscoveryProxyService` - Network discovery

### Tier 2: Supporting Services (SHOULD document)
- `FileManagementService` - File utilities
- `FileIntegrityService` - File validation
- `FilamentTypeService` - Filament management
- `WorkerAuthService` - Worker authentication
- `ArtifactsService` - Build artifacts
- `CatalogServiceAdapter` - Catalog operations

### Tier 3: Infrastructure (CAN document)
- Background services
- Health checks
- Authorization handlers
- Utility classes

## Documentation Templates

### Service Class Template

```csharp
/// <summary>
/// Service for [primary purpose] including [key features].
/// </summary>
/// <remarks>
/// [Additional context about architecture, dependencies, or patterns used]
/// Key responsibilities:
/// - [Responsibility 1]
/// - [Responsibility 2]
/// - [Responsibility 3]
/// </remarks>
public class ServiceNameService : IServiceName
{
    /// <summary>
    /// Creates a new instance of [ServiceName].
    /// </summary>
    /// <param name="dependency1">Description of dependency</param>
    /// <param name="dependency2">Description of dependency</param>
    public ServiceNameService(
        IDependency1 dependency1,
        IDependency2 dependency2)
    {
        // Constructor body
    }

    /// <summary>
    /// [Action verb] [what it does] [with what parameters].
    /// </summary>
    /// <param name="param1">Description of parameter</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Description of return value</returns>
    /// <exception cref="ExceptionType">When this exception is thrown</exception>
    /// <remarks>
    /// Additional implementation notes, side effects, or important behavior.
    /// </remarks>
    public async Task<ReturnType> MethodNameAsync(string param1, CancellationToken ct)
    {
        // Method body
    }
}
```

### Controller Template

```csharp
/// <summary>
/// API controller for managing [resource type] operations.
/// </summary>
/// <remarks>
/// Provides RESTful endpoints for [brief description of what the controller does].
/// All endpoints require [authentication/authorization details if applicable].
/// </remarks>
[ApiController]
[Route("api/[controller]")]
public class ResourceController : ControllerBase
{
    /// <summary>
    /// Retrieves [what] from [where].
    /// </summary>
    /// <param name="id">Unique identifier</param>
    /// <returns>Response containing [what]</returns>
    /// <response code="200">Successfully retrieved the resource</response>
    /// <response code="404">Resource not found</response>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(ResourceDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetAsync(Guid id)
    {
        // Method body
    }
}
```

### Interface Template

```csharp
/// <summary>
/// Contract for [purpose of interface].
/// </summary>
/// <remarks>
/// Implementations must [key requirements or guarantees].
/// </remarks>
public interface IServiceName
{
    /// <summary>
    /// [Action] [what] [when/where/how].
    /// </summary>
    /// <param name="param">Parameter description</param>
    /// <returns>Return value description</returns>
    Task<ReturnType> MethodAsync(string param);
}
```

### DTO/Record Template

```csharp
/// <summary>
/// Data transfer object representing [what this represents].
/// </summary>
/// <param name="Property1">Description of property</param>
/// <param name="Property2">Description of property</param>
/// <remarks>
/// Used for [specific use case or API endpoint].
/// </remarks>
public record ResourceDto(
    Guid Id,
    string Name,
    DateTime CreatedAt
);
```

## Documentation Standards

### XML Doc Tags to Use

1. **`<summary>`** - Brief description (1-2 sentences) of what the class/method does
2. **`<remarks>`** - Detailed explanation, architectural notes, usage patterns
3. **`<param>`** - Every parameter must be documented
4. **`<returns>`** - What the method returns and under what conditions
5. **`<exception>`** - All exceptions that can be thrown
6. **`<example>`** - Code examples for complex APIs (optional but helpful)
7. **`<seealso>`** - Related classes or methods (optional)

### Writing Guidelines

1. **Be Specific**: "Uploads a 3D model file" not "Handles upload"
2. **Include Context**: Mention virtual folders, database tracking, or physical storage
3. **Document Side Effects**: Does it modify database? Write files? Send notifications?
4. **Explain "Why"**: Not just "what" but why this approach or pattern
5. **Use Active Voice**: "Creates a folder" not "A folder is created"
6. **Include Validation**: Document what validation occurs and when exceptions are thrown
7. **Mention Async Behavior**: Note if operations are long-running or use background processing

### Common Patterns to Document

1. **Virtual Folder Architecture**:
   ```
   /// <remarks>
   /// Uses virtual folder architecture where folders exist only in database for organizational
   /// purposes. Physical files are stored in flat directory structure with GUID-based names.
   /// </remarks>
   ```

2. **Entity Framework Operations**:
   ```
   /// <remarks>
   /// Uses Entity Framework change tracking. Modified entities are automatically detected
   /// and persisted when SaveChangesAsync is called.
   /// </remarks>
   ```

3. **Transaction Handling**:
   ```
   /// <remarks>
   /// All database operations are performed within a single transaction via UnitOfWork.
   /// If any operation fails, all changes are rolled back.
   /// </remarks>
   ```

4. **Best-Effort Operations**:
   ```
   /// <remarks>
   /// Thumbnail generation is best-effort - upload succeeds even if thumbnail generation fails.
   /// Failed thumbnail generation is logged but doesn't prevent file upload.
   /// </remarks>
   ```

5. **Security Considerations**:
   ```
   /// <remarks>
   /// All file paths are validated via IsSafePath to prevent directory traversal attacks.
   /// Files are stored with GUID-based names to prevent naming collisions and security issues.
   /// </remarks>
   ```

## Automation Script

To identify undocumented classes, run:

```bash
# Find classes missing documentation
find src/api -name "*.cs" -type f -exec grep -L "/// <summary>" {} \;

# Count documented vs undocumented classes
echo "Documented:"
grep -r "/// <summary>" src/api --include="*.cs" | wc -l
echo "Total classes:"
grep -r "public class\|public interface\|public record" src/api --include="*.cs" | wc -l
```

## Documentation Checklist

When documenting a class, ensure:

- [ ] Class has `<summary>` and `<remarks>` tags
- [ ] All public methods have `<summary>` tags
- [ ] All parameters have `<param>` tags
- [ ] All return values have `<returns>` tags  
- [ ] All exceptions have `<exception>` tags
- [ ] Complex logic has `<remarks>` explaining the approach
- [ ] Dependencies are explained in constructor docs
- [ ] Side effects are documented
- [ ] Security considerations are noted
- [ ] Performance characteristics are mentioned if relevant

## Examples of Well-Documented Classes

1. **GcodeFilesService** - Comprehensive service documentation
   - Class-level documentation with architecture explanation
   - All methods documented with purpose and parameters
   - Helper methods in organized regions
   - Security and validation notes included

2. **FolderManagementService** - Simple service documentation
   - Clear purpose and scope
   - Interface contract documented
   - Single responsibility clearly stated

3. **GcodeFilesController** - Controller documentation
   - HTTP methods and status codes documented
   - Request/response types specified
   - Error scenarios documented

## Tips for Quick Documentation

1. **Start with the interface** - Document interfaces first, then implementations can reference them
2. **Use regions** - Group related methods and document the region purpose
3. **Copy patterns** - Reuse documentation patterns from similar methods
4. **Document as you code** - Add docs when writing new code, not as cleanup
5. **Use IntelliSense** - Let IDE help you fill in parameter names and types
6. **Review during code review** - Make documentation a PR requirement

## Additional Resources

- [Microsoft XML Documentation](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/xmldoc/)
- [Documentation Best Practices](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/xmldoc/recommended-tags)
- [StyleCop Documentation Rules](https://github.com/DotNetAnalyzers/StyleCopAnalyzers/blob/master/documentation/DocumentationRules.md)
