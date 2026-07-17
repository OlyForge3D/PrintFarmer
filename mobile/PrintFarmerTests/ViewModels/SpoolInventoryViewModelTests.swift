import XCTest
@testable import PrintFarmer

/// Tests for SpoolInventoryViewModel: loading, filtering, search, NFC scanning,
/// delete, and error handling using MockSpoolService and MockScannerService.
@MainActor
final class SpoolInventoryViewModelTests: XCTestCase {

    private var mockService: MockSpoolService!
    private var mockScanner: MockScannerService!
    private var viewModel: SpoolInventoryViewModel!

    override func setUp() {
        super.setUp()
        mockService = MockSpoolService()
        mockScanner = MockScannerService()
        viewModel = SpoolInventoryViewModel()
        viewModel.configure(spoolService: mockService)
        viewModel.configureNFC(scanner: mockScanner)
    }

    override func tearDown() {
        viewModel = nil
        mockScanner = nil
        mockService = nil
        super.tearDown()
    }

    // MARK: - Initial State

    func testInitialState() {
        XCTAssertTrue(viewModel.spools.isEmpty)
        XCTAssertEqual(viewModel.searchText, "")
        XCTAssertNil(viewModel.selectedMaterial)
        XCTAssertNil(viewModel.selectedStatus)
        XCTAssertFalse(viewModel.isLoading)
        XCTAssertNil(viewModel.errorMessage)
        XCTAssertFalse(viewModel.isScanning)
        XCTAssertNil(viewModel.scanError)
        XCTAssertNil(viewModel.scannedSpoolData)
        XCTAssertFalse(viewModel.showScannedDataSheet)
        XCTAssertNil(viewModel.highlightedSpoolId)
    }

    // MARK: - Load Spools

    func testLoadSpoolsSuccess() async {
        let spools = [makeSpool(id: 1, material: "PLA"), makeSpool(id: 2, material: "PETG")]
        mockService.spoolsPageToReturn = SpoolmanPagedResult(items: spools, totalCount: 2)

        await viewModel.loadSpools()

        XCTAssertEqual(viewModel.spools.count, 2)
        XCTAssertTrue(mockService.listSpoolsCalled)
        XCTAssertFalse(viewModel.isLoading)
        XCTAssertNil(viewModel.errorMessage)
    }

    func testLoadSpoolsEmpty() async {
        mockService.spoolsPageToReturn = SpoolmanPagedResult(items: [], totalCount: 0)

        await viewModel.loadSpools()

        XCTAssertTrue(viewModel.spools.isEmpty)
        XCTAssertNil(viewModel.errorMessage)
    }

    func testLoadSpoolsError() async {
        mockService.errorToThrow = NetworkError.serverError(500)

        await viewModel.loadSpools()

        XCTAssertTrue(viewModel.spools.isEmpty)
        XCTAssertNotNil(viewModel.errorMessage)
        XCTAssertFalse(viewModel.isLoading)
    }

    func testLoadSpoolsClearsError() async {
        mockService.errorToThrow = NetworkError.noConnection
        await viewModel.loadSpools()
        XCTAssertNotNil(viewModel.errorMessage)

        mockService.errorToThrow = nil
        mockService.spoolsPageToReturn = SpoolmanPagedResult(items: [], totalCount: 0)
        await viewModel.loadSpools()
        XCTAssertNil(viewModel.errorMessage)
    }

    func testLoadSpoolsWithoutConfigureShowsError() async {
        let unconfigured = SpoolInventoryViewModel()

        await unconfigured.loadSpools()

        XCTAssertEqual(unconfigured.errorMessage, "Spool service not available")
    }

    func testLoadSpoolsPassesCorrectLimitAndOffset() async {
        mockService.spoolsPageToReturn = SpoolmanPagedResult(items: [], totalCount: 0)

        await viewModel.loadSpools()

        XCTAssertEqual(mockService.listSpoolsArgs?.limit, 200)
        XCTAssertEqual(mockService.listSpoolsArgs?.offset, 0)
    }

    // MARK: - Available Materials

    func testAvailableMaterialsSorted() {
        viewModel.spools = [
            makeSpool(id: 1, material: "PETG"),
            makeSpool(id: 2, material: "ABS"),
            makeSpool(id: 3, material: "PLA"),
        ]

        XCTAssertEqual(viewModel.availableMaterials, ["ABS", "PETG", "PLA"])
    }

    func testAvailableMaterialsDeduplicates() {
        viewModel.spools = [
            makeSpool(id: 1, material: "PLA"),
            makeSpool(id: 2, material: "PLA"),
            makeSpool(id: 3, material: "PETG"),
        ]

        XCTAssertEqual(viewModel.availableMaterials, ["PETG", "PLA"])
    }

    // MARK: - Status Filtering

    func testFilterAvailableExcludesInUseAndArchived() {
        viewModel.spools = [
            makeSpool(id: 1, material: "PLA", inUse: false, archived: false),
            makeSpool(id: 2, material: "PLA", inUse: true, archived: false),
            makeSpool(id: 3, material: "PLA", inUse: false, archived: true),
        ]
        viewModel.selectedStatus = .available

        XCTAssertEqual(viewModel.filteredSpools.count, 1)
        XCTAssertEqual(viewModel.filteredSpools.first?.id, 1)
    }

    func testFilterAvailableWithFalseInUse() {
        viewModel.spools = [
            makeSpool(id: 1, material: "PLA", inUse: false, archived: false),
        ]
        viewModel.selectedStatus = .available

        XCTAssertEqual(viewModel.filteredSpools.count, 1)
    }

    func testFilterInUse() {
        viewModel.spools = [
            makeSpool(id: 1, material: "PLA", inUse: true),
            makeSpool(id: 2, material: "PLA", inUse: false),
            makeSpool(id: 3, material: "PLA", inUse: false),
        ]
        viewModel.selectedStatus = .inUse

        XCTAssertEqual(viewModel.filteredSpools.count, 1)
        XCTAssertEqual(viewModel.filteredSpools.first?.id, 1)
    }

    func testFilterLow() {
        viewModel.spools = [
            makeSpool(id: 1, material: "PLA", remainingWeightG: 100, initialWeightG: 1000),  // 10% — low
            makeSpool(id: 2, material: "PLA", remainingWeightG: 500, initialWeightG: 1000),  // 50% — not low
            makeSpool(id: 3, material: "PLA", remainingWeightG: 199, initialWeightG: 1000),  // 19.9% — low
        ]
        viewModel.selectedStatus = .low

        XCTAssertEqual(viewModel.filteredSpools.count, 2)
        let ids = viewModel.filteredSpools.map(\.id)
        XCTAssertTrue(ids.contains(1))
        XCTAssertTrue(ids.contains(3))
    }

    func testFilterLowExcludesNilWeights() {
        viewModel.spools = [
            makeSpool(id: 1, material: "PLA", remainingWeightG: nil, initialWeightG: 1000),
            makeSpool(id: 2, material: "PLA", remainingWeightG: 100, initialWeightG: nil),
            makeSpool(id: 3, material: "PLA", remainingWeightG: nil, initialWeightG: nil),
        ]
        viewModel.selectedStatus = .low

        XCTAssertTrue(viewModel.filteredSpools.isEmpty)
    }

    func testFilterLowExcludesZeroInitialWeight() {
        viewModel.spools = [
            makeSpool(id: 1, material: "PLA", remainingWeightG: 0, initialWeightG: 0),
        ]
        viewModel.selectedStatus = .low

        XCTAssertTrue(viewModel.filteredSpools.isEmpty)
    }

    func testFilterEmpty() {
        viewModel.spools = [
            makeSpool(id: 1, material: "PLA", remainingWeightG: 0, initialWeightG: 1000),
            makeSpool(id: 2, material: "PLA", remainingWeightG: 500, initialWeightG: 1000),
        ]
        viewModel.selectedStatus = .empty

        XCTAssertEqual(viewModel.filteredSpools.count, 1)
        XCTAssertEqual(viewModel.filteredSpools.first?.id, 1)
    }

    func testFilterEmptyWithNilRemainingAndNonNilInitial() {
        // nil remainingWeightG with non-nil initialWeightG → treated as empty
        viewModel.spools = [
            makeSpool(id: 1, material: "PLA", remainingWeightG: nil, initialWeightG: 1000),
        ]
        viewModel.selectedStatus = .empty

        XCTAssertEqual(viewModel.filteredSpools.count, 1)
    }

    func testFilterEmptyWithBothNilReturnsNothing() {
        viewModel.spools = [
            makeSpool(id: 1, material: "PLA", remainingWeightG: nil, initialWeightG: nil),
        ]
        viewModel.selectedStatus = .empty

        XCTAssertTrue(viewModel.filteredSpools.isEmpty)
    }

    func testNoStatusFilterReturnsAll() {
        viewModel.spools = [
            makeSpool(id: 1, material: "PLA"),
            makeSpool(id: 2, material: "PETG"),
        ]
        viewModel.selectedStatus = nil

        XCTAssertEqual(viewModel.filteredSpools.count, 2)
    }

    // MARK: - Material Filtering

    func testFilterByMaterial() {
        viewModel.spools = [
            makeSpool(id: 1, material: "PLA"),
            makeSpool(id: 2, material: "PETG"),
            makeSpool(id: 3, material: "PLA"),
        ]
        viewModel.selectedMaterial = "PLA"

        XCTAssertEqual(viewModel.filteredSpools.count, 2)
    }

    func testNoMaterialFilterReturnsAll() {
        viewModel.spools = [
            makeSpool(id: 1, material: "PLA"),
            makeSpool(id: 2, material: "PETG"),
        ]
        viewModel.selectedMaterial = nil

        XCTAssertEqual(viewModel.filteredSpools.count, 2)
    }

    // MARK: - Search

    func testSearchByMaterial() {
        viewModel.spools = [
            makeSpool(id: 1, material: "PLA"),
            makeSpool(id: 2, material: "PETG"),
        ]
        viewModel.searchText = "PLA"

        XCTAssertEqual(viewModel.filteredSpools.count, 1)
        XCTAssertEqual(viewModel.filteredSpools.first?.id, 1)
    }

    func testSearchIsCaseInsensitive() {
        viewModel.spools = [
            makeSpool(id: 1, material: "PLA"),
        ]
        viewModel.searchText = "pla"

        XCTAssertEqual(viewModel.filteredSpools.count, 1)
    }

    func testSearchByVendor() {
        viewModel.spools = [
            makeSpool(id: 1, material: "PLA", vendor: "Prusament"),
            makeSpool(id: 2, material: "PLA", vendor: "Hatchbox"),
        ]
        viewModel.searchText = "Prusament"

        XCTAssertEqual(viewModel.filteredSpools.count, 1)
        XCTAssertEqual(viewModel.filteredSpools.first?.id, 1)
    }

    func testSearchByName() {
        viewModel.spools = [
            makeSpool(id: 1, material: "PLA", name: "Galaxy Black"),
            makeSpool(id: 2, material: "PLA", name: "Signal White"),
        ]
        viewModel.searchText = "galaxy"

        XCTAssertEqual(viewModel.filteredSpools.count, 1)
        XCTAssertEqual(viewModel.filteredSpools.first?.id, 1)
    }

    func testEmptySearchReturnsAll() {
        viewModel.spools = [
            makeSpool(id: 1, material: "PLA"),
            makeSpool(id: 2, material: "PETG"),
        ]
        viewModel.searchText = ""

        XCTAssertEqual(viewModel.filteredSpools.count, 2)
    }

    func testSearchNoMatch() {
        viewModel.spools = [
            makeSpool(id: 1, material: "PLA"),
        ]
        viewModel.searchText = "NYLON"

        XCTAssertTrue(viewModel.filteredSpools.isEmpty)
    }

    // MARK: - Combined Filters

    func testMaterialAndStatusCombined() {
        viewModel.spools = [
            makeSpool(id: 1, material: "PLA", inUse: true),
            makeSpool(id: 2, material: "PETG", inUse: true),
            makeSpool(id: 3, material: "PLA", inUse: false, archived: false),
        ]
        viewModel.selectedMaterial = "PLA"
        viewModel.selectedStatus = .inUse

        XCTAssertEqual(viewModel.filteredSpools.count, 1)
        XCTAssertEqual(viewModel.filteredSpools.first?.id, 1)
    }

    func testMaterialAndSearchCombined() {
        viewModel.spools = [
            makeSpool(id: 1, material: "PLA", vendor: "Prusament"),
            makeSpool(id: 2, material: "PLA", vendor: "Hatchbox"),
            makeSpool(id: 3, material: "PETG", vendor: "Prusament"),
        ]
        viewModel.selectedMaterial = "PLA"
        viewModel.searchText = "Prusament"

        XCTAssertEqual(viewModel.filteredSpools.count, 1)
        XCTAssertEqual(viewModel.filteredSpools.first?.id, 1)
    }

    func testAllThreeFiltersCombined() {
        viewModel.spools = [
            makeSpool(id: 1, material: "PLA", inUse: true, vendor: "Prusament"),
            makeSpool(id: 2, material: "PLA", inUse: true, vendor: "Hatchbox"),
            makeSpool(id: 3, material: "PLA", inUse: false, vendor: "Prusament"),
            makeSpool(id: 4, material: "PETG", inUse: true, vendor: "Prusament"),
        ]
        viewModel.selectedMaterial = "PLA"
        viewModel.selectedStatus = .inUse
        viewModel.searchText = "Prusament"

        XCTAssertEqual(viewModel.filteredSpools.count, 1)
        XCTAssertEqual(viewModel.filteredSpools.first?.id, 1)
    }

    // MARK: - hasActiveSearch & activeFilterDescription

    func testHasActiveSearchFalseWhenNoFilters() {
        XCTAssertFalse(viewModel.hasActiveSearch)
    }

    func testHasActiveSearchTrueWithSearchText() {
        viewModel.searchText = "PLA"
        XCTAssertTrue(viewModel.hasActiveSearch)
    }

    func testHasActiveSearchTrueWithMaterial() {
        viewModel.selectedMaterial = "PLA"
        XCTAssertTrue(viewModel.hasActiveSearch)
    }

    func testHasActiveSearchTrueWithStatus() {
        viewModel.selectedStatus = .available
        XCTAssertTrue(viewModel.hasActiveSearch)
    }

    func testActiveFilterDescriptionIncludesAllFilters() {
        viewModel.selectedMaterial = "PLA"
        viewModel.selectedStatus = .inUse
        viewModel.searchText = "test"

        let desc = viewModel.activeFilterDescription
        XCTAssertTrue(desc.contains("material: PLA"))
        XCTAssertTrue(desc.contains("status: In Use"))
        XCTAssertTrue(desc.contains("search: \"test\""))
    }

    // MARK: - Clear Filters

    func testClearFiltersResetsAll() {
        viewModel.selectedMaterial = "PLA"
        viewModel.selectedStatus = .inUse
        viewModel.searchText = "test"

        viewModel.clearFilters()

        XCTAssertNil(viewModel.selectedMaterial)
        XCTAssertNil(viewModel.selectedStatus)
        XCTAssertEqual(viewModel.searchText, "")
    }

    // MARK: - Find & Highlight

    func testFindSpoolById() {
        viewModel.spools = [
            makeSpool(id: 1, material: "PLA"),
            makeSpool(id: 2, material: "PETG"),
        ]

        let found = viewModel.findSpool(byId: 2)
        XCTAssertNotNil(found)
        XCTAssertEqual(found?.id, 2)
    }

    func testFindSpoolByIdNotFound() {
        viewModel.spools = [makeSpool(id: 1, material: "PLA")]

        XCTAssertNil(viewModel.findSpool(byId: 999))
    }

    func testClearHighlight() {
        viewModel.highlightedSpoolId = 42
        viewModel.clearHighlight()
        XCTAssertNil(viewModel.highlightedSpoolId)
    }

    // MARK: - #726 Highlight authority (generation + identity guard)

    /// Deterministic park/release helper for the highlight-expiry seam.
    /// Awaits a `CheckedContinuation` keyed by (generation, spoolId) so tests
    /// can pause the retained expiry task, mutate authority synchronously,
    /// then release the parked continuation and `await task.value` to
    /// observe the guard's decision. No sleep / Task.yield / polling / clock.
    private final class ExpiryPark: @unchecked Sendable {
        private let lock = NSLock()
        private var waiting: [String: CheckedContinuation<Void, Never>] = [:]
        private var preReleased: Set<String> = []

        private static func key(_ gen: UInt64, _ spoolId: Int) -> String {
            "\(gen)-\(spoolId)"
        }

        func park(gen: UInt64, spoolId: Int) async {
            let k = Self.key(gen, spoolId)
            await withCheckedContinuation { (c: CheckedContinuation<Void, Never>) in
                lock.lock()
                if preReleased.remove(k) != nil {
                    lock.unlock()
                    c.resume()
                } else {
                    waiting[k] = c
                    lock.unlock()
                }
            }
        }

        func release(gen: UInt64, spoolId: Int) {
            let k = Self.key(gen, spoolId)
            lock.lock()
            if let c = waiting.removeValue(forKey: k) {
                lock.unlock()
                c.resume()
            } else {
                preReleased.insert(k)
                lock.unlock()
            }
        }
    }

    func testSetHighlightAdvancesGenerationSynchronously() {
        XCTAssertEqual(viewModel.highlightGeneration, 0)
        viewModel.setHighlight(spoolId: 1)
        XCTAssertEqual(viewModel.highlightedSpoolId, 1)
        XCTAssertEqual(viewModel.highlightGeneration, 1)
        viewModel.setHighlight(spoolId: 2)
        XCTAssertEqual(viewModel.highlightedSpoolId, 2)
        XCTAssertEqual(viewModel.highlightGeneration, 2)
    }

    func testStaleExpiryUnderControlledContinuationDoesNotClearNewerHighlight() async {
        // Primary Dallas-required test: park expiry A immediately before its
        // clear attempt, synchronously accept/publish B, release A, and prove
        // B and its destination identity remain. Then release B's expiry and
        // prove B clears exactly once.
        let park = ExpiryPark()
        viewModel._highlightExpirySleepOverride = { gen, sid in
            await park.park(gen: gen, spoolId: sid)
        }

        viewModel.setHighlight(spoolId: 1)
        let taskA = viewModel._currentHighlightExpiryTask
        XCTAssertNotNil(taskA)
        XCTAssertEqual(viewModel.highlightedSpoolId, 1)
        XCTAssertEqual(viewModel.highlightGeneration, 1)

        // Synchronously accept/publish B; the authority advance happens
        // inside the setter, not deferred to a SwiftUI `.onChange`.
        viewModel.setHighlight(spoolId: 2)
        let taskB = viewModel._currentHighlightExpiryTask
        XCTAssertNotNil(taskB)
        XCTAssertEqual(viewModel.highlightedSpoolId, 2)
        XCTAssertEqual(viewModel.highlightGeneration, 2)

        // Release A's parked continuation. Its cancellation was requested by
        // `setHighlight(spoolId: 2)`, but the guard must not depend on that:
        // even if the sleep-override ignores cancellation and resumes, the
        // captured (gen=1, spoolId=1) must not match current (gen=2, spoolId=2).
        park.release(gen: 1, spoolId: 1)
        await taskA?.value

        XCTAssertEqual(viewModel.highlightedSpoolId, 2,
                       "Stale expiry A must not clear newer highlight B")
        XCTAssertEqual(viewModel.highlightGeneration, 2)

        // Release B's current expiry and prove it clears exactly once.
        park.release(gen: 2, spoolId: 2)
        await taskB?.value

        XCTAssertNil(viewModel.highlightedSpoolId, "Current expiry B clears B")
    }

    func testStaleExpiryClearsNothingEvenWhenSpoolIdCollides() async {
        // Same-spool sequence: A=1 → B=2 → C=1. C reassigns spoolId 1 with
        // generation 3. Stale expiry for the first assignment (gen=1, id=1)
        // must not clear C, because generations differ even though ids match.
        let park = ExpiryPark()
        viewModel._highlightExpirySleepOverride = { gen, sid in
            await park.park(gen: gen, spoolId: sid)
        }

        viewModel.setHighlight(spoolId: 1)
        let taskA = viewModel._currentHighlightExpiryTask
        viewModel.setHighlight(spoolId: 2)
        viewModel.setHighlight(spoolId: 1)
        XCTAssertEqual(viewModel.highlightedSpoolId, 1)
        XCTAssertEqual(viewModel.highlightGeneration, 3)

        park.release(gen: 1, spoolId: 1)
        await taskA?.value

        XCTAssertEqual(viewModel.highlightedSpoolId, 1,
                       "Same-id stale expiry must be a no-op via generation guard")
        XCTAssertEqual(viewModel.highlightGeneration, 3)
    }

    func testCurrentExpiryClearsHighlightWhenGuardMatches() async {
        let park = ExpiryPark()
        viewModel._highlightExpirySleepOverride = { gen, sid in
            await park.park(gen: gen, spoolId: sid)
        }

        viewModel.setHighlight(spoolId: 7)
        let task = viewModel._currentHighlightExpiryTask
        XCTAssertEqual(viewModel.highlightedSpoolId, 7)

        park.release(gen: viewModel.highlightGeneration, spoolId: 7)
        await task?.value

        XCTAssertNil(viewModel.highlightedSpoolId)
    }

    func testInvalidateHighlightOwnershipPreventsParkedExpiryFromClearing() async {
        // Simulates the view's `.onDisappear` invalidating expiry ownership
        // while a parked task exists. Releasing the parked continuation must
        // not clear the still-visible highlight, and must not mutate the
        // highlight after the view is off-screen.
        let park = ExpiryPark()
        viewModel._highlightExpirySleepOverride = { gen, sid in
            await park.park(gen: gen, spoolId: sid)
        }

        viewModel.setHighlight(spoolId: 9)
        let parkedTask = viewModel._currentHighlightExpiryTask
        XCTAssertEqual(viewModel.highlightedSpoolId, 9)
        let priorGen = viewModel.highlightGeneration

        viewModel.invalidateHighlightOwnership()
        XCTAssertEqual(viewModel.highlightedSpoolId, 9,
                       "Invalidation must not preemptively clear the visible highlight")
        XCTAssertGreaterThan(viewModel.highlightGeneration, priorGen,
                             "Invalidation must advance generation to stale parked expiries")
        XCTAssertNil(viewModel._currentHighlightExpiryTask,
                     "Invalidation must release ownership of the expiry task")

        park.release(gen: priorGen, spoolId: 9)
        await parkedTask?.value

        XCTAssertEqual(viewModel.highlightedSpoolId, 9,
                       "Stale off-screen expiry must not clear the highlight")
    }

    func testClearHighlightInvalidatesGenerationAndClearsValue() async {
        // Legacy `clearHighlight()` must also invalidate any retained expiry
        // ownership so a parked task cannot later revive/clear stale state.
        let park = ExpiryPark()
        viewModel._highlightExpirySleepOverride = { gen, sid in
            await park.park(gen: gen, spoolId: sid)
        }

        viewModel.setHighlight(spoolId: 5)
        let parkedTask = viewModel._currentHighlightExpiryTask
        let priorGen = viewModel.highlightGeneration

        viewModel.clearHighlight()
        XCTAssertNil(viewModel.highlightedSpoolId)
        XCTAssertGreaterThan(viewModel.highlightGeneration, priorGen)

        park.release(gen: priorGen, spoolId: 5)
        await parkedTask?.value

        XCTAssertNil(viewModel.highlightedSpoolId)
    }

    // MARK: - Delete Spool

    func testDeleteSpoolSuccess() async {
        let spool = makeSpool(id: 1, material: "PLA")
        viewModel.spools = [spool, makeSpool(id: 2, material: "PETG")]

        await viewModel.deleteSpool(spool)

        XCTAssertEqual(viewModel.spools.count, 1)
        XCTAssertEqual(viewModel.spools.first?.id, 2)
        XCTAssertEqual(mockService.deleteSpoolCalledWith, 1)
        XCTAssertNil(viewModel.errorMessage)
    }

    func testDeleteSpoolError() async {
        let spool = makeSpool(id: 1, material: "PLA")
        viewModel.spools = [spool]
        mockService.errorToThrow = NetworkError.serverError(500)

        await viewModel.deleteSpool(spool)

        // Spool should remain on error
        XCTAssertEqual(viewModel.spools.count, 1)
        XCTAssertNotNil(viewModel.errorMessage)
    }

    func testDeleteWithoutConfigureDoesNotCrash() async {
        let unconfigured = SpoolInventoryViewModel()
        let spool = makeSpool(id: 1, material: "PLA")
        unconfigured.spools = [spool]

        await unconfigured.deleteSpool(spool)

        // Spool should remain since service isn't configured
        XCTAssertEqual(unconfigured.spools.count, 1)
    }

    // MARK: - NFC Scanning

    func testHandleNFCScanNotAvailable() {
        mockScanner.mockIsAvailable = false

        viewModel.handleNFCScan()

        XCTAssertEqual(viewModel.scanError, "NFC scanning is not available on this device.")
        XCTAssertFalse(viewModel.isScanning)
    }

    func testHandleNFCScanWithoutScannerConfigured() {
        let unconfigured = SpoolInventoryViewModel()
        unconfigured.configure(spoolService: mockService)

        unconfigured.handleNFCScan()

        XCTAssertNotNil(unconfigured.scanError)
    }

    // MARK: - Helpers

    private func makeSpool(
        id: Int,
        material: String,
        name: String = "Test Spool",
        inUse: Bool = false,
        archived: Bool? = false,
        vendor: String? = nil,
        filamentName: String? = nil,
        remainingWeightG: Double? = 500,
        initialWeightG: Double? = 1000
    ) -> SpoolmanSpool {
        SpoolmanSpool(
            id: id, name: name, material: material,
            colorHex: "#000000", inUse: inUse, filamentName: filamentName,
            vendor: vendor, registeredAt: nil, firstUsedAt: nil,
            lastUsedAt: nil, remainingWeightG: remainingWeightG,
            initialWeightG: initialWeightG, usedWeightG: nil,
            spoolWeightG: nil, remainingLengthMm: nil, usedLengthMm: nil,
            location: nil, lotNumber: nil, archived: archived,
            price: nil, comment: nil, hasNfcTag: nil, usedPercent: nil,
            remainingPercent: nil
        )
    }
}
