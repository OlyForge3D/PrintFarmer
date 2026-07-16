import XCTest
@testable import PrintFarmer

/// Tests for DeepLinkHandler: URL parsing for NFC printer tag deep links.
final class DeepLinkHandlerTests: XCTestCase {

    private let testId = UUID(uuidString: "550e8400-e29b-41d4-a716-446655440000")!

    // MARK: - Valid URLs

    func testParseValidPrinterURL() {
        let url = URL(string: "printfarmer://printer/550E8400-E29B-41D4-A716-446655440000")!
        let result = DeepLinkHandler.parse(url: url)
        XCTAssertEqual(result, .printerDetail(id: testId))
    }

    func testParseValidPrinterReadyURL() {
        let url = URL(string: "printfarmer://printer/550E8400-E29B-41D4-A716-446655440000/ready")!
        let result = DeepLinkHandler.parse(url: url)
        XCTAssertEqual(result, .printerReady(id: testId))
    }

    // MARK: - Invalid URLs

    func testParseInvalidScheme() {
        let url = URL(string: "https://printer/550E8400-E29B-41D4-A716-446655440000")!
        XCTAssertNil(DeepLinkHandler.parse(url: url))
    }

    func testParseUnknownHost() {
        let url = URL(string: "printfarmer://unknown/550E8400-E29B-41D4-A716-446655440000")!
        XCTAssertNil(DeepLinkHandler.parse(url: url))
    }

    func testParseInvalidUUID() {
        let url = URL(string: "printfarmer://printer/not-a-uuid")!
        XCTAssertNil(DeepLinkHandler.parse(url: url))
    }

    func testParseEmptyPath() {
        let url = URL(string: "printfarmer://printer")!
        XCTAssertNil(DeepLinkHandler.parse(url: url))
    }

    // MARK: - Edge Cases

    func testParseReadyCaseInsensitive() {
        let url = URL(string: "printfarmer://printer/550E8400-E29B-41D4-A716-446655440000/READY")!
        let result = DeepLinkHandler.parse(url: url)
        XCTAssertEqual(result, .printerReady(id: testId))
    }

    func testParseExtraPathComponents() {
        let url = URL(string: "printfarmer://printer/550E8400-E29B-41D4-A716-446655440000/ready/extra")!
        let result = DeepLinkHandler.parse(url: url)
        XCTAssertEqual(result, .printerReady(id: testId))
    }

    // MARK: - #714 Item C: spool ID must be a positive integer

    func testParseValidPositiveSpoolURL() {
        let url = URL(string: "printfarmer://spool/42")!
        XCTAssertEqual(DeepLinkHandler.parse(url: url), .spoolDetail(id: 42))
    }

    func testParseZeroSpoolIDIsRejected() {
        let url = URL(string: "printfarmer://spool/0")!
        XCTAssertNil(DeepLinkHandler.parse(url: url), "a zero spool ID must never be treated as a valid destination")
    }

    func testParseNegativeSpoolIDIsRejected() {
        let url = URL(string: "printfarmer://spool/-5")!
        XCTAssertNil(DeepLinkHandler.parse(url: url), "a negative spool ID must never be treated as a valid destination")
    }

    func testParseNonNumericSpoolPathIsRejected() {
        let url = URL(string: "printfarmer://spool/not-a-number")!
        XCTAssertNil(DeepLinkHandler.parse(url: url))
    }
}
