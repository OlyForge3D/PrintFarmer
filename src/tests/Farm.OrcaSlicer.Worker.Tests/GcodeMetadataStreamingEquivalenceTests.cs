using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Farm.OrcaSlicer.Worker.Services;
using FluentAssertions;
using Xunit;

namespace Farm.OrcaSlicer.Worker.Tests;

/// <summary>
/// Issue #3341: G-code metadata extraction now streams the file instead of materialising it with
/// File.ReadAllLinesAsync. These tests pin the streaming implementation to a verbatim copy of the
/// previous ReadAllLines-based algorithm so any divergence in parse results fails loudly.
/// </summary>
public sealed class GcodeMetadataStreamingEquivalenceTests : IDisposable
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly UTF8Encoding Utf8WithBom = new(encoderShouldEmitUTF8Identifier: true);

    private readonly string _directory =
        Path.Join(Path.GetTempPath(), $"printfarmer-gcode-metadata-{Guid.NewGuid():N}");

    public GcodeMetadataStreamingEquivalenceTests()
    {
        Directory.CreateDirectory(_directory);
    }

    public static TheoryData<string, string> SyntheticInputs => new()
    {
        { "orca-header-lf", "; estimated printing time (normal mode) = 1h 23m 45s\n; filament used [mm] = 1234.56\n; layer_count = 42\nG1 Z0.2\n" },
        { "seconds-only", "; estimated printing time = 95s\nG1 X1\n" },
        { "grams", "; filament used [g] = 12.5g\n" },
        { "crlf", "; estimated printing time = 2h 5m\r\n; filament used = 10.5g\r\n;LAYER:0\r\n;LAYER:7\r\nG1 Z0.4\r\n" },
        { "cr-only", "; estimated printing time = 2h 5m\r;LAYER:3\rG1 Z0.4\r" },
        { "no-trailing-newline-metadata", "G1 X1\n; layer_count = 9" },
        { "no-trailing-newline-zmove", "G1 Z0.2\nG0 Z0.4" },
        { "blank-lines", "\n\n\n; layer_count = 5\n\n\n\n" },
        { "only-blank-lines", "\n\r\n\n" },
        { "empty", "" },
        { "layer-comments-fallback", ";LAYER:0\n;LAYER:15\n;LAYER:3\n" },
        { "z-move-fallback", "G1 Z0.2 F600\nG0 Z0.4\nG1 X2 Z0.6\n g1 Z1\nG1 Z0.8\n" },
        { "last-value-wins", "; layer_count = 3\n; layer_count = 11\n; filament used = 1.0g\n; filament used = 2.5g\n" },
        { "unicode-comment", "; ünïcödé ☃ comment\n; estimated printing time = 0h 1m\n" },
        { "leading-bom-literal", "\uFEFF; layer_count = 6\nG1 Z0.2\n" },
    };

    [Theory]
    [MemberData(nameof(SyntheticInputs))]
    public async Task ExtractGcodeMetadataAsync_SyntheticInput_MatchesLegacyReadAllLines(string name, string content)
    {
        string withoutBom = WriteFile($"{name}.gcode", content, Utf8NoBom);
        string withBom = WriteFile($"{name}.bom.gcode", content, Utf8WithBom);

        await AssertEquivalentAsync(withoutBom);
        await AssertEquivalentAsync(withBom);
        await AssertEquivalentAsync(WriteFile($"{name}.utf16le.gcode", content, new UnicodeEncoding(bigEndian: false, byteOrderMark: true)));
        await AssertEquivalentAsync(WriteFile($"{name}.utf16be.gcode", content, new UnicodeEncoding(bigEndian: true, byteOrderMark: true)));
        await AssertEquivalentAsync(WriteFile($"{name}.utf32le.gcode", content, new UTF32Encoding(bigEndian: false, byteOrderMark: true)));
    }

    [Fact]
    public async Task ExtractGcodeMetadataAsync_RealSampleGcodeCorpus_MatchesLegacyReadAllLines()
    {
        string corpus = FindSampleGcodeDirectory();
        string[] files = Directory.GetFiles(corpus, "*.gcode", SearchOption.AllDirectories);
        files.Should().NotBeEmpty("the shared sample G-code corpus must be available for the equivalence check");

        foreach (string file in files)
        {
            await AssertEquivalentAsync(file);
        }
    }

    [Fact]
    public async Task ExtractGcodeMetadataAsync_LargeSyntheticFile_MatchesLegacyReadAllLines()
    {
        var builder = new StringBuilder();
        builder.Append("; estimated printing time (normal mode) = 12h 34m 56s\r\n");
        for (int layer = 0; layer < 2_000; layer++)
        {
            builder.Append(CultureInfo.InvariantCulture, $";LAYER:{layer}\r\nG1 Z{(layer + 1) * 0.2:F2}\r\n");
            for (int move = 0; move < 50; move++)
            {
                builder.Append(CultureInfo.InvariantCulture, $"G1 X{move}.123 Y{layer}.456 E0.0321\r\n");
            }
        }

        builder.Append("; filament used [mm] = 98765.4");
        string path = WriteFile("large.gcode", builder.ToString(), Utf8WithBom);

        await AssertEquivalentAsync(path);
    }

    [Fact]
    public async Task ExtractGcodeMetadataAsync_CancelledToken_Throws()
    {
        string path = WriteFile("cancel.gcode", "G1 Z0.2\n", Utf8NoBom);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        Func<Task> act = async () => await OrcaSlicingPipelineService.ExtractGcodeMetadataAsync(path, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static async Task AssertEquivalentAsync(string path)
    {
        LegacyMetadata expected = await LegacyExtractAsync(path);
        OrcaSlicingPipelineService.GcodeMetadata actual =
            await OrcaSlicingPipelineService.ExtractGcodeMetadataAsync(path, CancellationToken.None);

        actual.PrintTimeSeconds.Should().Be(expected.PrintTimeSeconds, "print time for {0}", path);
        actual.FilamentUsageGrams.Should().Be(expected.FilamentUsageGrams, "filament usage for {0}", path);
        actual.LayerCount.Should().Be(expected.LayerCount, "layer count for {0}", path);
    }

    private string WriteFile(string name, string content, Encoding encoding)
    {
        string path = Path.Join(_directory, name);
        File.WriteAllText(path, content, encoding);
        return path;
    }

    private static string FindSampleGcodeDirectory()
    {
        DirectoryInfo? current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            string candidate = Path.Join(current.FullName, "tests", "Farm.Infrastructure.Tests", "Infrastructure", "sample_gcode");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            candidate = Path.Join(current.FullName, "src", "tests", "Farm.Infrastructure.Tests", "Infrastructure", "sample_gcode");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate src/tests/Farm.Infrastructure.Tests/Infrastructure/sample_gcode.");
    }

    private sealed record LegacyMetadata(double PrintTimeSeconds, double FilamentUsageGrams, int LayerCount);

    // Verbatim copy of the pre-#3341 implementation (File.ReadAllLinesAsync). Do not "fix" it:
    // it is the oracle the streaming implementation must match.
    private static async Task<LegacyMetadata> LegacyExtractAsync(string gcodeFilePath)
    {
        FileInfo fileInfo = new FileInfo(gcodeFilePath);
        string[] lines = await File.ReadAllLinesAsync(gcodeFilePath, CancellationToken.None).ConfigureAwait(false);
        double printTimeSeconds = 0;
        double filamentUsageGrams = 0;
        int layerCount = 0;
        Regex printTimeRegex = new Regex(@";\s*estimated printing time.*?(\d+)h\s*(\d+)m", RegexOptions.IgnoreCase);
        Regex printTimeSecondsRegex = new Regex(@";\s*estimated printing time.*?(\d+)s", RegexOptions.IgnoreCase);
        Regex filamentRegex = new Regex(@";\s*filament used.*?(\d+\.?\d*)(?:mm|g)", RegexOptions.IgnoreCase);
        Regex layerRegex = new Regex(@";\s*layer_count\s*=\s*(\d+)", RegexOptions.IgnoreCase);
        Regex layerCommentRegex = new Regex(@";\s*LAYER:(\d+)", RegexOptions.IgnoreCase);
        int maxLayer = 0;
        foreach (string line in lines)
        {
            Match tm = printTimeRegex.Match(line);
            if (tm.Success)
            {
                printTimeSeconds = ((double)int.Parse(tm.Groups[1].Value, CultureInfo.InvariantCulture) * 3600) + ((double)int.Parse(tm.Groups[2].Value, CultureInfo.InvariantCulture) * 60);
            }
            else
            {
                Match ts = printTimeSecondsRegex.Match(line);
                if (ts.Success)
                {
                    printTimeSeconds = int.Parse(ts.Groups[1].Value, CultureInfo.InvariantCulture);
                }
            }

            Match fm = filamentRegex.Match(line);
            if (fm.Success)
            {
                double amount = double.Parse(fm.Groups[1].Value, CultureInfo.InvariantCulture);
                filamentUsageGrams = line.Contains("mm", StringComparison.Ordinal) ? amount * 0.0025 : amount;
            }

            Match lc = layerRegex.Match(line);
            if (lc.Success)
            {
                layerCount = int.Parse(lc.Groups[1].Value, CultureInfo.InvariantCulture);
            }

            Match lcm = layerCommentRegex.Match(line);
            if (lcm.Success)
            {
                maxLayer = Math.Max(maxLayer, int.Parse(lcm.Groups[1].Value, CultureInfo.InvariantCulture));
            }
        }

        if (layerCount == 0 && maxLayer > 0)
        {
            layerCount = maxLayer + 1;
        }

        const double epsilon = 0.0001;
        if (Math.Abs(printTimeSeconds) < epsilon)
        {
            printTimeSeconds = layerCount > 0 ? layerCount * 120 : 1800;
        }

        if (Math.Abs(filamentUsageGrams) < epsilon)
        {
            filamentUsageGrams = Math.Max(5.0, fileInfo.Length / 50000.0);
        }

        if (layerCount == 0)
        {
            layerCount = lines.Count(l => l.StartsWith("G1 Z", StringComparison.Ordinal) || l.StartsWith("G0 Z", StringComparison.Ordinal));
        }

        if (layerCount == 0)
        {
            layerCount = 100;
        }

        return new LegacyMetadata(printTimeSeconds, filamentUsageGrams, layerCount);
    }
}
