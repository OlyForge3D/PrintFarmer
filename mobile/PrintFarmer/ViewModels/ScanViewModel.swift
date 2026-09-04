import Foundation
import os

/// Drives the unified F9 scan station (#714): a single generic camera scan
/// type-dispatches to a printer deep-link, an enabled printed-parts destination,
/// or (falling back) the existing spool-barcode intake flow.
///
/// When printed-parts inventory is enabled, dispatch order matches the frozen
/// mobile scope posted on #714: printer → bin → part → spool. Not-found and
/// feature-disabled part resolvers fall through; other failures remain visible.
/// Disabling the capability skips or fences those resolvers without blocking
/// printer and spool routing.
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
        let requiresPrintedPartsInventory: Bool

        static func == (lhs: RecentScan, rhs: RecentScan) -> Bool {
            lhs.id == rhs.id
        }
    }

    private static let maxRecentScans = 20

    private enum PrintedPartsDispatchResult {
        case stop
        case continueToSpool
    }

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
    var isViewActive = true {
        didSet {
            guard oldValue && !isViewActive else { return }
            invalidateOperations(clearRecentScans: false)
        }
    }

    private let logger = Logger(subsystem: "com.printfarmer.ios", category: "ScanStation")
    private var scanner: (any BarcodeScannerProtocol)?
    private var partsInventoryService: (any PartsInventoryServiceProtocol)?
    private var barcodeIntakeService: (any BarcodeIntakeServiceProtocol)?
    private var spoolService: (any SpoolServiceProtocol)?
    private var printedPartsInventoryEnabled = true
    private var printedPartsCapabilityGeneration = 0
    private var operationGeneration = 0

    func configure(
        scanner: (any BarcodeScannerProtocol)?,
        partsInventoryService: any PartsInventoryServiceProtocol,
        barcodeIntakeService: any BarcodeIntakeServiceProtocol,
        spoolService: any SpoolServiceProtocol,
        printedPartsInventoryEnabled: Bool = true
    ) {
        invalidateOperations(clearRecentScans: true)
        self.scanner = scanner
        self.partsInventoryService = partsInventoryService
        self.barcodeIntakeService = barcodeIntakeService
        self.spoolService = spoolService
        setPrintedPartsInventoryEnabled(printedPartsInventoryEnabled)
    }

    func setPrintedPartsInventoryEnabled(_ isEnabled: Bool) {
        guard printedPartsInventoryEnabled != isEnabled else { return }

        printedPartsInventoryEnabled = isEnabled
        printedPartsCapabilityGeneration &+= 1
        guard !isEnabled else { return }

        switch pendingOutcome {
        case .some(.bin), .some(.part):
            pendingOutcome = nil
        case .some(.unknownCode), .none:
            break
        }
        recentScans.removeAll(where: \.requiresPrintedPartsInventory)
    }

    var isScannerAvailable: Bool {
        scanner?.isAvailable ?? false
    }

    @discardableResult
    func scan() -> Task<Void, Never>? {
        guard !isScanning else { return nil }
        guard let scanner, scanner.isAvailable else {
            errorMessage = "Scanning is not available on this device."
            return nil
        }

        let generation = operationGeneration
        isScanning = true
        errorMessage = nil

        return Task {
            let result = await scanner.scanBarcode()
            guard isOperationCurrent(generation) else { return }
            switch result {
            case .barcode(let code):
                await dispatch(code, operationGeneration: generation)
            case .cancelled:
                break
            case .error(let error):
                errorMessage = error.localizedDescription
            }
            guard isOperationCurrent(generation) else { return }
            isScanning = false
        }
    }

    /// Type-dispatches a single scanned code. Exposed directly (in addition
    /// to `scan()`) so tests and previews can drive dispatch without a real
    /// camera session.
    func dispatch(_ rawCode: String) async {
        await dispatch(rawCode, operationGeneration: operationGeneration)
    }

    private func dispatch(
        _ rawCode: String,
        operationGeneration generation: Int
    ) async {
        guard isOperationCurrent(generation) else { return }
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
            case .attentionItem, .filamentSwap:
                break
            }
        }

        if printedPartsInventoryEnabled {
            switch await dispatchPrintedParts(trimmed, operationGeneration: generation) {
            case .stop:
                return
            case .continueToSpool:
                break
            }
        }

        guard isOperationCurrent(generation) else { return }

        // A spool deep link (captured above) reaches this stage only
        // after enabled bin/part resolution missed — an unambiguous format,
        // so it takes priority over re-parsing the same code as a
        // structured URL/JSON payload below.
        if let deferredSpoolId {
            await routeToSpoolIfExists(
                id: deferredSpoolId,
                subtitle: "Spool #\(deferredSpoolId)",
                operationGeneration: generation
            )
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
            await routeToSpoolIfExists(
                id: spoolId,
                subtitle: "Spool #\(spoolId)",
                operationGeneration: generation
            )
            return
        }

        guard let barcodeIntakeService else {
            guard isOperationCurrent(generation) else { return }
            pendingOutcome = .unknownCode(trimmed)
            return
        }

        do {
            if let filament = try await barcodeIntakeService.resolveFilament(barcode: trimmed) {
                guard isOperationCurrent(generation) else { return }
                recordRecentScan(icon: "cylinder", title: filament.name ?? filament.material ?? "Spool", subtitle: "Spool barcode")
                pendingSpoolBarcode = trimmed
            } else {
                guard isOperationCurrent(generation) else { return }
                // Known raw barcodes retain Barcode Intake (handled above);
                // an unresolved bare positive-integer code that Barcode
                // Intake doesn't recognize falls back to being treated as
                // an (unresolved) spool ID rather than an unknown code.
                if let spoolId = QRCodeParser.parse(trimmed) {
                    await routeToSpoolIfExists(
                        id: spoolId,
                        subtitle: "Spool #\(spoolId)",
                        operationGeneration: generation
                    )
                } else {
                    recordRecentScan(icon: "questionmark.circle", title: "Unrecognized", subtitle: trimmed)
                    pendingOutcome = .unknownCode(trimmed)
                }
            }
        } catch {
            guard isOperationCurrent(generation) else { return }
            logger.warning("Spool resolution failed: \(error.localizedDescription)")
            errorMessage = error.localizedDescription
        }
    }

    private func dispatchPrintedParts(
        _ code: String,
        operationGeneration: Int
    ) async -> PrintedPartsDispatchResult {
        guard let partsInventoryService else {
            errorMessage = "Parts inventory service not available"
            return .stop
        }
        let generation = printedPartsCapabilityGeneration

        do {
            let bin = try await partsInventoryService.resolveBinByBarcode(code)
            if let interrupted = interruptedPrintedPartsResult(
                for: generation,
                operationGeneration: operationGeneration
            ) {
                return interrupted
            }
            recordRecentScan(
                icon: "shippingbox",
                title: bin.name,
                subtitle: "Bin \(bin.code)",
                requiresPrintedPartsInventory: true
            )
            pendingOutcome = .bin(bin)
            return .stop
        } catch NetworkError.notFound, NetworkError.featureDisabled {
            if let interrupted = interruptedPrintedPartsResult(
                for: generation,
                operationGeneration: operationGeneration
            ) {
                return interrupted
            }
        } catch {
            if let interrupted = interruptedPrintedPartsResult(
                for: generation,
                operationGeneration: operationGeneration
            ) {
                return interrupted
            }
            logger.warning("Bin resolution failed: \(error.localizedDescription)")
            errorMessage = error.localizedDescription
            return .stop
        }

        do {
            let part = try await partsInventoryService.resolvePartByBarcode(code)
            if let interrupted = interruptedPrintedPartsResult(
                for: generation,
                operationGeneration: operationGeneration
            ) {
                return interrupted
            }
            recordRecentScan(
                icon: "cube.box",
                title: part.name,
                subtitle: "SKU \(part.sku)",
                requiresPrintedPartsInventory: true
            )
            pendingOutcome = .part(part)
            return .stop
        } catch NetworkError.notFound, NetworkError.featureDisabled {
            if let interrupted = interruptedPrintedPartsResult(
                for: generation,
                operationGeneration: operationGeneration
            ) {
                return interrupted
            }
            return .continueToSpool
        } catch {
            if let interrupted = interruptedPrintedPartsResult(
                for: generation,
                operationGeneration: operationGeneration
            ) {
                return interrupted
            }
            logger.warning("Part resolution failed: \(error.localizedDescription)")
            errorMessage = error.localizedDescription
            return .stop
        }
    }

    private func interruptedPrintedPartsResult(
        for generation: Int,
        operationGeneration: Int
    ) -> PrintedPartsDispatchResult? {
        guard isOperationCurrent(operationGeneration) else { return .stop }
        guard generation != printedPartsCapabilityGeneration || !printedPartsInventoryEnabled else {
            return nil
        }
        return .continueToSpool
    }

    /// Confirms a candidate spool ID (from a deep link, a structured
    /// URL/JSON payload, or an unresolved bare-numeric fallback) actually
    /// exists on the currently-connected (active) server before routing
    /// to `.spoolDetail` (#714 Item C) — a structured/deep-link/numeric
    /// payload NEVER falls back to raw barcode registration regardless of
    /// the lookup's outcome; it either routes or surfaces an explicit
    /// error.
    private func routeToSpoolIfExists(
        id: Int,
        subtitle: String,
        operationGeneration: Int
    ) async {
        guard let spoolService else {
            errorMessage = "Spool service not available"
            return
        }

        do {
            let exists = try await spoolService.spoolExists(id: id)
            guard isOperationCurrent(operationGeneration) else { return }
            if exists {
                recordRecentScan(icon: "cylinder", title: "Spool", subtitle: subtitle)
                pendingDeepLinkDestination = .spoolDetail(id: id)
            } else {
                errorMessage = "Spool #\(id) was not found on this server."
            }
        } catch {
            guard isOperationCurrent(operationGeneration) else { return }
            logger.warning("Spool existence lookup failed: \(error.localizedDescription)")
            errorMessage = error.localizedDescription
        }
    }

    func clearError() {
        errorMessage = nil
    }

    private func isOperationCurrent(_ generation: Int) -> Bool {
        isViewActive && generation == operationGeneration
    }

    private func invalidateOperations(clearRecentScans: Bool) {
        operationGeneration &+= 1
        isScanning = false
        errorMessage = nil
        pendingOutcome = nil
        pendingDeepLinkDestination = nil
        pendingSpoolBarcode = nil
        if clearRecentScans {
            recentScans.removeAll()
        }
    }

    private func recordRecentScan(
        icon: String,
        title: String,
        subtitle: String,
        requiresPrintedPartsInventory: Bool = false
    ) {
        let entry = RecentScan(
            icon: icon,
            title: title,
            subtitle: subtitle,
            scannedAt: .now,
            requiresPrintedPartsInventory: requiresPrintedPartsInventory
        )
        recentScans.insert(entry, at: 0)
        if recentScans.count > Self.maxRecentScans {
            recentScans.removeLast(recentScans.count - Self.maxRecentScans)
        }
    }
}
