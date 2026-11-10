import Foundation

// MARK: - QR Code Parser

/// Parses spool IDs from Spoolman QR code payloads.
/// Supports URL format, plain numeric, and JSON payloads.
enum QRCodeParser {

    /// Attempts to extract a spool ID from a QR code string. Tries bare
    /// positive numeric first, then falls through to the structured
    /// (URL/JSON) forms. Used by callers with no barcode-symbology
    /// ambiguity to worry about (QR-only scanners: `QRSpoolScannerService`,
    /// `NFCService`'s text-record fallback) — for the unified scan station,
    /// which must NOT treat every numeric-looking code as a spool ID before
    /// giving Barcode Intake first crack, see `parseStructured(_:)`.
    /// - Parameter qrText: Raw QR code content.
    /// - Returns: The spool ID if parsing succeeds, nil otherwise.
    static func parse(_ qrText: String) -> Int? {
        let trimmed = qrText.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { return nil }

        // Plain numeric
        if let id = Int(trimmed), id > 0 {
            return id
        }

        return parseStructured(trimmed)
    }

    /// Extracts a spool ID only from structured (URL or JSON) payloads —
    /// deliberately excluding the bare-positive-integer form that `parse(_:)`
    /// also accepts. A genuine EAN/UPC barcode is also a bare numeric
    /// string, so the unified scan station needs a way to recognize an
    /// unambiguously-structured spool payload (a URL or JSON document)
    /// without misclassifying every scannable numeric barcode as a spool ID.
    /// - Parameter qrText: Raw QR/barcode content.
    /// - Returns: The spool ID if a structured form matches, nil otherwise.
    static func parseStructured(_ qrText: String) -> Int? {
        let trimmed = qrText.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { return nil }

        // URL format: https://host/spools/42, /spool/42, or printfarmer://spool/42
        if let id = parseURL(trimmed) {
            return id
        }

        // JSON format: {"spoolId": 42}
        if let id = parseJSON(trimmed) {
            return id
        }

        return nil
    }

    // MARK: - Private

    private static func parseURL(_ text: String) -> Int? {
        // Try as full URL first, then as path
        let pathComponents: [String]
        let host: String?
        if let url = URL(string: text) {
            pathComponents = url.pathComponents
            host = url.host?.lowercased()
        } else {
            pathComponents = text.components(separatedBy: "/").filter { !$0.isEmpty }
            host = nil
        }

        // Host-based form: `printfarmer://spool/42` (or `.../spools/42`) —
        // "spool"/"spools" is parsed as the URL's host rather than a path
        // component (there's no authority separator before it), so it's
        // otherwise invisible to a path-component-only scan.
        if let host, host == "spool" || host == "spools" {
            let idComponents = pathComponents.filter { $0 != "/" }
            if let first = idComponents.first, let id = Int(first), id > 0 {
                return id
            }
        }

        // Path-based form, singular or plural: .../spool/42 or .../spools/42
        for (index, component) in pathComponents.enumerated()
        where (component.lowercased() == "spool" || component.lowercased() == "spools") && index + 1 < pathComponents.count {
            if let id = Int(pathComponents[index + 1]), id > 0 {
                return id
            }
        }
        return nil
    }

    private static func parseJSON(_ text: String) -> Int? {
        guard let data = text.data(using: .utf8),
              let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else {
            return nil
        }
        // Accept spoolId or spool_id
        let value = json["spoolId"] ?? json["spool_id"] ?? json["id"]
        if let id = value as? Int, id > 0 {
            return id
        }
        if let str = value as? String, let id = Int(str), id > 0 {
            return id
        }
        return nil
    }
}

// MARK: - Scanned Barcode Normalizer

/// Normalizes scanner payloads before dispatch. VisionKit reports UPC-E in
/// its compressed 8-digit form, which fails a GTIN-12 mod-10 check, so it is
/// expanded to its equivalent UPC-A (GTIN-12) value. Other symbologies pass
/// through unchanged.
enum ScannedBarcodeNormalizer {

    static func payload(_ raw: String, isUPCE: Bool) -> String {
        guard isUPCE else { return raw }
        return expandUPCE(raw) ?? raw
    }

    /// Expands a UPC-E code (6 digits with implied number system 0, 7 digits
    /// with number system, or 8 digits with number system and check digit)
    /// to UPC-A. Returns nil for any other shape, a number system other than
    /// 0 or 1, or a check digit that does not match the expansion.
    static func expandUPCE(_ raw: String) -> String? {
        let code = raw.trimmingCharacters(in: .whitespacesAndNewlines)
        guard code.allSatisfy({ $0.isASCII && $0.isNumber }) else { return nil }
        let digits = code.compactMap { $0.wholeNumberValue }
        let numberSystem: Int
        let body: [Int]
        let providedCheck: Int?
        switch digits.count {
        case 6:
            (numberSystem, body, providedCheck) = (0, digits, nil)
        case 7:
            (numberSystem, body, providedCheck) = (digits[0], Array(digits[1...6]), nil)
        case 8:
            (numberSystem, body, providedCheck) = (digits[0], Array(digits[1...6]), digits[7])
        default:
            return nil
        }
        guard numberSystem == 0 || numberSystem == 1 else { return nil }

        let (d1, d2, d3, d4, d5, d6) = (body[0], body[1], body[2], body[3], body[4], body[5])
        let manufacturer: [Int]
        let product: [Int]
        switch d6 {
        case 0...2:
            manufacturer = [d1, d2, d6, 0, 0]
            product = [0, 0, d3, d4, d5]
        case 3:
            manufacturer = [d1, d2, d3, 0, 0]
            product = [0, 0, 0, d4, d5]
        case 4:
            manufacturer = [d1, d2, d3, d4, 0]
            product = [0, 0, 0, 0, d5]
        default:
            manufacturer = [d1, d2, d3, d4, d5]
            product = [0, 0, 0, 0, d6]
        }
        let upcABody = [numberSystem] + manufacturer + product
        var sum = 0
        for (index, digit) in upcABody.enumerated() {
            sum += index.isMultiple(of: 2) ? digit * 3 : digit
        }
        let check = (10 - sum % 10) % 10
        if let providedCheck, providedCheck != check { return nil }
        return (upcABody + [check]).map(String.init).joined()
    }
}
