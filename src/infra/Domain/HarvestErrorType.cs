namespace Farm.Infrastructure.Domain;

public enum HarvestErrorType
{
    ConnectionError = 0,      // Network/connectivity issues
    AuthenticationError = 1,  // API key or permission problems
    FileSystemError = 2,      // Can't access files/directories
    ValidationError = 3,      // File validation failures
    UnknownError = 4          // Unexpected exceptions
}
