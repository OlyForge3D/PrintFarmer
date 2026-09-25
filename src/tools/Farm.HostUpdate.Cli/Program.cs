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

return await HostUpdateCli.RunAsync(remaining, LoadConfiguration, Console.Out, Console.Error, cancellation.Token).ConfigureAwait(false);
