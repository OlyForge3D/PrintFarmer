using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Farm.Web.Shared;
using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Repositories.Users;

public interface IUsersRepository
{
    Task<IReadOnlyList<UserDto>> GetUsersAsync(CancellationToken ct = default);
    Task<Farm.Infrastructure.Domain.User?> GetUserEntityAsync(Guid id, CancellationToken ct = default);
    Task<bool> AnyUserByUsernameOrEmailAsync(string username, string email, CancellationToken ct = default);
    Task AddUserAsync(Farm.Infrastructure.Domain.User user, IEnumerable<Guid>? roleIds, CancellationToken ct = default);
    Task UpdateUserRolesAsync(Guid userId, IEnumerable<Guid> roleIds, CancellationToken ct = default);
    Task DeleteUserAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<RoleDto>> GetRolesAsync(CancellationToken ct = default);
    Task<bool> UsernameExistsAsync(string username, CancellationToken ct = default);
    Task<bool> EmailExistsAsync(string email, CancellationToken ct = default);
    Task SaveChangesAsync(CancellationToken ct = default);
}
