namespace Farm.Infrastructure.Services.Collections;

/// <summary>
/// Immutable identity/authorization context for a caller performing a collection operation.
/// Passing the caller explicitly (rather than reading ambient state) keeps the service layer
/// testable and gives the later library-sync journaling work a natural place to record the actor.
/// </summary>
/// <param name="UserId">The authenticated user's identifier.</param>
/// <param name="IsAdmin">Whether the caller holds an administrator role.</param>
public readonly record struct CollectionCaller(Guid UserId, bool IsAdmin);
