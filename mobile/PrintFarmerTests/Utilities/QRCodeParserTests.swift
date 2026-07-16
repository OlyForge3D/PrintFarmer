import XCTest
@testable import PrintFarmer

/// Tests for QRCodeParser: URL formats, plain numeric, JSON, and invalid inputs.
final class QRCodeParserTests: XCTestCase {

    // MARK: - URL Format

    func testURLFormatFullURL() {
        let result = QRCodeParser.parse("https://spoolman.example.com/spools/42")
        XCTAssertEqual(result, 42)
    }

    func testURLFormatTrailingSlash() {
        let result = QRCodeParser.parse("https://spoolman.example.com/spools/42/")
        XCTAssertEqual(result, 42)
    }

    func testURLFormatLocalhost() {
        let result = QRCodeParser.parse("http://localhost:7912/spools/123")
        XCTAssertEqual(result, 123)
    }

    func testURLFormatHTTP() {
        let result = QRCodeParser.parse("http://10.0.0.5/spools/7")
        XCTAssertEqual(result, 7)
    }

    func testURLFormatPathOnly() {
        let result = QRCodeParser.parse("/spools/99")
        XCTAssertEqual(result, 99)
    }

    func testURLFormatNestedPath() {
        let result = QRCodeParser.parse("https://example.com/api/v1/spools/55")
        XCTAssertEqual(result, 55)
    }

    // MARK: - Final remediation Blocker 5: singular /spool/ and printfarmer:// forms

    func testURLFormatSingularSpoolPath() {
        let result = QRCodeParser.parse("https://spoolman.example.com/spool/42")
        XCTAssertEqual(result, 42)
    }

    func testURLFormatSingularSpoolPathOnly() {
        let result = QRCodeParser.parse("/spool/17")
        XCTAssertEqual(result, 17)
    }

    func testDeepLinkSchemeSingularSpoolHost() {
        // `printfarmer://spool/{id}` — "spool" lands in the URL's host
        // position (there's no path segment before it), not a path
        // component, so a purely path-component-based scan would miss it.
        let result = QRCodeParser.parse("printfarmer://spool/42")
        XCTAssertEqual(result, 42)
    }

    func testDeepLinkSchemePluralSpoolsHost() {
        let result = QRCodeParser.parse("printfarmer://spools/42")
        XCTAssertEqual(result, 42)
    }

    func testDeepLinkSchemeEmbeddedHostIsIgnoredWhenNotSpool() {
        XCTAssertNil(QRCodeParser.parse("printfarmer://printer/42"))
    }

    // MARK: - Final remediation Blocker 5: parseStructured excludes bare numeric

    func testParseStructuredRejectsBarePositiveInteger() {
        // A bare numeric string is ambiguous with a genuine EAN/UPC barcode
        // — `parseStructured` must never treat it as a spool ID; only
        // `parse` (the QR-only-scanner umbrella) does.
        XCTAssertNil(QRCodeParser.parseStructured("42"))
        XCTAssertNotNil(QRCodeParser.parse("42"))
    }

    func testParseStructuredAcceptsURLForm() {
        XCTAssertEqual(QRCodeParser.parseStructured("https://host/spools/42"), 42)
    }

    func testParseStructuredAcceptsSingularSpoolURLForm() {
        XCTAssertEqual(QRCodeParser.parseStructured("https://host/spool/42"), 42)
    }

    func testParseStructuredAcceptsDeepLinkForm() {
        XCTAssertEqual(QRCodeParser.parseStructured("printfarmer://spool/42"), 42)
    }

    func testParseStructuredAcceptsJSONForm() {
        XCTAssertEqual(QRCodeParser.parseStructured("""
        {"spoolId": 42}
        """), 42)
    }

    func testParseStructuredRejectsRandomText() {
        XCTAssertNil(QRCodeParser.parseStructured("hello world"))
    }

    // MARK: - Plain Numeric

    func testPlainNumeric() {
        XCTAssertEqual(QRCodeParser.parse("42"), 42)
    }

    func testPlainNumericWithWhitespace() {
        XCTAssertEqual(QRCodeParser.parse("  42  "), 42)
    }

    func testPlainNumericLargeID() {
        XCTAssertEqual(QRCodeParser.parse("999999"), 999999)
    }

    func testPlainNumericOne() {
        XCTAssertEqual(QRCodeParser.parse("1"), 1)
    }

    // MARK: - JSON Format

    func testJSONSpoolId() {
        let result = QRCodeParser.parse("""
        {"spoolId": 42}
        """)
        XCTAssertEqual(result, 42)
    }

    func testJSONWithExtraFields() {
        let result = QRCodeParser.parse("""
        {"spoolId": 42, "name": "PLA"}
        """)
        XCTAssertEqual(result, 42)
    }

    func testJSONSnakeCaseSpoolId() {
        let result = QRCodeParser.parse("""
        {"spool_id": 88}
        """)
        XCTAssertEqual(result, 88)
    }

    func testJSONIdField() {
        let result = QRCodeParser.parse("""
        {"id": 15}
        """)
        XCTAssertEqual(result, 15)
    }

    func testJSONStringValue() {
        // Parser accepts string-typed IDs too
        let result = QRCodeParser.parse("""
        {"spoolId": "73"}
        """)
        XCTAssertEqual(result, 73)
    }

    // MARK: - Invalid Inputs

    func testEmptyString() {
        XCTAssertNil(QRCodeParser.parse(""))
    }

    func testWhitespaceOnly() {
        XCTAssertNil(QRCodeParser.parse("   "))
    }

    func testRandomText() {
        XCTAssertNil(QRCodeParser.parse("hello world"))
    }

    func testURLWithoutSpoolPath() {
        XCTAssertNil(QRCodeParser.parse("https://example.com/settings"))
    }

    func testNegativeNumber() {
        XCTAssertNil(QRCodeParser.parse("-5"))
    }

    func testZero() {
        // QRCodeParser rejects 0 (requires id > 0)
        XCTAssertNil(QRCodeParser.parse("0"))
    }

    func testMalformedJSON() {
        XCTAssertNil(QRCodeParser.parse("{spoolId: 42}"))
    }

    func testJSONWithoutIdField() {
        XCTAssertNil(QRCodeParser.parse("""
        {"name": "PLA", "color": "red"}
        """))
    }

    func testURLWithZeroId() {
        XCTAssertNil(QRCodeParser.parse("https://example.com/spools/0"))
    }

    func testURLWithNegativeId() {
        XCTAssertNil(QRCodeParser.parse("https://example.com/spools/-1"))
    }

    func testJSONWithNegativeId() {
        XCTAssertNil(QRCodeParser.parse("""
        {"spoolId": -3}
        """))
    }

    func testFloatingPointNumber() {
        XCTAssertNil(QRCodeParser.parse("42.5"))
    }
}
