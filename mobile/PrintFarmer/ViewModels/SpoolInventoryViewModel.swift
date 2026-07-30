import Foundation
import os

enum SpoolStatus: String, CaseIterable {
    case available = "Available"
    case inUse = "In Use"
    case low = "Low"
    case empty = "Empty"
}

@MainActor @Observable
final class SpoolInventoryViewModel {
    var spools: [SpoolmanSpool] = []
    var searchText = ""
    var selectedMaterial: String?
    var selectedStatus: SpoolStatus?
    var showOnlyMissingNFC = false
    var isLoading = false
    var errorMessage: String?
    var isViewActive = true

    // NFC scanning state
    var isScanning = false
    var scanError: String?
    var scannedSpoolData: ScannedSpoolData?
    var showScannedDataSheet = false
    var highlightedSpoolId: Int?

    // Monotonic authority for spool-highlight lifetime. Advanced synchronously
    // inside `setHighlight` and `invalidateHighlightOwnership`; a retained
    // expiry Task captures the generation at arm time and defers to
    // `clearHighlightIfCurrent(generation:spoolId:)` to enforce the guard.
    // #726: correctness must not depend on cancellation observation or on
    // deferred SwiftUI `.onChange` ordering.
    private(set) var highlightGeneration: UInt64 = 0

    @ObservationIgnored private var highlightExpiryTask: Task<Void, Never>?

    /// Production duration for a highlight before its retained expiry task
    /// attempts a guarded clear. Exposed so tests can shorten if needed;
    /// deterministic tests inject `_highlightExpirySleepOverride` instead.
    @ObservationIgnored var highlightExpiryDuration: Duration = .seconds(2)

    /// Test seam. When set, the retained expiry task awaits this closure
    /// (with the captured generation and spool id) instead of `Task.sleep`,
    /// letting tests park the expiry, mutate authority, and release the
    /// parked continuation to prove the identity+generation guard.
    /// Production code never sets this.
    @ObservationIgnored var _highlightExpirySleepOverride: (@Sendable (UInt64, Int) async -> Void)?

    /// Test seam. Exposes the currently retained expiry Task so tests can
    /// deterministically await its completion via `await task?.value` after
    /// releasing a parked continuation — no polling, sleep, or `Task.yield`.
    var _currentHighlightExpiryTask: Task<Void, Never>? { highlightExpiryTask }

    // NFC writing state
    var isWritingNFC = false
    var writeNFCError: String?

    private let logger = Logger(subsystem: "com.printfarmer.ios", category: "SpoolInventory")
    @ObservationIgnored private var spoolService: (any SpoolServiceProtocol)?
    @ObservationIgnored private var nfcScanner: (any SpoolScannerProtocol)?

    func configure(spoolService: any SpoolServiceProtocol) {
        self.spoolService = spoolService
    }

    func configureNFC(scanner: any SpoolScannerProtocol) {
        self.nfcScanner = scanner
    }

    var availableMaterials: [String] {
        let materials = Set(spools.map { $0.material })
        return materials.sorted()
    }

    var filteredSpools: [SpoolmanSpool] {
        var result = spools

        // Apply material filter first
        if let material = selectedMaterial {
            result = result.filter { $0.material == material }
        }

        // Apply status filter
        if let status = selectedStatus {
            result = result.filter { spool in
                switch status {
                case .available:
                    return !spool.inUse && !(spool.archived ?? false)
                case .inUse:
                    return spool.inUse
                case .low:
                    guard let remaining = spool.remainingWeightG,
                          let initial = spool.initialWeightG,
                          initial > 0 else { return false }
                    return (remaining / initial) < 0.2
                case .empty:
                    if let remaining = spool.remainingWeightG {
                        return remaining == 0
                    } else if spool.initialWeightG != nil {
                        return true
                    }
                    return false
                }
            }
        }

        // Apply "No NFC Tag" filter
        if showOnlyMissingNFC {
            result = result.filter { ($0.hasNfcTag ?? false) == false }
        }

        // Then apply search text filter
        guard !searchText.isEmpty else { return result }
        let query = searchText.lowercased()
        return result.filter { spool in
            spool.material.lowercased().contains(query)
            || (spool.filamentName?.lowercased().contains(query) ?? false)
            || (spool.vendor?.lowercased().contains(query) ?? false)
            || spool.name.lowercased().contains(query)
            || (spool.location?.lowercased().contains(query) ?? false)
            || (spool.comment?.lowercased().contains(query) ?? false)
            || spool.colorNameMatches(query)
        }
    }

    var hasActiveSearch: Bool {
        !searchText.isEmpty || selectedMaterial != nil || selectedStatus != nil || showOnlyMissingNFC
    }

    var activeFilterDescription: String {
        var parts: [String] = []
        if let material = selectedMaterial { parts.append("material: \(material)") }
        if let status = selectedStatus { parts.append("status: \(status.rawValue)") }
        if showOnlyMissingNFC { parts.append("missing NFC tag") }
        if !searchText.isEmpty { parts.append("search: \"\(searchText)\"") }
        return "No spools match your current filters (\(parts.joined(separator: ", ")))."
    }

    func clearFilters() {
        selectedMaterial = nil
        selectedStatus = nil
        showOnlyMissingNFC = false
        searchText = ""
    }

    func loadSpools() async {
        guard let spoolService else {
            errorMessage = "Spool service not available"
            return
        }

        isLoading = true
        errorMessage = nil

        do {
            let result = try await spoolService.listSpools(limit: 200, offset: 0)
            spools = result.items
        } catch {
            logger.warning("Failed to load spools: \(error.localizedDescription)")
            errorMessage = error.localizedDescription
        }

        isLoading = false
    }

    // MARK: - NFC Scanning

    func handleNFCScan() {
        guard let nfcScanner, nfcScanner.isAvailable else {
            scanError = "NFC scanning is not available on this device."
            return
        }

        isScanning = true
        scanError = nil

        Task {
            guard isViewActive else { return }
            let result = await nfcScanner.scan()
            guard isViewActive else { return }
            await handleScanResult(result)
            isScanning = false
        }
    }

    func findSpool(byId id: Int) -> SpoolmanSpool? {
        spools.first { $0.id == id }
    }

    /// Synchronously advances highlight authority to a new spool, publishes
    /// the new `highlightedSpoolId`, and arms a retained expiry task guarded
    /// by the freshly captured (generation, spoolId) pair. Cancels any prior
    /// expiry task on a best-effort basis, but correctness does not depend on
    /// cancellation being observed — the guard alone prevents a stale expiry
    /// (or one whose cancellation is dropped) from clearing a newer highlight.
    ///
    /// #726: this must run in a single `@MainActor` state transition so no
    /// window exists between publishing the new highlight and advancing
    /// authority. Callers must not modify `highlightedSpoolId` directly for
    /// production highlight assignments; use this method instead.
    func setHighlight(spoolId: Int) {
        highlightExpiryTask?.cancel()
        highlightGeneration &+= 1
        let capturedGeneration = highlightGeneration
        highlightedSpoolId = spoolId

        let duration = highlightExpiryDuration
        let sleepOverride = _highlightExpirySleepOverride
        highlightExpiryTask = Task { @MainActor [weak self] in
            if let sleepOverride {
                await sleepOverride(capturedGeneration, spoolId)
            } else {
                try? await Task.sleep(for: duration)
            }
            self?.clearHighlightIfCurrent(generation: capturedGeneration, spoolId: spoolId)
        }
    }

    /// Clears the highlight only when both the generation and spool identity
    /// captured by an expiry callback still match the current highlight. This
    /// is the single seam that guarantees a stale (or dropped-cancellation)
    /// expiry cannot erase a newer highlight.
    func clearHighlightIfCurrent(generation: UInt64, spoolId: Int) {
        guard generation == highlightGeneration,
              spoolId == highlightedSpoolId else { return }
        highlightedSpoolId = nil
        highlightExpiryTask = nil
    }

    /// Cancels the retained expiry task and advances the generation so any
    /// already-scheduled or parked expiry becomes a no-op via the guard in
    /// `clearHighlightIfCurrent`. Does not modify the visible
    /// `highlightedSpoolId` — the view's disappearance handler calls this so
    /// an off-screen view cannot later mutate a newer highlight, without
    /// preemptively clearing the current one.
    func invalidateHighlightOwnership() {
        highlightExpiryTask?.cancel()
        highlightExpiryTask = nil
        highlightGeneration &+= 1
    }

    func clearHighlight() {
        invalidateHighlightOwnership()
        highlightedSpoolId = nil
    }

    private func handleScanResult(_ result: SpoolScanResult) async {
        switch result {
        case .spoolId(let id):
            if let existing = findSpool(byId: id) {
                setHighlight(spoolId: existing.id)
            } else {
                // Reload and try again
                await loadSpools()
                if let existing = findSpool(byId: id) {
                    setHighlight(spoolId: existing.id)
                } else {
                    scanError = "Spool #\(id) not found in inventory."
                }
            }

        case .newSpoolData(let data):
            scannedSpoolData = data
            showScannedDataSheet = true

        case .cancelled:
            break

        case .error(let error):
            scanError = error.localizedDescription
        }
    }

    func deleteSpool(_ spool: SpoolmanSpool) async {
        guard let spoolService else { return }

        do {
            try await spoolService.deleteSpool(id: spool.id)
            spools.removeAll { $0.id == spool.id }
        } catch {
            logger.warning("Failed to delete spool: \(error.localizedDescription)")
            errorMessage = error.localizedDescription
        }
    }

    // MARK: - NFC Tag Writing

    /// Looks up filament data used by OpenPrintTag/OpenTag3D so UI previews can show real values.
    func matchingFilamentForTagPreview(for spool: SpoolmanSpool) async -> SpoolmanFilament? {
        let format = selectedTagFormat()
        return await matchingFilament(for: spool, format: format)
    }

    /// Writes a dual-record NFC tag for the given spool using NFCService.
    func writeNFCTag(for spool: SpoolmanSpool) async -> Bool {
        #if canImport(UIKit)
        guard let nfcService = nfcScanner as? NFCService else {
            writeNFCError = "NFC writing is not available on this device."
            return false
        }

        isWritingNFC = true
        writeNFCError = nil

        let format = selectedTagFormat()

        do {
            let filament = await matchingFilament(for: spool, format: format)

            try await nfcService.writeSpoolTag(spool: spool, filament: filament, format: format)
            // Persist NFC tag association to backend
            if let spoolService {
                _ = try await spoolService.updateSpool(
                    id: spool.id,
                    SpoolmanSpoolRequest(hasNfcTag: true)
                )
            }
            markSpoolNFCWritten(id: spool.id)
            isWritingNFC = false
            return true
        } catch {
            if let scanError = error as? SpoolScanError, case .cancelled = scanError {
                // User cancelled — not an error
            } else {
                writeNFCError = error.localizedDescription
            }
            isWritingNFC = false
            return false
        }
        #else
        writeNFCError = "NFC is not available on this platform."
        return false
        #endif
    }

    private func selectedTagFormat() -> NFCTagFormat {
        let formatRaw = UserDefaults.standard.string(forKey: "nfcTagFormat") ?? NFCTagFormat.openPrintTag.rawValue
        return NFCTagFormat(rawValue: formatRaw) ?? .openSpool
    }

    private func matchingFilament(for spool: SpoolmanSpool, format: NFCTagFormat) async -> SpoolmanFilament? {
        guard (format == .openTag3D || format == .openPrintTag), let spoolService else {
            return nil
        }

        guard let filamentId = spool.filamentId else {
            logger.warning("Spool \(spool.id) (\(spool.name)) missing filamentId from backend — cannot lookup temps/diameter. Ensure SpoolmanSpoolDto includes filamentId field and is non-null when spool has a filament assigned.")
            return nil
        }

        do {
            let filaments = try await spoolService.listFilaments()
            return filaments.first { $0.id == filamentId }
        } catch {
            logger.warning("Failed to load filaments for NFC payload: \(error.localizedDescription)")
            return nil
        }
    }

    /// Updates local state after successful NFC write.
    private func markSpoolNFCWritten(id: Int) {
        guard let index = spools.firstIndex(where: { $0.id == id }) else { return }
        let old = spools[index]
        spools[index] = SpoolmanSpool(
            id: old.id, name: old.name, material: old.material,
            colorHex: old.colorHex, inUse: old.inUse,
            filamentName: old.filamentName, vendor: old.vendor,
            registeredAt: old.registeredAt, firstUsedAt: old.firstUsedAt,
            lastUsedAt: old.lastUsedAt,
            remainingWeightG: old.remainingWeightG, initialWeightG: old.initialWeightG,
            usedWeightG: old.usedWeightG, spoolWeightG: old.spoolWeightG,
            remainingLengthMm: old.remainingLengthMm, usedLengthMm: old.usedLengthMm,
            location: old.location, lotNumber: old.lotNumber,
            archived: old.archived, price: old.price, comment: old.comment,
            hasNfcTag: true,
            usedPercent: old.usedPercent, remainingPercent: old.remainingPercent
        )
    }
}
