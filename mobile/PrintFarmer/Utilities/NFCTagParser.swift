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
            return "JSON format with broader field names. Compatible with OpenPrintTag readers."
        case .openTag3D:
            return "Compact binary format per opentag3d.info spec. Fits more data on small tags (NTAG213)."
        }
    }
}

// MARK: - NFC Tag Parser

/// Parses and creates NFC tag payloads for spool identification.
enum NFCTagParser {

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

    /// Parses an OpenPrintTag JSON payload into ScannedSpoolData.
    /// OpenPrintTag fields: filament_type, color, manufacturer, net_weight, filament_diameter, print_temp, spool_id
    static func parseOpenPrintTag(_ data: Data) -> ScannedSpoolData? {
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

    /// Creates an OpenPrintTag JSON payload from a SpoolmanSpool.
    static func createOpenPrintTagPayload(from spool: SpoolmanSpool, filament: SpoolmanFilament? = nil) -> Data? {
        var payload: [String: Any] = [:]
        payload["filament_type"] = spool.material
        if let hex = spool.colorHex { payload["color"] = hex }
        if let vendor = spool.vendor { payload["manufacturer"] = vendor }
        if let weight = spool.initialWeightG { payload["net_weight"] = weight }
        if let diameter = filament?.diameter { payload["filament_diameter"] = diameter }
        if let temp = filament?.settingsExtruderTemp, temp > 0 { payload["print_temp"] = temp }
        if let temp = filament?.settingsBedTemp, temp > 0 { payload["bed_temp"] = temp }
        payload["spool_id"] = spool.id

        return try? JSONSerialization.data(withJSONObject: payload, options: [.sortedKeys])
    }

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
