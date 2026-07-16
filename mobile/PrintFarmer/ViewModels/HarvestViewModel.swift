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
    var sharedBinCode: String = ""
    var usePerOutputBins = false

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
        !isSubmitting && result == nil
    }

    var hasWrongBinConflict: Bool { wrongBinConflict != nil }
    var hasMappingRequiredConflict: Bool { mappingRequiredConflict != nil }

    var canConfirmOverride: Bool {
        !overrideReason.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
    }

    func addManualOutputRow() {
        outputs.append(HarvestOutputDraft(sku: availableParts.first?.sku ?? "", name: availableParts.first?.name, quantity: 1))
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
            let allMappings = try await mappingsTask
            let parts = try await partsTask
            availableParts = parts

            let matching = allMappings.filter { mapping in
                (job.gcodeFileId != nil && mapping.gcodeFileId == job.gcodeFileId)
                    || (job.projectFileId != nil && mapping.printProjectFileId == job.projectFileId)
            }
            let partsBySku = Dictionary(uniqueKeysWithValues: parts.map { ($0.sku, $0) })
            outputs = matching.map { mapping in
                HarvestOutputDraft(
                    sku: mapping.sku,
                    name: partsBySku[mapping.sku]?.name,
                    quantity: mapping.quantity
                )
            }
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
        let explicitOutputs: [HarvestOutputRequestItem]? = outputs.isEmpty
            ? nil
            : outputs.map { HarvestOutputRequestItem(sku: $0.sku, quantity: $0.quantity) }
        let outputBins: [HarvestOutputBinRequest]? = usePerOutputBins
            ? outputs.compactMap { draft in
                let bin = draft.binCodeOverride.trimmingCharacters(in: .whitespacesAndNewlines)
                guard !bin.isEmpty else { return nil }
                return HarvestOutputBinRequest(partSku: draft.sku, binCode: bin)
            }
            : nil

        let request = HarvestJobRequest(
            binCode: trimmedBin.isEmpty ? nil : trimmedBin,
            quantityOverride: nil,
            outputs: explicitOutputs,
            operationKey: job.id.uuidString,
            outputBins: (outputBins?.isEmpty ?? true) ? nil : outputBins,
            allowWrongBin: allowWrongBin,
            overrideReason: allowWrongBin ? overrideReason.trimmingCharacters(in: .whitespacesAndNewlines) : nil
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
