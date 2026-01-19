namespace Farm.Infrastructure.Domain;

public enum AuthEventType
{
    None = 0,
    Login = 1,
    LoginFailed = 2,
    Logout = 3,
    Register = 4,
    PasswordChange = 5,
    PasswordReset = 6,
    PasswordResetInitiated = 7,
    AccountLocked = 8,
    AccountUnlocked = 9,
    RefreshToken = 10,
    TokenRevoked = 11
}
