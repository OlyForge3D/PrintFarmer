# Harvest Error Tracking Enhancement

## Overview

This document describes the comprehensive error tracking and categorization system implemented for G-code harvest operations in PrintFarmer. The enhancement provides detailed error information, user-friendly error messages, actionable suggestions, and retry indicators.

## Implementation Date

October 5, 2025

---

## Database Schema Enhancements

### New Fields Added to `GcodeHarvestOperation` Entity

| Field | Type | Description |
|-------|------|-------------|
| `ErrorType` | `string?` | Categorizes errors: ConnectionError, AuthenticationError, FileSystemError, ValidationError, UnknownError |
| `ErrorPhase` | `string?` | Where it failed: Discovery, Download, Processing, Completion |
| `ErrorDetails` | `string?` | JSON with exception type, stack trace, and additional debugging info |
| `FailedResource` | `string?` | File path or URL that caused the failure |
| `IsRetryable` | `bool` | Boolean indicating if operation can be retried (default: false) |
| `ErrorOccurredAt` | `DateTime?` | Exact timestamp when error occurred |

### New Enumerations

**Backend Enums** (`Infrastructure/Domain/Entities.cs`):

```csharp
public enum HarvestErrorType
{
    ConnectionError = 0,      // Network/connectivity issues
    AuthenticationError = 1,  // API key or permission problems
    FileSystemError = 2,      // Can't access files/directories
    ValidationError = 3,      // File validation failures
    UnknownError = 4          // Unexpected exceptions
}

public enum HarvestErrorPhase
{
    Discovery = 0,    // Failed during file listing
    Download = 1,     // Failed during file download
    Processing = 2,   // Failed during file processing/import
    Completion = 3    // Failed during finalization
}
```

**DTO Enums** (`shared/Models.cs`):
- `HarvestErrorTypeDto`
- `HarvestErrorPhaseDto`

---

## Backend Implementation

### HarvestErrorHelper Service

**Location:** `src/api/Services/HarvestErrorHelper.cs`

A new helper service providing error categorization, enrichment, and user-friendly messaging.

#### Key Methods

**`CategorizeError(Exception ex, string? failedResource = null)`**
- Automatically categorizes exceptions into error types
- Analyzes exception type and message content
- Returns string representation of `HarvestErrorType`

**Exception Classification Logic:**
```csharp
HttpRequestException → ConnectionError
TimeoutException → ConnectionError
TaskCanceledException → ConnectionError
UnauthorizedAccessException → AuthenticationError
IOException → FileSystemError
ArgumentException → ValidationError
Message contains "401"|"403" → AuthenticationError
Message contains "404" → FileSystemError
Message contains "timeout" → ConnectionError
Message contains "connection"|"network" → ConnectionError
Default → UnknownError
```

**`IsRetryableError(string errorType)`**
- Determines if an error type can be retried
- ConnectionError: ✅ Retryable
- FileSystemError: ✅ Retryable
- AuthenticationError: ❌ Not retryable (requires manual fix)
- ValidationError: ❌ Not retryable (data issue)
- UnknownError: ❌ Not retryable

**`CreateErrorDetailsJson(Exception ex, string? failedResource = null)`**
- Captures exception details in JSON format
- Includes: ExceptionType, StackTrace, InnerException, AdditionalInfo
- Used for debugging and support

**`GetUserFriendlyMessage(string errorType, string originalMessage)`**
- Creates clear error messages with context
- Adds appropriate prefix based on error type
- Example: "Connection failed: " + original message

**`GetErrorSuggestion(string errorType)`**
- Provides actionable suggestions based on error type
- Helps users resolve issues independently

**`SetOperationError(GcodeHarvestOperation operation, Exception ex, string phase, string? failedResource = null)`**
- Convenience method to update operation with all error details
- Sets status to Failed, timestamps, and all error fields
- One-line error handling in catch blocks

### Updated GcodeHarvestService

**Changes to `GcodeHarvestService.cs`:**

**Before:**
```csharp
catch (Exception ex)
{
    dbOperation.Status = GcodeHarvestStatus.Failed;
    dbOperation.ErrorMessage = $"File discovery failed: {ex.Message}";
    dbOperation.CompletedAt = DateTime.UtcNow;
    await scopedDb.SaveChangesAsync();
}
```

**After:**
```csharp
catch (Exception ex)
{
    HarvestErrorHelper.SetOperationError(
        dbOperation,
        ex,
        nameof(HarvestErrorPhase.Discovery),
        failedResource: printer.ServerUrl);
    await scopedDb.SaveChangesAsync();
}
```

### Updated DTOs

**`GcodeHarvestOperationDto` now includes:**
```csharp
string? ErrorType = null,
string? ErrorPhase = null,
string? ErrorDetails = null,
string? FailedResource = null,
bool IsRetryable = false,
DateTime? ErrorOccurredAt = null,
```

**Mapping updated in `MapToDto()`** to include all new error fields.

---

## Frontend Implementation

### TypeScript Types

**Location:** `src/Web/ReactApp/src/types/api.ts`

**Added to `GcodeHarvestOperation` interface:**
```typescript
error?: string;
errorType?: string;
errorPhase?: string;
errorDetails?: string;
failedResource?: string;
isRetryable?: boolean;
errorOccurredAt?: string;
```

### Harvest Error Helper Utility

**Location:** `src/Web/ReactApp/src/utils/harvestErrorHelper.ts`

**Key Functions:**

**`getHarvestErrorInfo(operation: GcodeHarvestOperation): ErrorInfo | null`**
- Extracts and enriches error information from operation
- Returns comprehensive error details for display
- Returns null if no error present

**`ErrorInfo` Interface:**
```typescript
interface ErrorInfo {
  title: string;          // User-friendly error title
  message: string;        // Error message
  suggestion?: string;    // Actionable suggestion
  iconType: 'connection' | 'auth' | 'filesystem' | 'validation' | 'unknown';
  canRetry: boolean;      // Whether error is retryable
  phase?: string;         // Phase where error occurred
  failedResource?: string; // Resource that caused failure
}
```

**Error Type Details:**

| Error Type | Title | Icon | Suggestion |
|------------|-------|------|------------|
| ConnectionError | Connection Failed | 🔌 Network badge | Verify printer is online and URL is correct. Check your network connection. |
| AuthenticationError | Authentication Failed | 🔒 Lock | Check your API key is valid and has the required permissions. |
| FileSystemError | File System Error | 📁 Document | Ensure the printer has the requested files and folders accessible. |
| ValidationError | Validation Failed | ⚠️ Alert circle | Check the harvest operation settings and try again. |
| UnknownError | Harvest Failed | ❌ X circle | (no suggestion) |

**`getPhaseDisplay(phase?: string): string`**
- Formats phase names for user display
- "Discovery" → "during file discovery"
- "Download" → "during file download"
- "Processing" → "during file processing"
- "Completion" → "during completion"

### ErrorIcon Component

**Location:** `src/Web/ReactApp/src/components/harvest/ErrorIcon.tsx`

Renders appropriate SVG icon based on error type with customizable className.

**Icons:**
- **Connection**: Network badge with checkmark
- **Authentication**: Locked padlock
- **FileSystem**: Document with lines
- **Validation**: Alert circle with exclamation
- **Unknown**: X in circle

### Enhanced PrinterCard Component

**Location:** `src/Web/ReactApp/src/components/harvest/PrinterCard.tsx`

**Enhanced Error Display:**
```tsx
{operation && (isFailed || operation.error) && (() => {
  const errorInfo = getHarvestErrorInfo(operation);
  if (!errorInfo) return null;

  return (
    <div className="bg-red-50 border border-red-300 rounded-lg p-2">
      <ErrorIcon type={errorInfo.iconType} />
      <p>{errorInfo.title} {errorInfo.canRetry && 🔄}</p>
      <p>{errorInfo.message}</p>
      {!compact && errorInfo.suggestion && (
        <p>💡 {errorInfo.suggestion}</p>
      )}
    </div>
  );
})()}
```

**Features:**
- Shows error title based on type (e.g., "Connection Failed")
- Displays error message
- Shows retry indicator (🔄) if error is retryable
- Displays suggestion in normal mode (hidden in compact to save space)
- Appropriate icon for error type
- Responsive sizing for compact mode

### Enhanced HarvestOperationDetails Component

**Location:** `src/Web/ReactApp/src/components/harvest/HarvestOperationDetails.tsx`

**Full Error Information Display:**
```tsx
{(isFailed || operation.error) && (() => {
  const errorInfo = getHarvestErrorInfo(operation);
  if (!errorInfo) return null;

  return (
    <div className="bg-red-50 border border-red-300 rounded-lg p-3 mb-4">
      <ErrorIcon type={errorInfo.iconType} />
      <p>{errorInfo.title}</p>
      <p>{errorInfo.message}</p>
      
      {errorInfo.phase && (
        <p>Failed {getPhaseDisplay(errorInfo.phase)}</p>
      )}
      
      {errorInfo.failedResource && (
        <p>Resource: {errorInfo.failedResource}</p>
      )}
      
      {errorInfo.suggestion && (
        <div className="bg-red-100 border border-red-200 rounded">
          <p>💡 Suggestion:</p>
          <p>{errorInfo.suggestion}</p>
        </div>
      )}
      
      {errorInfo.canRetry && (
        <p>🔄 This operation can be retried</p>
      )}
    </div>
  );
})()}
```

**Features:**
- Error title with appropriate icon
- Error message
- Phase where it failed
- Failed resource (URL or file path)
- Helpful suggestion in highlighted box
- Retry indicator if applicable

---

## User Experience Improvements

### Before Enhancement

```
❌ Harvest Failed
File discovery failed: Object reference not set to an instance of an object
```

**Issues:**
- Generic error title
- Technical jargon
- No guidance on how to fix
- No indication if retryable
- No context about where failure occurred

### After Enhancement

```
🔌 Connection Failed                                    🔄
Connection failed: Unable to connect to printer at http://192.168.1.100
Failed during file discovery
Resource: http://192.168.1.100

💡 Suggestion:
Verify printer is online and URL is correct. Check your network connection.

🔄 This operation can be retried
```

**Improvements:**
- ✅ Clear error category with icon
- ✅ User-friendly message
- ✅ Actionable suggestion
- ✅ Retry indicator
- ✅ Context about failure phase and resource
- ✅ Visual hierarchy and organization

---

## Error Type Examples

### 1. ConnectionError (Retryable ✅)

**Triggers:**
- Network timeouts
- Connection refused
- Unreachable host
- DNS resolution failures
- `HttpRequestException`, `TimeoutException`, `TaskCanceledException`

**Display:**
```
🔌 Connection Failed                                    🔄
Connection failed: The remote server is not responding
Failed during file discovery
Resource: http://192.168.1.100:7125

💡 Suggestion:
Verify printer is online and URL is correct. Check your network connection.

🔄 This operation can be retried
```

### 2. AuthenticationError (Not Retryable ❌)

**Triggers:**
- 401/403 HTTP responses
- Invalid API keys
- Missing permissions
- `UnauthorizedAccessException`

**Display:**
```
🔒 Authentication Failed
Authentication failed: API key is invalid or expired
Failed during file discovery
Resource: http://192.168.1.100:7125

💡 Suggestion:
Check your API key is valid and has the required permissions.
```

### 3. FileSystemError (Retryable ✅)

**Triggers:**
- 404 errors
- Missing directories
- Inaccessible files
- `IOException`

**Display:**
```
📁 File System Error                                   🔄
File system error: Directory '/gcodes' not found
Failed during file discovery
Resource: /gcodes

💡 Suggestion:
Ensure the printer has the requested files and folders accessible.

🔄 This operation can be retried
```

### 4. ValidationError (Not Retryable ❌)

**Triggers:**
- Invalid settings
- Bad parameters
- `ArgumentException`

**Display:**
```
⚠️ Validation Failed
Validation failed: File extension filter is invalid
Failed during file discovery

💡 Suggestion:
Check the harvest operation settings and try again.
```

### 5. UnknownError

**Triggers:**
- Unexpected exceptions
- Unhandled scenarios

**Display:**
```
❌ Harvest Failed
Error: An unexpected error occurred during processing
Failed during file discovery
```

---

## Technical Benefits

### Better Debugging
- ✅ Stack traces captured in `ErrorDetails` JSON field
- ✅ Exception type recorded for pattern analysis
- ✅ Inner exception messages preserved
- ✅ Failed resource context (URL/file path)
- ✅ Exact timestamp of failure

### User-Friendly Experience
- ✅ Clear categorization helps users understand issues
- ✅ Non-technical language in error titles and messages
- ✅ Visual icons provide instant recognition
- ✅ Consistent error presentation across UI

### Actionable Guidance
- ✅ Specific suggestions guide users to fixes
- ✅ Different suggestions per error type
- ✅ Phase information narrows down the problem
- ✅ Resource information identifies what to check

### Intelligent Retry Logic
- ✅ System knows which errors are worth retrying
- ✅ Connection and filesystem errors are retryable
- ✅ Auth and validation errors require manual intervention
- ✅ Retry indicator shown in UI

### Analytics Capability
- ✅ Can track common error patterns
- ✅ Error type distribution analysis
- ✅ Phase-based failure rate tracking
- ✅ Resource-specific problem identification

### Backward Compatibility
- ✅ `ErrorMessage` field preserved for existing code
- ✅ All new fields are optional (nullable)
- ✅ Graceful degradation if error details unavailable
- ✅ UI handles both old and new error formats

---

## Files Modified

### Backend

| File | Changes |
|------|---------|
| `src/Infrastructure/Domain/Entities.cs` | Added 6 error fields to `GcodeHarvestOperation` entity<br>Added `HarvestErrorType` and `HarvestErrorPhase` enums |
| `src/shared/Models.cs` | Updated `GcodeHarvestOperationDto` with 6 new error fields<br>Added `HarvestErrorTypeDto` and `HarvestErrorPhaseDto` enums |
| `src/api/Services/GcodeHarvestService.cs` | Enhanced error handling in all catch blocks<br>Updated `MapToDto()` to include new error fields |
| `src/api/Services/HarvestErrorHelper.cs` | **NEW** - Comprehensive error categorization and enrichment service |

### Frontend

| File | Changes |
|------|---------|
| `src/Web/ReactApp/src/types/api.ts` | Added 6 error fields to `GcodeHarvestOperation` interface |
| `src/Web/ReactApp/src/utils/harvestErrorHelper.ts` | **NEW** - Error information extraction and formatting utilities |
| `src/Web/ReactApp/src/components/harvest/ErrorIcon.tsx` | **NEW** - Icon component with type-specific SVG icons |
| `src/Web/ReactApp/src/components/harvest/PrinterCard.tsx` | Enhanced error display with categorization, suggestions, and retry indicators |
| `src/Web/ReactApp/src/components/harvest/HarvestOperationDetails.tsx` | Enhanced error display with full details, phase, resource, and suggestions |

---

## Testing Recommendations

### Manual Testing Scenarios

1. **Connection Error**
   - Stop printer or disconnect from network
   - Start harvest operation
   - Verify: Connection icon, appropriate message, retry indicator shown

2. **Authentication Error**
   - Configure printer with invalid API key
   - Start harvest operation
   - Verify: Lock icon, auth message, no retry indicator, key suggestion shown

3. **File System Error**
   - Configure harvest with non-existent directory path
   - Start harvest operation
   - Verify: Document icon, filesystem message, retry indicator, accessibility suggestion

4. **Validation Error**
   - Configure harvest with invalid file extension filter
   - Start harvest operation
   - Verify: Alert icon, validation message, no retry indicator, settings suggestion

5. **Unknown Error**
   - (Requires code modification to trigger)
   - Verify: X icon, generic error message, basic information displayed

### UI Testing

- **PrinterCard Component:**
  - Test error display in both normal and compact modes
  - Verify suggestion shown only in normal mode
  - Verify retry indicator positioning
  - Test long error messages for text wrapping

- **HarvestOperationDetails Component:**
  - Verify all error fields display correctly
  - Test suggestion box styling and readability
  - Verify phase and resource information formatting
  - Test with and without failed resource

### Database Validation

After triggering errors, check database for:
```sql
SELECT 
  ErrorType,
  ErrorPhase,
  ErrorMessage,
  FailedResource,
  IsRetryable,
  ErrorOccurredAt,
  ErrorDetails
FROM GcodeHarvestOperations
WHERE Status = 2; -- Failed status
```

Verify:
- ✅ ErrorType is set correctly
- ✅ ErrorPhase matches where failure occurred
- ✅ ErrorMessage is user-friendly
- ✅ FailedResource contains URL or path
- ✅ IsRetryable matches error type expectations
- ✅ ErrorOccurredAt is populated
- ✅ ErrorDetails contains valid JSON

---

## Future Enhancements

### Potential Improvements

1. **Automatic Retry Mechanism**
   - Use `IsRetryable` flag to automatically retry operations
   - Implement exponential backoff
   - Track retry count in operation

2. **Error History Dashboard**
   - Display error trends over time
   - Show most common error types
   - Identify problematic printers or resources

3. **Error Notifications**
   - Toast notifications for errors
   - Email alerts for critical failures
   - Webhook integration for external monitoring

4. **Enhanced Error Details**
   - Add HTTP status codes for API errors
   - Capture request/response headers
   - Include more detailed network diagnostics

5. **User-Configurable Error Handling**
   - Allow users to mark certain errors as expected
   - Configure auto-retry attempts
   - Customize error notification preferences

6. **Error Recovery Actions**
   - "Retry" button in UI
   - "Fix API Key" quick action
   - "Test Connection" diagnostic tool

7. **Localization**
   - Translate error messages and suggestions
   - Support multiple languages for international users

---

## Conclusion

The enhanced error tracking system provides PrintFarmer with enterprise-grade error handling and user guidance. By categorizing errors, providing actionable suggestions, and indicating retry feasibility, the system significantly improves both user experience and debugging capabilities.

The implementation maintains backward compatibility while adding rich error context that helps users resolve issues independently and helps administrators diagnose patterns quickly.

---

## Version History

| Date | Version | Changes |
|------|---------|---------|
| 2025-10-05 | 1.0 | Initial implementation with 5 error types, 4 phases, and comprehensive UI enhancements |
