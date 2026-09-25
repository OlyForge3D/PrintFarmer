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

    if (configPath is not null || index + 1 >= args.Length || !Path.IsPathRooted(args[index + 1]) || !File.Exists(args[index + 1]))
    {
        await Console.Error.WriteLineAsync("invalid_config: --config requires one existing absolute JSON file path").ConfigureAwait(false);
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
