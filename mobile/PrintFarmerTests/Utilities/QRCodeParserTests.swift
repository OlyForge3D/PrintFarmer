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

    // MARK: - UPC-E normalization

    func testUPCEEightDigitPayloadExpandsToValidUPCA() {
        let expanded = ScannedBarcodeNormalizer.expandUPCE("01234565")
        XCTAssertEqual(expanded, "012345000065")
        XCTAssertTrue(ScanViewModel.isSupportedProductBarcode(expanded ?? ""))
        XCTAssertNotEqual(
            ScannedBarcodeNormalizer.payload("01234565", isUPCE: true), "01234565",
            "the compressed UPC-E form can coincidentally pass the EAN-8 check, so it must be expanded"
        )
    }

    func testUPCESixAndSevenDigitFormsComputeTheCheckDigit() {
        XCTAssertEqual(ScannedBarcodeNormalizer.expandUPCE("123456"), "012345000065")
        XCTAssertEqual(ScannedBarcodeNormalizer.expandUPCE("0123456"), "012345000065")
    }

    func testUPCEExpansionCoversEveryLastDigitRule() {
        XCTAssertEqual(ScannedBarcodeNormalizer.expandUPCE("01234505"), "012000003455")
        XCTAssertEqual(ScannedBarcodeNormalizer.expandUPCE("01234531"), "012300000451")
        XCTAssertEqual(ScannedBarcodeNormalizer.expandUPCE("01234543"), "012340000053")
        XCTAssertEqual(ScannedBarcodeNormalizer.expandUPCE("01234565"), "012345000065")
    }

    func testUPCENumberSystemOneExpandsToAValidGTIN() throws {
        let expanded = try XCTUnwrap(ScannedBarcodeNormalizer.expandUPCE("1234565"))
        XCTAssertTrue(expanded.hasPrefix("1"))
        XCTAssertEqual(expanded.count, 12)
        XCTAssertTrue(ScanViewModel.isSupportedProductBarcode(expanded))
    }

    func testUPCERejectsBadCheckDigitNumberSystemAndShape() {
        XCTAssertNil(ScannedBarcodeNormalizer.expandUPCE("01234566"), "wrong check digit")
        XCTAssertNil(ScannedBarcodeNormalizer.expandUPCE("21234565"), "number system must be 0 or 1")
        XCTAssertNil(ScannedBarcodeNormalizer.expandUPCE("12345"))
        XCTAssertNil(ScannedBarcodeNormalizer.expandUPCE("ABCDEF"))
    }

    func testNormalizerOnlyExpandsUPCEPayloads() {
        XCTAssertEqual(ScannedBarcodeNormalizer.payload("01234565", isUPCE: true), "012345000065")
        XCTAssertEqual(ScannedBarcodeNormalizer.payload("01234565", isUPCE: false), "01234565")
        XCTAssertEqual(
            ScannedBarcodeNormalizer.payload("SPL-AB12", isUPCE: true), "SPL-AB12",
            "an unexpandable payload passes through so it still reaches the Unrecognized result"
        )
    }
}
