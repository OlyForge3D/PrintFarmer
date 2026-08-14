namespace Farm.Moonraker.Emulator.Domain;

/// <summary>
/// A single virtual G-code file. Content is kept in memory only — the emulator never
/// touches the real filesystem, network, or any external service.
/// </summary>
public sealed class VirtualFile
{
    public required string Path { get; init; }

    public byte[] Content { get; set; } = [];

    public DateTimeOffset Modified { get; set; }

    public VirtualGcodeMetadata Metadata { get; set; } = new();
}

/// <summary>Metadata Moonraker would have scanned out of a G-code file's header comments.</summary>
public sealed class VirtualGcodeMetadata
{
    public string? Slicer { get; set; } = "OrcaSlicer";

    public string? SlicerVersion { get; set; } = "2.1.0";

    public double? LayerHeight { get; set; } = 0.2;

    public double? FirstLayerHeight { get; set; } = 0.2;

    public double? ObjectHeight { get; set; } = 20.0;

    public double? FilamentTotal { get; set; } = 1200.0;

    public double? FilamentWeightTotal { get; set; } = 3.6;

    public int? EstimatedTime { get; set; } = 3600;

    public double? FirstLayerBedTemp { get; set; } = 60.0;

    public double? FirstLayerExtrTemp { get; set; } = 215.0;

    public long? GcodeStartByte { get; set; }

    public long? GcodeEndByte { get; set; }

    public List<VirtualThumbnail> Thumbnails { get; } = [];

    public List<VirtualPrintObject> Objects { get; } = [];
}

/// <summary>A thumbnail entry referenced from G-code metadata (relative_path resolves under the gcodes root).</summary>
public sealed record VirtualThumbnail(int Width, int Height, int Size, string RelativePath);

/// <summary>An excludable object parsed out of the (simulated) EXCLUDE_OBJECT header comments.</summary>
public sealed record VirtualPrintObject(string Name, double[]? Center = null);

/// <summary>Result of a directory-create operation, used to select the correct Moonraker-shaped error.</summary>
public enum DirectoryCreateResult
{
    Created,
    AlreadyExists,
    ParentMissing,
}

/// <summary>Result of a directory-delete operation, used to select the correct Moonraker-shaped error.</summary>
public enum DirectoryDeleteResult
{
    Deleted,
    NotFound,
    NotEmpty,
    RootProtected,
}

/// <summary>
/// An in-memory, root-scoped virtual filesystem covering the Moonraker "gcodes" root
/// (and any other declared root, e.g. "config"). All state is per-printer.
/// </summary>
public sealed class VirtualFileSystem
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, VirtualFile>> _roots = new(StringComparer.Ordinal);

    // Explicit directory entries (root -> set of normalized directory paths). Directories that only
    // contain files are already discoverable by scanning file path prefixes (see ListDirectory), but an
    // *empty* directory has no file to derive its existence from, so it must be tracked explicitly here
    // once created via POST server/files/directory, and removed once deleted via DELETE.
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _directories = new(StringComparer.Ordinal);

    public VirtualFileSystem()
    {
        Reset();
    }

    public void Reset()
    {
        _roots.Clear();
        _directories.Clear();
        foreach (string root in new[] { "gcodes", "config", "logs" })
        {
            _roots[root] = new ConcurrentDictionary<string, VirtualFile>(StringComparer.Ordinal);
            _directories[root] = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        }
    }

    public IReadOnlyCollection<string> Roots => _roots.Keys.ToArray();

    private ConcurrentDictionary<string, VirtualFile> RootFiles(string root) =>
        _roots.GetOrAdd(root, _ => new ConcurrentDictionary<string, VirtualFile>(StringComparer.Ordinal));

    private ConcurrentDictionary<string, byte> RootDirectories(string root) =>
        _directories.GetOrAdd(root, _ => new ConcurrentDictionary<string, byte>(StringComparer.Ordinal));

    public VirtualFile Seed(string root, string path, byte[] content, VirtualGcodeMetadata? metadata = null)
    {
        var file = new VirtualFile
        {
            Path = NormalizePath(path),
            Content = content,
            Modified = DateTimeOffset.UtcNow,
            Metadata = metadata ?? new VirtualGcodeMetadata(),
        };
        RootFiles(root)[file.Path] = file;
        return file;
    }

    public IReadOnlyList<VirtualFile> List(string root) =>
        RootFiles(root).Values.OrderBy(f => f.Path, StringComparer.Ordinal).ToList();

    public bool TryGet(string root, string path, out VirtualFile? file) =>
        RootFiles(root).TryGetValue(NormalizePath(path), out file);

    /// <summary>
    /// True if <paramref name="path"/> matches any seeded gcode file's thumbnail
    /// <c>relative_path</c> (e.g. <c>"thumbs/benchy-32x32.png"</c>). Real Moonraker/Klipper
    /// slicers physically write thumbnail images to disk under the gcodes root, so Moonraker's
    /// generic gcode-root download route (<c>GET server/files/gcodes/{path}</c>) also serves
    /// them — it isn't a thumbnail-specific route. The emulator only models thumbnails as
    /// metadata plus a dedicated <c>server/files/thumbs/{file}</c> route, so this lets the same
    /// physical path resolve through the gcode-root route too, matching how
    /// <c>MoonrakerClient.GetJobAsync</c> builds a print job's thumbnail URL as
    /// <c>{baseUrl}/server/files/gcodes/{relative_path}</c>.
    /// </summary>
    public bool IsKnownThumbnailPath(string root, string path)
    {
        string normalized = NormalizePath(path);
        return RootFiles(root).Values.Any(f => f.Metadata.Thumbnails.Any(t => NormalizePath(t.RelativePath) == normalized));
    }

    public VirtualFile Put(string root, string path, byte[] content)
    {
        var file = new VirtualFile
        {
            Path = NormalizePath(path),
            Content = content,
            Modified = DateTimeOffset.UtcNow,
        };
        RootFiles(root)[file.Path] = file;
        return file;
    }

    public bool Delete(string root, string path) => RootFiles(root).TryRemove(NormalizePath(path), out _);

    public bool Move(string root, string source, string dest)
    {
        string src = NormalizePath(source);
        string dst = NormalizePath(dest);
        ConcurrentDictionary<string, VirtualFile> files = RootFiles(root);
        if (!files.TryRemove(src, out VirtualFile? file))
        {
            return false;
        }

        VirtualFile moved = new()
        {
            Path = dst,
            Content = file.Content,
            Modified = DateTimeOffset.UtcNow,
            Metadata = file.Metadata,
        };
        files[dst] = moved;
        return true;
    }

    public bool Copy(string root, string source, string dest)
    {
        string src = NormalizePath(source);
        string dst = NormalizePath(dest);
        ConcurrentDictionary<string, VirtualFile> files = RootFiles(root);
        if (!files.TryGetValue(src, out VirtualFile? file))
        {
            return false;
        }

        files[dst] = new VirtualFile
        {
            Path = dst,
            Content = (byte[])file.Content.Clone(),
            Modified = DateTimeOffset.UtcNow,
            Metadata = file.Metadata,
        };
        return true;
    }

    /// <summary>Lists direct children (files and sub-directory names) of a directory path within a root.</summary>
    public (IReadOnlyList<string> Dirs, IReadOnlyList<VirtualFile> Files) ListDirectory(string root, string path)
    {
        string prefix = NormalizePath(path);
        if (prefix.Length > 0)
        {
            prefix += "/";
        }

        var dirs = new SortedSet<string>(StringComparer.Ordinal);
        var files = new List<VirtualFile>();
        foreach (VirtualFile file in RootFiles(root).Values)
        {
            if (!file.Path.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            string remainder = file.Path[prefix.Length..];
            int slash = remainder.IndexOf('/');
            if (slash < 0)
            {
                files.Add(file);
            }
            else
            {
                dirs.Add(remainder[..slash]);
            }
        }

        // Merge in explicitly-created directories (including ones with no files, e.g. freshly
        // created empty directories) so they show up as direct children too.
        foreach (string dirPath in RootDirectories(root).Keys)
        {
            if (!dirPath.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            string remainder = dirPath[prefix.Length..];
            if (remainder.Length == 0)
            {
                continue; // dirPath *is* the requested directory itself, not a child of it.
            }

            int slash = remainder.IndexOf('/');
            dirs.Add(slash < 0 ? remainder : remainder[..slash]);
        }

        return (dirs.ToList(), files.OrderBy(f => f.Path, StringComparer.Ordinal).ToList());
    }

    /// <summary>
    /// True if <paramref name="path"/> is the root itself, an explicitly-created directory, or a
    /// directory implied by the presence of a file/sub-directory beneath it.
    /// </summary>
    public bool DirectoryExists(string root, string path)
    {
        string normalized = NormalizePath(path);
        if (normalized.Length == 0)
        {
            return true; // The root of the "root" itself always exists.
        }

        if (RootDirectories(root).ContainsKey(normalized))
        {
            return true;
        }

        string prefix = normalized + "/";
        return RootFiles(root).Keys.Any(k => k.StartsWith(prefix, StringComparison.Ordinal)) ||
               RootDirectories(root).Keys.Any(k => k.StartsWith(prefix, StringComparison.Ordinal));
    }

    /// <summary>True if a plain file (not a directory) already occupies <paramref name="path"/>.</summary>
    public bool FileExists(string root, string path) => RootFiles(root).ContainsKey(NormalizePath(path));

    /// <summary>
    /// Creates an explicit (possibly empty) directory entry. Mirrors real Moonraker's <c>POST
    /// server/files/directory</c> semantics: fails if something already occupies the path, and fails
    /// if the immediate parent directory does not exist (Moonraker does not create intermediate
    /// directories implicitly).
    /// </summary>
    public DirectoryCreateResult CreateDirectory(string root, string path)
    {
        string normalized = NormalizePath(path);
        if (normalized.Length == 0 || DirectoryExists(root, normalized) || FileExists(root, normalized))
        {
            return DirectoryCreateResult.AlreadyExists;
        }

        int slash = normalized.LastIndexOf('/');
        string parent = slash < 0 ? string.Empty : normalized[..slash];
        if (!DirectoryExists(root, parent))
        {
            return DirectoryCreateResult.ParentMissing;
        }

        RootDirectories(root)[normalized] = 0;
        return DirectoryCreateResult.Created;
    }

    /// <summary>
    /// Deletes a directory (and, if <paramref name="force"/> is set, everything beneath it). Mirrors
    /// real Moonraker's <c>DELETE server/files/directory</c> semantics: 404-equivalent when nothing
    /// exists at the path, and a distinct "not empty" failure when <paramref name="force"/> is false
    /// and the directory still has children.
    /// </summary>
    public DirectoryDeleteResult DeleteDirectory(string root, string path, bool force)
    {
        string normalized = NormalizePath(path);
        if (normalized.Length == 0)
        {
            return DirectoryDeleteResult.RootProtected;
        }

        if (!DirectoryExists(root, normalized))
        {
            return DirectoryDeleteResult.NotFound;
        }

        (IReadOnlyList<string> dirs, IReadOnlyList<VirtualFile> files) = ListDirectory(root, normalized);
        bool hasChildren = dirs.Count > 0 || files.Count > 0;
        if (hasChildren && !force)
        {
            return DirectoryDeleteResult.NotEmpty;
        }

        if (hasChildren)
        {
            string prefix = normalized + "/";
            foreach (string fileKey in RootFiles(root).Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
            {
                RootFiles(root).TryRemove(fileKey, out _);
            }

            foreach (string dirKey in RootDirectories(root).Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
            {
                RootDirectories(root).TryRemove(dirKey, out _);
            }
        }

        RootDirectories(root).TryRemove(normalized, out _);
        return DirectoryDeleteResult.Deleted;
    }

    public static string NormalizePath(string path) => path.Trim('/', '\\').Replace('\\', '/');
}
