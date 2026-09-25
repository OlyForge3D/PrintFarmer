using System.Text.RegularExpressions;
using Farm.Infrastructure.Services.HostUpdates;

namespace Farm.HostUpdate.Cli;

internal enum HostUpdateCliCommand
{
    Help,
    Status,
    Recover,
}

/// <summary>Strict, fixed-grammar argument parser: unknown or repeated options are usage errors.</summary>
internal sealed partial class HostUpdateCliArguments
{
    private HostUpdateCliArguments(HostUpdateCliCommand command) => Command = command;

    public HostUpdateCliCommand Command { get; }

    public string? ReleaseId { get; private set; }

    public string? RequestId { get; private set; }

    public bool Preview { get; private set; }

    public bool Confirm { get; private set; }

    public bool Json { get; private set; }

    public string? ReapprovalToken { get; private set; }

    public static bool TryParse(IReadOnlyList<string> args, out HostUpdateCliArguments? parsed, out string? error)
    {
        parsed = null;
        error = null;
        if (args.Count == 0)
        {
            error = "missing_command";
            return false;
        }

        HostUpdateCliCommand? command = args[0] switch
        {
            "status" => HostUpdateCliCommand.Status,
            "recover" => HostUpdateCliCommand.Recover,
            "help" or "--help" or "-h" => HostUpdateCliCommand.Help,
            _ => null,
        };
        if (command is null)
        {
            error = "unknown_command";
            return false;
        }

        var result = new HostUpdateCliArguments(command.Value);
        if (command == HostUpdateCliCommand.Help)
        {
            if (args.Count != 1)
            {
                error = "unexpected_argument";
                return false;
            }

            parsed = result;
            return true;
        }

        string? confirmValue = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 1; i < args.Count; i++)
        {
            string option = args[i];
            if (!seen.Add(option))
            {
                error = "duplicate_option:" + option;
                return false;
            }

            switch (option)
            {
                case "--json":
                    result.Json = true;
                    break;
                case "--release":
                    if (!TryValue(args, ref i, out string? release))
                    {
                        error = "missing_value:--release";
                        return false;
                    }

                    result.ReleaseId = release;
                    break;
                case "--request-id" when command == HostUpdateCliCommand.Recover:
                    if (!TryValue(args, ref i, out string? requestId))
                    {
                        error = "missing_value:--request-id";
                        return false;
                    }

                    result.RequestId = requestId;
                    break;
                case "--preview" when command == HostUpdateCliCommand.Recover:
                    result.Preview = true;
                    break;
                case "--confirm" when command == HostUpdateCliCommand.Recover:
                    if (!TryValue(args, ref i, out confirmValue))
                    {
                        error = "missing_value:--confirm";
                        return false;
                    }

                    result.Confirm = true;
                    break;
                case "--reapprove-drift" when command == HostUpdateCliCommand.Recover:
                    if (!TryValue(args, ref i, out string? token))
                    {
                        error = "missing_value:--reapprove-drift";
                        return false;
                    }

                    result.ReapprovalToken = token;
                    break;
                default:
                    error = "unknown_option:" + option;
                    return false;
            }
        }

        if (result.ReleaseId is not null && !HostUpdateRecoveryRequestResolver.IsValidReleaseId(result.ReleaseId))
        {
            error = "invalid_release_id";
            return false;
        }

        if (result.RequestId is not null && !RequestIdPattern().IsMatch(result.RequestId))
        {
            error = "invalid_request_id";
            return false;
        }

        if (command == HostUpdateCliCommand.Recover)
        {
            if (result.ReleaseId is null)
            {
                error = "missing_option:--release";
                return false;
            }

            if (result.Preview == result.Confirm)
            {
                error = "exactly_one_of:--preview,--confirm";
                return false;
            }

            // Retyping the release id is the operator's explicit acknowledgement of which host
            // state will be changed; it must match byte for byte.
            if (result.Confirm && !string.Equals(confirmValue, result.ReleaseId, StringComparison.Ordinal))
            {
                error = "confirm_mismatch";
                return false;
            }

            if (result.ReapprovalToken is not null)
            {
                if (!result.Confirm)
                {
                    error = "reapprove_drift_requires_confirm";
                    return false;
                }

                if (!ReapprovalTokenPattern().IsMatch(result.ReapprovalToken))
                {
                    error = "invalid_reapproval_token";
                    return false;
                }
            }
        }

        parsed = result;
        return true;
    }

    private static bool TryValue(IReadOnlyList<string> args, ref int index, out string? value)
    {
        value = null;
        if (index + 1 >= args.Count || args[index + 1].StartsWith("--", StringComparison.Ordinal))
        {
            return false;
        }

        value = args[++index];
        return true;
    }

    [GeneratedRegex("^[A-Za-z0-9._:-]{1,128}$", RegexOptions.CultureInvariant)]
    private static partial Regex RequestIdPattern();

    [GeneratedRegex("^drift-[0-9a-f]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex ReapprovalTokenPattern();
}
