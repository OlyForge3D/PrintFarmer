import Foundation

// MARK: - QR Code Parser

/// Parses spool IDs from Spoolman QR code payloads.
/// Supports URL format, plain numeric, and JSON payloads.
enum QRCodeParser {

    /// Attempts to extract a spool ID from a QR code string.
    /// - Parameter qrText: Raw QR code content.
    /// - Returns: The spool ID if parsing succeeds, nil otherwise.
    static func parse(_ qrText: String) -> Int? {
        let trimmed = qrText.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { return nil }

        // Plain numeric
        if let id = Int(trimmed), id > 0 {
            return id
        }

        // URL format: https://host/spools/42 or /spools/42
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
        if let url = URL(string: text) {
            pathComponents = url.pathComponents
        } else {
            pathComponents = text.components(separatedBy: "/").filter { !$0.isEmpty }
        }

        // Look for "spools" followed by a numeric segment
        for (index, component) in pathComponents.enumerated()
        where component.lowercased() == "spools" && index + 1 < pathComponents.count {
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
