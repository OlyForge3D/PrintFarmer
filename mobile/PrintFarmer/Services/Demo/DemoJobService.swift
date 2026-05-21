import Foundation

// MARK: - Demo Job Service

final class DemoJobService: JobServiceProtocol, @unchecked Sendable {

    private static let jobs: [PrintJob] = {
        let now = Date()
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601

        func decode(_ json: String) -> PrintJob {
            // swiftlint:disable:next force_try
            try! decoder.decode(PrintJob.self, from: Data(json.utf8))
        }

        let iso = ISO8601DateFormatter()
        func ts(_ offset: TimeInterval) -> String {
            iso.string(from: now.addingTimeInterval(offset))
        }

        return [
            // 3 printing
            decode("""
            {"id":"\(DemoData.job1ID)","status":"Printing","priority":1,"queuePosition":0,
             "gcodeFileName":"phone_case_v3.gcode","assignedPrinterId":"\(DemoData.prusaMK4_1_ID)",
             "assignedPrinterName":"Prusa MK4 #1","createdAt":"\(ts(-86400))","actualStartTime":"\(ts(-7200))",
             "estimatedPrintTime":"3h 20m","estimatedFilamentUsage":45.0,"estimatedCost":2.50,
             "requiredMaterialType":"PLA","copies":1,"completedCopies":0,"remainingCopies":1}
            """),
            decode("""
            {"id":"\(DemoData.job2ID)","status":"Printing","priority":1,"queuePosition":0,
             "gcodeFileName":"cable_chain_link.gcode","assignedPrinterId":"\(DemoData.prusaMK4_1_ID)",
             "assignedPrinterName":"Prusa MK4 #1","createdAt":"\(ts(-43200))","actualStartTime":"\(ts(-3600))",
             "estimatedPrintTime":"1h 45m","estimatedFilamentUsage":22.0,"estimatedCost":1.20,
             "requiredMaterialType":"PLA","copies":4,"completedCopies":1,"remainingCopies":3}
            """),
            decode("""
            {"id":"\(DemoData.job3ID)","status":"Printing","priority":2,"queuePosition":0,
             "gcodeFileName":"gear_housing_x4.gcode","assignedPrinterId":"\(DemoData.bambuX1C_ID)",
             "assignedPrinterName":"Bambu X1C","createdAt":"\(ts(-72000))","actualStartTime":"\(ts(-5400))",
             "estimatedPrintTime":"6h 10m","estimatedFilamentUsage":120.0,"estimatedCost":8.50,
             "requiredMaterialType":"PETG","copies":4,"completedCopies":0,"remainingCopies":4}
            """),
            // 1 paused
            decode("""
            {"id":"\(DemoData.job4ID)","status":"Paused","priority":1,"queuePosition":0,
             "gcodeFileName":"enclosure_panel_left.gcode","assignedPrinterId":"\(DemoData.bambuP1S_ID)",
             "assignedPrinterName":"Bambu P1S","createdAt":"\(ts(-86400))","actualStartTime":"\(ts(-10800))",
             "estimatedPrintTime":"8h 30m","estimatedFilamentUsage":185.0,"estimatedCost":12.00,
             "requiredMaterialType":"ABS","copies":1,"completedCopies":0,"remainingCopies":1}
            """),
            // 2 queued
            decode("""
            {"id":"\(DemoData.job5ID)","status":"Queued","priority":1,"queuePosition":1,
             "gcodeFileName":"raspberry_pi_case.gcode","createdAt":"\(ts(-3600))",
             "estimatedPrintTime":"2h 15m","estimatedFilamentUsage":35.0,"estimatedCost":1.80,
             "requiredMaterialType":"PLA","copies":1,"completedCopies":0,"remainingCopies":1}
            """),
            decode("""
            {"id":"\(DemoData.job6ID)","status":"Queued","priority":3,"queuePosition":2,
             "gcodeFileName":"drone_propeller_guard.gcode","createdAt":"\(ts(-1800))",
             "estimatedPrintTime":"4h 45m","estimatedFilamentUsage":68.0,"estimatedCost":4.20,
             "requiredMaterialType":"PETG","copies":2,"completedCopies":0,"remainingCopies":2}
            """),
            // 3 completed
            decode("""
            {"id":"\(DemoData.job7ID)","status":"Completed","priority":1,"queuePosition":0,
             "gcodeFileName":"benchy_calibration.gcode","assignedPrinterId":"\(DemoData.prusaMK4_2_ID)",
             "assignedPrinterName":"Prusa MK4 #2","createdAt":"\(ts(-172800))",
             "actualStartTime":"\(ts(-170000))","actualEndTime":"\(ts(-166400))",
             "estimatedPrintTime":"1h 0m","actualPrintTime":"1h 0m",
             "estimatedFilamentUsage":15.0,"actualFilamentUsage":14.8,"estimatedCost":0.80,"actualCost":0.78,
             "requiredMaterialType":"PLA","copies":1,"completedCopies":1,"remainingCopies":0}
            """),
            decode("""
            {"id":"\(DemoData.job8ID)","status":"Completed","priority":2,"queuePosition":0,
             "gcodeFileName":"bracket_mount_x2.gcode","assignedPrinterId":"\(DemoData.bambuX1C_ID)",
             "assignedPrinterName":"Bambu X1C","createdAt":"\(ts(-259200))",
             "actualStartTime":"\(ts(-256000))","actualEndTime":"\(ts(-248000))",
             "estimatedPrintTime":"2h 15m","actualPrintTime":"2h 13m",
             "estimatedFilamentUsage":42.0,"actualFilamentUsage":41.5,"estimatedCost":3.20,"actualCost":3.15,
             "requiredMaterialType":"PETG","copies":2,"completedCopies":2,"remainingCopies":0}
            """),
            decode("""
            {"id":"\(UUID())","status":"Completed","priority":1,"queuePosition":0,
             "gcodeFileName":"lid_organizer.gcode","assignedPrinterId":"\(DemoData.prusaMK4_1_ID)",
             "assignedPrinterName":"Prusa MK4 #1","createdAt":"\(ts(-345600))",
             "actualStartTime":"\(ts(-344000))","actualEndTime":"\(ts(-336000))",
             "estimatedPrintTime":"2h 30m","actualPrintTime":"2h 13m",
             "estimatedFilamentUsage":55.0,"actualFilamentUsage":53.2,"estimatedCost":2.90,"actualCost":2.80,
             "requiredMaterialType":"PLA","copies":1,"completedCopies":1,"remainingCopies":0}
            """),
            // 2 failed
            decode("""
            {"id":"\(DemoData.job9ID)","status":"Failed","priority":1,"queuePosition":0,
             "gcodeFileName":"vase_mode_spiral.gcode","assignedPrinterId":"\(DemoData.voron24_ID)",
             "assignedPrinterName":"Voron 2.4","createdAt":"\(ts(-86400))",
             "actualStartTime":"\(ts(-82800))","actualEndTime":"\(ts(-79200))",
             "failureReason":"Layer adhesion failure at layer 42 — likely nozzle clog",
             "requiredMaterialType":"PLA","copies":1,"completedCopies":0,"remainingCopies":1}
            """),
            decode("""
            {"id":"\(DemoData.job10ID)","status":"Failed","priority":1,"queuePosition":0,
             "gcodeFileName":"lamp_shade_textured.gcode","assignedPrinterId":"\(DemoData.voron24_ID)",
             "assignedPrinterName":"Voron 2.4","createdAt":"\(ts(-172800))",
             "actualStartTime":"\(ts(-170000))","actualEndTime":"\(ts(-168000))",
             "failureReason":"Thermal runaway detected — heater disconnected",
             "requiredMaterialType":"PLA","copies":1,"completedCopies":0,"remainingCopies":1}
            """),
            // 1 cancelled
            decode("""
            {"id":"\(DemoData.job11ID)","status":"Cancelled","priority":0,"queuePosition":0,
             "gcodeFileName":"test_cube_20mm.gcode","assignedPrinterId":"\(DemoData.ender3V3_ID)",
             "assignedPrinterName":"Ender 3 V3","createdAt":"\(ts(-432000))",
             "requiredMaterialType":"PLA","copies":1,"completedCopies":0,"remainingCopies":1}
            """),
        ]
    }()

    func list() async throws -> [QueueOverview] {
        DemoData.printers.map { p in
            QueueOverview(
                printerId: p.id, printerName: p.name,
                printerModel: p.modelName ?? "Unknown", modelAliases: nil,
                isAvailable: p.state == "idle" && p.isOnline,
                queuedJobsCount: p.state == "printing" ? 1 : 0,
                currentJobId: nil, currentJobName: p.jobName,
                estimatedCompletionTime: nil, nozzleDiameter: 0.4,
                supportedMaterials: ["PLA", "PETG", "ABS"])
        }
    }

    func listAllJobs() async throws -> [QueuedPrintJobResponse] {
        Self.jobs.map { job in
            QueuedPrintJobResponse(
                job: QueuedJobInfo(
                    id: job.id.uuidString, name: job.gcodeFileName,
                    fileName: job.gcodeFileName,
                    assignedPrinterId: job.assignedPrinterId?.uuidString,
                    printerName: job.assignedPrinterName, printerModel: nil,
                    status: job.status?.rawValue ?? "Queued",
                    priority: job.priority, queuePosition: job.queuePosition,
                    estimatedPrintTimeSeconds: nil,
                    actualStartTimeUtc: job.actualStartTime, actualEndTimeUtc: job.actualEndTime,
                    actualPrintTimeSeconds: nil, failureReason: job.failureReason,
                    createdAtUtc: job.createdAt ?? Date(), updatedAtUtc: job.updatedAt,
                    thumbnailUrl: job.thumbnailUrl, filamentName: job.filamentName,
                    filamentColor: job.filamentColor,
                    copies: job.copies, completedCopies: job.completedCopies,
                    remainingCopies: job.remainingCopies),
                gcodeFile: nil, assignedPrinter: nil,
                estimatedStartTime: nil, estimatedCompletionTime: nil)
        }
    }

    func get(id: UUID) async throws -> PrintJob {
        guard let job = Self.jobs.first(where: { $0.id == id }) else {
            throw ServiceError.notImplemented("Job not found in demo data")
        }
        return job
    }

    func create(_ request: CreatePrintJobRequest) async throws -> PrintJob {
        throw ServiceError.notImplemented("create job — read-only in demo mode")
    }

    func update(id: UUID, _ request: UpdatePrintJobRequest) async throws -> PrintJob {
        throw ServiceError.notImplemented("update job — read-only in demo mode")
    }

    func delete(id: UUID) async throws {}
    func dispatch(id: UUID) async throws {}
    func cancel(id: UUID) async throws {}
    func abort(id: UUID) async throws {}
    func pause(id: UUID) async throws {}
    func resume(id: UUID) async throws {}
}
