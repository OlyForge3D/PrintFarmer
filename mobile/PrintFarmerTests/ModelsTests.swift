import XCTest
@testable import PrintFarmer

final class ModelsTests: XCTestCase {
    private let decoder: JSONDecoder = {
        let d = JSONDecoder()
        d.dateDecodingStrategy = .iso8601
        return d
    }()

    func testPrinterDecoding() throws {
        let json = """
        {
            "id": "550e8400-e29b-41d4-a716-446655440000",
            "name": "Prusa MK4",
            "backend": "Moonraker",
            "backendPort": 7125,
            "isOnline": true,
            "inMaintenance": false,
            "isEnabled": true
        }
        """
        let jsonData = Data(json.utf8)

        let printer = try decoder.decode(Printer.self, from: jsonData)
        XCTAssertEqual(printer.name, "Prusa MK4")
        XCTAssertEqual(printer.backend, .moonraker)
        XCTAssertTrue(printer.isOnline)
    }

    func testPrinterDecodesWithMissingOptionalFields() throws {
        // Minimal JSON — only id and name required, everything else has defaults
        let json = """
        {
            "id": "550e8400-e29b-41d4-a716-446655440000",
            "name": "Bare Printer"
        }
        """
        let jsonData = Data(json.utf8)

        let printer = try decoder.decode(Printer.self, from: jsonData)
        XCTAssertEqual(printer.name, "Bare Printer")
        XCTAssertEqual(printer.backend, .unknown)
        XCTAssertEqual(printer.backendPort, 80)
        XCTAssertFalse(printer.inMaintenance)
        XCTAssertTrue(printer.isEnabled)
        XCTAssertFalse(printer.isOnline)
    }

    func testPrintJobStatusEnum() {
        XCTAssertEqual(PrintJobStatus.printing.rawValue, "Printing")
        XCTAssertEqual(PrintJobStatus.completed.rawValue, "Completed")
    }
}
