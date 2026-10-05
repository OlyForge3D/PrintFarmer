import XCTest
@testable import PrintFarmer

private actor DelayedSpoolLookupService: SpoolServiceProtocol {
    let result: SpoolmanPagedResult<SpoolmanSpool>
    let switchAuthority: @MainActor @Sendable () -> Void

    init(
        result: SpoolmanPagedResult<SpoolmanSpool>,
        switchAuthority: @escaping @MainActor @Sendable () -> Void
    ) {
        self.result = result
        self.switchAuthority = switchAuthority
    }

    func listSpools(
        limit: Int, offset: Int, search: String?, material: String?, vendor: String?
    ) async throws -> SpoolmanPagedResult<SpoolmanSpool> {
        await switchAuthority()
        return result
    }

    func createSpool(_ request: SpoolmanSpoolRequest) async throws -> SpoolmanSpool { throw NetworkError.notFound }
    func updateSpool(id: Int, _ request: SpoolmanSpoolRequest) async throws -> SpoolmanSpool { throw NetworkError.notFound }
    func deleteSpool(id: Int) async throws {}
    func listFilaments() async throws -> [SpoolmanFilament] { [] }
    func createFilament(_ request: SpoolmanFilamentRequest) async throws -> SpoolmanFilament { throw NetworkError.notFound }
    func listVendors() async throws -> [SpoolmanVendor] { [] }
    func listMaterials() async throws -> [SpoolmanMaterial] { [] }
    func listAvailableMaterials() async throws -> [String] { [] }
}

@MainActor
private final class SpoolLookupAuthorityState {
    var current: PrinterDetailSpoolLookupAuthority

    init(_ authority: PrinterDetailSpoolLookupAuthority) {
        current = authority
    }
}

@MainActor
final class PrinterDetailSpoolLookupTests: XCTestCase {
    func testLateLookupResultIsDiscardedAfterAuthoritySwitch() async throws {
        let result = SpoolmanPagedResult(
            items: [try TestData.decodeSpoolmanSpool()],
            totalCount: 1
        )
        let authority = PrinterDetailSpoolLookupAuthority(
            serverID: UUID(), userID: UUID(), generation: 4,
            printerID: UUID(), spoolIDs: [1]
        )
        let authorityState = SpoolLookupAuthorityState(authority)
        let lookup = PrinterDetailSpoolLookup()
        let service = DelayedSpoolLookupService(result: result) {
            authorityState.current = PrinterDetailSpoolLookupAuthority(
                serverID: UUID(), userID: authority.userID, generation: authority.generation + 1,
                printerID: authority.printerID, spoolIDs: authority.spoolIDs
            )
        }

        await lookup.load(
            service: service,
            authority: authority,
            isCurrent: { authorityState.current == authority }
        )

        guard case .idle = lookup.state else {
            return XCTFail("A result from the previous server generation must not be retained.")
        }
    }

    func testLookupDistinguishesMissingAndInvalidWeightCapacity() {
        let noWeights = SpoolWeightMeter(remainingGrams: nil, initialGrams: nil)
        XCTAssertNil(noWeights.fraction)
        XCTAssertEqual(noWeights.label, "Remaining weight unavailable")

        let noCapacity = SpoolWeightMeter(remainingGrams: 84, initialGrams: nil)
        XCTAssertNil(noCapacity.fraction)
        XCTAssertEqual(noCapacity.label, "84 g remaining · starting weight unavailable")

        let zeroCapacity = SpoolWeightMeter(remainingGrams: 84, initialGrams: 0)
        XCTAssertNil(zeroCapacity.fraction)
        XCTAssertTrue(zeroCapacity.label.contains("starting weight unavailable"))

        let inconsistent = SpoolWeightMeter(remainingGrams: 1200, initialGrams: 1000)
        XCTAssertNil(inconsistent.fraction)
        XCTAssertTrue(inconsistent.label.contains("inconsistent"))
    }

    func testWeightMeterUsesOnlyTheRecordedInitialAndRemainingWeights() {
        let meter = SpoolWeightMeter(remainingGrams: 84, initialGrams: 1000)

        XCTAssertEqual(meter.fraction ?? .nan, 0.084, accuracy: 0.0001)
        XCTAssertTrue(meter.label.contains("84 g"))
        XCTAssertTrue(meter.label.contains("remaining"))
    }
}
