using SkiaSharp;
using Svg.Skia;

var repoRoot = args.Length > 0 ? args[0] : FindRepoRoot();
if (repoRoot is null)
{
    Console.Error.WriteLine("Repo root not found. Pass path as arg.");
    return 1;
}

var svgPath = Path.Combine(repoRoot, "Client", "wwwroot", "favicon.svg");
var outDir = Path.Combine(repoRoot, "Client", "wwwroot", "icons");
Directory.CreateDirectory(outDir);

if (!File.Exists(svgPath))
{
    Console.Error.WriteLine($"SVG not found at {svgPath}");
    return 2;
}

var sizes = new (int size, string name)[]
{
    (16, "favicon-16x16.png"),
    (32, "favicon-32x32.png"),
    (48, "favicon-48x48.png"),
    (96, "favicon-96x96.png"),
    (180, "apple-touch-icon.png"),
    (192, "android-chrome-192x192.png"),
    (512, "android-chrome-512x512.png")
};

using var stream = File.OpenRead(svgPath);
var svg = new SKSvg();
svg.Load(stream);

foreach (var (size, name) in sizes)
{
    using var bitmap = new SKBitmap(new SKImageInfo(size, size));
    using var canvas = new SKCanvas(bitmap);
    canvas.Clear(SKColors.Transparent);

    var picture = svg.Picture;
    if (picture == null)
    {
        Console.Error.WriteLine("Failed to load SVG picture.");
        return 3;
    }

    var svgBounds = picture.CullRect;
    var scaleX = size / svgBounds.Width;
    var scaleY = size / svgBounds.Height;
    var scale = Math.Min(scaleX, scaleY);
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

return 0;

static string? FindRepoRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir != null)
    {
        var solution = Path.Combine(dir.FullName, "farm-web.sln");
        if (File.Exists(solution))
            return dir.FullName;
        dir = dir.Parent;
    }
    return null;
}
