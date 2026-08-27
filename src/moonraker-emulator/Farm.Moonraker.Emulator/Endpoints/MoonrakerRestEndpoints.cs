using System.Linq;
using System.Text;
using Farm.Moonraker.Emulator.Domain;
using Farm.Moonraker.Emulator.Json;

namespace Farm.Moonraker.Emulator.Endpoints;

/// <summary>Maps every Moonraker REST route consumed by <c>Farm.Backend.Plugin.Moonraker</c>.</summary>
public static class MoonrakerRestEndpoints
{
    public static IEndpointRouteBuilder MapMoonrakerRest(this IEndpointRouteBuilder app)
    {
        app.MapGet("/", (HttpContext ctx) => Results.Ok(new
        {
            service = "printfarmer-moonraker-emulator",
            printer = Printer(ctx).Name,
            status = "ok",
        }));

        app.MapGet("/printer/info", GetPrinterInfoAsync);
        app.MapGet("/server/info", GetServerInfoAsync);
        app.MapGet("/machine/system_info", GetMachineSystemInfoAsync);

        app.MapGet("/printer/objects/list", GetObjectsListAsync);
        app.MapGet("/printer/objects/query", GetObjectsQueryAsync);

        app.MapPost("/printer/gcode/script", PostGcodeScriptAsync);
        app.MapPost("/printer/print/start", PostPrintStartAsync);
        app.MapPost("/printer/print/pause", (HttpContext ctx) => PostPrintControlAsync(ctx, p => p.Pause()));
        app.MapPost("/printer/print/resume", (HttpContext ctx) => PostPrintControlAsync(ctx, p => p.Resume()));
        app.MapPost("/printer/print/cancel", (HttpContext ctx) => PostPrintControlAsync(ctx, p => p.Cancel()));

        app.MapGet("/server/files/roots", GetFileRootsAsync);
        app.MapGet("/server/files/list", GetFileListAsync);
        app.MapGet("/server/files/directory", GetDirectoryAsync);
        app.MapPost("/server/files/directory", PostCreateDirectoryAsync);
        app.MapDelete("/server/files/directory", DeleteDirectoryAsync);
        app.MapPost("/server/files/move", PostMoveFileAsync);
        app.MapPost("/server/files/copy", PostCopyFileAsync);
        app.MapGet("/server/files/metadata", GetMetadataAsync);
        app.MapPost("/server/files/metascan", PostMetascanAsync);
        app.MapGet("/server/files/thumbnails", GetThumbnailsAsync);
        app.MapGet("/server/files/thumbs/{*file}", GetThumbnailBytesAsync);
        app.MapGet("/server/files/gcodes/{*path}", GetGcodeFileAsync);
        app.MapDelete("/server/files/gcodes/{*path}", DeleteGcodeFileAsync);
        app.MapGet("/server/files/config/{*path}", GetConfigFileAsync);
        app.MapPost("/server/files/upload", PostUploadAsync);
        app.MapGet("/server/files/camera/monitor.jpg", GetCameraMonitorSnapshotAsync);

        app.MapGet("/server/webcams/list", GetWebcamsListAsync);
        app.MapPost("/server/webcams/test", PostWebcamTestAsync);
        app.MapGet("/webcams/{name}/snapshot", GetWebcamSnapshotAsync);
        app.MapGet("/webcams/{name}/stream", GetWebcamStreamAsync);

        app.MapGet("/server/history/list", GetHistoryListAsync);
        app.MapGet("/server/history/job", GetHistoryJobAsync);
        app.MapDelete("/server/history/job", DeleteHistoryJobAsync);
        app.MapGet("/server/history/totals", GetHistoryTotalsAsync);
        app.MapPost("/server/history/reset_totals", PostHistoryResetTotalsAsync);

        app.MapGet("/server/spoolman/status", GetSpoolmanStatusAsync);
        app.MapGet("/server/spoolman/spool_id", GetSpoolmanSpoolIdAsync);
        app.MapPost("/server/spoolman/spool_id", PostSpoolmanSpoolIdAsync);
        app.MapPost("/server/spoolman/proxy", PostSpoolmanProxyAsync);

        return app;
    }

    internal static PrinterAggregate Printer(HttpContext ctx) => (PrinterAggregate)ctx.Items["printer"]!;

    private static double EventTime(PrinterAggregate p) => p.Clock.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

    // ---------------- printer / server info ----------------
    private static Task GetPrinterInfoAsync(HttpContext ctx)
    {
        PrinterAggregate p = Printer(ctx);
        p.Tick();
        return MoonrakerJson.WriteResultAsync(ctx, new Dictionary<string, object?>
        {
            ["state"] = p.KlippyState,
            ["state_message"] = p.KlippyStateMessage,
            ["hostname"] = p.Name,
            ["software_version"] = "v0.9.2-emulator",
            ["cpu_info"] = "PrintFarmer Moonraker Emulator",
            ["klipper_path"] = "/opt/klipper",
            ["python_path"] = "/opt/klipper/klippy-env/bin/python",
            ["log_file"] = "/opt/klipper/logs/klippy.log",
            ["config_file"] = "/etc/printer.cfg",
        });
    }

    private static Task GetServerInfoAsync(HttpContext ctx)
    {
        PrinterAggregate p = Printer(ctx);
        bool connected = p.KlippyState != "disconnected";
        return MoonrakerJson.WriteResultAsync(ctx, new Dictionary<string, object?>
        {
            ["klippy_connected"] = connected,
            ["klippy_state"] = p.KlippyState,
            ["components"] = new[]
            {
                "database", "file_manager", "machine", "data_store", "shell_command",
                "webcam", "history", "spoolman", "exclude_object",
            },
            ["failed_components"] = Array.Empty<string>(),
            ["registered_directories"] = new[] { "config", "gcodes", "logs" },
            ["warnings"] = Array.Empty<string>(),
            ["websocket_count"] = p.Connections.Count,
            ["moonraker_version"] = "v0.9.2-emulator",
            ["api_version"] = new[] { 1, 5, 0 },
            ["api_version_string"] = "1.5.0",
        });
    }

    private static Task GetMachineSystemInfoAsync(HttpContext ctx) =>
        MoonrakerJson.WriteResultAsync(ctx, new Dictionary<string, object?>
        {
            ["system_info"] = new Dictionary<string, object?>
            {
                ["cpu_info"] = new Dictionary<string, object?> { ["cpu_desc"] = "PrintFarmer Emulator vCPU", ["cpu_count"] = 1 },
                ["distribution"] = new Dictionary<string, object?> { ["name"] = "PrintFarmer Moonraker Emulator" },
                ["network"] = new Dictionary<string, object?>(),
            },
        });

    // ---------------- printer objects ----------------
    private static Task GetObjectsListAsync(HttpContext ctx)
    {
        Dictionary<string, object> snapshot = Printer(ctx).BuildObjectsSnapshot();
        return MoonrakerJson.WriteResultAsync(ctx, new Dictionary<string, object?> { ["objects"] = snapshot.Keys.ToArray() });
    }

    private static Task GetObjectsQueryAsync(HttpContext ctx)
    {
        PrinterAggregate p = Printer(ctx);
        p.Tick();
        if (p.KlippyState == "disconnected")
        {
            return MoonrakerJson.WriteWebRequestErrorAsync(ctx, StatusCodes.Status503ServiceUnavailable, "Klippy is not connected");
        }

        Dictionary<string, object> snapshot = p.BuildObjectsSnapshot();
        var status = new Dictionary<string, object?>(StringComparer.Ordinal);
        IQueryCollection query = ctx.Request.Query;
        IEnumerable<string> requested = query.Keys.Count > 0 ? query.Keys : snapshot.Keys;
        foreach (string name in requested)
        {
            if (!snapshot.TryGetValue(name, out object? value))
            {
                continue;
            }

            string fieldFilter = query[name].ToString();
            if (string.IsNullOrEmpty(fieldFilter) || value is not Dictionary<string, object?> fields)
            {
                status[name] = value;
                continue;
            }

            var filtered = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (string field in fieldFilter.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Where(fields.ContainsKey))
            {
                filtered[field] = fields[field];
            }

            status[name] = filtered;
        }

        return MoonrakerJson.WriteResultAsync(ctx, new Dictionary<string, object?>
        {
            ["status"] = status,
            ["eventtime"] = EventTime(p),
        });
    }

    // ---------------- gcode / print control ----------------
    private sealed record GcodeScriptRequest(string? Script);

    private static async Task PostGcodeScriptAsync(HttpContext ctx)
    {
        PrinterAggregate p = Printer(ctx);
        GcodeScriptRequest? body = await ctx.Request.ReadFromJsonAsync<GcodeScriptRequest>(MoonrakerJson.Options);
        string previousKlippyState = p.KlippyState;
        try
        {
            p.SendGcode(body?.Script ?? string.Empty);
            await MoonrakerJson.WriteResultAsync(ctx, "ok");

            // Some consumed commands (M112, FIRMWARE_RESTART/RESTART) move Klippy's
            // connection state directly rather than through the control API's scenario
            // switch — route through the same shared helper so the notify_klippy_*
            // broadcast behavior is identical either way.
            await BroadcastService.NotifyKlippyTransitionIfChangedAsync(p, previousKlippyState);
        }
        catch (PrinterBusyException ex)
        {
            await MoonrakerJson.WriteWebRequestErrorAsync(ctx, StatusCodes.Status400BadRequest, ex.Message);
        }
        catch (KlippyUnavailableException ex)
        {
            await MoonrakerJson.WriteWebRequestErrorAsync(ctx, StatusCodes.Status503ServiceUnavailable, ex.Message);
        }
        catch (GcodeParameterException ex)
        {
            await MoonrakerJson.WriteWebRequestErrorAsync(ctx, StatusCodes.Status400BadRequest, ex.Message);
        }
    }

    private sealed record PrintStartRequest(string? Filename);

    private static async Task PostPrintStartAsync(HttpContext ctx)
    {
        PrinterAggregate p = Printer(ctx);
        PrintStartRequest? body = await ctx.Request.ReadFromJsonAsync<PrintStartRequest>(MoonrakerJson.Options);
        try
        {
            p.StartPrint(body?.Filename ?? "unknown.gcode");
            await MoonrakerJson.WriteResultAsync(ctx, "ok");
            await BroadcastService.NotifyStatusUpdateAsync(p);
        }
        catch (PrinterBusyException ex)
        {
            await MoonrakerJson.WriteWebRequestErrorAsync(ctx, StatusCodes.Status409Conflict, ex.Message);
        }
        catch (KlippyUnavailableException ex)
        {
            await MoonrakerJson.WriteWebRequestErrorAsync(ctx, StatusCodes.Status503ServiceUnavailable, ex.Message);
        }
        catch (PrintFileNotFoundException ex)
        {
            await MoonrakerJson.WriteWebRequestErrorAsync(ctx, StatusCodes.Status404NotFound, ex.Message);
        }
    }

    private static async Task PostPrintControlAsync(HttpContext ctx, Action<PrinterAggregate> action)
    {
        PrinterAggregate p = Printer(ctx);
        try
        {
            action(p);
            await MoonrakerJson.WriteResultAsync(ctx, "ok");
            await BroadcastService.NotifyStatusUpdateAsync(p);
        }
        catch (PrinterBusyException ex)
        {
            await MoonrakerJson.WriteWebRequestErrorAsync(ctx, StatusCodes.Status400BadRequest, ex.Message);
        }
        catch (KlippyUnavailableException ex)
        {
            await MoonrakerJson.WriteWebRequestErrorAsync(ctx, StatusCodes.Status503ServiceUnavailable, ex.Message);
        }
    }

    // ---------------- files ----------------
    private static Task GetFileRootsAsync(HttpContext ctx) =>
        MoonrakerJson.WriteResultAsync(ctx, Printer(ctx).Files.Roots.Select(r => new Dictionary<string, object?>
        {
            ["name"] = r,
            ["path"] = $"/home/pi/printer_data/{r}",
            ["permissions"] = "rw",
        }).ToArray());

    private static Task GetFileListAsync(HttpContext ctx)
    {
        string root = ctx.Request.Query["root"].ToString() is { Length: > 0 } r ? r : "gcodes";
        return MoonrakerJson.WriteResultAsync(ctx, Printer(ctx).Files.List(root).Select(FileInfoDto).ToArray());
    }

    private static Dictionary<string, object?> FileInfoDto(VirtualFile f) => new()
    {
        ["path"] = f.Path,
        ["modified"] = (double)f.Modified.ToUnixTimeSeconds(),
        ["size"] = f.Content.LongLength,
        ["permissions"] = "rw",
    };

    private static Task GetDirectoryAsync(HttpContext ctx)
    {
        string rawPath = ctx.Request.Query["path"].ToString();
        (string root, string path) = SplitRootPath(rawPath);
        if (!Printer(ctx).Files.Roots.Contains(root, StringComparer.Ordinal))
        {
            return MoonrakerJson.WriteWebRequestErrorAsync(ctx, StatusCodes.Status404NotFound, $"Unknown root: {root}");
        }

        (IReadOnlyList<string> dirs, IReadOnlyList<VirtualFile> files) = Printer(ctx).Files.ListDirectory(root, path);
        return MoonrakerJson.WriteResultAsync(ctx, BuildDirectoryDto(rawPath, path, dirs, files));
    }

    private static Dictionary<string, object?> BuildDirectoryDto(
        string rawPath,
        string relativePath,
        IReadOnlyList<string> dirs,
        IReadOnlyList<VirtualFile> files) => new()
        {
            ["path"] = rawPath,
            ["dirname"] = relativePath,
            ["modified"] = (double)DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["size"] = 0,
            ["permissions"] = "rw",
            ["dirs"] = dirs.Select(d => new Dictionary<string, object?> { ["dirname"] = d, ["permissions"] = "rw" }).ToArray(),
            ["files"] = files.Select(f => new Dictionary<string, object?>
            {
                ["filename"] = f.Path,
                ["modified"] = (double)f.Modified.ToUnixTimeSeconds(),
                ["size"] = f.Content.LongLength,
                ["permissions"] = "rw",
            }).ToArray(),
            ["disk_usage"] = new Dictionary<string, object?> { ["total"] = 32_000_000_000L, ["used"] = 4_000_000_000L, ["free"] = 28_000_000_000L },
        };

    private sealed record DirectoryRequest(string? Path);

    private static async Task PostCreateDirectoryAsync(HttpContext ctx)
    {
        DirectoryRequest? body = await ctx.Request.ReadFromJsonAsync<DirectoryRequest>(MoonrakerJson.Options);
        if (string.IsNullOrWhiteSpace(body?.Path))
        {
            await MoonrakerJson.WriteWebRequestErrorAsync(ctx, StatusCodes.Status400BadRequest, "Invalid directory path");
            return;
        }

        (string root, string path) = SplitRootPath(body.Path);
        DirectoryCreateResult result = Printer(ctx).Files.CreateDirectory(root, path);
        switch (result)
        {
            case DirectoryCreateResult.AlreadyExists:
                await MoonrakerJson.WriteWebRequestErrorAsync(ctx, StatusCodes.Status400BadRequest, $"File or directory already exists: {body.Path}");
                return;
            case DirectoryCreateResult.ParentMissing:
                await MoonrakerJson.WriteWebRequestErrorAsync(ctx, StatusCodes.Status404NotFound, $"Parent directory does not exist: {body.Path}");
                return;
        }

        await MoonrakerJson.WriteResultAsync(ctx, new Dictionary<string, object?>
        {
            ["item"] = DirectoryItemDto(path, root, exists: true),
            ["action"] = "create_dir",
        });
    }

    private static Task DeleteDirectoryAsync(HttpContext ctx)
    {
        string rawPath = ctx.Request.Query["path"].ToString();
        bool force = bool.TryParse(ctx.Request.Query["force"].ToString(), out bool f) && f;
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            return MoonrakerJson.WriteWebRequestErrorAsync(ctx, StatusCodes.Status400BadRequest, "Invalid directory path");
        }

        (string root, string path) = SplitRootPath(rawPath);
        DirectoryDeleteResult result = Printer(ctx).Files.DeleteDirectory(root, path, force);
        switch (result)
        {
            case DirectoryDeleteResult.RootProtected:
                return MoonrakerJson.WriteWebRequestErrorAsync(ctx, StatusCodes.Status400BadRequest, "Cannot delete a root directory");
            case DirectoryDeleteResult.NotFound:
                return MoonrakerJson.WriteWebRequestErrorAsync(ctx, StatusCodes.Status404NotFound, $"Directory does not exist: {rawPath}");
            case DirectoryDeleteResult.NotEmpty:
                return MoonrakerJson.WriteWebRequestErrorAsync(ctx, StatusCodes.Status400BadRequest, $"Directory not empty: {rawPath}");
        }

        return MoonrakerJson.WriteResultAsync(ctx, new Dictionary<string, object?>
        {
            ["item"] = DirectoryItemDto(path, root, exists: false),
            ["action"] = "delete_dir",
        });
    }

    /// <summary>Builds the "item" object Moonraker returns for create/delete directory responses.</summary>
    private static Dictionary<string, object?> DirectoryItemDto(string path, string root, bool exists) => new()
    {
        ["path"] = path,
        ["root"] = root,
        ["modified"] = exists ? (double)DateTimeOffset.UtcNow.ToUnixTimeSeconds() : 0d,
        ["size"] = exists ? 4096 : 0,
        ["permissions"] = exists ? "rw" : string.Empty,
    };

    private sealed record MoveCopyRequest(string? Source, string? Dest);

    private static async Task PostMoveFileAsync(HttpContext ctx)
    {
        MoveCopyRequest? body = await ctx.Request.ReadFromJsonAsync<MoveCopyRequest>(MoonrakerJson.Options);
        (string root, string source) = SplitRootPath(body?.Source);
        (_, string dest) = SplitRootPath(body?.Dest);
        bool moved = Printer(ctx).Files.Move(root, source, dest);
        if (!moved)
        {
            await MoonrakerJson.WriteWebRequestErrorAsync(ctx, StatusCodes.Status404NotFound, "File not found");
            return;
        }

        await MoonrakerJson.WriteResultAsync(ctx, new Dictionary<string, object?>
        {
            ["item"] = new Dictionary<string, object?> { ["path"] = dest, ["root"] = root },
            ["source_item"] = new Dictionary<string, object?> { ["path"] = source, ["root"] = root },
            ["action"] = "move_file",
        });
    }

    private static async Task PostCopyFileAsync(HttpContext ctx)
    {
        MoveCopyRequest? body = await ctx.Request.ReadFromJsonAsync<MoveCopyRequest>(MoonrakerJson.Options);
        (string root, string source) = SplitRootPath(body?.Source);
        (_, string dest) = SplitRootPath(body?.Dest);
        bool copied = Printer(ctx).Files.Copy(root, source, dest);
        if (!copied)
        {
            await MoonrakerJson.WriteWebRequestErrorAsync(ctx, StatusCodes.Status404NotFound, "File not found");
            return;
        }

        await MoonrakerJson.WriteResultAsync(ctx, new Dictionary<string, object?>
        {
            ["item"] = new Dictionary<string, object?> { ["path"] = dest, ["root"] = root },
            ["action"] = "create_file",
        });
    }

    private static (string Root, string Path) SplitRootPath(string? value)
    {
        string normalized = VirtualFileSystem.NormalizePath(value ?? string.Empty);
        if (normalized is "gcodes" or "config" or "logs")
        {
            return (normalized, string.Empty);
        }

        int slash = normalized.IndexOf('/');
        return slash < 0 ? ("gcodes", normalized) : (normalized[..slash], normalized[(slash + 1)..]);
    }

    private static Task GetMetadataAsync(HttpContext ctx)
    {
        string filename = ctx.Request.Query["filename"].ToString();
        if (!Printer(ctx).Files.TryGet("gcodes", filename, out VirtualFile? file) || file is null)
        {
            return MoonrakerJson.WriteWebRequestErrorAsync(ctx, StatusCodes.Status404NotFound, $"Metadata not available for {filename}");
        }

        return MoonrakerJson.WriteResultAsync(ctx, MetadataDto(file));
    }

    private static Dictionary<string, object?> MetadataDto(VirtualFile file)
    {
        VirtualGcodeMetadata m = file.Metadata;
        return new Dictionary<string, object?>
        {
            ["filename"] = file.Path,
            ["size"] = file.Content.LongLength,
            ["modified"] = (double)file.Modified.ToUnixTimeSeconds(),
            ["slicer"] = m.Slicer,
            ["slicer_version"] = m.SlicerVersion,
            ["layer_height"] = m.LayerHeight,
            ["first_layer_height"] = m.FirstLayerHeight,
            ["object_height"] = m.ObjectHeight,
            ["filament_total"] = m.FilamentTotal,
            ["filament_weight_total"] = m.FilamentWeightTotal,
            ["estimated_time"] = m.EstimatedTime,
            ["first_layer_bed_temp"] = m.FirstLayerBedTemp,
            ["first_layer_extr_temp"] = m.FirstLayerExtrTemp,
            ["gcode_start_byte"] = m.GcodeStartByte,
            ["gcode_end_byte"] = m.GcodeEndByte,
            ["thumbnails"] = m.Thumbnails.Select(t => new Dictionary<string, object?>
            {
                ["width"] = t.Width,
                ["height"] = t.Height,
                ["size"] = t.Size,
                ["relative_path"] = t.RelativePath,
            }).ToArray(),
            ["object_info"] = m.Objects.Select(o => new Dictionary<string, object?>
            {
                ["name"] = o.Name,
                ["center"] = o.Center,
            }).ToArray(),
        };
    }

    private sealed record MetascanRequest(string? Filename);

    private static async Task PostMetascanAsync(HttpContext ctx)
    {
        MetascanRequest? body = await ctx.Request.ReadFromJsonAsync<MetascanRequest>(MoonrakerJson.Options);
        string filename = body?.Filename ?? string.Empty;
        if (!Printer(ctx).Files.TryGet("gcodes", filename, out VirtualFile? file) || file is null)
        {
            await MoonrakerJson.WriteWebRequestErrorAsync(ctx, StatusCodes.Status404NotFound, $"{filename} does not exist");
            return;
        }

        await MoonrakerJson.WriteResultAsync(ctx, MetadataDto(file));
    }

    private static Task GetThumbnailsAsync(HttpContext ctx)
    {
        string filename = ctx.Request.Query["filename"].ToString();
        if (!Printer(ctx).Files.TryGet("gcodes", filename, out VirtualFile? file) || file is null)
        {
            return MoonrakerJson.WriteResultAsync(ctx, Array.Empty<object>());
        }

        return MoonrakerJson.WriteResultAsync(ctx, file.Metadata.Thumbnails.Select(t => new Dictionary<string, object?>
        {
            ["width"] = t.Width,
            ["height"] = t.Height,
            ["size"] = t.Size,
            ["thumbnail_path"] = t.RelativePath,
            ["relative_path"] = t.RelativePath,
        }).ToArray());
    }

    /// <summary>
    /// Deterministic 1x1 PNG fixture, used only for thumbnail routes (<c>server/files/thumbs/{file}</c>).
    /// Real Moonraker slicer thumbnails are PNG, so this keeps that route's content type/bytes aligned.
    /// </summary>
    private static readonly byte[] OnePixelPng =
    [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
        0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x08, 0x02, 0x00, 0x00, 0x00, 0x90, 0x77, 0x53,
        0xDE, 0x00, 0x00, 0x00, 0x0C, 0x49, 0x44, 0x41, 0x54, 0x08, 0xD7, 0x63, 0xF8, 0xCF, 0xC0, 0x00,
        0x00, 0x03, 0x01, 0x01, 0x00, 0x18, 0xDD, 0x8D, 0xB0, 0x00, 0x00, 0x00, 0x00, 0x49, 0x45, 0x4E,
        0x44, 0xAE, 0x42, 0x60, 0x82,
    ];

    /// <summary>
    /// Deterministic 1x1 <b>baseline Huffman-coded</b> JPEG fixture (SOF0), used for every route that
    /// declares <c>image/jpeg</c> or <c>multipart/x-mixed-replace</c> (camera snapshot/monitor and MJPEG
    /// stream routes). Earlier revisions reused <see cref="OnePixelPng"/> under a JPEG content type,
    /// which produced bytes that do not start with the JPEG SOI marker (<c>FF D8 FF</c>) and cannot be
    /// decoded as JPEG by browsers/img elements. This fixture is a real, GDI+-encoded, decoder-verified
    /// baseline JPEG (not the well-known 107-byte "smallest possible JPEG", which uses arithmetic coding
    /// and is rejected by virtually every real-world decoder including browsers) so consumers reliably
    /// render it.
    /// </summary>
    private static readonly byte[] OnePixelJpeg = Convert.FromBase64String(
        "/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAAgGBgcGBQgHBwcJCQgKDBQNDAsLDBkSEw8UHRofHh0aHBwgJC4nICIsIxwcKDcpLDAxNDQ0Hyc5PTgyPC4zNDL/2wBDAQkJCQwLDBgNDRgyIRwhMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjL/wAARCAABAAEDASIAAhEBAxEB/8QAHwAAAQUBAQEBAQEAAAAAAAAAAAECAwQFBgcICQoL/8QAtRAAAgEDAwIEAwUFBAQAAAF9AQIDAAQRBRIhMUEGE1FhByJxFDKBkaEII0KxwRVS0fAkM2JyggkKFhcYGRolJicoKSo0NTY3ODk6Q0RFRkdISUpTVFVWV1hZWmNkZWZnaGlqc3R1dnd4eXqDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uHi4+Tl5ufo6erx8vP09fb3+Pn6/8QAHwEAAwEBAQEBAQEBAQAAAAAAAAECAwQFBgcICQoL/8QAtREAAgECBAQDBAcFBAQAAQJ3AAECAxEEBSExBhJBUQdhcRMiMoEIFEKRobHBCSMzUvAVYnLRChYkNOEl8RcYGRomJygpKjU2Nzg5OkNERUZHSElKU1RVVldYWVpjZGVmZ2hpanN0dXZ3eHl6goOEhYaHiImKkpOUlZaXmJmaoqOkpaanqKmqsrO0tba3uLm6wsPExcbHyMnK0tPU1dbX2Nna4uPk5ebn6Onq8vP09fb3+Pn6/9oADAMBAAIRAxEAPwAooooA/9k=");

    private static Task GetThumbnailBytesAsync(HttpContext ctx, string file)
    {
        ctx.Response.ContentType = "image/png";
        ctx.Response.Headers["X-Emulator-Thumbnail-File"] = file;
        return ctx.Response.Body.WriteAsync(OnePixelPng).AsTask();
    }

    private static async Task GetGcodeFileAsync(HttpContext ctx, string path)
    {
        // MoonrakerClient.GetJobAsync builds a thumbnail URL by Uri.EscapeDataString-ing a
        // relative_path like "thumbs/benchy-32x32.png", which percent-encodes the "/" itself
        // (-> "thumbs%2Fbenchy-32x32.png"). ASP.NET Core's routing preserves an encoded slash
        // literally rather than decoding it into an extra path segment (a deliberate framework
        // safeguard against path-traversal ambiguity), so the {*path} catch-all parameter arrives
        // as that single still-encoded segment. Un-escape it before any lookup so this resolves
        // the same way a real multi-segment path would.
        string decodedPath = Uri.UnescapeDataString(path);
        if (Printer(ctx).Files.TryGet("gcodes", decodedPath, out VirtualFile? file) && file is not null)
        {
            ctx.Response.ContentType = "application/octet-stream";
            ctx.Response.ContentLength = file.Content.LongLength;
            await ctx.Response.Body.WriteAsync(file.Content);
            return;
        }

        // Real Moonraker's gcode-root download route is not thumbnail-specific: slicers write
        // thumbnail images physically to disk under the gcodes root (commonly a "thumbs/"
        // subdirectory), so this same route serves them too. MoonrakerClient.GetJobAsync relies
        // on exactly this when it builds a print job's thumbnail URL as
        // {baseUrl}/server/files/gcodes/{relative_path} — serve deterministic PNG bytes for any
        // seeded thumbnail path instead of 404ing.
        if (Printer(ctx).Files.IsKnownThumbnailPath("gcodes", decodedPath))
        {
            ctx.Response.ContentType = "image/png";
            await ctx.Response.Body.WriteAsync(OnePixelPng);
            return;
        }

        ctx.Response.StatusCode = StatusCodes.Status404NotFound;
    }

    private static Task DeleteGcodeFileAsync(HttpContext ctx, string path)
    {
        bool deleted = Printer(ctx).Files.Delete("gcodes", path);
        return deleted
            ? MoonrakerJson.WriteResultAsync(ctx, path)
            : MoonrakerJson.WriteWebRequestErrorAsync(ctx, StatusCodes.Status404NotFound, $"{path} does not exist");
    }

    /// <summary>
    /// Generic "config" root file download — real Moonraker serves every root's files at
    /// <c>server/files/{root}/{path}</c>, not just gcodes. Currently only needed for
    /// <c>MoonrakerSubscriptionService</c>'s Qidibox dictionary fetch (a raw
    /// <see cref="System.Net.Http.HttpClient"/> GET of <c>officiall_filas_list.cfg</c>, not
    /// through <c>MoonrakerClient</c>), so this stays narrowly scoped to that root rather than
    /// generalizing to every root up front.
    /// </summary>
    private static async Task GetConfigFileAsync(HttpContext ctx, string path)
    {
        string decodedPath = Uri.UnescapeDataString(path);
        if (!Printer(ctx).Files.TryGet("config", decodedPath, out VirtualFile? file) || file is null)
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        ctx.Response.ContentType = "text/plain; charset=utf-8";
        ctx.Response.ContentLength = file.Content.LongLength;
        await ctx.Response.Body.WriteAsync(file.Content);
    }

    private static async Task PostUploadAsync(HttpContext ctx)
    {
        if (!ctx.Request.HasFormContentType)
        {
            await MoonrakerJson.WriteWebRequestErrorAsync(ctx, StatusCodes.Status400BadRequest, "Multipart form data required");
            return;
        }

        IFormCollection form = await ctx.Request.ReadFormAsync();
        IFormFile? uploaded = form.Files["file"];
        if (uploaded is null)
        {
            await MoonrakerJson.WriteWebRequestErrorAsync(ctx, StatusCodes.Status400BadRequest, "No file part named 'file' found");
            return;
        }

        string root = form["root"].FirstOrDefault() is { Length: > 0 } r ? r : "gcodes";
        string path = form["path"].FirstOrDefault() is { Length: > 0 } p ? p : uploaded.FileName;
        using var buffer = new MemoryStream();
        await uploaded.CopyToAsync(buffer);
        VirtualFile stored = Printer(ctx).Files.Put(root, path, buffer.ToArray());

        bool print = string.Equals(form["print"].FirstOrDefault(), "true", StringComparison.OrdinalIgnoreCase);
        bool printStarted = false;
        if (print && root == "gcodes")
        {
            try
            {
                Printer(ctx).StartPrint(stored.Path);
                printStarted = true;
            }
            catch (Exception ex) when (ex is PrinterBusyException or KlippyUnavailableException or PrintFileNotFoundException)
            {
                // Upload still succeeded even if the auto-print could not start; Moonraker behaves the same way.
            }
        }

        await MoonrakerJson.WriteResultAsync(ctx, new Dictionary<string, object?>
        {
            ["item"] = new Dictionary<string, object?>
            {
                ["path"] = $"{root}/{stored.Path}",
                ["root"] = root,
                ["size"] = stored.Content.LongLength,
                ["modified"] = (double)stored.Modified.ToUnixTimeSeconds(),
            },
            ["action"] = "create_file",
        });

        if (printStarted)
        {
            await BroadcastService.NotifyStatusUpdateAsync(Printer(ctx));
        }
    }

    private static Task GetCameraMonitorSnapshotAsync(HttpContext ctx)
    {
        ctx.Response.ContentType = "image/jpeg";
        return ctx.Response.Body.WriteAsync(OnePixelJpeg).AsTask();
    }

    // ---------------- webcams ----------------
    private static Task GetWebcamsListAsync(HttpContext ctx)
    {
        PrinterAggregate p = Printer(ctx);
        string basePath = ctx.Request.PathBase.HasValue ? ctx.Request.PathBase.Value! : string.Empty;
        return MoonrakerJson.WriteResultAsync(ctx, new Dictionary<string, object?>
        {
            ["webcams"] = p.Webcams.Select(w => new Dictionary<string, object?>
            {
                ["name"] = w.Name,
                ["uid"] = w.Uid,
                ["enabled"] = w.Enabled,
                ["service"] = w.Service,
                ["location"] = w.Location,
                ["icon"] = w.Icon,
                ["target_fps"] = w.TargetFps,
                ["target_fps_idle"] = w.TargetFpsIdle,
                ["stream_url"] = $"{basePath}/webcams/{Uri.EscapeDataString(w.Name)}/{w.StreamPath}",
                ["snapshot_url"] = $"{basePath}/webcams/{Uri.EscapeDataString(w.Name)}/{w.SnapshotPath}",
                ["source"] = "database",
                ["flip_horizontal"] = false,
                ["flip_vertical"] = false,
                ["rotation"] = 0,
                ["aspect_ratio"] = "4:3",
            }).ToArray(),
        });
    }

    private static Task PostWebcamTestAsync(HttpContext ctx)
    {
        string? uid = ctx.Request.Query["uid"].ToString() is { Length: > 0 } u ? u : null;
        string? name = ctx.Request.Query["name"].ToString() is { Length: > 0 } n ? n : null;
        WebcamFixture? webcam = Printer(ctx).Webcams.FirstOrDefault(w =>
            (uid is not null && string.Equals(w.Uid, uid, StringComparison.Ordinal)) ||
            (name is not null && string.Equals(w.Name, name, StringComparison.OrdinalIgnoreCase)));
        if (webcam is null)
        {
            return MoonrakerJson.WriteWebRequestErrorAsync(ctx, StatusCodes.Status404NotFound, "Webcam not found");
        }

        return MoonrakerJson.WriteResultAsync(ctx, new Dictionary<string, object?>
        {
            ["stream_url"] = $"/webcams/{Uri.EscapeDataString(webcam.Name)}/{webcam.StreamPath}",
            ["snapshot_url"] = $"/webcams/{Uri.EscapeDataString(webcam.Name)}/{webcam.SnapshotPath}",
        });
    }

    private static Task GetWebcamSnapshotAsync(HttpContext ctx, string name)
    {
        ctx.Response.ContentType = "image/jpeg";
        ctx.Response.Headers["X-Emulator-Webcam-Name"] = name;
        return ctx.Response.Body.WriteAsync(OnePixelJpeg).AsTask();
    }

    private static async Task GetWebcamStreamAsync(HttpContext ctx, string name)
    {
        // Deterministic stand-in for an MJPEG stream: a single boundary part, then close.
        ctx.Response.ContentType = "multipart/x-mixed-replace; boundary=frame";
        ctx.Response.Headers["X-Emulator-Webcam-Name"] = name;
        byte[] header = Encoding.ASCII.GetBytes("--frame\r\nContent-Type: image/jpeg\r\n\r\n");
        await ctx.Response.Body.WriteAsync(header);
        await ctx.Response.Body.WriteAsync(OnePixelJpeg);
    }

    // ---------------- history ----------------
    private static Task GetHistoryListAsync(HttpContext ctx)
    {
        PrinterAggregate p = Printer(ctx);
        IReadOnlyList<HistoryJobEntry> all = p.History.Snapshot();

        int limit = int.TryParse(ctx.Request.Query["limit"], out int l) ? l : 50;
        int start = int.TryParse(ctx.Request.Query["start"], out int s) ? s : 0;
        string order = ctx.Request.Query["order"].ToString() is { Length: > 0 } o ? o : "desc";
        double? since = double.TryParse(ctx.Request.Query["since"], out double sv) ? sv : null;
        double? before = double.TryParse(ctx.Request.Query["before"], out double bv) ? bv : null;

        IEnumerable<HistoryJobEntry> filtered = all;
        if (since.HasValue)
        {
            filtered = filtered.Where(j => j.StartTime >= since.Value);
        }

        if (before.HasValue)
        {
            filtered = filtered.Where(j => j.StartTime <= before.Value);
        }

        List<HistoryJobEntry> ordered = string.Equals(order, "desc", StringComparison.OrdinalIgnoreCase)
            ? filtered.OrderByDescending(j => j.StartTime).ToList()
            : filtered.OrderBy(j => j.StartTime).ToList();

        List<HistoryJobEntry> page = ordered.Skip(start).Take(Math.Max(limit, 0)).ToList();

        return MoonrakerJson.WriteResultAsync(ctx, new Dictionary<string, object?>
        {
            ["count"] = ordered.Count,
            ["jobs"] = page.Select(HistoryJobDto).ToArray(),
        });
    }

    private static Dictionary<string, object?> HistoryJobDto(HistoryJobEntry j) => new()
    {
        ["job_id"] = j.JobId,
        ["exists"] = j.Exists,
        ["end_time"] = j.EndTime,
        ["filament_used"] = j.FilamentUsed,
        ["filename"] = j.Filename,
        ["metadata"] = new Dictionary<string, object?>(),
        ["print_duration"] = j.PrintDuration,
        ["status"] = j.Status,
        ["start_time"] = j.StartTime,
        ["total_duration"] = j.TotalDuration,
        ["user"] = "emulator",
        ["thumbnail_url"] = j.ThumbnailUrl,
    };

    private static Task GetHistoryJobAsync(HttpContext ctx)
    {
        string uid = ctx.Request.Query["uid"].ToString();
        HistoryJobEntry? job = Printer(ctx).History.Find(uid);
        return job is null
            ? MoonrakerJson.WriteWebRequestErrorAsync(ctx, StatusCodes.Status404NotFound, $"Invalid job uid: {uid}")
            : MoonrakerJson.WriteResultAsync(ctx, HistoryJobDto(job));
    }

    private static Task DeleteHistoryJobAsync(HttpContext ctx)
    {
        string uid = ctx.Request.Query["uid"].ToString();
        bool removed = Printer(ctx).History.Remove(uid);
        return removed
            ? MoonrakerJson.WriteResultAsync(ctx, new[] { uid })
            : MoonrakerJson.WriteWebRequestErrorAsync(ctx, StatusCodes.Status404NotFound, $"Invalid job uid: {uid}");
    }

    private static Task GetHistoryTotalsAsync(HttpContext ctx)
    {
        HistoryStore h = Printer(ctx).History;
        return MoonrakerJson.WriteResultAsync(ctx, new Dictionary<string, object?>
        {
            ["job_totals"] = new Dictionary<string, object?>
            {
                ["total_jobs"] = h.TotalJobs,
                ["total_time"] = h.TotalTime,
                ["total_print_time"] = h.TotalPrintTime,
                ["total_filament_used"] = h.TotalFilamentUsed,
                ["longest_job"] = h.LongestJob,
                ["longest_print"] = h.LongestPrint,
            },
        });
    }

    private static Task PostHistoryResetTotalsAsync(HttpContext ctx)
    {
        PrinterAggregate p = Printer(ctx);
        var previous = new Dictionary<string, object?>
        {
            ["total_jobs"] = p.History.TotalJobs,
            ["total_time"] = p.History.TotalTime,
            ["total_print_time"] = p.History.TotalPrintTime,
            ["total_filament_used"] = p.History.TotalFilamentUsed,
            ["longest_job"] = p.History.LongestJob,
            ["longest_print"] = p.History.LongestPrint,
        };
        p.History.ResetTotals();
        return MoonrakerJson.WriteResultAsync(ctx, new Dictionary<string, object?> { ["last_totals"] = previous });
    }

    // ---------------- spoolman ----------------
    private static Task GetSpoolmanStatusAsync(HttpContext ctx)
    {
        SpoolmanFixture s = Printer(ctx).Spoolman;
        return MoonrakerJson.WriteResultAsync(ctx, new Dictionary<string, object?>
        {
            ["spoolman_connected"] = s.Connected,
            ["pending_reports"] = Array.Empty<object>(),
            ["spool_id"] = s.ActiveSpoolId,
        });
    }

    private static Task GetSpoolmanSpoolIdAsync(HttpContext ctx) =>
        MoonrakerJson.WriteResultAsync(ctx, new Dictionary<string, object?> { ["spool_id"] = Printer(ctx).Spoolman.ActiveSpoolId });

    private sealed record SpoolIdRequest(int? SpoolId);

    private static async Task PostSpoolmanSpoolIdAsync(HttpContext ctx)
    {
        SpoolIdRequest? body = await ctx.Request.ReadFromJsonAsync<SpoolIdRequest>(MoonrakerJson.Options);
        Printer(ctx).Spoolman.ActiveSpoolId = body?.SpoolId;
        await MoonrakerJson.WriteResultAsync(ctx, "ok");
    }

    private sealed record SpoolmanProxyRequest(string? RequestMethod, string? Path, string? Query, JsonElement? Body, bool UseV2Response);

    private static async Task PostSpoolmanProxyAsync(HttpContext ctx)
    {
        SpoolmanProxyRequest? body = await ctx.Request.ReadFromJsonAsync<SpoolmanProxyRequest>(MoonrakerJson.Options);
        SpoolmanFixture spoolman = Printer(ctx).Spoolman;
        string path = (body?.Path ?? string.Empty).TrimEnd('/');

        if (!spoolman.Connected)
        {
            await MoonrakerJson.WriteWebRequestErrorAsync(ctx, StatusCodes.Status502BadGateway, "Spoolman server is not connected");
            return;
        }

        if (string.Equals(path, "/v1/spool", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(body?.RequestMethod, "GET", StringComparison.OrdinalIgnoreCase))
        {
            await MoonrakerJson.WriteResultAsync(ctx, spoolman.Spools().Select(SpoolDto).ToArray());
            return;
        }

        if (path.StartsWith("/v1/spool/", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(path["/v1/spool/".Length..], out int spoolId) &&
            string.Equals(body?.RequestMethod, "GET", StringComparison.OrdinalIgnoreCase))
        {
            SpoolmanSpool? spool = spoolman.Find(spoolId);
            if (spool is null)
            {
                await MoonrakerJson.WriteWebRequestErrorAsync(ctx, StatusCodes.Status404NotFound, "Spool not found");
                return;
            }

            await MoonrakerJson.WriteResultAsync(ctx, SpoolDto(spool));
            return;
        }

        // Unknown/unmodeled Spoolman path: the emulator only models the subset of the Spoolman
        // proxy surface that Farm.Backend.Plugin.Moonraker actually consumes (list spools, get
        // spool by id). Any other path/method must fail loudly with a Moonraker-shaped error
        // rather than fabricating a success response — silently returning HTTP 200 with an
        // empty object would hide the fact that this path is unimplemented.
        await MoonrakerJson.WriteWebRequestErrorAsync(
            ctx,
            StatusCodes.Status404NotFound,
            $"Unsupported Spoolman proxy request: {body?.RequestMethod ?? "GET"} {body?.Path ?? string.Empty}");
    }

    private static Dictionary<string, object?> SpoolDto(SpoolmanSpool s) => new()
    {
        ["id"] = s.Id,
        ["filament"] = new Dictionary<string, object?>
        {
            ["name"] = s.FilamentName,
            ["material"] = s.Material,
            ["color_hex"] = s.Color.TrimStart('#'),
        },
        ["remaining_weight"] = s.RemainingWeight,
        ["used_weight"] = s.UsedWeight,
    };
}
