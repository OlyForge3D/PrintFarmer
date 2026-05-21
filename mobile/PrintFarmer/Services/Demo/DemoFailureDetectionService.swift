import Foundation

final class DemoFailureDetectionService: FailureDetectionServiceProtocol, @unchecked Sendable {
    func getStatus() async throws -> FailureDetectionMonitorStatus {
        // Return a JSON-decoded demo status matching the backend shape
        let json = """
        {
            "monitoringEnabled": true,
            "confidenceThreshold": 0.6,
            "scanIntervalSeconds": 30,
            "autoPauseOnFailure": true,
            "configuredPrinterCount": 2,
            "activelyMonitoredPrinterCount": 2,
            "lastAnalyzedPrinterCount": 2,
            "lastFailureCount": 0,
            "printers": [
                {
                    "printerId": "\(DemoData.prusaMK4_1_ID.uuidString)",
                    "printerName": "Prusa MK4 #1",
                    "state": "monitoring",
                    "reason": "Actively watching this print",
                    "isPrinting": true,
                    "detectionSource": "pooled",
                    "lastOutcome": "healthy",
                    "lastConfidence": null
                },
                {
                    "printerId": "\(DemoData.bambuX1C_ID.uuidString)",
                    "printerName": "Bambu X1C",
                    "state": "monitoring",
                    "reason": "Actively watching this print",
                    "isPrinting": true,
                    "detectionSource": "pooled",
                    "lastOutcome": "healthy",
                    "lastConfidence": null
                }
            ]
        }
        """
        return try JSONDecoder().decode(FailureDetectionMonitorStatus.self, from: Data(json.utf8))
    }
}
