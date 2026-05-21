import Foundation

// MARK: - Demo Dispatch Service

final class DemoDispatchService: DispatchServiceProtocol, @unchecked Sendable {

    func getQueueStatus() async throws -> DispatchQueueStatus {
        DispatchQueueStatus(
            pendingUnassignedJobs: 2,
            totalQueuedJobs: 6,
            idlePrinters: 1,
            busyPrinters: 4,
            printerQueueDepths: [
                PrinterQueueDepth(printerId: DemoData.prusaMK4_1_ID, printerName: "Prusa MK4 #1",
                                  queueDepth: 2, isPrinting: true, isAvailable: false),
                PrinterQueueDepth(printerId: DemoData.prusaMK4_2_ID, printerName: "Prusa MK4 #2",
                                  queueDepth: 0, isPrinting: false, isAvailable: true),
                PrinterQueueDepth(printerId: DemoData.bambuX1C_ID, printerName: "Bambu X1C",
                                  queueDepth: 1, isPrinting: true, isAvailable: false),
                PrinterQueueDepth(printerId: DemoData.bambuP1S_ID, printerName: "Bambu P1S",
                                  queueDepth: 1, isPrinting: false, isAvailable: false),
                PrinterQueueDepth(printerId: DemoData.voron24_ID, printerName: "Voron 2.4",
                                  queueDepth: 0, isPrinting: false, isAvailable: false),
                PrinterQueueDepth(printerId: DemoData.ender3V3_ID, printerName: "Ender 3 V3",
                                  queueDepth: 0, isPrinting: false, isAvailable: false),
            ],
            stats: DispatchStats(
                dispatchesLast24Hours: 8,
                averageScoreLast24Hours: 87.5,
                autoDispatchesLast24Hours: 5,
                failedDispatchesLast24Hours: 1))
    }

    func getHistory(page: Int?, pageSize: Int?) async throws -> DispatchHistoryPage {
        let now = Date()
        let jobNames = ["benchy.gcode", "phone_case.gcode", "bracket.gcode", "gear.gcode", "lid.gcode"]
        let printerIds = [DemoData.prusaMK4_1_ID, DemoData.prusaMK4_2_ID, DemoData.bambuX1C_ID,
                          DemoData.bambuP1S_ID, DemoData.voron24_ID]
        let printerNames = ["Prusa MK4 #1", "Prusa MK4 #2", "Bambu X1C", "Bambu P1S", "Voron 2.4"]

        let entries: [DispatchHistoryEntry] = (0..<20).map { i in
            let isFailed = i % 4 == 3
            return DispatchHistoryEntry(
                id: UUID(),
                printJobId: UUID(),
                jobName: jobNames[i % 5],
                printerId: printerIds[i % 5],
                printerName: printerNames[i % 5],
                action: isFailed ? "Failed" : "Dispatched",
                score: Double.random(in: 70...98),
                reason: isFailed ? "Printer offline" : "Best match by material and queue depth",
                createdAtUtc: now.addingTimeInterval(Double(-3600 * (i + 1))))
        }
        return DispatchHistoryPage(items: entries, totalCount: 48, page: page ?? 1, pageSize: pageSize ?? 20)
    }
}
