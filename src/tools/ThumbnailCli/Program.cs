using System;
using System.Collections.Generic;
using System.IO;
using Farm.Web.Api.Services;

namespace ThumbnailCli;

internal static class Program
{
    private sealed record CliOptions(
        string Input,
        string Output,
        string Preset,
        int Width,
        int Height,
        int? ZoomPercent,
        string? View,
        bool EnableGroundShadow,
        bool TwoSided,
        bool EnableAmbientOcclusion);

    private static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0 || HasFlag(args, "-h", "--help"))
            {
                PrintHelp();
                return 0;
            }

            var opts = Parse(args);

            if (!File.Exists(opts.Input))
            {
                Console.Error.WriteLine($"Input file not found: {opts.Input}");
                return 1;
            }

            BasePreviewRenderer renderer = opts.Preset.Equals("prusa", StringComparison.OrdinalIgnoreCase)
                ? new PrusaPreviewRenderer()
                : new OrcaPreviewRenderer();

            RenderOptions options = opts.Preset.Equals("prusa", StringComparison.OrdinalIgnoreCase)
                ? PrusaPreset.Create()
                : OrcaPreset.Create();

            options.Width = opts.Width;
            options.Height = opts.Height;

            int defaultZoomPercent = opts.Preset.Equals("prusa", StringComparison.OrdinalIgnoreCase) ? 44 : 40;
            if (opts.ZoomPercent.HasValue)
            {
                options.SetZoomPercent(defaultZoomPercent, opts.ZoomPercent.Value);
            }
            options.EnableGroundShadow = opts.EnableGroundShadow;
            options.TwoSided = opts.TwoSided;
            options.EnableAmbientOcclusion = opts.EnableAmbientOcclusion;
            
            // Apply camera view
            if (!string.IsNullOrWhiteSpace(opts.View))
            {
                var viewName = opts.View;
                if (!options.SetCameraView(viewName))
                {
                    Console.Error.WriteLine($"Warning: Unknown view '{viewName}', using default 'front'");
                    options.SetCameraView("front");
                }
            }

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(opts.Output)) ?? ".");

            renderer.Render(opts.Input, opts.Output, options);
            Console.WriteLine($"✓ Rendered {opts.Preset} thumbnail -> {opts.Output}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }

    private static CliOptions Parse(IReadOnlyList<string> args)
    {
        string input = Require(args, "--input", "-i");
        string output = GetValue(args, "--output", "-o") ?? Path.ChangeExtension(input, ".png");
        string preset = GetValue(args, "--preset", "-p")?.ToLowerInvariant() ?? "orca";

        int width = int.TryParse(GetValue(args, "--width", "-w"), out var w) ? w : 1024;
        int height = int.TryParse(GetValue(args, "--height", "-h"), out var h) ? h : 1024;
        
        int? zoomPercent = int.TryParse(GetValue(args, "--zoom", "-z"), out var zv) ? zv : null;
        
        string? view = GetValue(args, "--view", "-v")?.ToLowerInvariant();

        bool enableGroundShadow = !HasFlag(args, "--no-shadow");
        bool twoSided = !HasFlag(args, "--single-sided");
        bool enableAo = !HasFlag(args, "--no-ao");

        return new CliOptions(input, output, preset, width, height, zoomPercent, view, enableGroundShadow, twoSided, enableAo);
    }

    private static bool HasFlag(IReadOnlyList<string> args, params string[] keys)
    {
        foreach (var a in args)
        {
            foreach (var k in keys)
            {
                if (string.Equals(a, k, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        return false;
    }

    private static string Require(IReadOnlyList<string> args, params string[] keys)
    {
        var value = GetValue(args, keys);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"Missing required option: {string.Join(" | ", keys)}");
        }
        return value;
    }

    private static string? GetValue(IReadOnlyList<string> args, params string[] keys)
    {
        for (int i = 0; i < args.Count; i++)
        {
            foreach (var key in keys)
            {
                if (string.Equals(args[i], key, StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 < args.Count)
                        return args[i + 1];
                    return null;
                }
            }
        }
        return null;
    }

#pragma warning disable CA1303 // Do not pass literals as localized parameters
    private static void PrintHelp()
    {
        Console.WriteLine("Thumbnail CLI");
        Console.WriteLine("Usage: dotnet run --project ./src/tools/ThumbnailCli -- --input model.stl [options]\n");
        Console.WriteLine("Options:");
        Console.WriteLine("  -i, --input <path>     Input model file (.stl, .3mf, .obj, etc)");
        Console.WriteLine("  -o, --output <path>    Output png (default: input with .png)");
        Console.WriteLine("  -p, --preset <orca|prusa>  Renderer preset (default: orca)");
        Console.WriteLine("  -w, --width <int>      Width in pixels (default: 1024)");
        Console.WriteLine("  -h, --height <int>     Height in pixels (default: 1024)");
        Console.WriteLine("  -z, --zoom <percent>   Zoom as percentage 25-500 (default: 40 orca, 44 prusa)");
        Console.WriteLine("  -v, --view <view>      Camera view: front|top|bottom|left|right|back (default: front)");
        Console.WriteLine("      --no-shadow        Disable ground shadow");
        Console.WriteLine("      --single-sided     Disable two-sided rendering (culls backfaces)");
        Console.WriteLine("      --no-ao            Disable ambient occlusion");
        Console.WriteLine("  --help                 Show help");
    }
#pragma warning restore CA1303
}
