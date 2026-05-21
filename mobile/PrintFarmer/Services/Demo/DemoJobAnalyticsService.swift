import Foundation

// MARK: - Demo Job Analytics Service

final class DemoJobAnalyticsService: JobAnalyticsServiceProtocol, @unchecked Sendable {

    func getQueuedJobs(filterStatus: String?, filterModel: String?, filterMaterial: String?,
                       limit: Int?, offset: Int?) async throws -> [QueuedJobWithMeta] {
        let now = Date()
        return [
            QueuedJobWithMeta(
                job: QueuedJobAnalytics(
                    id: DemoData.job5ID.uuidString, name: "raspberry_pi_case.gcode",
                    status: "Queued", priority: 1, queuePosition: 1,
                    assignedPrinterId: nil, printerName: nil, printerModel: nil,
                    fileName: "raspberry_pi_case.gcode", thumbnailUrl: nil,
                    createdAt: now.addingTimeInterval(-3600), startedAt: nil, completedAt: nil),
                gcodeFile: GcodeFileMeta(id: UUID().uuidString, fileName: "raspberry_pi_case.gcode",
                                         materialType: "PLA", nozzleDiameter: 0.4, thumbnailUrl: nil),
                assignedPrinter: nil,
                estimatedStartTime: now.addingTimeInterval(7200),
                estimatedCompletionTime: now.addingTimeInterval(15300)),
            QueuedJobWithMeta(
                job: QueuedJobAnalytics(
                    id: DemoData.job6ID.uuidString, name: "drone_propeller_guard.gcode",
                    status: "Queued", priority: 3, queuePosition: 2,
                    assignedPrinterId: nil, printerName: nil, printerModel: nil,
                    fileName: "drone_propeller_guard.gcode", thumbnailUrl: nil,
                    createdAt: now.addingTimeInterval(-1800), startedAt: nil, completedAt: nil),
                gcodeFile: GcodeFileMeta(id: UUID().uuidString, fileName: "drone_propeller_guard.gcode",
                                         materialType: "PETG", nozzleDiameter: 0.4, thumbnailUrl: nil),
                assignedPrinter: nil,
                estimatedStartTime: now.addingTimeInterval(14400),
                estimatedCompletionTime: now.addingTimeInterval(31500)),
        ]
    }

    func getStats() async throws -> QueueStats {
        QueueStats(
            totalQueued: 2, totalPrinting: 3, totalPaused: 1,
            averageWaitTimeMinutes: 45,
            byModel: [
                QueuePrinterModelStats(modelName: "MK4", totalQueued: 1, currentlyPrinting: 2,
                                       oldestQueuedAtUtc: Date().addingTimeInterval(-3600), averageQueueWaitMinutes: 30),
                QueuePrinterModelStats(modelName: "X1 Carbon", totalQueued: 1, currentlyPrinting: 1,
                                       oldestQueuedAtUtc: Date().addingTimeInterval(-1800), averageQueueWaitMinutes: 60),
            ])
    }

    func getModelStats() async throws -> [QueuePrinterModelStats] {
        [
            QueuePrinterModelStats(modelName: "MK4", totalQueued: 1, currentlyPrinting: 2,
                                   oldestQueuedAtUtc: Date().addingTimeInterval(-3600), averageQueueWaitMinutes: 30),
            QueuePrinterModelStats(modelName: "X1 Carbon", totalQueued: 1, currentlyPrinting: 1,
                                   oldestQueuedAtUtc: Date().addingTimeInterval(-1800), averageQueueWaitMinutes: 60),
            QueuePrinterModelStats(modelName: "P1S", totalQueued: 0, currentlyPrinting: 0,
                                   oldestQueuedAtUtc: nil, averageQueueWaitMinutes: 0),
        ]
    }

    func getHistory(limit: Int?, offset: Int?, sortBy: String?, statuses: String?,
                    dateStart: Date?, dateEnd: Date?) async throws -> QueueHistoryPage {
        let now = Date()
        return QueueHistoryPage(
            entries: [
                QueueHistoryEntry(id: DemoData.job7ID.uuidString, jobName: "benchy_calibration.gcode",
                                  printerName: "Prusa MK4 #2", status: "Completed",
                                  completedAt: now.addingTimeInterval(-166400), durationSeconds: 3600),
                QueueHistoryEntry(id: DemoData.job8ID.uuidString, jobName: "bracket_mount_x2.gcode",
                                  printerName: "Bambu X1C", status: "Completed",
                                  completedAt: now.addingTimeInterval(-248000), durationSeconds: 8000),
                QueueHistoryEntry(id: DemoData.job9ID.uuidString, jobName: "vase_mode_spiral.gcode",
                                  printerName: "Voron 2.4", status: "Failed",
                                  completedAt: now.addingTimeInterval(-79200), durationSeconds: 3600),
            ],
            totalCount: 3, currentPage: 1, pageSize: 20,
            stats: QueueHistoryStats(totalCompleted: 772, totalFailed: 41, averageDurationMinutes: 132))
    }

    func getTimeline(dateFrom: Date?, dateTo: Date?, printerId: UUID?,
                     filterStatus: String?, limit: Int?) async throws -> [TimelineEvent] {
        let now = Date()
        return [
            TimelineEvent(jobId: DemoData.job1ID.uuidString, jobName: "phone_case_v3.gcode",
                          printerName: "Prusa MK4 #1", state: "Printing",
                          enteredAtUtc: now.addingTimeInterval(-7200), exitedAtUtc: nil,
                          durationSeconds: nil, estimatedDurationSeconds: 12000, variancePercent: nil),
            TimelineEvent(jobId: DemoData.job3ID.uuidString, jobName: "gear_housing_x4.gcode",
                          printerName: "Bambu X1C", state: "Printing",
                          enteredAtUtc: now.addingTimeInterval(-5400), exitedAtUtc: nil,
                          durationSeconds: nil, estimatedDurationSeconds: 22200, variancePercent: nil),
        ]
    }

    func getJobStateHistory(jobId: String) async throws -> JobStateHistory {
        let now = Date()
        return JobStateHistory(
            jobId: jobId, jobName: "phone_case_v3.gcode",
            transitions: [
                StateTransition(state: "Queued", enteredAt: now.addingTimeInterval(-86400),
                                exitedAt: now.addingTimeInterval(-7200), durationSeconds: 79200),
                StateTransition(state: "Printing", enteredAt: now.addingTimeInterval(-7200),
                                exitedAt: nil, durationSeconds: nil),
            ],
            totalDurationSeconds: 86400, estimatedDurationSeconds: 12000, variancePercent: nil)
    }

    func getDurationAnalytics(printerId: UUID?, dateFrom: Date?, dateTo: Date?) async throws -> DurationAnalytics {
        DurationAnalytics(
            totalJobs: 847,
            averageEstimatedSeconds: 7920,
            averageActualSeconds: 7680,
            overallAccuracyPercent: 96.9,
            overallVariancePercent: 3.1)
    }
}
