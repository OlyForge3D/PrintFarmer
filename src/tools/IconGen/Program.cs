using SkiaSharp;
using Svg.Skia;

// Icon generation tool with flexible arguments.
// Args:
//  [0] => repo root (optional; auto-detected if omitted)
//  [1] => svg source path (optional)
//  [2] => output directory (optional)

var repoRoot = args.Length > 0 && !string.IsNullOrWhiteSpace(args[0])
    ? Path.GetFullPath(args[0])
    : FindRepoRoot();

if (repoRoot is null)
{
    Console.Error.WriteLine("Repo root not found. Pass path as first arg.");
    return 1;
}

// Candidate SVG locations (new React app first, then legacy Blazor client)
var candidateSvgs = new[]
{
    args.Length > 1 && !string.IsNullOrWhiteSpace(args[1]) ? args[1] : null,
    Path.Combine(repoRoot, "src", "Web", "ReactApp", "public", "printfarmer-logo.svg"),
    Path.Combine(repoRoot, "Client", "wwwroot", "favicon.svg"),
    Path.Combine(repoRoot, "archived", "blazor-client", "client", "wwwroot", "favicon.svg")
};

var svgPath = candidateSvgs
    .Where(p => !string.IsNullOrWhiteSpace(p))
    .Select(p => Path.GetFullPath(p!))
    .FirstOrDefault(File.Exists);

if (svgPath is null)
{
    Console.Error.WriteLine("No SVG source found. Provide path as arg[1].");
    return 2;
}

var outDir = args.Length > 2 && !string.IsNullOrWhiteSpace(args[2])
    ? Path.GetFullPath(args[2])
    : Path.Combine(repoRoot, "src", "Web", "ReactApp", "public", "icons");

Directory.CreateDirectory(outDir);

var sizes = new (int size, string name)[]
{
    (16, "favicon-16x16.png"),
    (32, "favicon-32x32.png"),
    (48, "favicon-48x48.png"),
    (96, "favicon-96x96.png"),
    (128, "icon-128x128.png"),
    (180, "apple-touch-icon.png"),
    (192, "android-chrome-192x192.png"),
    (256, "icon-256x256.png"),
    (384, "icon-384x384.png"),
    (512, "android-chrome-512x512.png")
};

Console.WriteLine($"Using SVG: {svgPath}");
Console.WriteLine($"Output dir: {outDir}");

using var stream = File.OpenRead(svgPath);
var svg = new SKSvg();
svg.Load(stream);

var picture = svg.Picture;
if (picture == null)
{
    Console.Error.WriteLine("Failed to load SVG picture.");
    return 3;
}

foreach (var (size, name) in sizes)
{
    using var bitmap = new SKBitmap(new SKImageInfo(size, size));
    using var canvas = new SKCanvas(bitmap);
    canvas.Clear(SKColors.Transparent);

    var svgBounds = picture.CullRect;
    // Uniform scale to fit
    var scale = Math.Min(size / svgBounds.Width, size / svgBounds.Height);
    // Centering translation after scale
    var scaledWidth = svgBounds.Width * scale;
    var scaledHeight = svgBounds.Height * scale;
    var offsetX = (size - scaledWidth) / 2f;
    var offsetY = (size - scaledHeight) / 2f;
    canvas.Translate(offsetX, offsetY);
    canvas.Scale(scale);
    canvas.Translate(-svgBounds.Left, -svgBounds.Top);
    canvas.DrawPicture(picture);
    canvas.Flush();

    using var image = SKImage.FromBitmap(bitmap);
    using var data = image.Encode(SKEncodedImageFormat.Png, 100);
    var outPath = Path.Combine(outDir, name);
    using var fs = File.Open(outPath, FileMode.Create, FileAccess.Write);
    data.SaveTo(fs);
    Console.WriteLine($"Wrote {outPath}");
}

// Convenience copies for common favicon names in public root
try
{
    var publicRoot = Path.Combine(repoRoot, "src", "Web", "ReactApp", "public");
    var favicon32 = Path.Combine(outDir, "favicon-32x32.png");
    var favicon16 = Path.Combine(outDir, "favicon-16x16.png");
    if (File.Exists(favicon32))
    {
        File.Copy(favicon32, Path.Combine(publicRoot, "favicon.png"), overwrite: true);
    }

    if (File.Exists(favicon16))
    {
        File.Copy(favicon16, Path.Combine(publicRoot, "favicon-16x16.png"), overwrite: true);
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Copy convenience favicon failed: {ex.Message}");
}

return 0;

static string? FindRepoRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir != null)
    {
        var solution = Path.Combine(dir.FullName, "farm-web.sln");
        if (File.Exists(solution))
        {
            return dir.FullName;
        }

        dir = dir.Parent;
    }
    return null;
}
