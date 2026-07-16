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
    private(set) var isSubmitting = false
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

    /// Synchronous, atomic check-and-set re-entrancy guard for the "exactly
    /// once per sheet" submit contract (final #714 remediation, superseding
    /// the prior per-response interpretation): the first successful
    /// response for a presented sheet is the only one that may ever
    /// complete, and a rapid or delayed double-tap of Submit/Override must
    /// never send a second POST while the first is in flight — or after it
    /// has already succeeded. Must be called directly from the button's
    /// action closure BEFORE any `Task {}` is created, so the check and the
    /// `isSubmitting = true` set happen on the same run-loop turn as the
    /// tap, with no intervening suspension point a guard placed inside the
    /// async `submit()` body could race on (two independently-scheduled
    /// `Task`s hopping onto `@MainActor` are not guaranteed to preserve tap
    /// order). Shared by both the main submit button and the wrong-bin
    /// override-confirm button, since they mutate the same `isSubmitting`/
    /// `result` state and must never run concurrently either.
    ///
    /// Returns `true` if the caller may proceed to create the submit `Task`.
    func beginSubmit() -> Bool {
        guard !isSubmitting && result == nil else { return false }
        isSubmitting = true
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
        if !isBaselineSetExactMatch { return true }

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

    /// When a server-resolved baseline exists, the live outputs' SKU set
    /// must be an EXACT match (both directions) of the baseline SKU set —
    /// quantity edits are fine, but the operator cannot drop a subset (or
    /// all) of the resolved mapping, nor add an extra SKU beyond it. A
    /// one-directional subset check would let an extra SKU slip through
    /// unflagged, so this compares full normalized sets rather than just
    /// checking that every baseline SKU is still present. Vacuously true
    /// when there was no resolved baseline (a manually-built output set
    /// from scratch, e.g. `partMappingRequired` recovery, has no baseline
    /// membership to preserve).
    private var isBaselineSetExactMatch: Bool {
        guard !resolvedBaseline.isEmpty else { return true }
        let currentSkus = Set(outputs.map { Self.normalizeIdentity($0.sku) })
        let baselineSkus = Set(resolvedBaseline.map { Self.normalizeIdentity($0.sku) })
        return currentSkus == baselineSkus
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

    /// Multiplies a per-print quantity by the job's copy count, clamping to
    /// `Int.max` on overflow rather than trapping (`Int * Int` crashes on
    /// overflow) — the (1...10000) quantity bound enforced elsewhere still
    /// rejects an absurdly large prefilled total; this just keeps the
    /// multiplication itself from ever crashing the app on a malformed
    /// `copies`/`quantity` combination.
    private static func multipliedTotal(_ quantity: Int, copies: Int) -> Int {
        let (total, overflowed) = quantity.multipliedReportingOverflow(by: copies)
        return overflowed ? Int.max : total
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
            // Multi-copy totals: `mapping.quantity` is the per-print
            // quantity (server's `QuantityPerPrint`). For an untouched
            // mapping the server applies `QuantityPerPrint * copies`
            // (`PartHarvestService.ResolveOutputsAsync`, copies defaulting
            // to `Math.Max(1, job.Copies)`), so the prefilled/baseline
            // total must match that here — otherwise an unedited
            // `outputs: nil` submission would harvest a different quantity
            // than what's displayed, and an edited total would be compared
            // against the wrong baseline.
            let copies = max(1, job.copies)
            outputs = matching.map { mapping in
                HarvestOutputDraft(
                    sku: mapping.sku,
                    name: partsBySku[mapping.sku]?.name,
                    quantity: Self.multipliedTotal(mapping.quantity, copies: copies)
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
        // Once-per-sheet contract: a `result` already means a prior attempt
        // succeeded — no further attempt may ever reach the server again
        // for this presented sheet, even if `submit()` is called directly
        // (bypassing the synchronous `beginSubmit()` guard, e.g. a delayed
        // duplicate call from an earlier tap). This is the terminal state;
        // never released, unlike the failure-path releases below.
        guard result == nil else { return }

        // Defense-in-depth: the UI already disables Submit via `canSubmit`
        // when the edited output set is invalid (delete-all, incomplete
        // baseline membership, blank/duplicate/unknown SKU, out-of-range
        // quantity, or an unregistered per-output bin) — Dispute A requires
        // this never be allowed to reach the server as a fallback-to-nil or
        // partial-baseline request, so guard here too. Releases the
        // `beginSubmit()` guard (rather than leaving it stuck) since this
        // path never reaches the network.
        guard !hasInvalidOutputEdits else {
            isSubmitting = false
            return
        }

        // Callers are expected to have already called the synchronous
        // `beginSubmit()` guard before creating the `Task` that invokes
        // this method — setting `isSubmitting` here too is a harmless
        // no-op in that case, and keeps direct test/preview calls to
        // `submit()` (bypassing `beginSubmit()`) behaviorally correct.
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
            // Once-per-sheet contract: the guard is HELD (never released)
            // on success. No further submit or override-confirm attempt —
            // including a delayed duplicate `Task` from an earlier tap that
            // hadn't yet observed `result != nil` — can ever fire a second
            // POST for this presented sheet.
        } catch NetworkError.partsInventoryConflict(let conflict) {
            isSubmitting = false
            if conflict.isWrongBin {
                wrongBinConflict = conflict
            } else if conflict.isPartMappingRequired {
                mappingRequiredConflict = conflict
            } else {
                errorMessage = conflict.detail ?? conflict.title ?? "Printed-parts conflict"
            }
        } catch {
            isSubmitting = false
            logger.warning("Harvest failed: \(error.localizedDescription)")
            errorMessage = error.localizedDescription
        }
    }

    /// Retries the harvest with `allowWrongBin=true` and the operator's
    /// entered `overrideReason`, per Dallas's requirement that an override
    /// requires a non-empty reason.
    func confirmWrongBinOverride(partsInventoryService: any PartsInventoryServiceProtocol) async {
        guard canConfirmOverride else { return }
        await submit(partsInventoryService: partsInventoryService, allowWrongBin: true)
    }
}
