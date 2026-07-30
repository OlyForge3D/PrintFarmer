import Foundation

// MARK: - Printer History (matches HistoryListResponse / HistoryJob)
//
// Wire models for `GET /api/printers/{id}/history`. The backend mirrors the
// Moonraker history schema: snake_case keys and Unix-epoch *seconds* (Double)
// for timestamps. Consumed by the Printer Detail v2 history tail (issue #712),
// which shows the last few jobs with their outcome. Decoded defensively so a
// partial/degraded provider payload never fails the whole response.

struct PrinterHistoryList: Codable, Sendable {
    let count: Int
    let jobs: [PrinterHistoryJob]

    private enum CodingKeys: String, CodingKey {
        case count, jobs
    }

    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        jobs = try c.decodeIfPresent([PrinterHistoryJob].self, forKey: .jobs) ?? []
        count = try c.decodeIfPresent(Int.self, forKey: .count) ?? jobs.count
    }

    init(count: Int, jobs: [PrinterHistoryJob]) {
        self.count = count
        self.jobs = jobs
    }
}

struct PrinterHistoryJob: Codable, Sendable, Identifiable {
    let jobId: String
    let status: String
    let filename: String
    let startTime: Double
    let endTime: Double?
    let printDuration: Double
    let totalDuration: Double
    let filamentUsed: Double
    let thumbnailUrl: String?

    var id: String { jobId }

    /// Job outcome derived from the provider `status` string. Announced as
    /// text (never color alone) for VoiceOver.
    enum Outcome: String, Sendable {
        case completed
        case cancelled
        case failed
        case inProgress
        case unknown

        var label: String {
            switch self {
            case .completed: "Completed"
            case .cancelled: "Cancelled"
            case .failed: "Failed"
            case .inProgress: "In progress"
            case .unknown: "Unknown"
            }
        }
    }

    var outcome: Outcome {
        switch status.lowercased() {
        case "completed", "complete", "finished", "done", "ok", "success":
            return .completed
        case "cancelled", "canceled", "aborted":
            return .cancelled
        case "error", "failed", "failure", "klippy_shutdown", "interrupted", "server_exit":
            return .failed
        case "in_progress", "printing", "started":
            return .inProgress
        default:
            return .unknown
        }
    }

    /// End timestamp as a `Date`, when the job has finished.
    var endDate: Date? {
        guard let endTime, endTime > 0 else { return nil }
        return Date(timeIntervalSince1970: endTime)
    }

    var startDate: Date? {
        guard startTime > 0 else { return nil }
        return Date(timeIntervalSince1970: startTime)
    }

    private enum CodingKeys: String, CodingKey {
        case jobId = "job_id"
        case status
        case filename
        case startTime = "start_time"
        case endTime = "end_time"
        case printDuration = "print_duration"
        case totalDuration = "total_duration"
        case filamentUsed = "filament_used"
        case thumbnailUrl = "thumbnail_url"
    }

    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        jobId = try c.decodeIfPresent(String.self, forKey: .jobId) ?? UUID().uuidString
        status = try c.decodeIfPresent(String.self, forKey: .status) ?? ""
        filename = try c.decodeIfPresent(String.self, forKey: .filename) ?? ""
        startTime = try c.decodeIfPresent(Double.self, forKey: .startTime) ?? 0
        endTime = try c.decodeIfPresent(Double.self, forKey: .endTime)
        printDuration = try c.decodeIfPresent(Double.self, forKey: .printDuration) ?? 0
        totalDuration = try c.decodeIfPresent(Double.self, forKey: .totalDuration) ?? 0
        filamentUsed = try c.decodeIfPresent(Double.self, forKey: .filamentUsed) ?? 0
        thumbnailUrl = try c.decodeIfPresent(String.self, forKey: .thumbnailUrl)
    }

    init(
        jobId: String,
        status: String,
        filename: String,
        startTime: Double,
        endTime: Double?,
        printDuration: Double,
        totalDuration: Double,
        filamentUsed: Double,
        thumbnailUrl: String? = nil
    ) {
        self.jobId = jobId
        self.status = status
        self.filename = filename
        self.startTime = startTime
        self.endTime = endTime
        self.printDuration = printDuration
        self.totalDuration = totalDuration
        self.filamentUsed = filamentUsed
        self.thumbnailUrl = thumbnailUrl
    }
}
