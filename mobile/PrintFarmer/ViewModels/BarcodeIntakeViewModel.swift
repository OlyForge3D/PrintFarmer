import Foundation
import os

@MainActor @Observable
final class FilamentCatalogSelection {
    enum Option: Hashable {
        case unspecified
        case value(String)

        var label: String {
            switch self {
            case .unspecified: return "Not specified"
            case .value(let value): return value
            }
        }

        init(_ value: String?) {
            let trimmed = value?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
            self = trimmed.isEmpty ? .unspecified : .value(trimmed)
        }
    }

    var filaments: [SpoolmanFilament] = [] {
        didSet {
            material = nil
            vendor = nil
            filamentId = nil
        }
    }
    var material: Option? {
        didSet {
            if material != oldValue { vendor = nil; filamentId = nil }
        }
    }
    var vendor: Option? {
        didSet {
            if vendor != oldValue { filamentId = nil }
        }
    }
    var filamentId: Int?

    var materials: [Option] {
        sortedOptions(filaments.map { Option($0.material) })
    }

    var vendors: [Option] {
        guard let material else { return [] }
        return sortedOptions(filaments.filter { Option($0.material) == material }.map { Option($0.vendor) })
    }

    var matchingFilaments: [SpoolmanFilament] {
        guard let material, let vendor else { return [] }
        return filaments.filter { Option($0.material) == material && Option($0.vendor) == vendor }
            .sorted {
                let comparison = Option($0.name).label.localizedCaseInsensitiveCompare(Option($1.name).label)
                return comparison == .orderedSame ? $0.id < $1.id : comparison == .orderedAscending
            }
    }

    var selectedFilament: SpoolmanFilament? {
        matchingFilaments.first { $0.id == filamentId }
    }

    private func sortedOptions(_ options: [Option]) -> [Option] {
        Array(Set(options)).sorted {
            let comparison = $0.label.localizedCaseInsensitiveCompare($1.label)
            if comparison != .orderedSame { return comparison == .orderedAscending }
            return String(describing: $0) < String(describing: $1)
        }
    }
}

@MainActor @Observable
final class BarcodeIntakeViewModel {
    var isScanning = false
    var lastScannedBarcode: String?
    var lastResolvedFilament: SpoolmanFilament?
    var importedThisSession: [SpoolmanSpool] = []
    var pendingUnknownBarcode: String?
    /// A remembered non-GTIN code whose filament is known; the user confirms
    /// before another spool of that filament is created.
    var pendingAliasMatch: AliasMatch?
    /// Set when a lookup or write failed for a reason other than a genuine
    /// miss; the view offers Retry instead of creating anything.
    private(set) var retryAction: RetryAction?
    /// Set when spool creation failed in a way that may still have created the
    /// spool (timeout, lost connection, 5xx, unreadable response). Retry is
    /// withheld so the user can't add a duplicate spool.
    private(set) var spoolOutcomeUncertain = false
    var isBusy = false
    var errorMessage: String?
    var isViewActive = true

    struct AliasMatch: Equatable {
        let code: String
        let filament: SpoolmanFilament

        static func == (lhs: AliasMatch, rhs: AliasMatch) -> Bool {
            lhs.code == rhs.code && lhs.filament.id == rhs.filament.id
        }
    }

    enum RetryAction: Equatable {
        case lookup(String)
        case remember(code: String, filamentId: Int)
        case addAliasSpool(code: String, filamentId: Int)
    }

    static let notProductBarcodeMessage =
        "That code can't be used for spool intake. Use Add Spool to create this spool manually."
    static let aliasConflictMessage =
        "This code is already remembered for a different filament. Nothing was added."
    static let spoolOutcomeUncertainMessage =
        "The spool may have been added. Check your spools before adding it again."

    /// True only when the request definitely did not create anything on the
    /// server, so it is safe to retry a non-idempotent spool creation.
    /// A generic client error is excluded: the server reports an upstream
    /// Spoolman timeout or failure on spool creation as HTTP 400, after which
    /// the spool may already exist.
    nonisolated static func isDefiniteFailure(_ error: Error) -> Bool {
        guard let networkError = error as? NetworkError else { return false }
        switch networkError {
        case .invalidURL, .unauthorized, .forbidden, .notFound, .featureDisabled,
             .methodNotAllowed, .conflict, .partsInventoryConflict, .preconditionFailed,
             .preconditionRequired, .serverUnreachable, .authFailed,
             .insecureTransportBlocked, .certificateChanged, .certificateNotTrusted:
            return true
        default:
            return false
        }
    }

    /// Maximum length the server accepts for a remembered code.
    nonisolated static let maxRememberedCodeLength = 128

    /// True for a non-GTIN code the server can remember as an exact-match
    /// alias (e.g. an Amazon FNSKU such as `X0022Z40I1`). Mirrors the server's
    /// `ScanCodeAliasValidator`: GTINs use the separate Spoolman mapping, and
    /// digit-only values are never aliases.
    nonisolated static func isRememberableCode(_ value: String) -> Bool {
        let trimmed = value.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty,
              // The server measures UTF-16 code units, not grapheme clusters.
              trimmed.utf16.count <= maxRememberedCodeLength,
              // Digit-only values are spool IDs or (possibly mistyped) GTINs;
              // an alias always contains another character, so it can never
              // be confused with GTIN digit normalization.
              !isNumericShaped(trimmed) else { return false }
        return !trimmed.unicodeScalars.contains {
            CharacterSet.controlCharacters.contains($0) || $0.properties.generalCategory == .format
        }
    }

    /// A rememberable code that is not a PrintFarmer app link; internal
    /// printer/job QR links must never be remembered as filament codes.
    static func isRememberableScan(_ value: String) -> Bool {
        let trimmed = value.trimmingCharacters(in: .whitespacesAndNewlines)
        guard isRememberableCode(trimmed) else { return false }
        return URL(string: trimmed).flatMap(DeepLinkHandler.parse(url:)) == nil
    }

    nonisolated static func isNumericShaped(_ value: String) -> Bool {
        value.allSatisfy { ("0"..."9").contains($0) || $0 == " " || $0 == "-" }
    }

    /// True when `code` uses the remembered-code alias path rather than GTIN.
    var pendingCodeIsAlias: Bool {
        guard let pendingUnknownBarcode else { return false }
        return !ScanViewModel.isSupportedProductBarcode(pendingUnknownBarcode)
    }

    var importedCount: Int { importedThisSession.count }
    /// The resolved filament's GTIN, shown only when the scanned code itself was a GTIN;
    /// a remembered non-GTIN code (e.g. an FNSKU) must not be presented as a GTIN.
    var lastResolvedBarcode: String? {
        guard let lastScannedBarcode, ScanViewModel.isSupportedProductBarcode(lastScannedBarcode) else { return nil }
        return lastResolvedFilament?.gtin
    }
    var lastResolvedVendorSKU: String? { lastResolvedFilament?.articleNumber }

    private let logger = Logger(subsystem: "com.printfarmer.ios", category: "BarcodeIntake")
    private var barcodeService: (any BarcodeIntakeServiceProtocol)?
    private var scanner: (any BarcodeScannerProtocol)?

    func configure(barcodeService: any BarcodeIntakeServiceProtocol, scanner: (any BarcodeScannerProtocol)? = nil) {
        self.barcodeService = barcodeService
        self.scanner = scanner
    }

    func scanNext() {
        guard let scanner, scanner.isAvailable else {
            errorMessage = "Barcode scanning is not available on this device."
            return
        }

        isScanning = true
        errorMessage = nil

        Task {
            defer { isScanning = false }
            let result = await scanner.scanBarcode()
            guard isViewActive else { return }
            switch result {
            case .barcode(let barcode):
                await handleScannedBarcode(barcode)
            case .cancelled:
                break
            case .error(let error):
                errorMessage = error.localizedDescription
            }
        }
    }

    func handleScannedBarcode(_ barcode: String) async {
        let trimmed = barcode.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { return }
        guard let barcodeService else {
            errorMessage = "Barcode intake service not available"
            return
        }

        lastScannedBarcode = trimmed
        lastResolvedFilament = nil
        pendingUnknownBarcode = nil
        pendingAliasMatch = nil
        retryAction = nil
        errorMessage = nil

        // The server's GTIN normalizer strips non-digits, so a non-GTIN value
        // (Code 128/39 text, a non-spool QR) must never be resolved or mapped
        // as a GTIN. It uses the separate exact-match alias store instead.
        guard ScanViewModel.isSupportedProductBarcode(trimmed) else {
            if Self.isRememberableScan(trimmed) {
                await lookupAlias(trimmed, service: barcodeService)
            } else {
                errorMessage = Self.notProductBarcodeMessage
            }
            return
        }

        isBusy = true

        do {
            let filament = try await barcodeService.resolveFilament(barcode: trimmed)
            lastResolvedFilament = filament
            if filament != nil {
                let spool = try await barcodeService.importSpool(barcode: trimmed, fields: SpoolImportFields())
                importedThisSession.insert(spool, at: 0)
                pendingUnknownBarcode = nil
            } else {
                pendingUnknownBarcode = trimmed
            }
        } catch {
            logger.warning("Barcode intake failed: \(error.localizedDescription)")
            errorMessage = error.localizedDescription
        }

        isBusy = false
    }

    @discardableResult
    func importUnknownBarcode(with filament: SpoolmanFilament) async -> Bool {
        await importUnknownBarcode(filamentId: filament.id)
    }

    @discardableResult
    func importUnknownBarcode(filamentId: Int) async -> Bool {
        guard let barcode = pendingUnknownBarcode else { return false }
        if Self.isRememberableScan(barcode) {
            return await rememberAndAddSpool(code: barcode, filamentId: filamentId)
        }
        guard ScanViewModel.isSupportedProductBarcode(barcode) else {
            pendingUnknownBarcode = nil
            errorMessage = Self.notProductBarcodeMessage
            return false
        }
        guard let barcodeService else {
            errorMessage = "Barcode intake service not available"
            return false
        }

        isBusy = true
        errorMessage = nil

        do {
            lastResolvedFilament = try await barcodeService.saveMapping(barcode: barcode, filamentId: filamentId)
            let spool = try await barcodeService.importSpool(barcode: barcode, fields: SpoolImportFields())
            importedThisSession.insert(spool, at: 0)
            pendingUnknownBarcode = nil
            isBusy = false
            return true
        } catch {
            logger.warning("Unknown barcode resolution failed: \(error.localizedDescription)")
            errorMessage = error.localizedDescription
        }

        isBusy = false
        return false
    }

    /// Adds another spool of the remembered filament after the user confirms.
    @discardableResult
    func confirmAliasMatch() async -> Bool {
        guard !isBusy, let match = pendingAliasMatch, let barcodeService else { return false }
        isBusy = true
        errorMessage = nil
        retryAction = nil
        defer { isBusy = false }
        do {
            let spool = try await barcodeService.createSpool(filamentId: match.filament.id)
            importedThisSession.insert(spool, at: 0)
            lastResolvedFilament = match.filament
            pendingAliasMatch = nil
            return true
        } catch {
            logger.warning("Remembered-code spool creation failed: \(error.localizedDescription)")
            if Self.isDefiniteFailure(error) {
                fail(error, retry: .addAliasSpool(code: match.code, filamentId: match.filament.id))
            } else {
                pendingAliasMatch = nil
                failUncertain()
            }
            return false
        }
    }

    /// Retries only spool creation; the remembered code is already saved.
    @discardableResult
    private func addAliasSpool(code: String, filamentId: Int) async -> Bool {
        guard !isBusy else { return false }
        guard let barcodeService else {
            errorMessage = "Barcode intake service not available"
            return false
        }
        isBusy = true
        defer { isBusy = false }
        do {
            let spool = try await barcodeService.createSpool(filamentId: filamentId)
            importedThisSession.insert(spool, at: 0)
            if let match = pendingAliasMatch, match.filament.id == filamentId {
                lastResolvedFilament = match.filament
            }
            pendingUnknownBarcode = nil
            pendingAliasMatch = nil
            return true
        } catch {
            logger.warning("Remembered-code spool retry failed: \(error.localizedDescription)")
            if Self.isDefiniteFailure(error) {
                fail(error, retry: .addAliasSpool(code: code, filamentId: filamentId))
            } else {
                pendingAliasMatch = nil
                failUncertain()
            }
            return false
        }
    }

    func cancelAliasMatch() {
        pendingAliasMatch = nil
        errorMessage = nil
        retryAction = nil
    }

    /// Re-runs the operation that failed. Saving an alias is idempotent on the
    /// server, so retrying after a failed spool creation never duplicates it.
    func retry() async {
        guard let action = retryAction else { return }
        retryAction = nil
        errorMessage = nil
        switch action {
        case .lookup(let code):
            await handleScannedBarcode(code)
        case .remember(let code, let filamentId):
            pendingUnknownBarcode = code
            await rememberAndAddSpool(code: code, filamentId: filamentId)
        case .addAliasSpool(let code, let filamentId):
            await addAliasSpool(code: code, filamentId: filamentId)
        }
    }

    func skipUnknownBarcode() {
        pendingUnknownBarcode = nil
        errorMessage = nil
        retryAction = nil
        spoolOutcomeUncertain = false
    }

    func clearError() {
        errorMessage = nil
        retryAction = nil
        spoolOutcomeUncertain = false
    }

    private func lookupAlias(_ code: String, service: any BarcodeIntakeServiceProtocol) async {
        isBusy = true
        defer { isBusy = false }
        do {
            let lookup = try await service.lookupCodeAlias(code: code)
            if let filament = lookup.filament {
                lastResolvedFilament = filament
                pendingAliasMatch = AliasMatch(code: code, filament: filament)
            } else {
                // Genuine miss (or the remembered filament was deleted):
                // let the user pick or create the filament to remember.
                pendingUnknownBarcode = code
            }
        } catch {
            logger.warning("Remembered-code lookup failed: \(error.localizedDescription)")
            fail(error, retry: .lookup(code))
        }
    }

    @discardableResult
    private func rememberAndAddSpool(code: String, filamentId: Int) async -> Bool {
        guard let barcodeService else {
            errorMessage = "Barcode intake service not available"
            return false
        }
        isBusy = true
        errorMessage = nil
        retryAction = nil
        defer { isBusy = false }

        let alias: ScanCodeAlias
        do {
            alias = try await barcodeService.saveCodeAlias(code: code, filamentId: filamentId)
        } catch NetworkError.conflict {
            pendingUnknownBarcode = nil
            errorMessage = Self.aliasConflictMessage
            return false
        } catch {
            logger.warning("Remembering code failed: \(error.localizedDescription)")
            fail(error, retry: .remember(code: code, filamentId: filamentId))
            return false
        }

        do {
            let spool = try await barcodeService.createSpool(filamentId: alias.filamentId)
            importedThisSession.insert(spool, at: 0)
            lastResolvedFilament = alias.filament
            pendingUnknownBarcode = nil
            return true
        } catch {
            logger.warning("Spool creation after remembering code failed: \(error.localizedDescription)")
            if Self.isDefiniteFailure(error) {
                fail(error, retry: .addAliasSpool(code: code, filamentId: alias.filamentId))
            } else {
                failUncertain()
            }
            return false
        }
    }

    private func fail(_ error: Error, retry: RetryAction) {
        errorMessage = error.localizedDescription
        retryAction = retry
        spoolOutcomeUncertain = false
    }

    private func failUncertain() {
        errorMessage = Self.spoolOutcomeUncertainMessage
        retryAction = nil
        spoolOutcomeUncertain = true
    }
}
