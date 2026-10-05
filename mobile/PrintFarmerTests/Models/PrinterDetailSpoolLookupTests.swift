import XCTest
@testable import PrintFarmer

@MainActor
final class PrinterDetailSpoolLookupTests: XCTestCase {
    func testLateLookupResultIsDiscardedAfterAuthoritySwitch() async throws {
        let mockAPIClient = MockAPIClient()
        let entered = AsyncBarrier()
        let release = AsyncBarrier()
        mockAPIClient.asyncRequestHandler = { request in
            await entered.arriveAndWait()
            await release.arriveAndWait()
            let json = #"{"items":[{"id":8,"name":"Assigned spool","material":"PLA","initialWeightG":1000,"remainingWeightG":84}],"totalCount":1}"#
            return (TestData.httpResponse(url: request.url, statusCode: 200), Data(json.utf8))
        }
        let service = SpoolService(apiClient: mockAPIClient.apiClient)
        let lookup = PrinterDetailSpoolLookup()
        let authority = PrinterDetailSpoolLookupAuthority(
            serverID: UUID(), userID: UUID(), generation: 4,
            printerID: UUID(), spoolIDs: [8]
        )
        var currentAuthority = authority
        let request = Task {
            await lookup.load(
                service: service,
                authority: authority,
                isCurrent: { currentAuthority == authority }
            )
        }

        await entered.waitUntilArrived()
        currentAuthority = PrinterDetailSpoolLookupAuthority(
            serverID: UUID(), userID: authority.userID, generation: authority.generation + 1,
            printerID: authority.printerID, spoolIDs: authority.spoolIDs
        )
        await release.release()
        await request.value

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
