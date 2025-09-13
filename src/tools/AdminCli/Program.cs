using System.Net.Http.Json;
using System.Text.Json;

namespace Farm.Tools.AdminCli;

internal static class Program
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static async Task<int> Main(string[] args)
    {
#pragma warning disable CA1303 // Suppress localization warnings for CLI output
        var argsDic = ParseArgs(args);
        if (argsDic.ContainsKey("help") || argsDic.Count == 0)
        {
            PrintHelp();
            return 0;
        }

        var baseUrl = argsDic.GetValueOrDefault("base-url", "http://localhost:5245").TrimEnd('/');
        using var http = new HttpClient { BaseAddress = new Uri(baseUrl) };

        if (argsDic.ContainsKey("status"))
        {
            await PrintStatusAsync(http);
            return 0;
        }

        var hasUsername = argsDic.TryGetValue("username", out var _);
        var hasEmail = argsDic.TryGetValue("email", out var _);
        var hasPassword = argsDic.TryGetValue("password", out var _);
        if (!hasUsername || !hasEmail || !hasPassword)
        {
            await Console.Error.WriteLineAsync("ERROR: --username, --email and --password are required unless using --status.");
            PrintHelp();
            return 1;
        }

        var password = argsDic["password"];
        if (password.Length < 12)
        {
            await Console.Error.WriteLineAsync("ERROR: Password must be at least 12 characters (server requirement).");
            return 2;
        }

        Console.WriteLine($"[AdminCli] Attempting initial admin creation against {baseUrl} ...");

        var status = await http.GetFromJsonAsync<SetupStatus>("/api/setup/status");
        if (status == null)
        {
            await Console.Error.WriteLineAsync("ERROR: Could not retrieve setup status.");
            return 3;
        }

        if (!status.NeedsSetup)
        {
            Console.WriteLine("Admin already exists; attempting idempotent login with supplied credentials...");
        }

        var payload = new
        {
            username = argsDic["username"],
            email = argsDic["email"],
            password,
            firstName = argsDic.GetValueOrDefault("first-name", "Admin"),
            lastName = argsDic.GetValueOrDefault("last-name", "User")
        };

        var response = await http.PostAsJsonAsync("/api/setup/initial-admin", payload, Json);
        var body = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            await Console.Error.WriteLineAsync($"Server returned {(int)response.StatusCode} {response.StatusCode}\n{body}");
            return 4;
        }

        try
        {
            var result = JsonSerializer.Deserialize<AuthResult>(body, Json);
            if (result == null || !result.Success || string.IsNullOrWhiteSpace(result.Token))
            {
                await Console.Error.WriteLineAsync("Unexpected response: " + body);
                return 5;
            }

            Console.WriteLine("SUCCESS: Admin ready.");
            Console.WriteLine("Username: " + payload.username);
            Console.WriteLine("Email:    " + payload.email);
            Console.WriteLine("JWT:      " + result.Token);
            Console.WriteLine("Expires:  " + result.ExpiresAt);
            return 0;
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync("Failed to parse response: " + ex.Message + "\nRaw: " + body);
            return 6;
        }
#pragma warning restore CA1303
    }

    private static async Task PrintStatusAsync(HttpClient http)
    {
        try
        {
            var status = await http.GetFromJsonAsync<SetupStatus>("/api/setup/status");
            if (status == null)
            {
#pragma warning disable CA1303
                Console.WriteLine("Status: <null response>");
#pragma warning restore CA1303
                return;
            }
            Console.WriteLine("needsSetup=" + status.NeedsSetup.ToString().ToLowerInvariant());
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync("Error retrieving status: " + ex.Message);
        }
    }

    private static void PrintHelp()
    {
#pragma warning disable CA1303
        Console.WriteLine("PrintFarmer Admin CLI\n");
        Console.WriteLine("Commands:");
        Console.WriteLine("  --status                Show if initial setup is required");
        Console.WriteLine("  --username <value>      Admin username");
        Console.WriteLine("  --email <value>         Admin email");
        Console.WriteLine("  --password <value>      Admin password (min 12 chars)");
        Console.WriteLine("  --first-name <value>    First name (optional)");
        Console.WriteLine("  --last-name <value>     Last name (optional)");
        Console.WriteLine("  --base-url <url>        API base URL (default http://localhost:5245)");
        Console.WriteLine("  --help                  Display this help");
        Console.WriteLine();
        Console.WriteLine("Examples:");
        Console.WriteLine("  dotnet run --project src/tools/AdminCli -- --status");
        Console.WriteLine("  dotnet run --project src/tools/AdminCli -- --username admin --email admin@example.com --password 'LongPassword123!' --first-name Admin --last-name User");
#pragma warning restore CA1303
    }

    private static Dictionary<string, string> ParseArgs(string[] raw)
    {
        var dic = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int i = 0;
        while (i < raw.Length)
        {
            var token = raw[i];
            if (!token.StartsWith("--", StringComparison.Ordinal))
            {
                i++;
                continue;
            }
            var key = token[2..];
            if (key.Length == 0)
            {
                i++;
                continue;
            }
            if (i + 1 < raw.Length && !raw[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                dic[key] = raw[i + 1];
                i += 2;
            }
            else
            {
                dic[key] = "true";
                i++;
            }
        }
        return dic;
    }

    private sealed record SetupStatus(bool NeedsSetup);
    private sealed record AuthResult(bool Success, string? Token, DateTime? ExpiresAt, object? User, string? Error);
}
