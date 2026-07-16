import Foundation
import os

/// A single confirmable SKU/quantity row on the harvest sheet. Prefilled
/// from `GET /api/parts-inventory/mappings` where resolvable; operators can
/// adjust quantity or add rows manually when no mapping exists.
struct HarvestOutputDraft: Identifiable, Equatable {
    let id = UUID()
    var sku: String
    var name: String?
    var quantity: Int
    /// Per-SKU destination bin override. Only sent when
    /// `HarvestViewModel.usePerOutputBins` is enabled — otherwise every
    /// output uses the shared `binCode`.
    var binCodeOverride: String = ""
    /// `true` for rows added via `addManualOutputRow()` (no server-resolved
    /// mapping backs them), which drives the SKU-picker UI in
    /// `HarvestSheetView` — auto-resolved rows show a static label.
    var isManuallyAdded: Bool = false
}

/// Lightweight, `id`-free snapshot of a resolved output row used to detect
/// whether the operator has manually edited the server-resolved mapping
/// (see `HarvestViewModel.hasManualOutputEdits`). Deliberately excludes
/// `HarvestOutputDraft.id` (a fresh random `UUID` per instance would always
/// compare unequal) and avoids keying a `Dictionary` by SKU (which would
/// trap on duplicate SKUs while the operator is mid-edit).
private struct OutputSnapshot: Equatable {
    let sku: String
    let quantity: Int
}

/// Drives the F9 (#714) harvest sheet: confirm quantity (prefilled from the
/// job's output mapping where resolvable), scan/select a destination bin,
/// submit the harvest, and surface Dallas's adjudicated `wrongBin` /
/// `partMappingRequired` conflicts with their exact required detail.
@MainActor @Observable
final class HarvestViewModel {
    let job: PrintJob

    var outputs: [HarvestOutputDraft] = []
    var availableParts: [PartInventoryResponse] = []
    /// Loaded in `loadContext()` for the H3 bin scan/select affordance in
    /// `HarvestSheetView` (offline/no-scanner picker fallback).
    var availableBins: [BinResponse] = []
    var sharedBinCode: String = ""
    var usePerOutputBins = false
    /// Server-resolved baseline captured at the end of `loadContext()`.
    /// Compared against the live `outputs` array (via `hasManualOutputEdits`)
    /// to decide whether `submit()` must send explicit outputs + a required
    /// override reason, or may send `outputs: nil` and let the server
    /// resolve the mapping itself (see PartHarvestService.ResolveOutputsAsync).
    private var resolvedBaseline: [OutputSnapshot] = []

    var isLoadingContext = false
    var isSubmitting = false
    var errorMessage: String?
    var result: HarvestJobResponse?

    /// Populated when the harvest is blocked by a `wrongBin` 409 —
    /// mismatches must be shown verbatim (SKU, expected bin, scanned bin)
    /// per Dallas's adjudication, never a synthesized message.
    var wrongBinConflict: PartsInventoryConflict?
    /// Populated when the harvest is blocked by a `partMappingRequired`
    /// 409 — the operator must either configure a mapping (web-only, out
    /// of scope here) or add an explicit SKU output below and resubmit.
    var mappingRequiredConflict: PartsInventoryConflict?
    var overrideReason: String = ""

    /// Preset when opened via the bin-scan shortcut (#714 frozen scope) so
    /// the operator doesn't have to re-scan the bin they just scanned.
    private let presetBinCode: String?
    private let logger = Logger(subsystem: "com.printfarmer.ios", category: "Harvest")

    init(job: PrintJob, presetBinCode: String? = nil) {
        self.job = job
        self.presetBinCode = presetBinCode
        self.sharedBinCode = presetBinCode ?? ""
    }

    var canSubmit: Bool {
        guard !isSubmitting && result == nil else { return false }
        guard !hasInvalidOutputEdits else { return false }
        if hasManualOutputEdits {
            return isReasonValid(overrideReason)
        }
        return true
    }

    /// `true` when the live `outputs` array differs from the server-resolved
    /// `resolvedBaseline` captured in `loadContext()` — i.e. the operator has
    /// added, removed, or edited a row rather than accepting the auto
    /// resolution. Manually-added rows (no baseline counterpart at all)
    /// always count as an edit.
    var hasManualOutputEdits: Bool {
        let live = outputs.map { OutputSnapshot(sku: $0.sku, quantity: $0.quantity) }
        return live != resolvedBaseline
    }

    /// `true` when the operator's edit leaves the output set in a state that
    /// must never be submitted — per Dallas's Dispute A adjudication this is
    /// an explicitly invalid edit, not merely "unedited": Submit must be
    /// disabled and the request must never silently fall back to
    /// `outputs: nil` (full server re-resolution) or a partial baseline.
    /// Covers: deleting some/all resolved baseline SKUs, a blank/duplicate/
    /// unknown-or-inactive SKU, an out-of-range quantity, and — in
    /// per-output-bin mode — any row missing a registered active bin (this
    /// last check applies whenever `usePerOutputBins` is on, independent of
    /// `hasManualOutputEdits`, since assigning per-output bins doesn't touch
    /// SKU/quantity and so wouldn't otherwise be detected as an "edit").
    var hasInvalidOutputEdits: Bool {
        if usePerOutputBins && !isPerOutputBinAssignmentComplete { return true }

        guard hasManualOutputEdits else { return false }
        if outputs.isEmpty { return true }
        if !isBaselineMembershipComplete { return true }

        let normalizedSkus = outputs.map { Self.normalizeIdentity($0.sku) }
        if normalizedSkus.contains(where: \.isEmpty) { return true }
        if Set(normalizedSkus).count != normalizedSkus.count { return true }
        if !normalizedSkus.allSatisfy(isKnownActivePartSku) { return true }
        if !outputs.allSatisfy({ (1...10000).contains($0.quantity) }) { return true }

        return false
    }

    /// When per-output bins are enabled, every output row must carry a
    /// nonblank, registered, active bin code — never silently compacted
    /// away as a blank/unregistered row. Vacuously complete when there are
    /// no output rows yet to assign bins to.
    private var isPerOutputBinAssignmentComplete: Bool {
        guard !outputs.isEmpty else { return true }
        let normalizedBins = outputs.map { Self.normalizeIdentity($0.binCodeOverride) }
        guard normalizedBins.allSatisfy({ !$0.isEmpty }) else { return false }
        return normalizedBins.allSatisfy(isKnownActiveBinCode)
    }

    /// When a server-resolved baseline exists, every baseline SKU must still
    /// be present among the live outputs — quantity edits are fine, but the
    /// operator cannot silently drop a subset (or all) of the resolved
    /// mapping. Vacuously true when there was no resolved baseline (a
    /// manually-built output set from scratch has no membership to preserve).
    private var isBaselineMembershipComplete: Bool {
        guard !resolvedBaseline.isEmpty else { return true }
        let currentSkus = Set(outputs.map { Self.normalizeIdentity($0.sku) })
        return resolvedBaseline.allSatisfy { currentSkus.contains(Self.normalizeIdentity($0.sku)) }
    }

    /// Active parts loaded by `loadContext()` (`listParts()` defaults to
    /// `includeInactive: false`), so membership here already implies active.
    private func isKnownActivePartSku(_ normalizedSku: String) -> Bool {
        availableParts.contains { Self.normalizeIdentity($0.sku) == normalizedSku }
    }

    /// Active bins loaded by `loadContext()` (`listBins()` defaults to
    /// `includeInactive: false`), so membership here already implies active
    /// and registered.
    private func isKnownActiveBinCode(_ normalizedBinCode: String) -> Bool {
        availableBins.contains { Self.normalizeIdentity($0.code) == normalizedBinCode }
    }

    /// Mirrors the server's canonical identity normalization
    /// (`PartInventoryIdentity.NormalizeSku`/`NormalizeBinCode` — NFKC fold,
    /// trim, then uppercase) so duplicate/membership/lookup checks agree
    /// with the server's width- and case-insensitive comparison.
    private static func normalizeIdentity(_ value: String) -> String {
        value.trimmingCharacters(in: .whitespacesAndNewlines)
            .precomposedStringWithCompatibilityMapping
            .uppercased()
    }

    private func isReasonValid(_ reason: String) -> Bool {
        let trimmed = reason.trimmingCharacters(in: .whitespacesAndNewlines)
        return !trimmed.isEmpty && trimmed.count <= 1000
    }

    var hasWrongBinConflict: Bool { wrongBinConflict != nil }
    var hasMappingRequiredConflict: Bool { mappingRequiredConflict != nil }

    var canConfirmOverride: Bool {
        isReasonValid(overrideReason)
    }

    func addManualOutputRow() {
        outputs.append(
            HarvestOutputDraft(
                sku: availableParts.first?.sku ?? "",
                name: availableParts.first?.name,
                quantity: 1,
                isManuallyAdded: true
            )
        )
    }

    func removeOutputRow(_ id: UUID) {
        outputs.removeAll { $0.id == id }
    }

    func loadContext(partsInventoryService: any PartsInventoryServiceProtocol) async {
        isLoadingContext = true
        errorMessage = nil

        do {
            async let mappingsTask = partsInventoryService.mappings()
            async let partsTask = partsInventoryService.listParts()
            async let binsTask = partsInventoryService.listBins()
            let allMappings = try await mappingsTask
            let parts = try await partsTask
            availableParts = parts
            availableBins = (try? await binsTask) ?? []

            // Exclusive project-first precedence, matching the server's
            // PartOutputMappingResolver.ResolveCurrentMappingsAsync: project
            // mappings are authoritative when present; gcode mappings are
            // only consulted as a fallback when no project mapping exists.
            // This is NOT an OR-union of both sources.
            let projectMatches = job.projectFileId.map { projectId in
                allMappings.filter { $0.printProjectFileId == projectId }
            } ?? []
            let matching: [PartOutputMappingResponse]
            if !projectMatches.isEmpty {
                matching = projectMatches
            } else if let gcodeId = job.gcodeFileId {
                matching = allMappings.filter { $0.gcodeFileId == gcodeId }
            } else {
                matching = []
            }
            let partsBySku = Dictionary(uniqueKeysWithValues: parts.map { ($0.sku, $0) })
            outputs = matching.map { mapping in
                HarvestOutputDraft(
                    sku: mapping.sku,
                    name: partsBySku[mapping.sku]?.name,
                    quantity: mapping.quantity
                )
            }
            resolvedBaseline = outputs.map { OutputSnapshot(sku: $0.sku, quantity: $0.quantity) }
        } catch {
            logger.warning("Failed to load harvest context: \(error.localizedDescription)")
            errorMessage = error.localizedDescription
        }

        isLoadingContext = false
    }

    func submit(partsInventoryService: any PartsInventoryServiceProtocol, allowWrongBin: Bool = false) async {
        // Defense-in-depth: the UI already disables Submit via `canSubmit`
        // when the edited output set is invalid (delete-all, incomplete
        // baseline membership, blank/duplicate/unknown SKU, out-of-range
        // quantity, or an unregistered per-output bin) — Dispute A requires
        // this never be allowed to reach the server as a fallback-to-nil or
        // partial-baseline request, so guard here too.
        guard !hasInvalidOutputEdits else { return }

        isSubmitting = true
        errorMessage = nil
        if !allowWrongBin {
            wrongBinConflict = nil
            mappingRequiredConflict = nil
        }

        let trimmedBin = sharedBinCode.trimmingCharacters(in: .whitespacesAndNewlines)
        // Only send explicit outputs when the operator has actually deviated
        // from the server-resolved baseline (added/removed/edited a row).
        // Sending a non-empty `outputs` array with an unedited, auto-resolved
        // mapping trips the server's unconditional "outputs requires a
        // non-blank overrideReason" validation (PartHarvestService
        // .ResolveOutputsAsync) even when nothing was actually overridden —
        // so unedited mappings send `nil` and let the server resolve them.
        // (The invalid "edited to empty/incomplete" case is excluded by the
        // `hasInvalidOutputEdits` guard above, so `hasManualOutputEdits` here
        // only ever means a genuinely valid, non-empty explicit edit.)
        let hasEdits = hasManualOutputEdits
        let explicitOutputs: [HarvestOutputRequestItem]? = hasEdits
            ? outputs.map { HarvestOutputRequestItem(sku: $0.sku, quantity: $0.quantity) }
            : nil
        // Validated by `hasInvalidOutputEdits` above to be complete (every
        // row has a nonblank, registered active bin) whenever per-output
        // bins are in use — safe to `map` directly, never silently compact
        // away a blank row.
        let outputBins: [HarvestOutputBinRequest]? = usePerOutputBins
            ? outputs.map { draft in
                HarvestOutputBinRequest(
                    partSku: draft.sku,
                    binCode: draft.binCodeOverride.trimmingCharacters(in: .whitespacesAndNewlines)
                )
            }
            : nil
        // A reason is required whenever we're overriding a wrongBin conflict
        // OR sending explicit (manually-edited) outputs — both are operator
        // overrides of the server's default resolution.
        let needsOverrideReason = allowWrongBin || explicitOutputs != nil
        let trimmedReason = overrideReason.trimmingCharacters(in: .whitespacesAndNewlines)

        let request = HarvestJobRequest(
            binCode: trimmedBin.isEmpty ? nil : trimmedBin,
            quantityOverride: nil,
            outputs: explicitOutputs,
            operationKey: job.id.uuidString,
            outputBins: (outputBins?.isEmpty ?? true) ? nil : outputBins,
            allowWrongBin: allowWrongBin,
            overrideReason: needsOverrideReason ? trimmedReason : nil
        )

        do {
            let response = try await partsInventoryService.harvestJob(jobId: job.id, request: request)
            result = response
            wrongBinConflict = nil
            mappingRequiredConflict = nil
        } catch NetworkError.partsInventoryConflict(let conflict) {
            if conflict.isWrongBin {
                wrongBinConflict = conflict
            } else if conflict.isPartMappingRequired {
                mappingRequiredConflict = conflict
            } else {
                errorMessage = conflict.detail ?? conflict.title ?? "Printed-parts conflict"
            }
        } catch {
            logger.warning("Harvest failed: \(error.localizedDescription)")
            errorMessage = error.localizedDescription
        }

        isSubmitting = false
    }

    /// Retries the harvest with `allowWrongBin=true` and the operator's
    /// entered `overrideReason`, per Dallas's requirement that an override
    /// requires a non-empty reason.
    func confirmWrongBinOverride(partsInventoryService: any PartsInventoryServiceProtocol) async {
        guard canConfirmOverride else { return }
        await submit(partsInventoryService: partsInventoryService, allowWrongBin: true)
    }
}
