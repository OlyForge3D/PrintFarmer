using System.Security.Cryptography;
using System.Text;
using Farm.Web.Api.Services.Gcode.Safety;

namespace Farm.Modules.Gcode.Services.Gcode.Safety;

/// <summary>Rejects empty programs and records non-empty input without interpreting its commands.</summary>
public sealed class GcodeSafetyValidator : IGcodeSafetyValidator
{
    /// <inheritdoc/>
    public GcodeSafetyResult<GcodeSafetyReport> Validate(GcodeSafetyRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrEmpty(request.Gcode))
        {
            return GcodeSafetyResult.Failure<GcodeSafetyReport>(
                GcodeSafetyProblemCodes.Malformed,
                "gcode",
                "The g-code program is empty.");
        }

        int commandCount = 0;
        foreach (string rawLine in request.Gcode.Split('\n'))
        {
            int commentStart = rawLine.IndexOf(';');
            string line = (commentStart < 0 ? rawLine : rawLine[..commentStart]).Trim();
            if (line.Length > 0)
            {
                commandCount++;
            }
        }

        string sha256 = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(request.Gcode)))
            .ToLowerInvariant();
        return GcodeSafetyResult.Success(new GcodeSafetyReport(
            request.Checkpoint,
            sha256,
            commandCount,
            DateTime.UtcNow));
    }
}
