using SkiaSharp;
using Svg.Skia;

// Icon generation tool with flexible arguments.
// Args:
//  [0] => repo root (optional; auto-detected if omitted)
//  [1] => svg source path (optional)
//  [2] => output directory (optional)

string? repoRoot = args.Length > 0 && !string.IsNullOrWhiteSpace(args[0])
    ? Path.GetFullPath(args[0])
    : FindRepoRoot();

if (repoRoot is null)
{
    Console.Error.WriteLine("Repo root not found. Pass path as first arg.");
    return 1;
}

// Candidate SVG locations (new React app first, then legacy Blazor client)
string?[] candidateSvgs = new[]
{
    args.Length > 1 && !string.IsNullOrWhiteSpace(args[1]) ? args[1] : null,
    Path.Combine(repoRoot, "src", "Web", "ReactApp", "public", "printfarmer-logo.svg"),
    Path.Combine(repoRoot, "Client", "wwwroot", "favicon.svg"),
    Path.Combine(repoRoot, "archived", "blazor-client", "client", "wwwroot", "favicon.svg")
};

string? svgPath = candidateSvgs
    .Where(p => !string.IsNullOrWhiteSpace(p))
    .Select(p => Path.GetFullPath(p!))
    .FirstOrDefault(File.Exists);

if (svgPath is null)
{
    Console.Error.WriteLine("No SVG source found. Provide path as arg[1].");
    return 2;
}

string outDir = args.Length > 2 && !string.IsNullOrWhiteSpace(args[2])
    ? Path.GetFullPath(args[2])
    : Path.Combine(repoRoot, "src", "Web", "ReactApp", "public", "icons");

Directory.CreateDirectory(outDir);

(int size, string name)[] sizes = new (int size, string name)[]
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

using FileStream stream = File.OpenRead(svgPath);
SKSvg svg = new SKSvg();
svg.Load(stream);

SKPicture? picture = svg.Picture;
if (picture == null)
{
    Console.Error.WriteLine("Failed to load SVG picture.");
    return 3;
}

foreach ((int size, string? name) in sizes)
{
    using SKBitmap bitmap = new SKBitmap(new SKImageInfo(size, size));
    using SKCanvas canvas = new SKCanvas(bitmap);
    canvas.Clear(SKColors.Transparent);

    SKRect svgBounds = picture.CullRect;
    // Uniform scale to fit
    float scale = Math.Min(size / svgBounds.Width, size / svgBounds.Height);
    // Centering translation after scale
    float scaledWidth = svgBounds.Width * scale;
    float scaledHeight = svgBounds.Height * scale;
    float offsetX = (size - scaledWidth) / 2f;
    float offsetY = (size - scaledHeight) / 2f;
    canvas.Translate(offsetX, offsetY);
    canvas.Scale(scale);
    canvas.Translate(-svgBounds.Left, -svgBounds.Top);
    canvas.DrawPicture(picture);
    canvas.Flush();

    using SKImage image = SKImage.FromBitmap(bitmap);
    using SKData data = image.Encode(SKEncodedImageFormat.Png, 100);
    string outPath = Path.Combine(outDir, name);
    using FileStream fs = File.Open(outPath, FileMode.Create, FileAccess.Write);
    data.SaveTo(fs);
    Console.WriteLine($"Wrote {outPath}");
}

// Convenience copies for common favicon names in public root
try
{
    string publicRoot = Path.Combine(repoRoot, "src", "Web", "ReactApp", "public");
    string favicon32 = Path.Combine(outDir, "favicon-32x32.png");
    string favicon16 = Path.Combine(outDir, "favicon-16x16.png");
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
    DirectoryInfo? dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir != null)
    {
        string solution = Path.Combine(dir.FullName, "farm-web.sln");
        if (File.Exists(solution))
        {
            return dir.FullName;
        }

        dir = dir.Parent;
    }

    return null;
}
