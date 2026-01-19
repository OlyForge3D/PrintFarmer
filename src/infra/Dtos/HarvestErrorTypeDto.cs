namespace Farm.Infrastructure;

public enum HarvestErrorTypeDto
{
    ConnectionError = 0,
    AuthenticationError = 1,
    FileSystemError = 2,
    ValidationError = 3,
    UnknownError = 4
}
