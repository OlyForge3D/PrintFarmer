# Message Envelope Documentation

## Overview

The Message Envelope system provides standardized job messaging with built-in idempotency support for slicer job processing. This system ensures duplicate job submissions are handled gracefully and provides reliable job tracking.

## Envelope Structure v1.0

### Fields

| Field | Type | Description | Required |
|-------|------|-------------|----------|
| `JobId` | Guid | Unique identifier for this specific job instance | Yes |
| `SlicerType` | SlicerEngineType | Type of slicer engine requested | Yes |
| `Priority` | SlicingJobPriority | Job processing priority | Yes |
| `Attempt` | int | Attempt number for retry tracking (starts at 1) | Yes |
| `CorrelationId` | Guid | Correlation identifier for idempotency and request tracking | Yes |
| `Checksum` | string | SHA-256 checksum of job content for duplicate detection | Yes |
| `SubmittedAt` | DateTime | UTC timestamp when the job was first submitted | Yes |
| `Version` | string | Message envelope version (currently "1.0") | Yes |

### Example JSON
```json
{
  "jobId": "123e4567-e89b-12d3-a456-426614174000",
  "slicerType": "OrcaSlicer",
  "priority": "Normal", 
  "attempt": 1,
  "correlationId": "987fcdeb-51a2-43d7-8b9c-0123456789ab",
  "checksum": "j0NDRmSPa5bfid2pAcUXaxCm2Dlh3TwahqzLfj+5Y8U=",
  "submittedAt": "2024-09-08T00:00:00.0000000Z",
  "version": "1.0"
}
```

## Idempotency Logic

### Duplicate Detection

Jobs are considered duplicates when they have:
1. **Same CorrelationId**: Links related job submissions
2. **Same Checksum**: Ensures content hasn't changed

### Checksum Calculation

The checksum is calculated from the `SlicingJobContent` which includes:
- UserId
- PrinterId  
- ModelFileUrl
- ModelFileName
- SlicerEngine
- SlicerProfile (all slicer settings)
- Priority
- Metadata

The checksum uses SHA-256 hash of the JSON-serialized content with consistent formatting:
- camelCase property names
- No indentation
- Null values omitted

### Usage Patterns

#### Automatic Envelope Generation
```csharp
var request = new SlicingJobRequest
{
    UserId = userId,
    ModelFileUrl = "model.stl",
    // ... other fields
};

// Envelope auto-generated if not provided
var response = await orchestrator.SubmitJobAsync(request);
```

#### Explicit Envelope Control  
```csharp
var jobContent = SlicingJobContent.FromRequest(request);
var envelope = MessageEnvelope.Create(jobContent, SlicerEngineType.OrcaSlicer);

request.Envelope = envelope;
var response = await orchestrator.SubmitJobAsync(request);
```

#### Retry Scenarios
```csharp
// Create retry with incremented attempt number
var retryEnvelope = MessageEnvelope.CreateRetry(originalEnvelope);
request.Envelope = retryEnvelope;
```

## Version Compatibility

### Version 1.0 (Current)
- Initial implementation
- All fields required
- SHA-256 checksum algorithm
- UTC timestamps

### Future Versions
When introducing new envelope versions:
1. Update `MessageEnvelope.CurrentVersion` constant
2. Add version-specific validation logic
3. Maintain backward compatibility for existing jobs
4. Document breaking changes

## Implementation Details

### Storage Requirements
- **Redis Keys**: `slicer:correlation:{correlationId}:{checksum}` → jobId mapping
- **Expiration**: 30 days for correlation mappings
- **Atomicity**: Redis transactions ensure consistent correlation storage

### Error Handling
- **Checksum Mismatch**: Throws `ArgumentException` if provided envelope doesn't match content
- **Duplicate Detection**: Returns existing job without re-enqueueing
- **Missing Engine**: Validates slicer engine availability before processing

### Performance Considerations
- Checksum calculation: O(n) where n = serialized content size
- Duplicate lookup: O(1) Redis key lookup
- Memory: Minimal overhead (~200 bytes per envelope)

## Testing Scenarios

### Unit Tests
- ✅ Envelope creation and validation
- ✅ Checksum consistency and uniqueness  
- ✅ Duplicate detection logic
- ✅ Retry envelope generation

### Integration Tests
- ✅ Duplicate submission handling
- ✅ Checksum mismatch detection
- ✅ Auto-envelope generation
- ✅ Separate jobs for different correlations

## Migration Guide

For existing codebases:
1. Update `SlicingJobRequest` to include optional `Envelope` property
2. Add envelope fields to `DistributedSlicingJob`
3. Implement `FindExistingJobAsync` and `JobExistsAsync` in job queue
4. Update job submission logic to check for duplicates
5. Add correlation data to storage layer

## Error Codes

| Error | Code | Description |
|-------|------|-------------|
| Checksum Mismatch | `ArgumentException` | Request content doesn't match envelope checksum |
| Invalid Engine | `ArgumentException` | Requested slicer engine not available |
| File Not Found | `FileNotFoundException` | Model file doesn't exist |
| Engine Unhealthy | `InvalidOperationException` | Slicer engine currently unavailable |