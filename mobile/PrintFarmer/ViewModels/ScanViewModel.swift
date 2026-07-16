import Foundation
import os

/// Drives the unified F9 scan station (#714): a single generic camera scan
/// type-dispatches to a printer deep-link, a printed-part bin, a printed-part
/// SKU, or (falling back) the existing spool-barcode intake flow.
///
/// Dispatch order matches the frozen mobile scope posted on #714: printer →
/// bin → part → spool. Each step only advances past a definitive "not
/// found" (`NetworkError.notFound`); any other failure (network outage,
/// `.featureDisabled`, etc.) surfaces immediately instead of being silently
/// swallowed and retried against the next resolver.
@MainActor @Observable
final class ScanViewModel {
    /// A resolved scan outcome that the view presents as a sheet. Printer
    /// matches are handled separately via `pendingPrinterId` since those
    /// navigate (push onto the Farm tab stack) rather than present a sheet.
    enum ScanOutcome: Identifiable {
        case bin(BinResponse)
        case part(PartInventoryResponse)
        case unknownCode(String)

        var id: String {
            switch self {
            case .bin(let bin): "bin-\(bin.id.uuidString)"
            case .part(let part): "part-\(part.id.uuidString)"
            case .unknownCode(let code): "unknown-\(code)"
            }
        }
    }

    /// A session-local (non-persisted) recent-scan entry. #714's frozen
    /// mobile scope deliberately does not use
    /// `GET /api/spoolman/barcodes/scan-logs` (that route is
    /// `farm_admin`-only) — recent scans live only in this view-model for
    /// the current app session.
    struct RecentScan: Identifiable, Equatable {
        let id = UUID()
        let icon: String
        let title: String
        let subtitle: String
        let scannedAt: Date

        static func == (lhs: RecentScan, rhs: RecentScan) -> Bool {
            lhs.id == rhs.id
        }
    }

    private static let maxRecentScans = 20

    var isScanning = false
    var errorMessage: String?
    var pendingOutcome: ScanOutcome?
    /// Set when a scanned code parses as a `printfarmer://printer/...` or
    /// `printfarmer://spool/...`-style deep link (including a structured
    /// spool URL/JSON QR payload resolved via `QRCodeParser`). The view
    /// observes this and forwards it to `AppRouter.navigate(to:)` (the same
    /// entry point used for NFC/URL deep links) then clears it — printers
    /// and spools are never presented as a sheet from this view-model.
    var pendingDeepLinkDestination: DeepLinkDestination?
    /// Set when a scanned code resolves to a known filament barcode so the
    /// view can forward it into the existing `BarcodeIntakeView` flow
    /// (reused as-is per #714's frozen scope) instead of re-implementing
    /// spool import here.
    var pendingSpoolBarcode: String?
    var recentScans: [RecentScan] = []
    var isViewActive = true

    private let logger = Logger(subsystem: "com.printfarmer.ios", category: "ScanStation")
    private var scanner: (any BarcodeScannerProtocol)?
    private var partsInventoryService: (any PartsInventoryServiceProtocol)?
    private var barcodeIntakeService: (any BarcodeIntakeServiceProtocol)?
    private var spoolService: (any SpoolServiceProtocol)?

    func configure(
        scanner: (any BarcodeScannerProtocol)?,
        partsInventoryService: any PartsInventoryServiceProtocol,
        barcodeIntakeService: any BarcodeIntakeServiceProtocol,
        spoolService: any SpoolServiceProtocol
    ) {
        self.scanner = scanner
        self.partsInventoryService = partsInventoryService
        self.barcodeIntakeService = barcodeIntakeService
        self.spoolService = spoolService
    }

    var isScannerAvailable: Bool {
        scanner?.isAvailable ?? false
    }

    func scan() {
        guard let scanner, scanner.isAvailable else {
            errorMessage = "Scanning is not available on this device."
            return
        }

        isScanning = true
        errorMessage = nil

        Task {
            defer { isScanning = false }
            let result = await scanner.scanBarcode()
            guard isViewActive else { return }
            switch result {
            case .barcode(let code):
                await dispatch(code)
            case .cancelled:
                break
            case .error(let error):
                errorMessage = error.localizedDescription
            }
        }
    }

    /// Type-dispatches a single scanned code. Exposed directly (in addition
    /// to `scan()`) so tests and previews can drive dispatch without a real
    /// camera session.
    func dispatch(_ rawCode: String) async {
        let trimmed = rawCode.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { return }
        errorMessage = nil

        // A spool deep link's ID is captured but deliberately NOT routed
        // yet — preserving the frozen printer → bin → part → spool
        // dispatch precedence (#714 Item C) means a spool candidate,
        // however it was recognized, never skips ahead of bin/part
        // resolution attempts. It is only ever routed (after those
        // attempts miss) once `routeToSpoolIfExists` confirms the ID
        // against the active server.
        var deferredSpoolId: Int?

        if let url = URL(string: trimmed), let destination = DeepLinkHandler.parse(url: url) {
            switch destination {
            case .printerDetail, .printerReady:
                recordRecentScan(icon: "printer", title: "Printer", subtitle: trimmed)
                pendingDeepLinkDestination = destination
                return
            case .spoolDetail(let id):
                deferredSpoolId = id
            }
        }

        guard let partsInventoryService else {
            errorMessage = "Parts inventory service not available"
            return
        }

        do {
            let bin = try await partsInventoryService.resolveBinByBarcode(trimmed)
            guard isViewActive else { return }
            recordRecentScan(icon: "shippingbox", title: bin.name, subtitle: "Bin \(bin.code)")
            pendingOutcome = .bin(bin)
            return
        } catch NetworkError.notFound, NetworkError.featureDisabled {
            // Not a bin, or printed-parts inventory is disabled server-side —
            // either way fall through to part resolution rather than surfacing
            // an error (the feature gate must not block spool/printer routing).
        } catch {
            logger.warning("Bin resolution failed: \(error.localizedDescription)")
            errorMessage = error.localizedDescription
            return
        }

        do {
            let part = try await partsInventoryService.resolvePartByBarcode(trimmed)
            guard isViewActive else { return }
            recordRecentScan(icon: "cube.box", title: part.name, subtitle: "SKU \(part.sku)")
            pendingOutcome = .part(part)
            return
        } catch NetworkError.notFound, NetworkError.featureDisabled {
            // Not a part SKU, or printed-parts inventory is disabled server-side —
            // either way fall through to spool resolution.
        } catch {
            logger.warning("Part resolution failed: \(error.localizedDescription)")
            errorMessage = error.localizedDescription
            return
        }

        guard isViewActive else { return }

        // A spool deep link (captured above) reaches this stage only
        // after bin/part resolution both missed — an unambiguous format,
        // so it takes priority over re-parsing the same code as a
        // structured URL/JSON payload below.
        if let deferredSpoolId {
            await routeToSpoolIfExists(id: deferredSpoolId, subtitle: "Spool #\(deferredSpoolId)")
            return
        }

        // Structured spool payloads (URL or JSON forms — e.g.
        // `https://host/spools/42`, `{"spoolId": 42}`) are unambiguous and
        // must never be registered as a raw barcode, even if Barcode
        // Intake is unavailable. A bare positive-integer code is
        // deliberately NOT treated as structured here (see
        // `QRCodeParser.parseStructured`) since genuine EAN/UPC barcodes
        // are also numeric — those get first crack via Barcode Intake
        // below, falling back to a spool ID only on a definitive miss.
        if let spoolId = QRCodeParser.parseStructured(trimmed) {
            await routeToSpoolIfExists(id: spoolId, subtitle: "Spool #\(spoolId)")
            return
        }

        guard let barcodeIntakeService else {
            pendingOutcome = .unknownCode(trimmed)
            return
        }

        do {
            if let filament = try await barcodeIntakeService.resolveFilament(barcode: trimmed) {
                guard isViewActive else { return }
                recordRecentScan(icon: "cylinder", title: filament.name ?? filament.material ?? "Spool", subtitle: "Spool barcode")
                pendingSpoolBarcode = trimmed
            } else {
                guard isViewActive else { return }
                // Known raw barcodes retain Barcode Intake (handled above);
                // an unresolved bare positive-integer code that Barcode
                // Intake doesn't recognize falls back to being treated as
                // an (unresolved) spool ID rather than an unknown code.
                if let spoolId = QRCodeParser.parse(trimmed) {
                    await routeToSpoolIfExists(id: spoolId, subtitle: "Spool #\(spoolId)")
                } else {
                    recordRecentScan(icon: "questionmark.circle", title: "Unrecognized", subtitle: trimmed)
                    pendingOutcome = .unknownCode(trimmed)
                }
            }
        } catch {
            logger.warning("Spool resolution failed: \(error.localizedDescription)")
            errorMessage = error.localizedDescription
        }
    }

    /// Confirms a candidate spool ID (from a deep link, a structured
    /// URL/JSON payload, or an unresolved bare-numeric fallback) actually
    /// exists on the currently-connected (active) server before routing
    /// to `.spoolDetail` (#714 Item C) — a structured/deep-link/numeric
    /// payload NEVER falls back to raw barcode registration regardless of
    /// the lookup's outcome; it either routes or surfaces an explicit
    /// error.
    private func routeToSpoolIfExists(id: Int, subtitle: String) async {
        guard let spoolService else {
            errorMessage = "Spool service not available"
            return
        }

        do {
            let exists = try await spoolService.spoolExists(id: id)
            guard isViewActive else { return }
            if exists {
                recordRecentScan(icon: "cylinder", title: "Spool", subtitle: subtitle)
                pendingDeepLinkDestination = .spoolDetail(id: id)
            } else {
                errorMessage = "Spool #\(id) was not found on this server."
            }
        } catch {
            guard isViewActive else { return }
            logger.warning("Spool existence lookup failed: \(error.localizedDescription)")
            errorMessage = error.localizedDescription
        }
    }

    func clearError() {
        errorMessage = nil
    }

    private func recordRecentScan(icon: String, title: String, subtitle: String) {
        let entry = RecentScan(icon: icon, title: title, subtitle: subtitle, scannedAt: .now)
        recentScans.insert(entry, at: 0)
        if recentScans.count > Self.maxRecentScans {
            recentScans.removeLast(recentScans.count - Self.maxRecentScans)
        }
    }
}
