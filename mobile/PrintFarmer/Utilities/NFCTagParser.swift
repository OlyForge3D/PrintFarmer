import Foundation

// MARK: - NFC Tag Format

/// The NDEF format used when writing spool data to NFC tags.
enum NFCTagFormat: String, CaseIterable, Identifiable {
    case openSpool = "OpenSpool"
    case openPrintTag = "OpenPrintTag"
    case openTag3D = "OpenTag3D"
    var id: String { rawValue }

    var description: String {
        switch self {
        case .openSpool:
            return "JSON format used by Bambu Lab and other OpenSpool-compatible printers."
        case .openPrintTag:
            return "OpenPrintTag CBOR payload in an application/vnd.openprinttag NDEF record."
        case .openTag3D:
            return "Compact binary format per opentag3d.info spec. Fits more data on small tags (NTAG213)."
        }
    }
}

// MARK: - NFC Tag Parser

/// Parses and creates NFC tag payloads for spool identification.
enum NFCTagParser {
    static func canonicalUID(from identifier: Data) -> String? {
        guard !identifier.isEmpty else { return nil }
        return identifier.map { String(format: "%02X", $0) }.joined()
    }

    static func parseNavigationURL(_ url: URL) -> SpoolScanResult? {
        switch DeepLinkHandler.parse(url: url) {
        case .printerDetail(let id): .printerId(id)
        case .spoolDetail(let id): .spoolId(id)
        default: nil
        }
    }

    // MARK: - OpenSpool Format

    /// Parses an OpenSpool JSON payload into ScannedSpoolData.
    /// OpenSpool fields: material, color_hex, brand, weight_g, diameter_mm, temp_c, spoolman_id
    static func parseOpenSpool(_ data: Data) -> ScannedSpoolData? {
        guard let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else {
            return nil
        }
        return ScannedSpoolData(
            material: json["material"] as? String,
            colorHex: json["color_hex"] as? String,
            vendor: json["brand"] as? String,
            weight: doubleValue(json["weight_g"]),
            diameter: doubleValue(json["diameter_mm"]),
            temperature: intValue(json["temp_c"]),
            spoolmanId: intValue(json["spoolman_id"])
        )
    }

    // MARK: - OpenPrintTag Format

    /// Parses the OpenPrintTag CBOR meta and main sections into spool intake fields.
    static func parseOpenPrintTag(_ data: Data) -> ScannedSpoolData? {
        guard data.count <= 4096,
              let (metaValue, metaEnd) = try? OpenPrintTagCBOR.decode(data, at: 0),
              let meta = metaValue.mapValue else {
            return nil
        }

        let mainStart = meta[0]?.integerValue ?? metaEnd
        let mainEnd: Int
        if let auxiliaryOffset = meta[2]?.integerValue {
            mainEnd = auxiliaryOffset
        } else if let mainSize = meta[1]?.integerValue {
            let (end, overflow) = mainStart.addingReportingOverflow(mainSize)
            guard !overflow else { return nil }
            mainEnd = end
        } else {
            mainEnd = data.count
        }
        guard mainStart >= metaEnd,
              mainEnd <= data.count,
              mainEnd > mainStart,
              mainEnd - mainStart <= 512,
              let (mainValue, _) = try? OpenPrintTagCBOR.decode(data, at: mainStart),
              let main = mainValue.mapValue else {
            return nil
        }

        let material = main[52]?.textValue ?? main[10]?.textValue ?? main[9]?.integerValue.flatMap(materialTypeName)
        let weight = main[17]?.numberValue ?? main[16]?.numberValue
        let temperature = main[36]?.integerValue ?? main[34]?.integerValue ?? main[35]?.integerValue
        return ScannedSpoolData(
            material: material,
            colorHex: colorHex(main[19]?.bytesValue),
            vendor: main[11]?.textValue,
            weight: weight,
            diameter: main[30]?.numberValue,
            temperature: temperature,
            spoolmanId: nil
        )
    }

    /// Reads the PrintFarmer-specific JSON text record written by older app versions.
    static func parseLegacyOpenPrintTag(_ data: Data) -> ScannedSpoolData? {
        guard let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else {
            return nil
        }
        return ScannedSpoolData(
            material: json["filament_type"] as? String,
            colorHex: json["color"] as? String,
            vendor: json["manufacturer"] as? String,
            weight: doubleValue(json["net_weight"]),
            diameter: doubleValue(json["filament_diameter"]),
            temperature: intValue(json["print_temp"]),
            spoolmanId: intValue(json["spool_id"])
        )
    }

    // MARK: - Payload Creation

    /// Creates an OpenSpool JSON payload from a SpoolmanSpool.
    static func createOpenSpoolPayload(from spool: SpoolmanSpool) -> Data? {
        var payload: [String: Any] = [:]
        payload["material"] = spool.material
        if let hex = spool.colorHex { payload["color_hex"] = hex }
        if let vendor = spool.vendor { payload["brand"] = vendor }
        if let weight = spool.initialWeightG { payload["weight_g"] = weight }
        payload["spoolman_id"] = spool.id

        return try? JSONSerialization.data(withJSONObject: payload, options: [.sortedKeys])
    }

    /// Creates an OpenPrintTag meta section followed by its main CBOR section.
    static func createOpenPrintTagPayload(from spool: SpoolmanSpool, filament: SpoolmanFilament? = nil) -> Data? {
        var main: [UInt64: OpenPrintTagCBOR.Value] = [8: .unsigned(0)]
        if let materialType = materialTypeCode(spool.material) {
            main[9] = .unsigned(materialType)
        }
        if let materialName = boundedText(filament?.name ?? spool.filamentName ?? spool.material, maximumBytes: 63) {
            main[10] = .text(materialName)
        }
        if let brand = boundedText(spool.vendor ?? filament?.vendor, maximumBytes: 31) {
            main[11] = .text(brand)
        }
        if let gtin = filament?.gtin, let value = UInt64(gtin) {
            main[4] = .unsigned(value)
        }
        if let weight = filament?.weight, weight > 0, weight.isFinite {
            main[16] = .number(weight)
        }
        if let weight = spool.initialWeightG, weight > 0, weight.isFinite {
            main[17] = .number(weight)
        }
        if let spoolWeight = spool.spoolWeightG ?? filament?.spoolWeight,
           spoolWeight >= 0, spoolWeight.isFinite {
            main[18] = .number(spoolWeight)
        }
        if let color = colorBytes(spool.colorHex ?? filament?.colorHex) {
            main[19] = .bytes(color)
        }
        if let density = filament?.density, density > 0, density.isFinite {
            main[29] = .number(density)
        }
        if let diameter = filament?.diameter, diameter > 0, diameter.isFinite {
            main[30] = .number(diameter)
        }

        var payload = Data([0xA0])
        payload.append(OpenPrintTagCBOR.encodeMap(main))
        return payload
    }

    static let openPrintTagMimeType = "application/vnd.openprinttag"

    // MARK: - OpenTag3D Format

    /// MIME type for OpenTag3D NDEF media records.
    static let openTag3DMimeType = "application/opentag3d"

    /// Core payload size (offsets 0x00–0x6F).
    private static let openTag3DCoreSize = 0x70  // 112 bytes

    /// Core + Extended payload size (offsets 0x00–0xBA).
    private static let openTag3DExtendedSize = 0xBB  // 187 bytes

    /// Creates an OpenTag3D binary payload from a SpoolmanSpool and optional filament data.
    /// Memory-mapped layout per opentag3d.info spec, all multi-byte integers unsigned big-endian.
    /// Writes Extended fields (0x70–0xBA) when data is available, otherwise Core only.
    static func createOpenTag3DPayload(from spool: SpoolmanSpool, filament: SpoolmanFilament? = nil) -> Data? {
        let hasExtended = spool.lotNumber != nil || spool.spoolWeightG != nil
            || spool.remainingWeightG != nil || spool.remainingLengthMm != nil
        let payloadSize = hasExtended ? openTag3DExtendedSize : openTag3DCoreSize
        var buf = Data(count: payloadSize)

        // 0x00  Tag Version — 1000 (v1.000, 3 implied decimals)
        writeUInt16BE(&buf, value: 1000, at: 0x00)

        // 0x02  Base Material Name (5 bytes, null-padded UTF-8)
        writeUTF8(&buf, string: spool.material, at: 0x02, length: 5)

        // 0x07  Material Modifiers (5 bytes)
        let modifiers = extractModifiers(baseMaterial: spool.material, filamentName: spool.filamentName)
        writeUTF8(&buf, string: modifiers, at: 0x07, length: 5)

        // 0x0C  Reserved gap (15 bytes) — already zeroed

        // 0x1B  Filament Manufacturer (16 bytes)
        writeUTF8(&buf, string: spool.vendor, at: 0x1B, length: 16)

        // 0x2B  Color Name (32 bytes) — use spool name
        writeUTF8(&buf, string: spool.name, at: 0x2B, length: 32)

        // 0x4B  Color 1 RGBA (4 bytes)
        let rgba = parseColorHexToRGBA(spool.colorHex)
        buf[0x4B] = rgba.r
        buf[0x4C] = rgba.g
        buf[0x4D] = rgba.b
        buf[0x4E] = rgba.a

        // 0x50–0x5B  Colors 2-4 — already zeroed (single color)

        // 0x5C  Target Diameter µm — from filament or default 1.75mm
        let diameterMicrons: UInt16
        if let d = filament?.diameter {
            diameterMicrons = UInt16(clamping: Int((d * 1000).rounded()))
        } else {
            diameterMicrons = 1750
        }
        writeUInt16BE(&buf, value: diameterMicrons, at: 0x5C)

        // 0x5E  Target Weight grams
        if let weight = spool.initialWeightG {
            writeUInt16BE(&buf, value: UInt16(clamping: Int(weight.rounded())), at: 0x5E)
        }

        // 0x60  Print Temperature (°C ÷ 5) — from filament
        if let temp = filament?.settingsExtruderTemp, temp > 0 {
            buf[0x60] = UInt8(clamping: temp / 5)
        }

        // 0x61  Bed Temperature (°C ÷ 5) — from filament
        if let temp = filament?.settingsBedTemp, temp > 0 {
            buf[0x61] = UInt8(clamping: temp / 5)
        }

        // 0x62  Density (g/cm³ × 1000) — from filament or material lookup
        if let density = filament?.density, density > 0 {
            writeUInt16BE(&buf, value: UInt16(clamping: Int((density * 1000).rounded())), at: 0x62)
        } else {
            writeUInt16BE(&buf, value: defaultDensity(for: spool.material), at: 0x62)
        }

        // 0x64  Transmission Distance — 0 = unknown

        // MARK: Extended Fields (0x70–0xBA)
        if hasExtended {
            // 0x90  Serial Number / Batch ID (16 bytes)
            writeUTF8(&buf, string: spool.lotNumber, at: 0x90, length: 16)

            // 0xAC  Empty Spool Weight (grams)
            if let spoolWeight = spool.spoolWeightG {
                writeUInt16BE(&buf, value: UInt16(clamping: Int(spoolWeight.rounded())), at: 0xAC)
            }

            // 0xAE  Measured Filament Weight (grams)
            if let remainingWeight = spool.remainingWeightG {
                writeUInt16BE(&buf, value: UInt16(clamping: Int(remainingWeight.rounded())), at: 0xAE)
            }

            // 0xB0  Measured Filament Length (meters)
            if let remainingMm = spool.remainingLengthMm {
                let meters = UInt16(clamping: Int((remainingMm / 1000.0).rounded()))
                writeUInt16BE(&buf, value: meters, at: 0xB0)
            }
        }

        return buf
    }

    /// Parses an OpenTag3D binary payload into ScannedSpoolData.
    /// Handles both Core-only (112 bytes) and Core+Extended (187 bytes) payloads.
    static func parseOpenTag3D(_ data: Data) -> ScannedSpoolData? {
        guard data.count >= openTag3DCoreSize else { return nil }

        // Validate major version is 1 (version 1000–1999)
        let version = readUInt16BE(data, at: 0x00)
        guard version >= 1000, version < 2000 else { return nil }

        let material = readUTF8(data, at: 0x02, length: 5)
        let manufacturer = readUTF8(data, at: 0x1B, length: 16)

        // Color RGBA → #RRGGBB hex string
        let r = data[0x4B], g = data[0x4C], b = data[0x4D]
        let hasColor = (r | g | b) != 0
        let colorHex = hasColor ? String(format: "#%02X%02X%02X", r, g, b) : nil

        let weight = readUInt16BE(data, at: 0x5E)
        let diameterMicrons = readUInt16BE(data, at: 0x5C)
        let tempEncoded = data[0x60]
        let temperature = Int(tempEncoded) * 5

        return ScannedSpoolData(
            material: material,
            colorHex: colorHex,
            vendor: manufacturer,
            weight: weight > 0 ? Double(weight) : nil,
            diameter: diameterMicrons > 0 ? Double(diameterMicrons) / 1000.0 : nil,
            temperature: temperature > 0 ? temperature : nil,
            spoolmanId: nil
        )
    }

    // MARK: - OpenTag3D Binary Helpers

    private static func writeUInt16BE(_ buf: inout Data, value: UInt16, at offset: Int) {
        buf[offset] = UInt8(value >> 8)
        buf[offset + 1] = UInt8(value & 0xFF)
    }

    private static func readUInt16BE(_ data: Data, at offset: Int) -> UInt16 {
        UInt16(data[offset]) << 8 | UInt16(data[offset + 1])
    }

    private static func writeUTF8(_ buf: inout Data, string: String?, at offset: Int, length: Int) {
        guard let string, !string.isEmpty else { return }
        let bytes = Array(string.utf8.prefix(length))
        for (i, byte) in bytes.enumerated() {
            buf[offset + i] = byte
        }
    }

    private static func readUTF8(_ data: Data, at offset: Int, length: Int) -> String? {
        let slice = data[offset ..< offset + length]
        let trimmed = slice.prefix(while: { $0 != 0 })
        guard !trimmed.isEmpty else { return nil }
        return String(bytes: trimmed, encoding: .utf8)
    }

    private static func boundedText(_ value: String?, maximumBytes: Int) -> String? {
        guard let value, !value.isEmpty, value.utf8.count <= maximumBytes else { return nil }
        return value
    }

    private static func colorBytes(_ value: String?) -> Data? {
        guard let value else { return nil }
        let hex = value.hasPrefix("#") ? String(value.dropFirst()) : value
        guard hex.count == 6 || hex.count == 8,
              let number = UInt64(hex, radix: 16) else {
            return nil
        }
        let byteCount = hex.count / 2
        return Data((0..<byteCount).map { index in
            UInt8((number >> ((byteCount - index - 1) * 8)) & 0xFF)
        })
    }

    private static func colorHex(_ bytes: Data?) -> String? {
        guard let bytes, bytes.count == 3 || bytes.count == 4 else { return nil }
        return String(format: "#%02X%02X%02X", bytes[0], bytes[1], bytes[2])
    }

    private static func materialTypeCode(_ material: String) -> UInt64? {
        switch material.uppercased() {
        case "PLA": 0
        case "PETG": 1
        case "TPU": 2
        case "ABS": 3
        case "ASA": 4
        case "PC": 5
        default: nil
        }
    }

    private static func materialTypeName(_ value: Int) -> String? {
        switch value {
        case 0: "PLA"
        case 1: "PETG"
        case 2: "TPU"
        case 3: "ABS"
        case 4: "ASA"
        case 5: "PC"
        default: nil
        }
    }

    /// Converts `#RRGGBB` hex → (R, G, B, 255). Falls back to mid-gray if absent.
    private static func parseColorHexToRGBA(_ hex: String?) -> (r: UInt8, g: UInt8, b: UInt8, a: UInt8) {
        guard let hex, !hex.isEmpty else { return (128, 128, 128, 255) }
        let clean = hex.hasPrefix("#") ? String(hex.dropFirst()) : hex
        guard clean.count >= 6, let value = UInt32(clean.prefix(6), radix: 16) else {
            return (128, 128, 128, 255)
        }
        return (UInt8((value >> 16) & 0xFF), UInt8((value >> 8) & 0xFF), UInt8(value & 0xFF), 255)
    }

    /// Derives modifier string (e.g. "CF", "Silk") by stripping base material from filament name.
    private static func extractModifiers(baseMaterial: String, filamentName: String?) -> String? {
        guard let name = filamentName else { return nil }
        let cleaned = name.replacingOccurrences(of: baseMaterial, with: "", options: .caseInsensitive)
            .trimmingCharacters(in: .whitespaces)
        return cleaned.isEmpty ? nil : cleaned
    }

    /// Known material densities (g/cm³ × 1000).
    private static func defaultDensity(for material: String) -> UInt16 {
        switch material.uppercased() {
        case "PLA": return 1240
        case "PETG": return 1270
        case "ABS": return 1070
        case "TPU": return 1210
        case "ASA": return 1070
        case "NYLON", "PA": return 1140
        case "PC": return 1200
        default: return 0
        }
    }

    // MARK: - Helpers

    private static func doubleValue(_ value: Any?) -> Double? {
        if let d = value as? Double { return d }
        if let i = value as? Int { return Double(i) }
        if let s = value as? String { return Double(s) }
        return nil
    }

    private static func intValue(_ value: Any?) -> Int? {
        if let i = value as? Int { return i }
        if let d = value as? Double { return Int(d) }
        if let s = value as? String { return Int(s) }
        return nil
    }
}

private enum OpenPrintTagCBOR {
    enum Value {
        case unsigned(UInt64)
        case negative(Int64)
        case bytes(Data)
        case text(String)
        case array([Value])
        case map([UInt64: Value])
        case number(Double)
        case null

        var integerValue: Int? {
            switch self {
            case .unsigned(let value) where value <= UInt64(Int.max):
                Int(value)
            case .negative(let value):
                Int(exactly: value)
            default:
                nil
            }
        }

        var numberValue: Double? {
            switch self {
            case .unsigned(let value): Double(value)
            case .negative(let value): Double(value)
            case .number(let value): value
            default: nil
            }
        }

        var bytesValue: Data? {
            guard case .bytes(let value) = self else { return nil }
            return value
        }

        var textValue: String? {
            guard case .text(let value) = self else { return nil }
            return value
        }

        var mapValue: [UInt64: Value]? {
            guard case .map(let value) = self else { return nil }
            return value
        }
    }

    private enum DecodeError: Error {
        case malformed
    }

    static func decode(_ data: Data, at start: Int) throws -> (Value, Int) {
        var reader = Reader(bytes: Array(data), offset: start)
        let value = try reader.read(depth: 0)
        return (value, reader.offset)
    }

    static func encodeMap(_ map: [UInt64: Value]) -> Data {
        var data = Data()
        appendHead(major: 5, value: UInt64(map.count), to: &data)
        for key in map.keys.sorted() {
            appendHead(major: 0, value: key, to: &data)
            append(map[key]!, to: &data)
        }
        return data
    }

    private static func append(_ value: Value, to data: inout Data) {
        switch value {
        case .unsigned(let number):
            appendHead(major: 0, value: number, to: &data)
        case .negative(let number):
            appendHead(major: 1, value: UInt64(-1 - number), to: &data)
        case .bytes(let bytes):
            appendHead(major: 2, value: UInt64(bytes.count), to: &data)
            data.append(bytes)
        case .text(let text):
            let bytes = Data(text.utf8)
            appendHead(major: 3, value: UInt64(bytes.count), to: &data)
            data.append(bytes)
        case .array(let values):
            appendHead(major: 4, value: UInt64(values.count), to: &data)
            values.forEach { append($0, to: &data) }
        case .map(let map):
            appendHead(major: 5, value: UInt64(map.count), to: &data)
            for key in map.keys.sorted() {
                appendHead(major: 0, value: key, to: &data)
                append(map[key]!, to: &data)
            }
        case .number(let number):
            data.append(0xFB)
            appendBigEndian(number.bitPattern, to: &data)
        case .null:
            data.append(0xF6)
        }
    }

    private static func appendHead(major: UInt8, value: UInt64, to data: inout Data) {
        let prefix = major << 5
        switch value {
        case 0..<24:
            data.append(prefix | UInt8(value))
        case 24...UInt64(UInt8.max):
            data.append(prefix | 24)
            data.append(UInt8(value))
        case 256...UInt64(UInt16.max):
            data.append(prefix | 25)
            appendBigEndian(UInt16(value), to: &data)
        case 65536...UInt64(UInt32.max):
            data.append(prefix | 26)
            appendBigEndian(UInt32(value), to: &data)
        default:
            data.append(prefix | 27)
            appendBigEndian(value, to: &data)
        }
    }

    private static func appendBigEndian<T: FixedWidthInteger>(_ value: T, to data: inout Data) {
        for shift in stride(from: T.bitWidth - 8, through: 0, by: -8) {
            data.append(UInt8(truncatingIfNeeded: value >> T(shift)))
        }
    }

    private struct Reader {
        let bytes: [UInt8]
        var offset: Int

        mutating func read(depth: Int) throws -> Value {
            guard depth < 16, offset < bytes.count else { throw DecodeError.malformed }
            let initial = bytes[offset]
            offset += 1
            let major = initial >> 5
            let additional = initial & 0x1F

            if major == 7 {
                return try readSimple(additional)
            }
            if major == 6 {
                _ = try readArgument(additional)
                return try read(depth: depth + 1)
            }
            if additional == 31 {
                return try readIndefinite(major: major, depth: depth)
            }

            let argument = try readArgument(additional)
            switch major {
            case 0:
                return .unsigned(argument)
            case 1:
                guard argument <= UInt64(Int64.max) else { throw DecodeError.malformed }
                return .negative(-1 - Int64(argument))
            case 2:
                return .bytes(try readBytes(argument))
            case 3:
                guard let text = String(data: try readBytes(argument), encoding: .utf8) else {
                    throw DecodeError.malformed
                }
                return .text(text)
            case 4:
                guard argument <= UInt64(bytes.count) else { throw DecodeError.malformed }
                return .array(try (0..<Int(argument)).map { _ in try read(depth: depth + 1) })
            case 5:
                guard argument <= UInt64(bytes.count / 2) else { throw DecodeError.malformed }
                var map: [UInt64: Value] = [:]
                for _ in 0..<Int(argument) {
                    let key = try read(depth: depth + 1)
                    let value = try read(depth: depth + 1)
                    if case .unsigned(let integer) = key {
                        map[integer] = value
                    }
                }
                return .map(map)
            default:
                throw DecodeError.malformed
            }
        }

        private mutating func readIndefinite(major: UInt8, depth: Int) throws -> Value {
            switch major {
            case 2:
                var result = Data()
                while !consumeBreak() {
                    guard case .bytes(let part) = try read(depth: depth + 1) else {
                        throw DecodeError.malformed
                    }
                    result.append(part)
                }
                return .bytes(result)
            case 3:
                var result = Data()
                while !consumeBreak() {
                    guard case .text(let part) = try read(depth: depth + 1) else {
                        throw DecodeError.malformed
                    }
                    result.append(contentsOf: part.utf8)
                }
                guard let text = String(data: result, encoding: .utf8) else { throw DecodeError.malformed }
                return .text(text)
            case 4:
                var values: [Value] = []
                while !consumeBreak() {
                    values.append(try read(depth: depth + 1))
                    guard values.count <= bytes.count else { throw DecodeError.malformed }
                }
                return .array(values)
            case 5:
                var map: [UInt64: Value] = [:]
                while !consumeBreak() {
                    let key = try read(depth: depth + 1)
                    let value = try read(depth: depth + 1)
                    if case .unsigned(let integer) = key {
                        map[integer] = value
                    }
                    guard map.count <= bytes.count else { throw DecodeError.malformed }
                }
                return .map(map)
            default:
                throw DecodeError.malformed
            }
        }

        private mutating func readSimple(_ additional: UInt8) throws -> Value {
            switch additional {
            case 20, 21, 22, 23:
                return .null
            case 25:
                let bits = UInt16(try readFixedInteger(byteCount: 2))
                return .number(Double(Float16(bitPattern: bits)))
            case 26:
                let bits = UInt32(try readFixedInteger(byteCount: 4))
                return .number(Double(Float(bitPattern: bits)))
            case 27:
                let bits = try readFixedInteger(byteCount: 8)
                return .number(Double(bitPattern: bits))
            default:
                throw DecodeError.malformed
            }
        }

        private mutating func readArgument(_ additional: UInt8) throws -> UInt64 {
            switch additional {
            case 0..<24:
                UInt64(additional)
            case 24:
                try readFixedInteger(byteCount: 1)
            case 25:
                try readFixedInteger(byteCount: 2)
            case 26:
                try readFixedInteger(byteCount: 4)
            case 27:
                try readFixedInteger(byteCount: 8)
            default:
                throw DecodeError.malformed
            }
        }

        private mutating func readFixedInteger(byteCount: Int) throws -> UInt64 {
            guard byteCount <= 8, offset <= bytes.count - byteCount else { throw DecodeError.malformed }
            var value: UInt64 = 0
            for byte in bytes[offset ..< offset + byteCount] {
                value = (value << 8) | UInt64(byte)
            }
            offset += byteCount
            return value
        }

        private mutating func readBytes(_ length: UInt64) throws -> Data {
            guard length <= UInt64(bytes.count), offset <= bytes.count - Int(length) else {
                throw DecodeError.malformed
            }
            defer { offset += Int(length) }
            return Data(bytes[offset ..< offset + Int(length)])
        }

        private mutating func consumeBreak() -> Bool {
            guard offset < bytes.count, bytes[offset] == 0xFF else { return false }
            offset += 1
            return true
        }
    }
}
