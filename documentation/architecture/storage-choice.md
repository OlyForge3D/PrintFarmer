# ADR: Storage Strategy for Slicer Microservices

**Status**: Accepted  
**Date**: 2025-09-07  
**Decision Makers**: PrintFarmer Development Team  
**Technical Story**: [Epic #54 - Slicer Microservices Architecture](https://github.com/jpapiez/PrintFarmer/issues/54)

## Context

PrintFarmer's slicer microservices require efficient storage and retrieval of 3D model files (input) and generated G-code files (output). The storage system must handle files ranging from kilobytes to gigabytes, provide secure access for distributed workers, and support both development and production deployment scenarios.

### Requirements

**Functional Requirements:**
- Store and retrieve 3D model files (.stl, .obj, .3mf, .ply)
- Store and retrieve generated G-code files (.gcode)
- Support file metadata and content-type detection
- Secure file access with expiring URLs
- File lifecycle management and cleanup
- Support for temporary working directories

**Non-Functional Requirements:**
- Handle files up to 1GB in size
- Support 100+ concurrent file operations
- 99.9% availability for file operations
- Cross-platform compatibility (Windows, Linux, macOS)
- Simple deployment and operational requirements
- Cost-effective for small to medium deployments

### Current Context
- PrintFarmer supports both monolithic and microservices deployment
- Development environments primarily use local development machines
- Production deployments range from single Docker containers to Kubernetes clusters
- No existing cloud storage infrastructure
- Team familiar with file system operations

## Decision

**We will implement a local file system storage strategy with abstraction for future cloud storage integration.**

### Primary Implementation: LocalSlicerFileStorage

```csharp
public class LocalSlicerFileStorage : ISlicerFileStorage
{
    private readonly LocalFileStorageOptions _options;
    
    // Directory structure:
    // {BasePath}/
    //   models/           # Input 3D model files
    //     {userId}/
    //       {jobId}/
    //   gcode/           # Generated G-code files  
    //     {userId}/
    //       {jobId}/
    //   temp/            # Temporary working files
    //     {workerId}/
    //       {jobId}/
}
```

### File Organization Strategy
- **User Isolation**: Files organized by user ID to support multi-tenancy
- **Job Scoping**: Each slicing job gets dedicated subdirectory
- **Worker Isolation**: Temporary files isolated by worker ID
- **Type Separation**: Clear separation between models and G-code
- **Automatic Cleanup**: Background cleanup of expired temporary files

## Alternatives Considered

### 1. Cloud Object Storage (S3, Azure Blob, GCS)
**Pros:**
- Virtually unlimited storage capacity
- Built-in durability and availability (99.999999999%)
- Global content delivery and edge caching
- Automatic backup and versioning
- Pay-per-use pricing model

**Cons:**
- Network latency for every file operation
- Requires cloud provider account and billing
- Vendor lock-in and migration complexity
- Egress costs for large files
- Additional complexity for development environments

**Decision**: Rejected for primary implementation due to operational complexity and cost for typical PrintFarmer deployments

### 2. Database Blob Storage (SQL Server FILESTREAM, PostgreSQL Large Objects)
**Pros:**
- ACID transaction guarantees
- Integrated with existing database infrastructure
- Strong consistency and backup integration
- Familiar operational model

**Cons:**
- Poor performance for large files
- Database size inflation affects all operations
- Backup and restore complexity
- Limited to single database instance scaling
- Not suitable for distributed worker access

**Decision**: Rejected due to performance characteristics and scaling limitations

### 3. Network File Systems (NFS, SMB/CIFS)
**Pros:**
- Centralized storage accessible by all workers
- Familiar file system semantics
- Good performance over local networks
- Supports existing file-based workflows

**Cons:**
- Single point of failure without HA setup
- Network latency affects all operations
- Complex access control management
- Platform compatibility issues (Windows/Linux)
- Requires additional infrastructure setup

**Decision**: Rejected due to reliability and complexity concerns

### 4. Distributed File Systems (MinIO, Ceph, GlusterFS)
**Pros:**
- High availability and fault tolerance
- Horizontal scaling capabilities
- S3-compatible APIs available
- No vendor lock-in

**Cons:**
- Significant operational complexity
- Requires multiple nodes for redundancy
- Overkill for small to medium deployments
- Learning curve for operations team
- Additional infrastructure requirements

**Decision**: Rejected as over-engineered for current requirements

## Implementation Details

### Directory Structure
```
/var/printfarmer/storage/
├── models/                    # Input 3D model files
│   └── {userId}/
│       └── {jobId}/
│           ├── model.stl
│           └── metadata.json
├── gcode/                     # Generated G-code files
│   └── {userId}/
│       └── {jobId}/
│           ├── output.gcode
│           └── statistics.json
└── temp/                      # Temporary working files
    └── {workerId}/
        └── {jobId}/
            ├── preprocessed.stl
            ├── settings.ini
            └── logs/
```

### File Access Patterns
```csharp
public async Task<string> UploadFileAsync(string key, Stream fileStream, 
    string contentType, CancellationToken cancellationToken = default)
{
    var filePath = GetFilePath(key);
    Directory.CreateDirectory(Path.GetDirectoryName(filePath));
    
    using var fileWriteStream = File.Create(filePath);
    await fileStream.CopyToAsync(fileWriteStream, cancellationToken);
    
    return GenerateFileUrl(key);
}
```

### Security Implementation
```csharp
private bool IsValidPath(string path)
{
    // Prevent directory traversal attacks
    var normalizedPath = Path.GetFullPath(path);
    return normalizedPath.StartsWith(_options.BasePath, StringComparison.OrdinalIgnoreCase);
}

private string GenerateSecureUrl(string key, TimeSpan expiration)
{
    // Generate URL with HMAC signature for validation
    var payload = $"{key}:{DateTimeOffset.UtcNow.Add(expiration).ToUnixTimeSeconds()}";
    var signature = ComputeHmac(payload, _options.SecretKey);
    return $"/api/files/{key}?expires={expiration}&signature={signature}";
}
```

### Storage Configuration
```json
{
  "LocalFileStorage": {
    "BasePath": "/var/printfarmer/storage",
    "MaxFileSizeBytes": 1073741824,
    "TempFileRetentionHours": 24,
    "CompletedJobRetentionDays": 30,
    "EnableCompression": false,
    "SecretKey": "{generated-secret-key}"
  }
}
```

## Storage Abstraction Layer

### Interface Design
```csharp
public interface ISlicerFileStorage
{
    Task<string> UploadFileAsync(string key, Stream fileStream, string contentType, CancellationToken cancellationToken = default);
    Task<string> UploadFileAsync(string key, byte[] fileData, string contentType, CancellationToken cancellationToken = default);
    Task<Stream> DownloadFileAsync(string keyOrUrl, CancellationToken cancellationToken = default);
    Task<byte[]> DownloadFileBytesAsync(string keyOrUrl, CancellationToken cancellationToken = default);
    Task<bool> FileExistsAsync(string keyOrUrl, CancellationToken cancellationToken = default);
    Task DeleteFileAsync(string keyOrUrl, CancellationToken cancellationToken = default);
    Task<SlicerFileMetadata> GetFileMetadataAsync(string keyOrUrl, CancellationToken cancellationToken = default);
    Task CleanupExpiredFilesAsync(TimeSpan maxAge, CancellationToken cancellationToken = default);
}
```

### Future Cloud Storage Support
The abstraction enables seamless integration with cloud storage:

```csharp
// Factory pattern for storage provider selection
services.AddSingleton<ISlicerFileStorage>(provider =>
{
    var config = provider.GetRequiredService<IConfiguration>();
    return config.GetValue<string>("Storage:Provider") switch
    {
        "Local" => provider.GetRequiredService<LocalSlicerFileStorage>(),
        "S3" => provider.GetRequiredService<S3SlicerFileStorage>(),
        "Azure" => provider.GetRequiredService<AzureSlicerFileStorage>(),
        _ => throw new InvalidOperationException("Unknown storage provider")
    };
});
```

## Trade-offs

### Advantages of Local File System
✅ **Zero External Dependencies**: No cloud accounts or additional services required  
✅ **Predictable Performance**: Direct disk I/O without network latency  
✅ **Simple Operations**: Standard file system tools for backup and maintenance  
✅ **Cost Effective**: Only storage hardware costs, no ongoing service fees  
✅ **Development Friendly**: Easy local development and testing  
✅ **Full Control**: Complete control over data location and access  

### Disadvantages of Local File System  
❌ **Limited Scalability**: Bounded by single machine storage capacity  
❌ **No Built-in Redundancy**: Requires manual backup and disaster recovery  
❌ **Geographic Distribution**: Cannot easily distribute across regions  
❌ **Operational Burden**: Team responsible for storage maintenance and monitoring  
❌ **Backup Complexity**: Must implement backup strategies manually  
❌ **Concurrent Access**: File locking issues with high concurrency  

### Risk Mitigation Strategies

1. **Storage Capacity Monitoring**
   ```csharp
   public class StorageHealthCheck : IHealthCheck
   {
       public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
       {
           var driveInfo = new DriveInfo(_basePath);
           var freeSpacePercent = (double)driveInfo.AvailableFreeSpace / driveInfo.TotalSize * 100;
           
           return freeSpacePercent < 10 
               ? Task.FromResult(HealthCheckResult.Unhealthy("Low disk space"))
               : Task.FromResult(HealthCheckResult.Healthy());
       }
   }
   ```

2. **Automated Backup Strategy**
   ```bash
   # Daily backup script
   #!/bin/bash
   rsync -av --delete /var/printfarmer/storage/ /backup/printfarmer-$(date +%Y%m%d)/
   find /backup/printfarmer-* -mtime +7 -exec rm -rf {} \;
   ```

3. **File Cleanup Automation**
   ```csharp
   [BackgroundService]
   public class StorageCleanupService : BackgroundService
   {
       protected override async Task ExecuteAsync(CancellationToken stoppingToken)
       {
           while (!stoppingToken.IsCancellationRequested)
           {
               await _fileStorage.CleanupExpiredFilesAsync(TimeSpan.FromHours(24), stoppingToken);
               await Task.Delay(TimeSpan.FromHours(6), stoppingToken);
           }
       }
   }
   ```

## Performance Characteristics

### File Size vs Performance
| File Size | Upload Time* | Download Time* | Disk Space |
|-----------|--------------|----------------|------------|
| 1 MB      | ~50ms        | ~20ms          | 1 MB       |
| 10 MB     | ~200ms       | ~100ms         | 10 MB      |
| 100 MB    | ~2s          | ~1s            | 100 MB     |
| 1 GB      | ~20s         | ~10s           | 1 GB       |

*Times based on SSD storage and local operations

### Concurrent Operations
- **Read Operations**: Limited by disk I/O bandwidth (~500 MB/s SSD)
- **Write Operations**: Limited by disk write speed (~300 MB/s SSD)  
- **File Count**: Modern file systems handle millions of files efficiently
- **Directory Traversal**: Optimized by hierarchical structure (user/job)

### Storage Optimization
```csharp
public class LocalFileStorageOptions
{
    public bool EnableCompression { get; set; } = false;          // Optional GZIP compression
    public bool EnableDeduplication { get; set; } = false;       // Content-based deduplication
    public int MaxConcurrentOperations { get; set; } = 50;       // Semaphore limit
    public bool PreallocateSpace { get; set; } = true;           // Avoid fragmentation
}
```

## Monitoring and Alerting

### Key Metrics
```csharp
public class StorageMetrics
{
    public long TotalFiles { get; set; }
    public long TotalSizeBytes { get; set; }
    public long AvailableSpaceBytes { get; set; }
    public double AverageUploadTimeMs { get; set; }
    public double AverageDownloadTimeMs { get; set; }
    public int ConcurrentOperations { get; set; }
    public long FilesCleanedUpToday { get; set; }
}
```

### Health Checks
- **Disk Space**: Alert when available space < 10%
- **File Operations**: Alert when P95 latency > 5 seconds
- **I/O Errors**: Alert on file system errors or corruption
- **Backup Status**: Alert when backup fails or is overdue
- **Cleanup Status**: Alert when file cleanup fails

### Prometheus Metrics
```csharp
private static readonly Counter _filesUploaded = Metrics.CreateCounter("printfarmer_files_uploaded_total");
private static readonly Histogram _uploadDuration = Metrics.CreateHistogram("printfarmer_file_upload_duration_seconds");
private static readonly Gauge _diskSpaceUsed = Metrics.CreateGauge("printfarmer_disk_space_used_bytes");
```

## Rollback Plan

If local file system proves inadequate, migration strategies include:

### Phase 1: Cloud Storage Migration
1. **Assessment**: Evaluate cloud storage costs and requirements
2. **Implementation**: Develop cloud storage provider (S3, Azure Blob)
3. **Migration**: Gradual migration of existing files to cloud storage
4. **Cutover**: Switch new files to cloud storage backend

### Phase 2: Hybrid Strategy
```csharp
public class HybridSlicerFileStorage : ISlicerFileStorage
{
    private readonly ISlicerFileStorage _localStorage;
    private readonly ISlicerFileStorage _cloudStorage;
    
    public async Task<string> UploadFileAsync(string key, Stream fileStream, string contentType, CancellationToken cancellationToken = default)
    {
        // Upload to local storage first for immediate availability
        var localUrl = await _localStorage.UploadFileAsync(key, fileStream, contentType, cancellationToken);
        
        // Async upload to cloud storage for durability
        _ = Task.Run(() => _cloudStorage.UploadFileAsync(key, fileStream, contentType, cancellationToken));
        
        return localUrl;
    }
}
```

### Migration Tools
```csharp
public class StorageMigrationService
{
    public async Task MigrateToCloudAsync(ISlicerFileStorage sourceStorage, ISlicerFileStorage targetStorage, CancellationToken cancellationToken = default)
    {
        // Enumerate all files in source storage
        // Copy to target storage with verification
        // Update database references
        // Cleanup source files after verification
    }
}
```

### Rollback Triggers
- Storage capacity growth exceeds available hardware
- Backup/restore requirements become too complex
- Geographic distribution needed for performance
- Compliance requirements mandate cloud storage
- Team cannot maintain storage infrastructure

## Success Criteria

### Performance Targets
- **Upload Performance**: < 2 seconds for 100MB files
- **Download Performance**: < 1 second for 100MB files
- **Availability**: 99.9% file operation success rate
- **Capacity**: Support 1TB+ storage per deployment
- **Throughput**: 100+ concurrent file operations

### Operational Targets
- **Setup Time**: < 15 minutes for new deployment
- **Backup Recovery**: < 4 hours RTO for complete restore
- **Monitoring**: Real-time visibility into storage health
- **Maintenance**: < 1 hour/month for routine operations

### Development Experience
- **Local Development**: Zero additional setup required
- **Testing**: Full file operations available in unit/integration tests
- **Debugging**: Standard file system tools for troubleshooting
- **Deployment**: Works in all container environments

## Timeline

### Implementation (Completed)
- **Week 1**: Core LocalSlicerFileStorage implementation ✅
- **Week 2**: File security and access control ✅
- **Week 3**: Cleanup automation and health checks ✅
- **Week 4**: Performance optimization and monitoring ✅

### Validation (Next 3 months)
- **Month 1**: Load testing with large files and concurrent operations
- **Month 2**: Storage capacity planning and backup strategy validation
- **Month 3**: Operational runbook development and team training

### Future Development (6-12 months)
- **Cloud Integration**: Abstract interface ready for cloud provider implementation
- **Hybrid Strategy**: Support both local and cloud storage simultaneously
- **Advanced Features**: Compression, deduplication, and tiered storage

## References

- [.NET File I/O Best Practices](https://docs.microsoft.com/en-us/dotnet/standard/io/)
- [ASP.NET Core File Upload](https://docs.microsoft.com/en-us/aspnet/core/mvc/models/file-uploads)
- [Linux File System Performance](https://www.kernel.org/doc/Documentation/filesystems/)
- [PrintFarmer Slicer Architecture](slicer-microservices.md)
- [LocalSlicerFileStorage Implementation](../../src/api/Services/SlicerServices/LocalSlicerFileStorage.cs)