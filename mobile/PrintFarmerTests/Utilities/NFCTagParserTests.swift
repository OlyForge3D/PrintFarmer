import XCTest
@testable import PrintFarmer

/// Tests for NFCTagParser: OpenSpool/OpenPrintTag parsing, payload creation, and edge cases.
final class NFCTagParserTests: XCTestCase {

    // MARK: - OpenSpool: All Fields

    func testOpenSpoolAllFields() throws {
        let json: [String: Any] = [
            "material": "PLA",
            "color_hex": "#FF5733",
            "brand": "Prusament",
            "weight_g": 1000.0,
            "diameter_mm": 1.75,
            "temp_c": 215,
            "spoolman_id": 42
        ]
        let data = try JSONSerialization.data(withJSONObject: json)

        let result = NFCTagParser.parseOpenSpool(data)

        XCTAssertNotNil(result)
        XCTAssertEqual(result?.material, "PLA")
        XCTAssertEqual(result?.colorHex, "#FF5733")
        XCTAssertEqual(result?.vendor, "Prusament")
        XCTAssertEqual(result?.weight, 1000.0)
        XCTAssertEqual(result?.diameter, 1.75)
        XCTAssertEqual(result?.temperature, 215)
        XCTAssertEqual(result?.spoolmanId, 42)
    }

    // MARK: - OpenSpool: Partial Fields

    func testOpenSpoolMaterialOnly() throws {
        let json: [String: Any] = ["material": "PETG"]
        let data = try JSONSerialization.data(withJSONObject: json)

        let result = NFCTagParser.parseOpenSpool(data)

        XCTAssertNotNil(result)
        XCTAssertEqual(result?.material, "PETG")
        XCTAssertNil(result?.colorHex)
        XCTAssertNil(result?.vendor)
        XCTAssertNil(result?.weight)
        XCTAssertNil(result?.diameter)
        XCTAssertNil(result?.temperature)
        XCTAssertNil(result?.spoolmanId)
    }

    func testOpenSpoolColorOnly() throws {
        let json: [String: Any] = ["color_hex": "#000000"]
        let data = try JSONSerialization.data(withJSONObject: json)

        let result = NFCTagParser.parseOpenSpool(data)

        XCTAssertNotNil(result)
        XCTAssertNil(result?.material)
        XCTAssertEqual(result?.colorHex, "#000000")
    }

    func testOpenSpoolSpoolmanIdOnly() throws {
        let json: [String: Any] = ["spoolman_id": 99]
        let data = try JSONSerialization.data(withJSONObject: json)

        let result = NFCTagParser.parseOpenSpool(data)

        XCTAssertNotNil(result)
        XCTAssertEqual(result?.spoolmanId, 99)
        XCTAssertNil(result?.material)
    }

    // MARK: - OpenSpool: Edge Cases

    func testOpenSpoolEmptyJSON() throws {
        let data = try JSONSerialization.data(withJSONObject: [String: Any]())

        let result = NFCTagParser.parseOpenSpool(data)

        // Empty JSON returns a ScannedSpoolData with all nils
        XCTAssertNotNil(result)
        XCTAssertNil(result?.material)
        XCTAssertNil(result?.spoolmanId)
    }

    func testOpenSpoolInvalidData() {
        let data = Data("not json".utf8)
        XCTAssertNil(NFCTagParser.parseOpenSpool(data))
    }

    func testOpenSpoolEmptyData() {
        let data = Data()
        XCTAssertNil(NFCTagParser.parseOpenSpool(data))
    }

    func testOpenSpoolIntegerWeightAsInt() throws {
        let json: [String: Any] = ["weight_g": 800]
        let data = try JSONSerialization.data(withJSONObject: json)

        let result = NFCTagParser.parseOpenSpool(data)

        XCTAssertEqual(result?.weight, 800.0)
    }

    func testOpenSpoolStringTypedValues() throws {
        let json: [String: Any] = [
            "weight_g": "750.5",
            "temp_c": "210",
            "spoolman_id": "42"
        ]
        let data = try JSONSerialization.data(withJSONObject: json)

        let result = NFCTagParser.parseOpenSpool(data)

        XCTAssertEqual(result?.weight, 750.5)
        XCTAssertEqual(result?.temperature, 210)
        XCTAssertEqual(result?.spoolmanId, 42)
    }

    // MARK: - OpenPrintTag: All Fields

    func testOpenPrintTagAllFields() throws {
        let json: [String: Any] = [
            "filament_type": "ABS",
            "color": "#222222",
            "manufacturer": "Hatchbox",
            "net_weight": 750.0,
            "filament_diameter": 1.75,
            "print_temp": 240,
            "spool_id": 88
        ]
        let data = try JSONSerialization.data(withJSONObject: json)

        let result = NFCTagParser.parseOpenPrintTag(data)

        XCTAssertNotNil(result)
        XCTAssertEqual(result?.material, "ABS")
        XCTAssertEqual(result?.colorHex, "#222222")
        XCTAssertEqual(result?.vendor, "Hatchbox")
        XCTAssertEqual(result?.weight, 750.0)
        XCTAssertEqual(result?.diameter, 1.75)
        XCTAssertEqual(result?.temperature, 240)
        XCTAssertEqual(result?.spoolmanId, 88)
    }

    // MARK: - OpenPrintTag: Partial Fields

    func testOpenPrintTagMaterialOnly() throws {
        let json: [String: Any] = ["filament_type": "TPU"]
        let data = try JSONSerialization.data(withJSONObject: json)

        let result = NFCTagParser.parseOpenPrintTag(data)

        XCTAssertNotNil(result)
        XCTAssertEqual(result?.material, "TPU")
        XCTAssertNil(result?.vendor)
        XCTAssertNil(result?.spoolmanId)
    }

    func testOpenPrintTagManufacturerAndColor() throws {
        let json: [String: Any] = [
            "manufacturer": "eSUN",
            "color": "#FFFFFF"
        ]
        let data = try JSONSerialization.data(withJSONObject: json)

        let result = NFCTagParser.parseOpenPrintTag(data)

        XCTAssertNotNil(result)
        XCTAssertEqual(result?.vendor, "eSUN")
        XCTAssertEqual(result?.colorHex, "#FFFFFF")
        XCTAssertNil(result?.material)
    }

    // MARK: - OpenPrintTag: Edge Cases

    func testOpenPrintTagInvalidData() {
        let data = Data([0xFF, 0xFE, 0x00])
        XCTAssertNil(NFCTagParser.parseOpenPrintTag(data))
    }

    func testOpenPrintTagArrayJSON() throws {
        let data = try JSONSerialization.data(withJSONObject: [1, 2, 3])
        XCTAssertNil(NFCTagParser.parseOpenPrintTag(data))
    }

    // MARK: - Payload Creation

    func testCreateOpenSpoolPayloadRoundTrip() throws {
        let spool = SpoolmanSpool(
            id: 42,
            name: "Black PLA",
            material: "PLA",
            colorHex: "#000000",
            inUse: true,
            filamentName: "Prusament PLA",
            vendor: "Prusa Research",
            registeredAt: nil,
            firstUsedAt: nil,
            lastUsedAt: nil,
            remainingWeightG: 750.0,
            initialWeightG: 1000.0,
            usedWeightG: 250.0,
            spoolWeightG: 200.0,
            remainingLengthMm: nil,
            usedLengthMm: nil,
            location: nil,
            lotNumber: nil,
            archived: false,
            price: nil,
            comment: nil,
            hasNfcTag: nil,
            usedPercent: nil,
            remainingPercent: nil
        )

        let payload = NFCTagParser.createOpenSpoolPayload(from: spool)
        XCTAssertNotNil(payload)

        let parsed = NFCTagParser.parseOpenSpool(payload!)
        XCTAssertNotNil(parsed)
        XCTAssertEqual(parsed?.material, "PLA")
        XCTAssertEqual(parsed?.colorHex, "#000000")
        XCTAssertEqual(parsed?.vendor, "Prusa Research")
        XCTAssertEqual(parsed?.weight, 1000.0)
        XCTAssertEqual(parsed?.spoolmanId, 42)
    }

    func testCreateOpenSpoolPayloadMinimalFields() throws {
        let spool = SpoolmanSpool(
            id: 7,
            name: "Generic",
            material: "PETG",
            colorHex: nil,
            inUse: false,
            filamentName: nil,
            vendor: nil,
            registeredAt: nil,
            firstUsedAt: nil,
            lastUsedAt: nil,
            remainingWeightG: nil,
            initialWeightG: nil,
            usedWeightG: nil,
            spoolWeightG: nil,
            remainingLengthMm: nil,
            usedLengthMm: nil,
            location: nil,
            lotNumber: nil,
            archived: nil,
            price: nil,
            comment: nil,
            hasNfcTag: nil,
            usedPercent: nil,
            remainingPercent: nil
        )

        let payload = NFCTagParser.createOpenSpoolPayload(from: spool)
        XCTAssertNotNil(payload)

        let parsed = NFCTagParser.parseOpenSpool(payload!)
        XCTAssertNotNil(parsed)
        XCTAssertEqual(parsed?.material, "PETG")
        XCTAssertEqual(parsed?.spoolmanId, 7)
        XCTAssertNil(parsed?.colorHex)
        XCTAssertNil(parsed?.vendor)
        XCTAssertNil(parsed?.weight)
    }

    func testCreateOpenSpoolPayloadIsValidJSON() throws {
        let spool = SpoolmanSpool(
            id: 1,
            name: "Test",
            material: "PLA",
            colorHex: "#FF0000",
            inUse: false,
            filamentName: nil,
            vendor: "TestVendor",
            registeredAt: nil,
            firstUsedAt: nil,
            lastUsedAt: nil,
            remainingWeightG: nil,
            initialWeightG: 500.0,
            usedWeightG: nil,
            spoolWeightG: nil,
            remainingLengthMm: nil,
            usedLengthMm: nil,
            location: nil,
            lotNumber: nil,
            archived: nil,
            price: nil,
            comment: nil,
            hasNfcTag: nil,
            usedPercent: nil,
            remainingPercent: nil
        )

        let payload = NFCTagParser.createOpenSpoolPayload(from: spool)!
        let json = try JSONSerialization.jsonObject(with: payload) as? [String: Any]
        XCTAssertNotNil(json)
        XCTAssertEqual(json?["material"] as? String, "PLA")
        XCTAssertEqual(json?["color_hex"] as? String, "#FF0000")
        XCTAssertEqual(json?["brand"] as? String, "TestVendor")
        XCTAssertEqual(json?["spoolman_id"] as? Int, 1)
    }

    // MARK: - OpenTag3D: Payload Creation

    func testCreateOpenTag3DPayload_basicSpool() {
        let spool = SpoolmanSpool(
            id: 42,
            name: "White PLA",
            material: "PLA",
            colorHex: "#FFFFFF",
            inUse: true,
            filamentName: "Prusament PLA",
            vendor: "Prusament",
            registeredAt: nil,
            firstUsedAt: nil,
            lastUsedAt: nil,
            remainingWeightG: 750.0,
            initialWeightG: 1000.0,
            usedWeightG: 250.0,
            spoolWeightG: 200.0,
            remainingLengthMm: nil,
            usedLengthMm: nil,
            location: nil,
            lotNumber: nil,
            archived: false,
            price: nil,
            comment: nil,
            hasNfcTag: nil,
            usedPercent: nil,
            remainingPercent: nil
        )

        let payload = NFCTagParser.createOpenTag3DPayload(from: spool)
        XCTAssertNotNil(payload)
        guard let data = payload else { return }

        // Minimum payload length: 0x66 = 102 bytes
        XCTAssertGreaterThanOrEqual(data.count, 0x66)

        // Tag version at offset 0x00: UInt16 BE = 1000
        let version = UInt16(data[0x00]) << 8 | UInt16(data[0x01])
        XCTAssertEqual(version, 1000)

        // Base Material at offset 0x02: "PLA" null-padded to 5 bytes
        let materialBytes = data[0x02..<0x07]
        let material = String(bytes: materialBytes.prefix(while: { $0 != 0 }), encoding: .utf8)
        XCTAssertEqual(material, "PLA")

        // Color 1 RGBA at offset 0x4B: white = (255, 255, 255, 255)
        XCTAssertEqual(data[0x4B], 255) // R
        XCTAssertEqual(data[0x4C], 255) // G
        XCTAssertEqual(data[0x4D], 255) // B
        XCTAssertEqual(data[0x4E], 255) // A

        // Weight at offset 0x5E: UInt16 BE = 1000 grams
        let weight = UInt16(data[0x5E]) << 8 | UInt16(data[0x5F])
        XCTAssertEqual(weight, 1000)
    }

    func testCreateOpenTag3DPayload_colorParsing() {
        // Test #FF6600 → RGBA (255, 102, 0, 255)
        let orangeSpool = SpoolmanSpool(
            id: 1,
            name: "Orange",
            material: "PLA",
            colorHex: "#FF6600",
            inUse: false,
            filamentName: nil,
            vendor: nil,
            registeredAt: nil,
            firstUsedAt: nil,
            lastUsedAt: nil,
            remainingWeightG: nil,
            initialWeightG: nil,
            usedWeightG: nil,
            spoolWeightG: nil,
            remainingLengthMm: nil,
            usedLengthMm: nil,
            location: nil,
            lotNumber: nil,
            archived: nil,
            price: nil,
            comment: nil,
            hasNfcTag: nil,
            usedPercent: nil,
            remainingPercent: nil
        )

        let orangePayload = NFCTagParser.createOpenTag3DPayload(from: orangeSpool)
        XCTAssertNotNil(orangePayload)
        if let data = orangePayload {
            XCTAssertEqual(data[0x4B], 255)  // R
            XCTAssertEqual(data[0x4C], 102)  // G
            XCTAssertEqual(data[0x4D], 0)    // B
            XCTAssertEqual(data[0x4E], 255)  // A
        }

        // Test nil color → default gray (128, 128, 128, 255)
        let noColorSpool = SpoolmanSpool(
            id: 2,
            name: "No Color",
            material: "PLA",
            colorHex: nil,
            inUse: false,
            filamentName: nil,
            vendor: nil,
            registeredAt: nil,
            firstUsedAt: nil,
            lastUsedAt: nil,
            remainingWeightG: nil,
            initialWeightG: nil,
            usedWeightG: nil,
            spoolWeightG: nil,
            remainingLengthMm: nil,
            usedLengthMm: nil,
            location: nil,
            lotNumber: nil,
            archived: nil,
            price: nil,
            comment: nil,
            hasNfcTag: nil,
            usedPercent: nil,
            remainingPercent: nil
        )

        let grayPayload = NFCTagParser.createOpenTag3DPayload(from: noColorSpool)
        XCTAssertNotNil(grayPayload)
        if let data = grayPayload {
            XCTAssertEqual(data[0x4B], 128)  // R
            XCTAssertEqual(data[0x4C], 128)  // G
            XCTAssertEqual(data[0x4D], 128)  // B
            XCTAssertEqual(data[0x4E], 255)  // A
        }
    }

    func testCreateOpenTag3DPayload_stringTruncation() {
        let spool = SpoolmanSpool(
            id: 1,
            name: "Test",
            material: "NYLON1",  // 6 chars → truncated to 5
            colorHex: nil,
            inUse: false,
            filamentName: nil,
            vendor: "Very Long Manufa Extra",  // 22 chars → truncated to 16
            registeredAt: nil,
            firstUsedAt: nil,
            lastUsedAt: nil,
            remainingWeightG: nil,
            initialWeightG: nil,
            usedWeightG: nil,
            spoolWeightG: nil,
            remainingLengthMm: nil,
            usedLengthMm: nil,
            location: nil,
            lotNumber: nil,
            archived: nil,
            price: nil,
            comment: nil,
            hasNfcTag: nil,
            usedPercent: nil,
            remainingPercent: nil
        )

        let payload = NFCTagParser.createOpenTag3DPayload(from: spool)
        XCTAssertNotNil(payload)
        guard let data = payload else { return }

        // Material at offset 0x02: "NYLON1" truncated to 5 bytes → "NYLON"
        let materialBytes = data[0x02..<0x07]
        let material = String(bytes: materialBytes.prefix(while: { $0 != 0 }), encoding: .utf8)
        XCTAssertEqual(material, "NYLON")

        // Manufacturer at offset 0x1B: truncated to 16 bytes → "Very Long Manufa"
        let mfgBytes = data[0x1B..<0x2B]
        let manufacturer = String(bytes: mfgBytes.prefix(while: { $0 != 0 }), encoding: .utf8)
        XCTAssertEqual(manufacturer, "Very Long Manufa")
    }

    func testCreateOpenTag3DPayload_stringPadding() {
        let spool = SpoolmanSpool(
            id: 1,
            name: "Test",
            material: "PLA",  // 3 chars → null-padded to 5
            colorHex: nil,
            inUse: false,
            filamentName: nil,
            vendor: "eSUN",  // 4 chars → null-padded to 16
            registeredAt: nil,
            firstUsedAt: nil,
            lastUsedAt: nil,
            remainingWeightG: nil,
            initialWeightG: nil,
            usedWeightG: nil,
            spoolWeightG: nil,
            remainingLengthMm: nil,
            usedLengthMm: nil,
            location: nil,
            lotNumber: nil,
            archived: nil,
            price: nil,
            comment: nil,
            hasNfcTag: nil,
            usedPercent: nil,
            remainingPercent: nil
        )

        let payload = NFCTagParser.createOpenTag3DPayload(from: spool)
        XCTAssertNotNil(payload)
        guard let data = payload else { return }

        // Material: "PLA" = [0x50, 0x4C, 0x41] + 2 null bytes
        XCTAssertEqual(data[0x02], 0x50) // P
        XCTAssertEqual(data[0x03], 0x4C) // L
        XCTAssertEqual(data[0x04], 0x41) // A
        XCTAssertEqual(data[0x05], 0x00) // null pad
        XCTAssertEqual(data[0x06], 0x00) // null pad

        // Manufacturer: "eSUN" = [0x65, 0x53, 0x55, 0x4E] + 12 null bytes
        XCTAssertEqual(data[0x1B], 0x65) // e
        XCTAssertEqual(data[0x1C], 0x53) // S
        XCTAssertEqual(data[0x1D], 0x55) // U
        XCTAssertEqual(data[0x1E], 0x4E) // N
        for offset in 0x1F..<0x2B {
            XCTAssertEqual(data[offset], 0x00,
                "Expected null padding at offset \(String(format: "0x%02X", offset))")
        }
    }

    // MARK: - OpenTag3D: Parsing

    func testParseOpenTag3D_validPayload() {
        // Build a valid binary payload manually (112 bytes = Core size)
        var data = Data(count: 0x70)

        // Tag version: 1000 = 0x03E8
        data[0x00] = 0x03
        data[0x01] = 0xE8

        // Base Material: "PETG" at offset 0x02
        let petg: [UInt8] = [0x50, 0x45, 0x54, 0x47, 0x00]
        for (i, b) in petg.enumerated() { data[0x02 + i] = b }

        // Manufacturer: "Hatchbox" at offset 0x1B
        for (i, b) in "Hatchbox".utf8.enumerated() { data[0x1B + i] = b }

        // Color 1 RGBA at 0x4B: red (255, 0, 0, 255)
        data[0x4B] = 0xFF
        data[0x4C] = 0x00
        data[0x4D] = 0x00
        data[0x4E] = 0xFF

        // Diameter at 0x5C: 1750µm = 0x06D6
        data[0x5C] = 0x06
        data[0x5D] = 0xD6

        // Weight at 0x5E: 1000g = 0x03E8
        data[0x5E] = 0x03
        data[0x5F] = 0xE8

        // Print Temp at 0x60: 210°C ÷ 5 = 42
        data[0x60] = 42

        // Bed Temp at 0x61: 60°C ÷ 5 = 12
        data[0x61] = 12

        // Density at 0x62: 1.27 × 1000 = 1270 = 0x04F6
        data[0x62] = 0x04
        data[0x63] = 0xF6

        let result = NFCTagParser.parseOpenTag3D(data)
        XCTAssertNotNil(result)
        XCTAssertEqual(result?.material, "PETG")
        XCTAssertEqual(result?.vendor, "Hatchbox")
        XCTAssertEqual(result?.colorHex, "#FF0000")
        XCTAssertEqual(result?.weight, 1000.0)
        XCTAssertEqual(result?.diameter, 1.75)
        XCTAssertEqual(result?.temperature, 210)
    }

    func testParseOpenTag3D_tooShort() {
        // Data shorter than minimum 112 bytes (Core) → returns nil
        let shortData = Data(count: 50)
        XCTAssertNil(NFCTagParser.parseOpenTag3D(shortData))

        let emptyData = Data()
        XCTAssertNil(NFCTagParser.parseOpenTag3D(emptyData))
    }

    func testParseOpenTag3D_wrongVersion() {
        // Major version 2 (version 2000 = 0x07D0) → returns nil
        var data = Data(count: 0x70)
        data[0x00] = 0x07
        data[0x01] = 0xD0

        XCTAssertNil(NFCTagParser.parseOpenTag3D(data))
    }

    // MARK: - OpenTag3D: Round Trip

    func testCreateOpenTag3DPayload_roundTrip() {
        let spool = SpoolmanSpool(
            id: 99,
            name: "Galaxy Black PLA",
            material: "PLA",
            colorHex: "#1A2B3C",
            inUse: true,
            filamentName: "eSUN PLA+",
            vendor: "eSUN",
            registeredAt: nil,
            firstUsedAt: nil,
            lastUsedAt: nil,
            remainingWeightG: 600.0,
            initialWeightG: 750.0,
            usedWeightG: 150.0,
            spoolWeightG: 200.0,
            remainingLengthMm: nil,
            usedLengthMm: nil,
            location: nil,
            lotNumber: nil,
            archived: false,
            price: nil,
            comment: nil,
            hasNfcTag: nil,
            usedPercent: nil,
            remainingPercent: nil
        )

        let payload = NFCTagParser.createOpenTag3DPayload(from: spool)
        XCTAssertNotNil(payload)
        guard let data = payload else { return }

        let parsed = NFCTagParser.parseOpenTag3D(data)
        XCTAssertNotNil(parsed)
        XCTAssertEqual(parsed?.material, "PLA")
        XCTAssertEqual(parsed?.colorHex, "#1A2B3C")
        XCTAssertEqual(parsed?.vendor, "eSUN")
        XCTAssertEqual(parsed?.weight, 750.0)
        // spoolmanId not stored in OpenTag3D binary format
        XCTAssertNil(parsed?.spoolmanId)
    }
}
