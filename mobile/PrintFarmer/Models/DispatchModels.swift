import Foundation

// MARK: - Dispatch Queue Status

struct DispatchQueueStatus: Codable, Sendable {
    let pendingUnassignedJobs: Int
    let totalQueuedJobs: Int
    let idlePrinters: Int
    let busyPrinters: Int
    let printerQueueDepths: [PrinterQueueDepth]
    let stats: DispatchStats
}

// MARK: - Printer Queue Depth

struct PrinterQueueDepth: Codable, Sendable {
    let printerId: UUID
    let printerName: String
    let queueDepth: Int
    let isPrinting: Bool
    let isAvailable: Bool
}

// MARK: - Dispatch Stats

struct DispatchStats: Codable, Sendable {
    let dispatchesLast24Hours: Int
    let averageScoreLast24Hours: Double
    let autoDispatchesLast24Hours: Int
    let failedDispatchesLast24Hours: Int
}

// MARK: - Dispatch History Page

struct DispatchHistoryPage: Codable, Sendable {
    let items: [DispatchHistoryEntry]
    let totalCount: Int
    let page: Int
    let pageSize: Int
}

// MARK: - Dispatch History Entry

struct DispatchHistoryEntry: Codable, Sendable, Identifiable {
    let id: UUID
    let printJobId: UUID
    let jobName: String?
    let printerId: UUID
    let printerName: String?
    let action: String
    let score: Double?
    let reason: String?
    let createdAtUtc: Date
}
