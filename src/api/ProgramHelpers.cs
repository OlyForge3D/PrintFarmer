using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Tasks;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Services.Authentication;
using Farm.Infrastructure.Settings;
using Farm.Infrastructure.Telemetry;
using Farm.Web.Api.Infrastructure;
using Farm.Web.Api.Infrastructure.Database;
using Farm.Web.Api.Services.Interfaces;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Farm.Web.Api
{
    internal static class ProgramHelpers
    {
        internal static void HandleDeferredConsoleRedirection(IUnifiedLoggingService? uls, ILogger<Program>? lg)
        {
            try
            {
                if (uls != null)
                {
                    uls.LogInformation("[UnifiedLogging] Console redirection initialized (deferred) - Console output now captured in OpenTelemetry");
                }
                else if (lg != null)
                {
                    lg.LogInformation("[UnifiedLogging] Console redirection initialized (deferred) - Console output now captured in OpenTelemetry");
                }
                else
                {
                    // If no logging services available, write to stderr as a fallback so it's visible during startup
                    Console.Error.WriteLine("[UnifiedLogging] Console redirection initialized (deferred) - but no logging pipeline available");
                }
            }
            catch (Exception ex)
            {
                try
                {
                    if (uls != null)
                    {
                        uls.LogWarning($"[UnifiedLogging] Deferred console redirection failed: {ex.Message}");
                    }
                    else if (lg != null)
                    {
                        lg.LogWarning("[UnifiedLogging] Deferred console redirection failed: {Message}", ex.Message);
                    }
                    else
                    {
                        Console.Error.WriteLine($"[UnifiedLogging][FALLBACK] Deferred console redirection failed: {ex.Message}");
                    }
                }
                catch
                {
                    Console.Error.WriteLine($"[UnifiedLogging][FALLBACK] Deferred console redirection failed: {ex.Message}");
                }
            }
        }

        internal static IResult AutoDetectNetworkRanges()
        {
            HashSet<string> suggestions = new(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (System.Net.NetworkInformation.NetworkInterface ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up)
                    {
                        continue;
                    }

                    IPInterfaceProperties props = ni.GetIPProperties();
                    foreach (UnicastIPAddressInformation ua in props.UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily == AddressFamily.InterNetwork)
                        {
                            int prefix = 24;
                            if (ua.IPv4Mask is not null)
                            {
                                byte[] maskBytes = ua.IPv4Mask.GetAddressBytes();
                                int ones = 0;
                                foreach (byte b in maskBytes)
                                {
                                    byte v = b;
                                    while (v != 0)
                                    {
                                        ones += v & 1;
                                        v >>= 1;
                                    }
                                }

                                if (ones > 0)
                                {
                                    prefix = ones;
                                }
                            }

                            byte[] networkBytes = ua.Address.GetAddressBytes();
                            if (prefix is >= 8 and <= 32)
                            {
                                int fullBytes = prefix / 8;
                                int remBits = prefix % 8;
                                if (remBits > 0 && fullBytes < networkBytes.Length)
                                {
                                    byte mask = (byte)(0xFF << (8 - remBits));
                                    networkBytes[fullBytes] = (byte)(networkBytes[fullBytes] & mask);
                                    for (int i = fullBytes + 1; i < networkBytes.Length; i++)
                                    {
                                        networkBytes[i] = 0;
                                    }
                                }
                                else
                                {
                                    for (int i = fullBytes; i < networkBytes.Length; i++)
                                    {
                                        networkBytes[i] = 0;
                                    }
                                }

                                IPAddress networkBase = new(networkBytes);
                                _ = suggestions.Add($"{networkBase}/{prefix}");
                            }
                        }
                    }
                }
            }
            catch
            {
            }

            return Results.Ok(new { ranges = suggestions.OrderBy(s => s).ToArray() });
        }

        internal static JwtBearerEvents CreateJwtEvents(IUnifiedLoggingService? startupUls, ILogger<Program>? startupLogger)
        {
            return new JwtBearerEvents
            {
                OnMessageReceived = context =>
                {
                    string auth = context.Request.Headers["Authorization"].ToString();
                    string snippet = string.Empty;
                    if (!string.IsNullOrEmpty(auth) && auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                    {
                        string tok = auth["Bearer ".Length..].Trim();
                        snippet = tok.Length > 12 ? tok[..12] + "..." : tok;
                        if (!string.IsNullOrEmpty(tok))
                        {
                            context.Token = tok;
                        }
                    }

                    try
                    {
                        string presence = !string.IsNullOrEmpty(auth) ? "present" : "missing";
                        if (startupUls != null)
                        {
                            startupUls.LogDebug($"[JWT][OnMessageReceived] Authorization header: {presence} tokenSnippet={snippet}");
                        }
                        else
                        {
                            startupLogger?.LogDebug("[JWT][OnMessageReceived] Authorization header: {Presence} tokenSnippet: {TokenSnippet}", presence, snippet);
                        }
                    }
                    catch
                    {
                    }

                    return Task.CompletedTask;
                },
                OnAuthenticationFailed = context =>
                {
                    try
                    {
                        string exType = context.Exception.GetType().Name;
                        string exMessage = context.Exception.Message;
                        if (startupUls != null)
                        {
                            startupUls.LogError(context.Exception, $"[JWT][OnAuthenticationFailed] {exType}: {exMessage}");
                        }
                        else
                        {
                            startupLogger?.LogError(context.Exception, "[JWT][OnAuthenticationFailed] {ExceptionType}: {Message}", exType, exMessage);
                        }
                    }
                    catch
                    {
                    }

                    return Task.CompletedTask;
                },
                OnTokenValidated = async context =>
                {
                    string sub = context.Principal?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? "<none>";
                    string roles = string.Join(',', context.Principal?.FindAll(System.Security.Claims.ClaimTypes.Role)?.Select(c => c.Value) ?? Array.Empty<string>());
                    try
                    {
                        if (startupUls != null)
                        {
                            startupUls.LogInformation($"[JWT][OnTokenValidated] user: {sub}, roles: [{roles}]");
                        }
                        else
                        {
                            startupLogger?.LogInformation("[JWT][OnTokenValidated] user: {User} roles: {Roles}", sub, roles);
                        }

                        // Check if token has been revoked. Prefer the raw token extracted from the Authorization header
                        // (context.Token) because that matches the original JWT string used to compute the stored token hash.
                        // Try to read raw token from Authorization header to ensure we compute the same hash
                        string? token = null;
                        try
                        {
                            string authHeader = context.HttpContext.Request.Headers["Authorization"].ToString();
                            if (!string.IsNullOrEmpty(authHeader) && authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                            {
                                token = authHeader["Bearer ".Length..].Trim();
                            }
                        }
                        catch
                        {
                        }

                        token ??= context.SecurityToken?.ToString();
                        if (!string.IsNullOrEmpty(token))
                        {
                            ITokenRevocationService? tokenRevocationService = context.HttpContext.RequestServices.GetService<ITokenRevocationService>();
                            if (tokenRevocationService != null)
                            {
                                bool isRevoked = await tokenRevocationService.IsTokenRevokedAsync(token);
                                if (isRevoked)
                                {
                                    if (startupUls != null)
                                    {
                                        startupUls.LogWarning($"[JWT][OnTokenValidated] Token revoked for user: {sub}");
                                    }
                                    else
                                    {
                                        startupLogger?.LogWarning("[JWT][OnTokenValidated] Token revoked for user: {User}", sub);
                                    }

                                    context.Fail("This token has been revoked.");
                                }
                            }
                        }
                    }
                    catch
                    {
                    }
                },
                OnChallenge = context =>
                {
                    try
                    {
                        string error = context.Error ?? "<none>";
                        string desc = context.ErrorDescription ?? "<none>";
                        if (startupUls != null)
                        {
                            startupUls.LogWarning($"[JWT][OnChallenge] Error={error} Desc={desc}");
                        }
                        else
                        {
                            startupLogger?.LogWarning("[JWT][OnChallenge] Error={Error} Desc={Desc}", error, desc);
                        }
                    }
                    catch
                    {
                    }

                    return Task.CompletedTask;
                }
            };
        }

        internal static async Task WriteHealthResponseAsync(HttpContext context, HealthReport report, IStartupStatus? startup, IHostEnvironment hostEnvironment)
        {
            context.Response.ContentType = "application/json";
            string result = JsonSerializer.Serialize(
                new
                {
                    Status = report.Status.ToString(),
                    TotalChecksDuration = report.TotalDuration,
                    Startup = startup == null ? null : new
                    {
                        phase = startup.Phase.ToString(),
                        ready = startup.IsReady,
                        failed = startup.IsFailed,
                        failureMessage = startup.FailureException?.Message,
                        failureStackTrace = (startup.FailureException != null && hostEnvironment.IsDevelopment()) ? startup.FailureException.StackTrace : null,
                        initStartedUtc = startup.InitializationStartedUtc,
                        initCompletedUtc = startup.InitializationCompletedUtc,
                        initDurationMs = startup.InitializationDuration?.TotalMilliseconds
                    },
                    Results = report.Entries.ToDictionary(
                        kvp => kvp.Key,
                        kvp => new
                        {
                            kvp.Value.Status,
                            kvp.Value.Duration,
                            kvp.Value.Description,
                            kvp.Value.Data
                        })
                },
                Program.HealthJsonOptions);

            await context.Response.WriteAsync(result);
        }

        internal static async Task InitializeDatabaseAsync(WebApplication app)
        {
            // Initialize settings and ensure DB schema exists. This runs post-build using app.Services to avoid building a separate provider.
            try
            {
                await using AsyncServiceScope initScope = app.Services.CreateAsyncScope();
                IServiceProvider sp = initScope.ServiceProvider;

                // Resolve required services for DB initialization and call into the existing initializer
                IUnifiedLoggingService logger = sp.GetRequiredService<IUnifiedLoggingService>();
                AppDbContext db = sp.GetRequiredService<AppDbContext>();
                IDatabaseInitializer dbInitializer = sp.GetRequiredService<IDatabaseInitializer>();
                IStartupStatus startupStatusResolved = sp.GetRequiredService<IStartupStatus>();

                // This call ensures the database schema exists and runs seeding. Do this before any
                // SettingsService or SettingsInitializationService read/write operations that depend on DB tables.
                await app.InitializeDatabaseAsync(logger, db, dbInitializer, startupStatusResolved);

                // After the DB schema exists and seeding has completed, apply environment-based settings initialization.
                try
                {
                    ISettingsInitializationService settingsInit = sp.GetRequiredService<ISettingsInitializationService>();
                    settingsInit.InitializeFromEnvironment<SpoolmanSettings>();
                    settingsInit.InitializeFromEnvironment<NetworkDiscoverySettings>();
                    app.Logger.LogInformation("[Startup] Settings initialization from environment variables completed");
                }
                catch (Exception innerEx)
                {
                    app.Logger.LogWarning(innerEx, "[Startup] Settings initialization from environment variables failed (non-fatal)");
                }
            }
            catch (Exception ex)
            {
                try
                {
                    app.Logger.LogWarning(ex, "[Startup] Settings/database initialization failed (non-fatal)");
                }
                catch
                {
                }
            }
        }
    }
}
