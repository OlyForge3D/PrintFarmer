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

// MARK: - Dispatch Candidate (matches DispatchCandidateDto)
//
// Returned by `GET /api/job-queue/{id}/candidates`: every printer ranked by
// compatibility for a given job, with eliminated printers surfaced (not
// dropped) so the operator sees *why* a printer can't take the job. Consumed
// by the Printer Detail v2 queue section (issue #712) to drive the
// dispatch-to affordance. Wire-matching only — no scoring is recomputed
// on-device.

struct DispatchCandidate: Codable, Sendable, Identifiable {
    let printerId: UUID
    let printerName: String
    let score: Double
    let eliminated: Bool
    let eliminationReasons: [String]

    var id: UUID { printerId }

    private enum CodingKeys: String, CodingKey {
        case printerId, printerName, score, eliminated, eliminationReasons
    }

    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        printerId = try c.decode(UUID.self, forKey: .printerId)
        printerName = try c.decodeIfPresent(String.self, forKey: .printerName) ?? ""
        score = try c.decodeIfPresent(Double.self, forKey: .score) ?? 0
        eliminated = try c.decodeIfPresent(Bool.self, forKey: .eliminated) ?? false
        eliminationReasons = try c.decodeIfPresent([String].self, forKey: .eliminationReasons) ?? []
    }

    init(
        printerId: UUID,
        printerName: String,
        score: Double,
        eliminated: Bool,
        eliminationReasons: [String]
    ) {
        self.printerId = printerId
        self.printerName = printerName
        self.score = score
        self.eliminated = eliminated
        self.eliminationReasons = eliminationReasons
    }
}

// MARK: - Dispatch-To Request (matches DispatchJobDto)

struct DispatchToRequest: Encodable, Sendable {
    let printerId: UUID
}
