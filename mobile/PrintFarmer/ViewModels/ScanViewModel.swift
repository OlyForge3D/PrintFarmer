import Foundation
import os

/// Drives the shared spool scanner. The mobile app only manages spools, so a
/// scan resolves exclusively to a spool: a structured spool QR/deep link or a
/// bare spool ID opens that spool, and a filament product barcode (GTIN) is
/// handed to the existing Barcode Intake flow. Known product barcodes import a
/// spool; unknown valid GTINs open the same intake flow so the operator can map
/// the barcode to a filament (persisted server-side) before a spool is created.
/// Nothing is created merely by scanning.
@MainActor @Observable
final class ScanViewModel {
    /// A resolved scan outcome that the view presents as a sheet. Existing
    /// spools navigate via `pendingDeepLinkDestination` instead.
    enum ScanOutcome: Identifiable {
        case unknownCode(String)
        case newSpool(ScannedSpoolData)

        var id: String {
            switch self {
            case .unknownCode(let code): "unknown-\(code)"
            case .newSpool: "new-spool"
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
    /// Set when a scanned code identifies an existing spool (a
    /// `printfarmer://spool/...` deep link, a structured spool URL/JSON QR
    /// payload, or a bare spool ID) confirmed on the active server. The view
    /// forwards it to `AppRouter.navigate(to:)` then clears it.
    var pendingDeepLinkDestination: DeepLinkDestination?
    /// Set when a scanned code is a filament product barcode — either one
    /// already mapped to a filament or an unknown valid GTIN — so the view
    /// can forward it into the existing `BarcodeIntakeView` flow, which
    /// imports known barcodes and asks the operator to map unknown ones.
    var pendingSpoolBarcode: String?
    var recentScans: [RecentScan] = []
    private(set) var hasFinishedNavigation = false
    var isViewActive = true {
        didSet {
            if isViewActive { startQueuedExternalScanIfNeeded() }
        }
    }

    private let logger = Logger(subsystem: "com.printfarmer.ios", category: "ScanStation")
    private var scanner: (any BarcodeScannerProtocol)?
    private var nfcScanner: (any SpoolScannerProtocol)?
    private var waitForScannerPresentation: @MainActor () async -> Bool = { true }
    private var barcodeIntakeService: (any BarcodeIntakeServiceProtocol)?
    private var spoolService: (any SpoolServiceProtocol)?
    private var handledExternalScanRequestID: UUID?
    private var isResultPresentationActive = false
    // Overlapping requests coalesce into one retry through the existing scanner pipeline.
    private var queuedExternalScanRequestID: UUID?

    func configure(
        scanner: (any BarcodeScannerProtocol)?,
        barcodeIntakeService: any BarcodeIntakeServiceProtocol,
        spoolService: any SpoolServiceProtocol,
        waitForScannerPresentation: @escaping @MainActor () async -> Bool = { true }
    ) {
        self.scanner = scanner
        self.waitForScannerPresentation = waitForScannerPresentation
        self.barcodeIntakeService = barcodeIntakeService
        self.spoolService = spoolService
    }

    var isScannerAvailable: Bool {
        scanner?.isAvailable ?? false
    }

    func configureNFC(scanner: any SpoolScannerProtocol) {
        nfcScanner = scanner
    }

    func scanNFC() {
        guard isViewActive, !isScanning, !hasFinishedNavigation,
              !isAwaitingResultAcknowledgment else { return }
        guard let nfcScanner, nfcScanner.isAvailable else {
            errorMessage = "NFC scanning is not available on this device."
            return
        }
        isScanning = true
        Task {
            defer {
                isScanning = false
                startQueuedExternalScanIfNeeded()
            }
            let result = await nfcScanner.scan()
            guard isViewActive else { return }
            switch result {
            case .printerId:
                errorMessage = Self.notSpoolTagMessage
            case .spoolId(let id):
                await routeToSpoolIfExists(id: id, subtitle: "Spool #\(id)")
            case .newSpoolData(let data):
                pendingOutcome = .newSpool(data)
            case .cancelled:
                break
            case .error(let error):
                errorMessage = error.localizedDescription
            }
        }
    }

    private var isAwaitingResultAcknowledgment: Bool {
        pendingOutcome != nil || pendingSpoolBarcode != nil
            || pendingDeepLinkDestination != nil || errorMessage != nil
            || isResultPresentationActive
    }

    func scan() {
        guard isViewActive, !isScanning, !hasFinishedNavigation,
              !isAwaitingResultAcknowledgment else { return }
        guard let scanner, scanner.isAvailable else {
            consumeQueuedExternalScanRequest()
            errorMessage = "Scanning is not available on this device."
            return
        }

        isScanning = true
        errorMessage = nil

        Task {
            var presentationReady = false
            defer {
                isScanning = false
                if presentationReady { startQueuedExternalScanIfNeeded() }
            }
            presentationReady = await waitForScannerPresentation()
            guard presentationReady, isViewActive, !hasFinishedNavigation,
                  !isAwaitingResultAcknowledgment else { return }
            consumeQueuedExternalScanRequest()
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

    func requestExternalScan(_ requestID: UUID) {
        guard requestID != handledExternalScanRequestID,
              requestID != queuedExternalScanRequestID else { return }
        queuedExternalScanRequestID = requestID
        startQueuedExternalScanIfNeeded()
    }

    private func startQueuedExternalScanIfNeeded() {
        guard queuedExternalScanRequestID != nil else { return }
        scan()
    }

    private func consumeQueuedExternalScanRequest() {
        guard let queuedExternalScanRequestID else { return }
        handledExternalScanRequestID = queuedExternalScanRequestID
        self.queuedExternalScanRequestID = nil
    }

    /// Retire this flow before routing, handing unstarted work back to its owner.
    func finishNavigation() -> UUID? {
        hasFinishedNavigation = true
        isViewActive = false
        pendingDeepLinkDestination = nil
        let requestID = queuedExternalScanRequestID
        consumeQueuedExternalScanRequest()
        return requestID
    }

    static let notSpoolTagMessage = "This NFC tag isn't a spool tag. Scan a spool tag instead."

    /// Dispatches a single scanned code to spool handling. Exposed directly
    /// (in addition to `scan()`) so tests and previews can drive dispatch
    /// without a real camera session.
    func dispatch(_ rawCode: String) async {
        let trimmed = rawCode.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { return }
        errorMessage = nil

        // Structured spool payloads (deep link, URL or JSON forms — e.g.
        // `printfarmer://spool/42`, `https://host/spools/42`,
        // `{"spoolId": 42}`) are unambiguous and must never be registered
        // as a raw barcode. A bare positive-integer code is deliberately NOT
        // treated as structured (see `QRCodeParser.parseStructured`) since
        // genuine EAN/UPC barcodes are also numeric.
        if let url = URL(string: trimmed),
           case .spoolDetail(let id) = DeepLinkHandler.parse(url: url) {
            await routeToSpoolIfExists(id: id, subtitle: "Spool #\(id)")
            return
        }
        if let spoolId = QRCodeParser.parseStructured(trimmed) {
            await routeToSpoolIfExists(id: spoolId, subtitle: "Spool #\(spoolId)")
            return
        }

        guard let barcodeIntakeService else {
            pendingOutcome = .unknownCode(trimmed)
            return
        }

        do {
            let filament = try await barcodeIntakeService.resolveFilament(barcode: trimmed)
            guard isViewActive else { return }
            if let filament {
                recordRecentScan(icon: "cylinder", title: filament.name ?? filament.material ?? "Spool", subtitle: "Spool barcode")
                pendingSpoolBarcode = trimmed
            } else if Self.isSupportedProductBarcode(trimmed) {
                // An unknown product barcode reuses Barcode Intake, which
                // asks the operator to map it to a filament (persisting the
                // GTIN server-side) before importing a spool.
                recordRecentScan(icon: "plus.circle", title: "New spool barcode", subtitle: trimmed)
                pendingSpoolBarcode = trimmed
            } else if let spoolId = QRCodeParser.parse(trimmed) {
                await routeToSpoolIfExists(id: spoolId, subtitle: "Spool #\(spoolId)")
            } else {
                recordRecentScan(icon: "questionmark.circle", title: "Unrecognized", subtitle: trimmed)
                pendingOutcome = .unknownCode(trimmed)
            }
        } catch {
            logger.warning("Spool resolution failed: \(error.localizedDescription)")
            errorMessage = error.localizedDescription
        }
    }

    /// Mirrors the server's `GtinNormalizer` for scanned codes: digits with
    /// optional space/dash separators forming a GTIN-8/12/13/14 with a valid
    /// GS1 mod-10 check digit. Only these values can be persisted as a
    /// filament barcode mapping by `POST /api/spoolman/barcodes`.
    nonisolated static func isSupportedProductBarcode(_ code: String) -> Bool {
        let trimmed = code.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty, trimmed.count <= 64,
              trimmed.allSatisfy({ $0.isASCII && ($0.isNumber || $0 == " " || $0 == "-") }) else {
            return false
        }
        let digits = trimmed.compactMap { $0.wholeNumberValue }
        guard [8, 12, 13, 14].contains(digits.count), let checkDigit = digits.last else { return false }
        var sum = 0
        for (offset, digit) in digits.dropLast().reversed().enumerated() {
            sum += offset.isMultiple(of: 2) ? digit * 3 : digit
        }
        return (10 - sum % 10) % 10 == checkDigit
    }

    /// Confirms a candidate spool ID (from a deep link, a structured
    /// URL/JSON payload, or an unresolved bare-numeric fallback) actually
    /// exists on the currently-connected (active) server before routing
    /// to `.spoolDetail` — such a payload NEVER falls back to raw barcode
    /// registration; it either routes or surfaces an explicit error.
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
        startQueuedExternalScanIfNeeded()
    }

    func resultPresentationDidAppear() {
        isResultPresentationActive = true
    }

    func resultPresentationDidDismiss() {
        isResultPresentationActive = false
        startQueuedExternalScanIfNeeded()
    }

    private func recordRecentScan(icon: String, title: String, subtitle: String) {
        let entry = RecentScan(icon: icon, title: title, subtitle: subtitle, scannedAt: .now)
        recentScans.insert(entry, at: 0)
        if recentScans.count > Self.maxRecentScans {
            recentScans.removeLast(recentScans.count - Self.maxRecentScans)
        }
    }
}
