using System.Runtime.InteropServices;
using Farm.HostUpdate.Cli;
using Microsoft.Extensions.Configuration;

// --config is consumed here, before command parsing, so the command grammar never sees paths.
var remaining = new List<string>(args.Length);
string? configPath = null;
int index = 0;
while (index < args.Length)
{
    if (!string.Equals(args[index], "--config", StringComparison.Ordinal))
    {
        remaining.Add(args[index]);
        index++;
        continue;
    }

    // Only the shape is a usage error. Existence and readability are proven by the guarded
    // loader (exit 3), because File.Exists also reports false for an access-denied file.
    if (configPath is not null || index + 1 >= args.Length || !Path.IsPathFullyQualified(args[index + 1]))
    {
        await Console.Error.WriteLineAsync("invalid_config: --config requires one absolute JSON file path").ConfigureAwait(false);
        return HostUpdateCliExitCodes.Usage;
    }

    configPath = args[index + 1];
    index += 2;
}

IConfiguration LoadConfiguration()
{
    var builder = new ConfigurationBuilder();
    if (configPath is not null)
    {
        builder.AddJsonFile(configPath, optional: false, reloadOnChange: false);
    }

    builder.AddEnvironmentVariables();
    return builder.Build();
}

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};

bool daemon = remaining.Count > 0 && string.Equals(remaining[0], "daemon", StringComparison.Ordinal);

// Issue #3118: `daemon --windows-service` runs under the Windows Service Control Manager, which
// starts and stops it; its redacted status lines go to the fixed daemon log instead of a console.
// The whole command is validated first, so an invalid form still exits 2 with the usage text.
if (daemon && OperatingSystem.IsWindows() &&
    HostUpdateCliArguments.TryParse(remaining, out HostUpdateCliArguments? serviceArguments, out _) &&
    serviceArguments!.WindowsService)
{
    return HostUpdateDaemonWindowsService.Run(remaining, LoadConfiguration, Console.Error);
}

// systemd stops the daemon with SIGTERM; stop it cleanly, as Ctrl+C does. Other commands keep the
// runtime's default SIGTERM handling.
using PosixSignalRegistration? sigterm = daemon && !OperatingSystem.IsWindows()
    ? PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
    {
        context.Cancel = true;
        cancellation.Cancel();
    })
    : null;

return await HostUpdateCli.RunAsync(remaining, LoadConfiguration, Console.Out, Console.Error, cancellation.Token).ConfigureAwait(false);
