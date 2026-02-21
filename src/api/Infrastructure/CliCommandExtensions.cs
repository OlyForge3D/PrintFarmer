using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Telemetry;
using Farm.Web.Api.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Farm.Web.Api.Infrastructure;

public static class CliCommandExtensions
{
    /// <summary>
    /// Handles headless CLI commands like --create-admin and --list-users.
    /// Returns true if a CLI command was executed (app should exit after).
    /// </summary>
    /// <param name="app">The WebApplication instance to handle CLI commands for.</param>
    /// <param name="args">The command-line arguments to process.</param>
    public static async Task<bool> HandleCliCommandsAsync(this WebApplication app, string[] args)
    {
        List<string> rawArgs = args.ToList();
        bool headlessCreateAdmin = rawArgs.Contains("--create-admin", StringComparer.OrdinalIgnoreCase);
        bool headlessListUsers = rawArgs.Contains("--list-users", StringComparer.OrdinalIgnoreCase);

        if (!headlessCreateAdmin && !headlessListUsers)
        {
            return false; // No CLI command, continue with normal startup
        }

        // Create an async scope to resolve scoped services without using the service locator pattern
        await using AsyncServiceScope scope = app.Services.CreateAsyncScope();

        // Resolve logger explicitly; if not registered, keep null to preserve Console fallback
        IUnifiedLoggingService? logger = null;
        try
        {
            logger = scope.ServiceProvider.GetRequiredService<IUnifiedLoggingService>();
        }
        catch (InvalidOperationException)
        {
            // No logger registered - fall back to Console output as before
        }

        // Ensure database is initialized for CLI operations
        try
        {
            AppDbContext cliDb = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            _ = await cliDb.Database.EnsureCreatedAsync();

            Farm.Infrastructure.Services.Interfaces.IDatabaseInitializer? dbInitializer = scope.ServiceProvider.GetService<Farm.Infrastructure.Services.Interfaces.IDatabaseInitializer>();
            if (dbInitializer != null)
            {
                await dbInitializer.SeedAllAsync();
            }
            else
            {
                if (logger != null)
                {
                    logger.LogWarning("[CLI] No DatabaseInitializer registered; skipping seeding.");
                }
                else
                {
                    await Console.Error.WriteLineAsync("[CLI] No DatabaseInitializer registered; skipping seeding.");
                }
            }
        }
        catch (Exception ex)
        {
            if (logger != null)
            {
                logger.LogError(ex, "[CLI] Database initialization failed: {Message}", ex.Message);
            }
            else
            {
                await Console.Error.WriteLineAsync($"[CLI] Database initialization failed: {ex.Message}");
            }

            Environment.Exit(1);
        }

        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        if (headlessListUsers)
        {
            await ListUsersAsync(db, logger);
            return true;
        }
        else
        {
            // At this point we know at least one CLI command flag was present and
            // headlessListUsers was false (it returned earlier). The remaining
            // possibility is headlessCreateAdmin, so use a plain else to avoid
            // analyzer warnings about unreachable conditions.
            await CreateAdminAsync(db, rawArgs, logger);
            return true;
        }

        // All CLI code paths return above; no further action required here.
        // Method intentionally falls through when a CLI command was handled.
    }

    private static async Task ListUsersAsync(AppDbContext db, IUnifiedLoggingService? logger)
    {
        List<User> users = await db.Users.Include(u => u.UserRoles).ThenInclude(ur => ur.Role).ToListAsync();
        if (logger != null)
        {
            logger.LogInformation($"Users ({users.Count}):");
            foreach (User u in users)
            {
                string roles = string.Join(',', u.UserRoles.Where(r => r.IsActive).Select(r => r.Role.Name));
                logger.LogInformation($" - {u.Username} <{u.Email}> Roles=[{roles}] Active={u.IsActive}");
            }
        }
        else
        {
            Console.WriteLine($"Users ({users.Count}):");
            foreach (User u in users)
            {
                string roles = string.Join(',', u.UserRoles.Where(r => r.IsActive).Select(r => r.Role.Name));
                Console.WriteLine($" - {u.Username} <{u.Email}> Roles=[{roles}] Active={u.IsActive}");
            }
        }
    }

    private static async Task CreateAdminAsync(AppDbContext db, List<string> rawArgs, IUnifiedLoggingService? logger)
    {
        string GetArg(string name)
        {
            int idx = rawArgs.IndexOf(name);
            return (idx >= 0 && idx + 1 < rawArgs.Count) ? rawArgs[idx + 1] : string.Empty;
        }

        string username = GetArg("--username");
        string email = GetArg("--email");
        string password = GetArg("--password");
        string firstName = GetArg("--first");
        string lastName = GetArg("--last");

        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            if (logger != null)
            {
                logger.LogError("Usage: --create-admin --username <user> --email <email> --password <pass> [--first <name>] [--last <name>]");
            }
            else
            {
                await Console.Error.WriteLineAsync("Usage: --create-admin --username <user> --email <email> --password <pass> [--first <name>] [--last <name>]");
            }

            Environment.Exit(1);
        }

        if (await db.Users.AnyAsync(u => u.Username == username))
        {
            if (logger != null)
            {
                logger.LogWarning($"User '{username}' already exists.");
            }
            else
            {
                await Console.Error.WriteLineAsync($"User '{username}' already exists.");
            }

            Environment.Exit(1);
        }

        PasswordHasher<User> passwordHasher = new();
        User newAdmin = new()
        {
            Username = username,
            Email = email,
            FirstName = firstName,
            LastName = lastName,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        newAdmin.PasswordHash = passwordHasher.HashPassword(newAdmin, password);
        _ = db.Users.Add(newAdmin);
        _ = await db.SaveChangesAsync();

        Role? adminRole = await db.Roles.FirstOrDefaultAsync(r => r.Name == "farm_admin");
        if (adminRole != null)
        {
            _ = db.UserRoles.Add(new UserRole
            {
                UserId = newAdmin.Id,
                RoleId = adminRole.Id,
                IsActive = true,
                AssignedAt = DateTime.UtcNow
            });
            _ = await db.SaveChangesAsync();
            if (logger != null)
            {
                logger.LogInformation($"Created admin user '{username}' with farm_admin role.");
            }
            else
            {
                Console.WriteLine($"Created admin user '{username}' with farm_admin role.");
            }
        }
        else
        {
            if (logger != null)
            {
                logger.LogWarning($"Created user '{username}' but farm_admin role not found. Run database seeders first.");
            }
            else
            {
                Console.WriteLine($"Created user '{username}' but farm_admin role not found. Run database seeders first.");
            }
        }
    }
}
