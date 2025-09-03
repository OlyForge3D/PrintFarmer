using Microsoft.EntityFrameworkCore;
using Farm.Web.Api.Domain;

namespace Farm.Web.Api.Data.Seed;

public static class AuthenticationDataSeeder
{
    public static async Task SeedAsync(AppDbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        try
        {
            // Try to query the Actions table to see if it exists
            await context.Actions.AnyAsync();
        }
        catch (Exception)
        {
            // If authentication tables don't exist yet, skip seeding
            // This can happen during initial database setup or testing
            return;
        }

        // Seed Actions first
        await SeedActionsAsync(context);
        
        // Seed Resources
        await SeedResourcesAsync(context);
        
        // Seed Roles
        await SeedRolesAsync(context);
        
        // Seed Role Permissions
        await SeedRolePermissionsAsync(context);

        await context.SaveChangesAsync();
    }

    private static async Task SeedActionsAsync(AppDbContext context)
    {
        var actions = new[]
        {
            new { Name = "create", DisplayName = "Create", Description = "Create new resources" },
            new { Name = "read", DisplayName = "Read", Description = "View and read resources" },
            new { Name = "update", DisplayName = "Update", Description = "Modify existing resources" },
            new { Name = "delete", DisplayName = "Delete", Description = "Remove resources" },
            new { Name = "execute", DisplayName = "Execute", Description = "Execute operations on resources" },
            new { Name = "admin", DisplayName = "Administer", Description = "Full administrative control" }
        };

        foreach (var action in actions)
        {
            if (!await context.Actions.AnyAsync(a => a.Name == action.Name))
            {
                context.Actions.Add(new Domain.Action
                {
                    Id = Guid.NewGuid(),
                    Name = action.Name,
                    DisplayName = action.DisplayName,
                    Description = action.Description,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                });
            }
        }
    }

    private static async Task SeedResourcesAsync(AppDbContext context)
    {
        var resources = new[]
        {
            new { Name = "printers", DisplayName = "Printers", ResourceType = "printer", Description = "3D printer management" },
            new { Name = "gcode_harvest", DisplayName = "G-code Harvest", ResourceType = "harvest", Description = "G-code file harvesting operations" },
            new { Name = "gcode_library", DisplayName = "G-code Library", ResourceType = "library", Description = "G-code file library management" },
            new { Name = "job_queue", DisplayName = "Print Job Queue", ResourceType = "queue", Description = "Print job queue management" },
            new { Name = "slicer_engines", DisplayName = "Slicer Engines", ResourceType = "slicer", Description = "Slicer integration and management" },
            new { Name = "users", DisplayName = "Users", ResourceType = "system", Description = "User account management" },
            new { Name = "roles", DisplayName = "Roles", ResourceType = "system", Description = "Role and permission management" },
            new { Name = "system_settings", DisplayName = "System Settings", ResourceType = "system", Description = "Application configuration and settings" },
            new { Name = "spoolman", DisplayName = "Spoolman Integration", ResourceType = "integration", Description = "Spoolman filament management integration" },
            new { Name = "network_discovery", DisplayName = "Network Discovery", ResourceType = "system", Description = "Printer network discovery and management" }
        };

        foreach (var resource in resources)
        {
            if (!await context.Resources.AnyAsync(r => r.Name == resource.Name))
            {
                context.Resources.Add(new Resource
                {
                    Id = Guid.NewGuid(),
                    Name = resource.Name,
                    DisplayName = resource.DisplayName,
                    Description = resource.Description,
                    ResourceType = resource.ResourceType,
                    IsActive = true,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                });
            }
        }
    }

    private static async Task SeedRolesAsync(AppDbContext context)
    {
        var roles = new[]
        {
            new { Name = "farm_admin", DisplayName = "Farm Administrator", Description = "Full access to all farm resources and user management", IsSystemRole = true },
            new { Name = "farm_user", DisplayName = "Farm User", Description = "Standard user access to printers and print operations", IsSystemRole = true }
        };

        foreach (var role in roles)
        {
            if (!await context.Roles.AnyAsync(r => r.Name == role.Name))
            {
                context.Roles.Add(new Role
                {
                    Id = Guid.NewGuid(),
                    Name = role.Name,
                    DisplayName = role.DisplayName,
                    Description = role.Description,
                    IsSystemRole = role.IsSystemRole,
                    IsActive = true,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                });
            }
        }
    }

    private static async Task SeedRolePermissionsAsync(AppDbContext context)
    {
        // Get the admin role - admins get all permissions
        var adminRole = await context.Roles.FirstOrDefaultAsync(r => r.Name == "farm_admin");
        if (adminRole != null)
        {
            var allResources = await context.Resources.ToListAsync();
            var adminAction = await context.Actions.FirstOrDefaultAsync(a => a.Name == "admin");
            
            if (adminAction != null)
            {
                foreach (var resource in allResources)
                {
                    if (!await context.RolePermissions.AnyAsync(rp => 
                        rp.RoleId == adminRole.Id && rp.ResourceId == resource.Id && rp.ActionId == adminAction.Id))
                    {
                        context.RolePermissions.Add(new RolePermission
                        {
                            Id = Guid.NewGuid(),
                            RoleId = adminRole.Id,
                            ResourceId = resource.Id,
                            ActionId = adminAction.Id,
                            Granted = true,
                            CreatedAt = DateTime.UtcNow
                        });
                    }
                }
            }
        }

        // Get the user role - users get read access to most resources
        var userRole = await context.Roles.FirstOrDefaultAsync(r => r.Name == "farm_user");
        if (userRole != null)
        {
            var userPermissions = new[]
            {
                ("printers", "read"),
                ("printers", "execute"), // Can control printers
                ("gcode_library", "read"),
                ("gcode_library", "create"), // Can upload files
                ("job_queue", "read"),
                ("job_queue", "create"), // Can create print jobs
                ("spoolman", "read")
            };

            foreach (var (resourceName, actionName) in userPermissions)
            {
                var resource = await context.Resources.FirstOrDefaultAsync(r => r.Name == resourceName);
                var action = await context.Actions.FirstOrDefaultAsync(a => a.Name == actionName);

                if (resource != null && action != null)
                {
                    if (!await context.RolePermissions.AnyAsync(rp => 
                        rp.RoleId == userRole.Id && rp.ResourceId == resource.Id && rp.ActionId == action.Id))
                    {
                        context.RolePermissions.Add(new RolePermission
                        {
                            Id = Guid.NewGuid(),
                            RoleId = userRole.Id,
                            ResourceId = resource.Id,
                            ActionId = action.Id,
                            Granted = true,
                            CreatedAt = DateTime.UtcNow
                        });
                    }
                }
            }
        }
    }
}