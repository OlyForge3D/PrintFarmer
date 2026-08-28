import Foundation
@testable import PrintFarmer

final class MockPartsInventoryService: PartsInventoryServiceProtocol, @unchecked Sendable {
    var partsToReturn: [PartInventoryResponse] = []
    var partToResolve: PartInventoryResponse?
    var binsToReturn: [BinResponse] = []
    var binToResolve: BinResponse?
    var binToRegister: BinResponse?
    var adjustmentToReturn: PartAdjustmentResponse?
    /// When true, the FIRST `adjustPart` call for a given `operationKey`
    /// still "commits" (records) the mutation exactly once — proving the
    /// server-side idempotent-dedupe contract — but the client simulates
    /// losing the response by throwing `responseLossError` instead of
    /// returning it. A SAME-key retry must then replay the already-
    /// committed result rather than applying a second mutation. See
    /// Blocker B commit-then-response-loss causal-proof tests.
    var simulateResponseLossOnFirstCommit = false
    var responseLossError: Error = NetworkError.timeout
    /// Optional async hook (see `harvestGate`) letting a test hold an
    /// `adjustPart` call in flight at a real suspension point to
    /// deterministically cancel between "commit" and "response delivery".
    var adjustPartGate: (() async -> Void)?
    var reorderCandidatesToReturn: [ReorderCandidateResponse] = []
    var mappingsToReturn: [PartOutputMappingResponse] = []
    var harvestResponseToReturn: HarvestJobResponse?
    /// Optional async hook invoked (after the call is recorded, before
    /// resolving success/error) so a test can hold a harvest response in
    /// flight via a real suspension point (e.g. an `AsyncGate`) — used to
    /// deterministically prove "exactly one POST" under rapid/delayed
    /// double-submit rather than a sequential-call reimplementation.
    var harvestGate: (() async -> Void)?
    var resolveBinGate: (() async -> Void)?

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
    /// Committed adjustment results keyed by `operationKey`, used to
    /// replay same-key retries without applying a second mutation (see
    /// `simulateResponseLossOnFirstCommit`).
    private var committedAdjustments: [String: PartAdjustmentResponse] = [:]
    /// Incremented only when a NEW mutation is actually committed
    /// (distinct from `adjustPartCalls.count`, which also counts replayed
    /// same-key retries) — lets Blocker B tests assert "exactly one
    /// applied mutation" independent of how many network round trips the
    /// retry took.
    private(set) var appliedMutationCount = 0

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
        if let resolveBinGate { await resolveBinGate() }
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

        if let key = request.operationKey, let committed = committedAdjustments[key] {
            // Same-key retry after a prior commit: replay the already-
            // committed result. No second mutation, and no re-throw of
            // `adjustPartError`/`responseLossError` — the server already
            // resolved this operationKey.
            return committed
        }

        if let adjustPartError { throw adjustPartError }
        guard let adjustmentToReturn else { throw NetworkError.notFound }

        if let key = request.operationKey {
            committedAdjustments[key] = adjustmentToReturn
        }
        appliedMutationCount += 1

        // The gate sits AFTER the mutation is committed but BEFORE the
        // response is "delivered" back to the caller — lets a test cancel
        // deterministically in exactly that window (Blocker B) and then
        // assert the commit already happened server-side.
        await adjustPartGate?()

        // Real URLSession-backed transports observe cooperative
        // cancellation of the task they're running on and throw once it's
        // cancelled, even after the server has already committed the
        // mutation — this single check makes the mock behave the same
        // way, so a test can prove that an UNSHIELDED call loses the
        // response to `CancellationError` while a call shielded in its own
        // unstructured `Task` (as production `submit()` now does) does
        // not, because that transport task is never itself cancelled.
        try Task.checkCancellation()

        if simulateResponseLossOnFirstCommit {
            // Only the first commit simulates a lost response; a
            // subsequent distinct key (new intent) commits normally.
            simulateResponseLossOnFirstCommit = false
            throw responseLossError
        }

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
        await harvestGate?()
        if let harvestError { throw harvestError }
        guard let harvestResponseToReturn else { throw NetworkError.notFound }
        return harvestResponseToReturn
    }
}
