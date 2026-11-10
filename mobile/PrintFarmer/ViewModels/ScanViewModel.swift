import Foundation
import os

/// Drives the shared spool scanner. The mobile app only manages spools, so a
/// scan resolves exclusively to a spool: a structured spool QR/deep link or a
/// bare spool ID opens that spool, and a filament product barcode (GTIN) is
/// handed to the existing Barcode Intake flow. Known product barcodes import a
/// spool; unknown valid GTINs open the same intake flow so the operator can map
/// the barcode to a filament (persisted server-side) before a spool is created.
/// Any other code reaches the Unrecognized result, which still offers manual
/// spool creation, so a scan never dead-ends. Nothing is created merely by
/// scanning.
@MainActor @Observable
final class ScanViewModel {
    /// A resolved scan outcome that the view presents as a sheet. Existing
    /// spools navigate via `pendingDeepLinkDestination` instead.
    enum ScanOutcome: Identifiable {
        case unknownCode(String)
        case newSpool(ScannedSpoolData)
        /// Manual spool creation offered from an unrecognized non-GTIN code.
        /// The code is never persisted: only valid GTINs can be mapped.
        case addSpoolManually(String)
        /// A structured spool link or ID whose spool is absent from the
        /// active server. It offers manual creation only, never GTIN intake.
        case missingSpool(id: Int, code: String)

        var id: String {
            switch self {
            case .unknownCode(let code): "unknown-\(code)"
            case .missingSpool(let id, _): "missing-spool-\(id)"
            case .newSpool: "new-spool"
            case .addSpoolManually(let code): "manual-\(code)"
            }
        }
    }

    /// A follow-up chosen from the Unrecognized result. It is applied only
    /// after that sheet finishes dismissing, so two sheets never swap in place.
    private enum DeferredAddSpool {
        case intake(String)
        case manual(String)
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
    private var deferredAddSpool: DeferredAddSpool?
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
                await routeToSpoolIfExists(id: id, code: "Spool #\(id)", subtitle: "Spool #\(id)")
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
            || isResultPresentationActive || deferredAddSpool != nil
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
        if let url = URL(string: trimmed), let destination = DeepLinkHandler.parse(url: url) {
            if case .spoolDetail(let id) = destination {
                await routeToSpoolIfExists(id: id, code: trimmed, subtitle: "Spool #\(id)")
            } else {
                // Recognized non-spool links (e.g. printer UUIDs) must never
                // reach barcode resolution: the server's GTIN normalizer
                // strips non-digits, so their digits could match a product.
                recordRecentScan(icon: "questionmark.circle", title: "Unrecognized", subtitle: trimmed)
                pendingOutcome = .unknownCode(trimmed)
            }
            return
        }
        if let spoolId = QRCodeParser.parseStructured(trimmed) {
            await routeToSpoolIfExists(id: spoolId, code: trimmed, subtitle: "Spool #\(spoolId)")
            return
        }

        // A code that is not a GTIN (Code 128/39 text, a non-spool QR, a
        // short number) is never sent for filament resolution: the server's
        // GTIN normalizer strips non-digits and could match the wrong
        // product. A bare number may still be an existing spool ID. Other
        // codes (e.g. an Amazon FNSKU) go to Barcode Intake, which looks up
        // the exact-match remembered-code alias stored separately from GTINs.
        // Only codes that can't be remembered reach the Unrecognized result.
        guard Self.isSupportedProductBarcode(trimmed) else {
            if let spoolId = QRCodeParser.parse(trimmed) {
                guard let exists = await existingSpoolLookup(id: spoolId) else { return }
                if exists {
                    recordRecentScan(icon: "cylinder", title: "Spool", subtitle: "Spool #\(spoolId)")
                    pendingDeepLinkDestination = .spoolDetail(id: spoolId)
                    return
                }
            }
            if barcodeIntakeService != nil, BarcodeIntakeViewModel.isRememberableCode(trimmed) {
                guard isViewActive else { return }
                recordRecentScan(icon: "plus.circle", title: "Spool code", subtitle: trimmed)
                pendingSpoolBarcode = trimmed
                return
            }
            recordRecentScan(icon: "questionmark.circle", title: "Unrecognized", subtitle: trimmed)
            pendingOutcome = .unknownCode(trimmed)
            return
        }

        guard let barcodeIntakeService else {
            pendingOutcome = .unknownCode(trimmed)
            return
        }

        // A numeric spool QR can also be a valid GTIN. An existing spool with
        // that ID keeps precedence over Barcode Intake, mapped or not, so it
        // is checked before filament resolution.
        if let spoolId = QRCodeParser.parse(trimmed) {
            guard let exists = await existingSpoolLookup(id: spoolId) else { return }
            if exists {
                recordRecentScan(icon: "cylinder", title: "Spool", subtitle: "Spool #\(spoolId)")
                pendingDeepLinkDestination = .spoolDetail(id: spoolId)
                return
            }
        }

        do {
            let filament = try await barcodeIntakeService.resolveFilament(barcode: trimmed)
            guard isViewActive else { return }
            if let filament {
                recordRecentScan(icon: "cylinder", title: filament.name ?? filament.material ?? "Spool", subtitle: "Spool barcode")
                pendingSpoolBarcode = trimmed
                return
            }
            // An unknown product barcode reuses Barcode Intake, which
            // asks the operator to map it to a filament (persisting the
            // GTIN server-side) before importing a spool.
            recordRecentScan(icon: "plus.circle", title: "New spool barcode", subtitle: trimmed)
            pendingSpoolBarcode = trimmed
        } catch {
            logger.warning("Spool resolution failed: \(error.localizedDescription)")
            errorMessage = error.localizedDescription
        }
    }

    /// A stricter subset of the server's `GtinNormalizer`: digits with only
    /// optional space/dash separators forming a GTIN-8/12/13/14 with a valid
    /// GS1 mod-10 check digit. The server strips any non-digit, so this
    /// narrower shape keeps arbitrary text from being treated as a product
    /// barcode. Only such values are offered for persistence through
    /// `POST /api/spoolman/barcodes`.
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

    /// Confirms a candidate spool ID from a deep link or a structured
    /// URL/JSON payload actually exists on the currently-connected (active)
    /// server before routing to `.spoolDetail` — such a payload NEVER falls
    /// back to raw barcode registration; it either routes or surfaces an
    /// explicit error.
    private func routeToSpoolIfExists(id: Int, code: String, subtitle: String) async {
        guard let exists = await existingSpoolLookup(id: id) else { return }
        if exists {
            recordRecentScan(icon: "cylinder", title: "Spool", subtitle: subtitle)
            pendingDeepLinkDestination = .spoolDetail(id: id)
        } else {
            recordRecentScan(icon: "questionmark.circle", title: "Spool not found", subtitle: subtitle)
            pendingOutcome = .missingSpool(id: id, code: code)
        }
    }

    /// Returns whether spool `id` exists on the active server, or nil after
    /// surfacing an error (or when the view retired) so callers fail closed.
    private func existingSpoolLookup(id: Int) async -> Bool? {
        guard let spoolService else {
            errorMessage = "Spool service not available"
            return nil
        }
        do {
            let exists = try await spoolService.spoolExists(id: id)
            guard isViewActive else { return nil }
            return exists
        } catch {
            guard isViewActive else { return nil }
            logger.warning("Spool existence lookup failed: \(error.localizedDescription)")
            errorMessage = error.localizedDescription
            return nil
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
        if let deferred = deferredAddSpool {
            deferredAddSpool = nil
            guard isViewActive, !hasFinishedNavigation else { return }
            switch deferred {
            case .intake(let barcode): pendingSpoolBarcode = barcode
            case .manual(let code): pendingOutcome = .addSpoolManually(code)
            }
            return
        }
        startQueuedExternalScanIfNeeded()
    }

    /// Called from the Unrecognized result so a scan never dead-ends. A
    /// valid GTIN (e.g. a normalized UPC-E) uses Barcode Intake, which can
    /// map it; any other code opens manual creation and is never written
    /// to the GTIN mapping. Nothing is created until the user saves.
    func addSpool(fromUnrecognized code: String) {
        switch pendingOutcome {
        case .unknownCode(let pending) where pending == code:
            // Recognized non-spool links (e.g. printer links) are never
            // remembered as filament codes; they use manual Add Spool.
            let isAppLink = URL(string: code).flatMap(DeepLinkHandler.parse(url:)) != nil
            let usesIntake = Self.isSupportedProductBarcode(code)
                || (!isAppLink && BarcodeIntakeViewModel.isRememberableCode(code))
            deferredAddSpool = usesIntake && barcodeIntakeService != nil
                ? .intake(code)
                : .manual(code)
        case .missingSpool(_, let pending) where pending == code:
            // A structured spool payload is never a product barcode.
            deferredAddSpool = .manual(code)
        default:
            return
        }
        pendingOutcome = nil
    }

    private func recordRecentScan(icon: String, title: String, subtitle: String) {
        let entry = RecentScan(icon: icon, title: title, subtitle: subtitle, scannedAt: .now)
        recentScans.insert(entry, at: 0)
        if recentScans.count > Self.maxRecentScans {
            recentScans.removeLast(recentScans.count - Self.maxRecentScans)
        }
    }
}
