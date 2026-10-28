using System.Text;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Gcode;
using Microsoft.Extensions.Logging.Abstractions;

namespace Farm.Infrastructure.Services.Printers;

/// <summary>Native facts for one file, obtained by a backend from a single native details call.</summary>
/// <param name="Metadata">Normalized native metadata; a malformed field must already be null.</param>
/// <param name="SourceSizeBytes">Current size reported by the printer, preferred over a stale listing size.</param>
/// <param name="ReadThumbnailAsync">Lazy native image fetch; a null result means the printer has no image.</param>
public sealed record PrinterFileNativeEnrichment(
    PrinterPrintMetadata? Metadata,
    long? SourceSizeBytes = null,
    Func<PrinterFileEnrichmentBudget, Task<byte[]?>>? ReadThumbnailAsync = null);

/// <summary>Backend-specific bounded readers composed by <see cref="PrinterFileEnrichmentAcquisition"/>.</summary>
/// <param name="ReadNativeAsync">Fetches native details exactly once; null when the backend has no native route.</param>
/// <param name="ReadRegionAsync">
/// Reads (offset, maximum bytes, expected size) from the exact source file, typically through
/// <see cref="PrinterFileEnrichmentTransport.ReadRegionAsync"/>; null when no bounded source route exists.
/// </param>
/// <param name="VerifyUnchangedAsync">Optional last check that the source still matches the observation.</param>
public sealed record PrinterFileEnrichmentSources(
    Func<PrinterFileEnrichmentBudget, Task<PrinterFileNativeEnrichment?>>? ReadNativeAsync,
    Func<long, int, long?, PrinterFileEnrichmentBudget, Task<PrinterFileRegion>>? ReadRegionAsync,
    Func<PrinterFileEnrichmentBudget, Task>? VerifyUnchangedAsync = null);

/// <summary>
/// Composes native details, a bounded source-region reader and the shared G-code parsers into one background
/// enrichment. Metadata and image outcomes are independent, every wire read is charged to one
/// <see cref="PrinterFileEnrichmentBudget"/>, and no path imports or streams a whole file.
/// Reasons are stable snake_case codes, for example request_limit, response_limit, deadline, capture_paused,
/// range_unsupported, region_limit, metadata_limit, decoded_limit, invalid_source_size, invalid_image,
/// unsupported_image_format, size_limit, image_limit, thumbnail_missing, not_in_bounded_source, source_changed,
/// plus the transport reasons such as authentication_required, source_missing and transfer_failed.
/// </summary>
public static class PrinterFileEnrichmentAcquisition
{
    private static readonly GcodeMetadataExtractorService DefaultExtractor =
        new(NullLogger<GcodeMetadataExtractorService>.Instance);

    /// <summary>
    /// Acquires native and bounded-source facts for one observed file under a fresh per-file budget.
    /// Native values always win; the bounded source only fills fields the native source left unknown.
    /// </summary>
    /// <exception cref="OperationCanceledException">
    /// The caller cancelled. The per-file deadline is reported as a reason, not thrown.
    /// </exception>
    public static async Task<PrinterFileEnrichmentContent> AcquireAsync(
        PrinterFileInventoryEntry observation,
        PrinterFileEnrichmentSources sources,
        bool captureThumbnail,
        CancellationToken ct,
        IGcodeMetadataExtractorService? extractor = null,
        PrinterBackend backend = PrinterBackend.Unknown,
        PrinterFileEnrichmentMetrics? metrics = null)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(sources);
        ct.ThrowIfCancellationRequested();
        metrics ??= PrinterFileEnrichmentMetrics.Shared;
        metrics.RecordEnrichment(backend);
        if (sources.ReadNativeAsync is null && sources.ReadRegionAsync is null)
        {
            return new(
                Metadata: null,
                MetadataState: PrinterFileEnrichmentState.Unsupported,
                ThumbnailState: PrinterFileEnrichmentState.Unsupported,
                MetadataReason: "unsupported",
                ThumbnailReason: "unsupported");
        }

        using PrinterFileEnrichmentBudget budget = new(ct);
        Run run = new(observation, sources, captureThumbnail, budget, extractor ?? DefaultExtractor, backend, metrics, ct);
        return await run.ExecuteAsync().ConfigureAwait(false);
    }

    private static PrinterFileEnrichmentState StateFor(string reason, bool hasValue) => reason switch
    {
        "source_missing" => PrinterFileEnrichmentState.Absent,
        "request_limit" or "response_limit" or "deadline" =>
            hasValue ? PrinterFileEnrichmentState.Partial : PrinterFileEnrichmentState.Deferred,
        "range_unsupported" or "region_limit" or "header_too_large" or "too_many_blocks" or "metadata_limit" or
            "decoded_limit" or "invalid_source_size" => PrinterFileEnrichmentState.Partial,
        _ when hasValue => PrinterFileEnrichmentState.Partial,
        _ when reason == "invalid_source_path" || reason.StartsWith("unsupported_", StringComparison.Ordinal) =>
            PrinterFileEnrichmentState.Unsupported,
        _ => PrinterFileEnrichmentState.Failed,
    };

    private sealed record ThumbnailOutcome(
        PrinterFileEnrichmentState State, string? Reason, byte[]? Image = null, string? ContentType = null);

    private sealed record Fallback(
        PrinterPrintMetadata? Metadata,
        bool Conclusive,
        string? Reason,
        string Source,
        IReadOnlyList<byte[]> Images,
        PrinterFileEnrichmentState ImageState,
        string? ImageReason)
    {
        public static Fallback Failure(string reason) =>
            new(null, false, reason, "bounded_gcode", [], StateFor(reason, hasValue: false), reason);
    }

    private sealed class Run
    {
        private readonly PrinterFileInventoryEntry observation;
        private readonly PrinterFileEnrichmentSources sources;
        private readonly bool captureThumbnail;
        private readonly PrinterFileEnrichmentBudget budget;
        private readonly IGcodeMetadataExtractorService extractor;
        private readonly CancellationToken ct;
        private readonly PrinterBackend backend;
        private readonly PrinterFileEnrichmentMetrics metrics;
        private PrinterPrintMetadata? native;
        private Func<PrinterFileEnrichmentBudget, Task<byte[]?>>? nativeImage;
        private long? size;
        private Fallback? fallback;
        private bool readSource;

        public Run(
            PrinterFileInventoryEntry observation, PrinterFileEnrichmentSources sources, bool captureThumbnail,
            PrinterFileEnrichmentBudget budget, IGcodeMetadataExtractorService extractor,
            PrinterBackend backend, PrinterFileEnrichmentMetrics metrics, CancellationToken ct)
        {
            this.observation = observation;
            this.sources = sources;
            this.captureThumbnail = captureThumbnail;
            this.budget = budget;
            this.extractor = extractor;
            this.ct = ct;
            this.backend = backend;
            this.metrics = metrics;
            size = observation.SizeBytes is >= 0 ? observation.SizeBytes : null;
        }

        private static bool IsRecoverableNativeFailure(string reason) =>
            reason is "invalid_preview_metadata" or "size_limit" or "encoded_response" or "incomplete_response";

        private static int Priority(PrinterFileEnrichmentState state) => state switch
        {
            PrinterFileEnrichmentState.Deferred => 5,
            PrinterFileEnrichmentState.Failed => 4,
            PrinterFileEnrichmentState.Partial => 3,
            PrinterFileEnrichmentState.Unsupported => 2,
            _ => 1,
        };

        private static bool HasToolUnit(PrinterPrintMetadata metadata) =>
            metadata.Tools is not null || metadata.FilamentTypePerTool is not null ||
            metadata.FilamentUsedPerToolG is not null || metadata.FilamentUsedPerToolMm is not null ||
            metadata.FilamentUsedPerToolCm3 is not null || metadata.FilamentCostPerTool is not null ||
            metadata.NozzleDiameterPerToolMm is not null || metadata.NozzleTemperaturePerToolC is not null ||
            metadata.BedTemperaturePerToolC is not null;

        private static bool MissingFields(PrinterPrintMetadata metadata) =>
            metadata.SlicerName is null || metadata.SlicerVersion is null ||
            (metadata.EstimatedPrintTimeSeconds is null && metadata.PrintTimeSeconds is null) ||
            metadata.LayerHeightMm is null || metadata.LayerCount is null || metadata.NozzleDiameterMm is null ||
            metadata.NozzleTemperatureC is null || metadata.BedTemperatureC is null ||
            metadata.InfillPercentage is null || metadata.InfillPattern is null || metadata.SupportEnabled is null ||
            metadata.FilamentType is null || metadata.FilamentUsedG is null || metadata.FilamentUsedMm is null ||
            metadata.PrinterModel is null || !HasToolUnit(metadata);

        /// <summary>
        /// Field-wise merge in which a native value always wins. Values that only make sense together (slicer
        /// identity, the per-tool group, total grams with its derived flag, print time) come from one source.
        /// </summary>
        private static PrinterPrintMetadata? Merge(PrinterPrintMetadata? native, PrinterPrintMetadata? fallback)
        {
            if (fallback is not { HasValues: true })
            {
                return native;
            }

            if (native is null)
            {
                return fallback;
            }

            bool takeIdentity = native.SlicerName is null && native.SlicerVersion is null;
            bool takeTools = !HasToolUnit(native);
            bool takeTime = native.EstimatedPrintTimeSeconds is null && native.PrintTimeSeconds is null;
            bool takeGrams = native.FilamentUsedG is null && fallback.FilamentUsedG is not null &&
                (fallback.FilamentUsedGIsDerived is not true || takeTools);
            bool allowSupportDetail = (native.SupportEnabled ?? fallback.SupportEnabled) is not false;
            return native with
            {
                SlicerName = takeIdentity ? fallback.SlicerName : native.SlicerName,
                SlicerVersion = takeIdentity ? fallback.SlicerVersion : native.SlicerVersion,
                EstimatedPrintTimeSeconds = takeTime ? fallback.EstimatedPrintTimeSeconds : native.EstimatedPrintTimeSeconds,
                LayerHeightMm = native.LayerHeightMm ?? fallback.LayerHeightMm,
                LayerCount = native.LayerCount ?? fallback.LayerCount,
                NozzleDiameterMm = native.NozzleDiameterMm ?? fallback.NozzleDiameterMm,
                NozzleTemperatureC = native.NozzleTemperatureC ?? fallback.NozzleTemperatureC,
                BedTemperatureC = native.BedTemperatureC ?? fallback.BedTemperatureC,
                InfillPercentage = native.InfillPercentage ?? fallback.InfillPercentage,
                InfillPattern = native.InfillPattern ?? fallback.InfillPattern,
                SupportEnabled = native.SupportEnabled ?? fallback.SupportEnabled,
                SupportType = native.SupportType ?? (allowSupportDetail ? fallback.SupportType : null),
                SupportStyle = native.SupportStyle ?? (allowSupportDetail ? fallback.SupportStyle : null),
                PrinterModel = native.PrinterModel ?? fallback.PrinterModel,
                Profile = native.Profile ?? fallback.Profile,
                FilamentType = native.FilamentType ?? fallback.FilamentType,
                FilamentUsedG = takeGrams ? fallback.FilamentUsedG : native.FilamentUsedG,
                FilamentUsedGIsDerived = takeGrams ? fallback.FilamentUsedGIsDerived : native.FilamentUsedGIsDerived,
                FilamentUsedMm = native.FilamentUsedMm ?? fallback.FilamentUsedMm,
                Tools = takeTools ? fallback.Tools : native.Tools,
                FilamentUsedPerToolG = takeTools ? fallback.FilamentUsedPerToolG : native.FilamentUsedPerToolG,
                FilamentUsedPerToolMm = takeTools ? fallback.FilamentUsedPerToolMm : native.FilamentUsedPerToolMm,
                FilamentTypePerTool = takeTools ? fallback.FilamentTypePerTool : native.FilamentTypePerTool,
            };
        }

        private static ThumbnailOutcome Validate(byte[]? image)
        {
            if (image is null or { Length: 0 })
            {
                return new(PrinterFileEnrichmentState.Absent, "thumbnail_missing");
            }

            if (image.Length > PrinterFileEnrichmentLimits.MaximumImageBytes)
            {
                return new(PrinterFileEnrichmentState.Failed, "size_limit");
            }

            if (PrinterFileImageValidator.TryValidate(image, out string? contentType, out _, out _))
            {
                return new(PrinterFileEnrichmentState.Ready, null, image, contentType);
            }

            return image.AsSpan().StartsWith("qoif"u8) || image.AsSpan().StartsWith("GIF8"u8)
                ? new(PrinterFileEnrichmentState.Unsupported, "unsupported_image_format")
                : new(PrinterFileEnrichmentState.Failed, "invalid_image");
        }

        private static (PrinterFileEnrichmentState State, string? Reason) TextImageMiss(
            GcodeMetadataExtractionResult parsed, bool complete) => parsed.ThumbnailReason switch
            {
                "unsupported-thumbnail" => (PrinterFileEnrichmentState.Unsupported, "unsupported_image_format"),
                "incomplete-thumbnail" => complete
                    ? (PrinterFileEnrichmentState.Failed, "invalid_image")
                    : (PrinterFileEnrichmentState.Partial, "region_limit"),
                "image-dimensions" => (PrinterFileEnrichmentState.Failed, "image_limit"),
                "image-size" => (PrinterFileEnrichmentState.Failed, "size_limit"),
                "invalid-thumbnail" => (PrinterFileEnrichmentState.Failed, "invalid_image"),
                "decoded-limit" => (PrinterFileEnrichmentState.Partial, "decoded_limit"),
                "capture-disabled" => (PrinterFileEnrichmentState.Deferred, "capture_paused"),
                "not-in-bounded-source" => (
                    PrinterFileEnrichmentState.Absent, complete ? "thumbnail_missing" : "not_in_bounded_source"),
                _ => (PrinterFileEnrichmentState.Absent, "thumbnail_missing"),
            };

        private static byte[] WithoutPartialEnd(byte[] bytes)
        {
            int end = bytes.AsSpan().LastIndexOfAny((byte)'\n', (byte)'\r');
            return end < 0 ? [] : bytes[..(end + 1)];
        }

        private static byte[] WithoutPartialStart(byte[] bytes)
        {
            int start = bytes.AsSpan().IndexOfAny((byte)'\n', (byte)'\r');
            return start < 0 ? [] : bytes[(start + 1)..];
        }

        public async Task<PrinterFileEnrichmentContent> ExecuteAsync()
        {
            try
            {
                if (sources.ReadNativeAsync is { } readNative)
                {
                    (PrinterFileNativeEnrichment? result, string? failure) =
                        await TryAsync(() => readNative(budget)).ConfigureAwait(false);
                    if (failure is not null)
                    {
                        if (sources.ReadRegionAsync is null || !IsRecoverableNativeFailure(failure))
                        {
                            return Terminal(failure);
                        }
                    }
                    else if (result is not null)
                    {
                        AcceptNative(result);
                    }
                }

                // The dedicated thumbnail read precedes optional bounded metadata fallback so a slow
                // G-code read cannot consume the deadline before the cheaper image request runs.
                ThumbnailOutcome? nativeThumbnail = await ReadNativeThumbnailAsync().ConfigureAwait(false);
                bool needFallback = sources.ReadRegionAsync is not null && (native is null || MissingFields(native));
                if (needFallback)
                {
                    string trigger = captureThumbnail && nativeThumbnail?.State != PrinterFileEnrichmentState.Ready
                        ? "metadata_and_thumbnail"
                        : "metadata";
                    fallback = await AcquireFallbackAsync(needTail: true, trigger).ConfigureAwait(false);
                }

                ThumbnailOutcome thumbnail = await ResolveThumbnailAsync(nativeThumbnail).ConfigureAwait(false);
                if (readSource && sources.VerifyUnchangedAsync is { } verify && BudgetReason() is null)
                {
                    // Best effort: only a detected change or disappearance discards the acquisition.
                    await TryAsync(async () =>
                    {
                        await verify(budget).ConfigureAwait(false);
                        return true;
                    }).ConfigureAwait(false);
                }

                return Compose(needFallback ? fallback : null, thumbnail);
            }
            catch (Exception ex) when (ReasonOf(ex) is { } reason)
            {
                return Terminal(reason);
            }
        }

        private async Task<Fallback> AcquireFallbackAsync(bool needTail, string trigger)
        {
            if (BudgetReason() is { } stop)
            {
                metrics.SkipFallback(backend, trigger, stop);
                return Fallback.Failure(stop);
            }

            if (size == 0)
            {
                metrics.SkipFallback(backend, trigger, "empty_source");
                return await ReadFallbackAsync(needTail).ConfigureAwait(false);
            }

            long started = metrics.StartFallback(backend, trigger);
            string outcome = "error";
            try
            {
                Fallback result = await ReadFallbackAsync(needTail).ConfigureAwait(false);
                outcome = result.Reason == "deadline" ? "timeout"
                    : result.Conclusive && result.Reason is null ? "completed"
                    : result.Metadata is { HasValues: true } || result.Images.Count > 0 ? "partial"
                    : "failed";
                return result;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                outcome = "cancelled";
                throw;
            }
            catch (Exception ex) when (ReasonOf(ex) is { } reason)
            {
                outcome = reason == "deadline" ? "timeout" : "failed";
                throw;
            }
            finally
            {
                metrics.CompleteFallback(backend, trigger, outcome, started);
            }
        }

        private void AcceptNative(PrinterFileNativeEnrichment result)
        {
            native = result.Metadata is { HasValues: true } metadata && metadata.IsValid() ? metadata : null;
            nativeImage = result.ReadThumbnailAsync;
            if (result.SourceSizeBytes is >= 0)
            {
                size = result.SourceSizeBytes;
            }
        }

        private string? ReasonOf(Exception ex) => ex switch
        {
            PrinterFileCopyException copy => copy.Reason,
            OperationCanceledException when ct.IsCancellationRequested => null,
            OperationCanceledException when budget.Token.IsCancellationRequested => "deadline",
            OperationCanceledException or HttpRequestException or IOException => "transfer_failed",
            _ => null,
        };

        private string? BudgetReason()
        {
            ct.ThrowIfCancellationRequested();
            if (budget.Token.IsCancellationRequested)
            {
                return "deadline";
            }

            if (budget.Requests >= PrinterFileEnrichmentLimits.MaximumRequests)
            {
                return "request_limit";
            }

            return budget.ResponseBytes >= PrinterFileEnrichmentLimits.MaximumResponseBytes ? "response_limit" : null;
        }

        // A changed source always discards the acquisition; a missing one does too unless the caller says otherwise.
        private async Task<(T? Value, string? Failure)> TryAsync<T>(Func<Task<T>> action, bool missingIsFatal = true)
        {
            try
            {
                return (await action().ConfigureAwait(false), null);
            }
            catch (Exception ex) when (ReasonOf(ex) is { } reason && reason != "source_changed" &&
                (!missingIsFatal || reason != "source_missing"))
            {
                return (default, reason);
            }
        }

        private Task<PrinterFileRegion> ReadRegionAsync(long offset, int count) =>
            sources.ReadRegionAsync is { } read
                ? read(offset, count, size, budget)
                : Task.FromException<PrinterFileRegion>(new PrinterFileCopyException("unsupported_source"));

        private PrinterFileEnrichmentContent Terminal(string reason)
        {
            PrinterFileEnrichmentState state = StateFor(reason, hasValue: false);
            return new(
                Metadata: null, MetadataState: state, ThumbnailState: state, MetadataReason: reason,
                ThumbnailReason: reason, ResponseBytes: budget.ResponseBytes);
        }

        private async Task<Fallback> ReadFallbackAsync(bool needTail)
        {
            readSource = true;
            if (size is 0)
            {
                return new(null, true, null, "bounded_gcode", [], PrinterFileEnrichmentState.Absent, "thumbnail_missing");
            }

            bool binaryHint = observation.RelativePath.EndsWith(".bgcode", StringComparison.OrdinalIgnoreCase);
            (PrinterFileRegion? head, string? failure) = await TryAsync(() => ReadRegionAsync(
                0, binaryHint ? PrinterFileEnrichmentLimits.BinaryPrefixBytes : PrinterFileEnrichmentLimits.TextRegionBytes))
                .ConfigureAwait(false);
            if (head is null)
            {
                return Fallback.Failure(failure ?? "transfer_failed");
            }

            if (BinaryGcodeReader.HasSignature(head.Bytes))
            {
                return await ReadBinaryAsync(head).ConfigureAwait(false);
            }

            if (head.Bytes.Length > PrinterFileEnrichmentLimits.TextRegionBytes)
            {
                head = new(head.Bytes[..PrinterFileEnrichmentLimits.TextRegionBytes], 0, false);
            }

            return await ReadTextAsync(head, needTail).ConfigureAwait(false);
        }

        private async Task<Fallback> ReadBinaryAsync(PrinterFileRegion head)
        {
            PrinterFileRegion prefix = head;
            string? prefixFailure = null;
            if (!head.CompleteFile && head.Bytes.Length < PrinterFileEnrichmentLimits.BinaryPrefixBytes)
            {
                (PrinterFileRegion? region, prefixFailure) = await TryAsync(
                    () => ReadRegionAsync(0, PrinterFileEnrichmentLimits.BinaryPrefixBytes)).ConfigureAwait(false);
                prefix = region ?? head;
            }

            BinaryGcodeBoundedContent bounded = await BinaryGcodeReader.ReadBoundedAsync(
                prefix.Bytes, prefix.CompleteFile, captureThumbnail, ct).ConfigureAwait(false);
            GcodeMetadataExtractionResult parsed = extractor.ExtractBoundedMetadata(
                BinaryGcodeReader.ToCommentText(new BinaryGcodeContent(bounded.Content.Metadata, [])), false, ct);
            PrinterPrintMetadata metadata = parsed.Metadata.ToPrinterPrintMetadata();
            string? reason = bounded.MetadataReason ?? parsed.MetadataReason switch
            {
                "metadata-limit" => "metadata_limit",
                "decoded-limit" => "decoded_limit",
                _ => null,
            };
            if (reason == "region_limit" && prefixFailure is not null)
            {
                reason = prefixFailure;
            }

            byte[][] images = [.. bounded.Content.Thumbnails
                .OrderByDescending(thumbnail => (long)thumbnail.Width * thumbnail.Height)
                .Select(thumbnail => thumbnail.Data)];
            return new(
                metadata.HasValues ? metadata : null, reason is null, reason, "bounded_bgcode", images,
                images.Length > 0 ? PrinterFileEnrichmentState.Ready : bounded.ThumbnailState,
                images.Length > 0 ? null : bounded.ThumbnailReason);
        }

        private async Task<Fallback> ReadTextAsync(PrinterFileRegion head, bool needTail)
        {
            bool complete = head.CompleteFile || (size is > 0 && head.Bytes.Length >= size);
            PrinterFileRegion? tail = null;
            string? tailFailure = null;
            if (!complete && needTail)
            {
                if (size is not > 0)
                {
                    tailFailure = "invalid_source_size";
                }
                else
                {
                    long start = Math.Max(head.Bytes.Length, size.Value - PrinterFileEnrichmentLimits.TextRegionBytes);
                    (tail, tailFailure) = await TryAsync(
                        () => ReadRegionAsync(start, checked((int)(size.Value - start)))).ConfigureAwait(false);
                }
            }

            // A window edge can cut a line (for example "; layer_height = 0."), so partial lines never reach the parser.
            byte[] window = tail switch
            {
                { } contiguous when contiguous.Offset == head.Bytes.Length => [.. head.Bytes, .. contiguous.Bytes],
                { } distant => [.. WithoutPartialEnd(head.Bytes), (byte)'\n', .. WithoutPartialStart(distant.Bytes)],
                null when complete => head.Bytes,
                null => WithoutPartialEnd(head.Bytes),
            };
            bool wholeFile = complete || (tail is { } joined && joined.Offset == head.Bytes.Length);
            GcodeMetadataExtractionResult parsed = extractor.ExtractBoundedMetadata(
                Encoding.UTF8.GetString(window), captureThumbnail, ct);
            string? reason = parsed.MetadataReason switch
            {
                "metadata-limit" => "metadata_limit",
                "decoded-limit" => "decoded_limit",
                _ => null,
            };
            if (reason is null && !wholeFile && tail is null)
            {
                reason = tailFailure ?? "region_limit";
            }

            PrinterPrintMetadata metadata = parsed.Metadata.ToPrinterPrintMetadata();
            byte[]? image = parsed.Metadata.ThumbnailData;
            (PrinterFileEnrichmentState imageState, string? imageReason) = image is null
                ? TextImageMiss(parsed, wholeFile)
                : (PrinterFileEnrichmentState.Ready, null);
            return new(
                metadata.HasValues ? metadata : null, reason is null, reason, "bounded_gcode",
                image is null ? [] : [image], imageState, imageReason);
        }

        private async Task<ThumbnailOutcome?> ReadNativeThumbnailAsync()
        {
            if (!captureThumbnail || nativeImage is not { } readImage)
            {
                return null;
            }

            if (BudgetReason() is { } exhausted)
            {
                return new(PrinterFileEnrichmentState.Deferred, exhausted);
            }

            readSource = true;
            (byte[]? bytes, string? failure) = await TryAsync(
                () => readImage(budget), missingIsFatal: false).ConfigureAwait(false);
            if (failure is not null)
            {
                return failure == "source_missing"
                    ? new(PrinterFileEnrichmentState.Absent, "thumbnail_missing")
                    : new(StateFor(failure, hasValue: false), failure);
            }

            return Validate(bytes);
        }

        private async Task<ThumbnailOutcome> ResolveThumbnailAsync(ThumbnailOutcome? nativeThumbnail)
        {
            if (!captureThumbnail)
            {
                return new(PrinterFileEnrichmentState.Deferred, "capture_paused");
            }

            List<(PrinterFileEnrichmentState State, string? Reason)> misses = [];
            if (nativeThumbnail is { } nativeOutcome)
            {
                if (nativeOutcome.State == PrinterFileEnrichmentState.Ready)
                {
                    return nativeOutcome;
                }

                misses.Add((nativeOutcome.State, nativeOutcome.Reason));
            }

            if (sources.ReadRegionAsync is not null)
            {
                if (fallback is null)
                {
                    fallback = await AcquireFallbackAsync(needTail: false, "thumbnail").ConfigureAwait(false);
                }

                if (fallback is { } embedded)
                {
                    if (embedded.Images.Count == 0)
                    {
                        misses.Add((embedded.ImageState, embedded.ImageReason));
                    }

                    foreach (byte[] candidate in embedded.Images)
                    {
                        ThumbnailOutcome checkedImage = Validate(candidate);
                        if (checkedImage.State == PrinterFileEnrichmentState.Ready)
                        {
                            return checkedImage;
                        }

                        misses.Add((checkedImage.State, checkedImage.Reason));
                    }
                }
            }

            if (misses.Count == 0)
            {
                return sources.ReadRegionAsync is null && nativeImage is null
                    ? new(PrinterFileEnrichmentState.Unsupported, "thumbnail_unsupported")
                    : new(PrinterFileEnrichmentState.Absent, "thumbnail_missing");
            }

            (PrinterFileEnrichmentState state, string? reason) = misses.MaxBy(miss => Priority(miss.State));
            return new(state, reason ?? (state == PrinterFileEnrichmentState.Absent ? "thumbnail_missing" : null));
        }

        private PrinterFileEnrichmentContent Compose(Fallback? used, ThumbnailOutcome thumbnail)
        {
            PrinterPrintMetadata? merged = Merge(native, used?.Metadata);
            string? reason = null;
            if (merged is not null && !(merged.IsValid() && merged.FitsStorage()))
            {
                merged = native is not null && native.FitsStorage() ? native : null;
                reason = "metadata_limit";
            }

            PrinterFileEnrichmentState state;
            if (reason is not null)
            {
                state = PrinterFileEnrichmentState.Partial;
            }
            else if (used is { Conclusive: false })
            {
                reason = used.Reason;
                state = StateFor(reason ?? "region_limit", merged is not null);
            }
            else
            {
                state = merged is not null ? PrinterFileEnrichmentState.Ready : PrinterFileEnrichmentState.Absent;
            }

            bool fallbackContributed = used?.Metadata is not null && merged is not null && merged != native;
            string? source = (native is not null, fallbackContributed) switch
            {
                (true, true) => $"native+{used?.Source}",
                (true, false) => "native",
                (false, true) => used?.Source,
                _ => null,
            };
            return new(
                merged, state, thumbnail.State, thumbnail.Image, thumbnail.ContentType, reason, thumbnail.Reason,
                source, budget.ResponseBytes);
        }
    }
}
