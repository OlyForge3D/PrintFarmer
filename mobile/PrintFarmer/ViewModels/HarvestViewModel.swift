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
        if hasManualOutputEdits && !outputs.isEmpty {
            return !overrideReason.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
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

    var hasWrongBinConflict: Bool { wrongBinConflict != nil }
    var hasMappingRequiredConflict: Bool { mappingRequiredConflict != nil }

    var canConfirmOverride: Bool {
        !overrideReason.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
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
        let hasEdits = hasManualOutputEdits && !outputs.isEmpty
        let explicitOutputs: [HarvestOutputRequestItem]? = hasEdits
            ? outputs.map { HarvestOutputRequestItem(sku: $0.sku, quantity: $0.quantity) }
            : nil
        let outputBins: [HarvestOutputBinRequest]? = usePerOutputBins
            ? outputs.compactMap { draft in
                let bin = draft.binCodeOverride.trimmingCharacters(in: .whitespacesAndNewlines)
                guard !bin.isEmpty else { return nil }
                return HarvestOutputBinRequest(partSku: draft.sku, binCode: bin)
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
