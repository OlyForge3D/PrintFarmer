import Foundation

// MARK: - Demo Parts Inventory Service
//
// Static, no-network stand-in for `PartsInventoryService` used by the demo
// ServiceContainer. Returns a small fixed catalog so UI code that consumes
// printed-parts inventory still exercises its populated and empty-state
// paths without any HTTP.

final class DemoPartsInventoryService: PartsInventoryServiceProtocol, @unchecked Sendable {

    private static let now = Date()

    private static let sampleBin = BinResponse(
        id: UUID(uuidString: "00000000-0000-0000-0000-0000000000B1")!,
        code: "BIN-A1",
        name: "Shelf A1",
        location: "Rack A",
        notes: nil,
        isActive: true,
        createdAt: now,
        updatedAt: now
    )

    private static let sampleParts: [PartInventoryResponse] = [
        PartInventoryResponse(
            id: UUID(uuidString: "00000000-0000-0000-0000-0000000000A1")!,
            sku: "BRKT-01",
            name: "Mounting Bracket",
            description: "Printed mounting bracket, PETG",
            modelFileRef: nil,
            defaultBinId: sampleBin.id,
            defaultBinCode: sampleBin.code,
            defaultBinName: sampleBin.name,
            onHand: 4,
            reorderPoint: 10,
            needsReorder: true,
            isActive: true,
            createdAt: now,
            updatedAt: now
        ),
        PartInventoryResponse(
            id: UUID(uuidString: "00000000-0000-0000-0000-0000000000A2")!,
            sku: "CLIP-02",
            name: "Cable Clip",
            description: nil,
            modelFileRef: nil,
            defaultBinId: sampleBin.id,
            defaultBinCode: sampleBin.code,
            defaultBinName: sampleBin.name,
            onHand: 32,
            reorderPoint: 15,
            needsReorder: false,
            isActive: true,
            createdAt: now,
            updatedAt: now
        ),
    ]

    func listParts(includeInactive: Bool) async throws -> [PartInventoryResponse] {
        Self.sampleParts
    }

    func resolvePartByBarcode(_ sku: String) async throws -> PartInventoryResponse {
        guard let match = Self.sampleParts.first(where: { $0.sku.caseInsensitiveCompare(sku) == .orderedSame }) else {
            throw NetworkError.notFound
        }
        return match
    }

    func listBins(includeInactive: Bool) async throws -> [BinResponse] {
        [Self.sampleBin]
    }

    func resolveBinByBarcode(_ code: String) async throws -> BinResponse {
        guard Self.sampleBin.code.caseInsensitiveCompare(code) == .orderedSame else {
            throw NetworkError.notFound
        }
        return Self.sampleBin
    }

    func registerBin(code: String, name: String?, location: String?) async throws -> BinResponse {
        Self.sampleBin
    }

    func adjustPart(sku: String, request: AdjustPartInventoryRequest) async throws -> PartAdjustmentResponse {
        PartAdjustmentResponse(
            id: UUID(),
            partInventoryId: Self.sampleParts.first?.id ?? UUID(),
            sku: sku,
            binId: Self.sampleBin.id,
            binCode: request.binCode ?? Self.sampleBin.code,
            delta: request.delta,
            resultingBalance: max(0, (Self.sampleParts.first(where: { $0.sku == sku })?.onHand ?? 0) + request.delta),
            reason: request.reason,
            printJobId: request.jobId,
            operationKey: request.operationKey,
            notes: request.notes,
            userId: "demo",
            createdAt: Self.now
        )
    }

    func reorderCandidates() async throws -> [ReorderCandidateResponse] {
        Self.sampleParts
            .filter(\.needsReorder)
            .map {
                ReorderCandidateResponse(
                    partInventoryId: $0.id,
                    sku: $0.sku,
                    name: $0.name,
                    onHand: $0.onHand,
                    reorderPoint: $0.reorderPoint,
                    deficit: max(0, $0.reorderPoint - $0.onHand)
                )
            }
    }

    func mappings(sku: String?) async throws -> [PartOutputMappingResponse] {
        []
    }

    func harvestJob(jobId: UUID, request: HarvestJobRequest) async throws -> HarvestJobResponse {
        HarvestJobResponse(
            printJobId: jobId,
            harvestedAt: Self.now,
            binId: Self.sampleBin.id,
            binCode: request.binCode ?? Self.sampleBin.code,
            alreadyHarvested: false,
            adjustments: [],
            outputs: []
        )
    }
}
