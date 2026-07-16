import Foundation
@testable import PrintFarmer

final class MockPartsInventoryService: PartsInventoryServiceProtocol, @unchecked Sendable {
    var partsToReturn: [PartInventoryResponse] = []
    var partToResolve: PartInventoryResponse?
    var binsToReturn: [BinResponse] = []
    var binToResolve: BinResponse?
    var binToRegister: BinResponse?
    var adjustmentToReturn: PartAdjustmentResponse?
    var reorderCandidatesToReturn: [ReorderCandidateResponse] = []
    var mappingsToReturn: [PartOutputMappingResponse] = []
    var harvestResponseToReturn: HarvestJobResponse?

    var listPartsError: Error?
    var resolvePartError: Error?
    var listBinsError: Error?
    var resolveBinError: Error?
    var registerBinError: Error?
    var adjustPartError: Error?
    var reorderCandidatesError: Error?
    var mappingsError: Error?
    var harvestError: Error?

    private(set) var listPartsCalls: [Bool] = []
    private(set) var resolvePartBarcodes: [String] = []
    private(set) var listBinsCalls: [Bool] = []
    private(set) var resolveBinCodes: [String] = []
    private(set) var registerBinCalls: [(code: String, name: String?, location: String?)] = []
    private(set) var adjustPartCalls: [(sku: String, request: AdjustPartInventoryRequest)] = []
    private(set) var mappingsSkuCalls: [String?] = []
    private(set) var harvestCalls: [(jobId: UUID, request: HarvestJobRequest)] = []

    func listParts(includeInactive: Bool) async throws -> [PartInventoryResponse] {
        listPartsCalls.append(includeInactive)
        if let listPartsError { throw listPartsError }
        return partsToReturn
    }

    func resolvePartByBarcode(_ sku: String) async throws -> PartInventoryResponse {
        resolvePartBarcodes.append(sku)
        if let resolvePartError { throw resolvePartError }
        guard let partToResolve else { throw NetworkError.notFound }
        return partToResolve
    }

    func listBins(includeInactive: Bool) async throws -> [BinResponse] {
        listBinsCalls.append(includeInactive)
        if let listBinsError { throw listBinsError }
        return binsToReturn
    }

    func resolveBinByBarcode(_ code: String) async throws -> BinResponse {
        resolveBinCodes.append(code)
        if let resolveBinError { throw resolveBinError }
        guard let binToResolve else { throw NetworkError.notFound }
        return binToResolve
    }

    func registerBin(code: String, name: String?, location: String?) async throws -> BinResponse {
        registerBinCalls.append((code, name, location))
        if let registerBinError { throw registerBinError }
        guard let binToRegister else { throw NetworkError.notFound }
        return binToRegister
    }

    func adjustPart(sku: String, request: AdjustPartInventoryRequest) async throws -> PartAdjustmentResponse {
        adjustPartCalls.append((sku, request))
        if let adjustPartError { throw adjustPartError }
        guard let adjustmentToReturn else { throw NetworkError.notFound }
        return adjustmentToReturn
    }

    func reorderCandidates() async throws -> [ReorderCandidateResponse] {
        if let reorderCandidatesError { throw reorderCandidatesError }
        return reorderCandidatesToReturn
    }

    func mappings(sku: String?) async throws -> [PartOutputMappingResponse] {
        mappingsSkuCalls.append(sku)
        if let mappingsError { throw mappingsError }
        if let sku {
            return mappingsToReturn.filter { $0.sku == sku }
        }
        return mappingsToReturn
    }

    func harvestJob(jobId: UUID, request: HarvestJobRequest) async throws -> HarvestJobResponse {
        harvestCalls.append((jobId, request))
        if let harvestError { throw harvestError }
        guard let harvestResponseToReturn else { throw NetworkError.notFound }
        return harvestResponseToReturn
    }
}
