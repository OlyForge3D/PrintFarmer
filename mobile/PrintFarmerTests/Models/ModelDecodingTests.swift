import XCTest
@testable import PrintFarmer

/// Comprehensive model decoding tests using realistic JSON from
/// the Printfarmer backend DTOs.
final class ModelDecodingTests: XCTestCase {

    private let decoder: JSONDecoder = {
        let d = JSONDecoder()
        d.dateDecodingStrategy = .iso8601
        return d
    }()

    // MARK: - Printer (CompletePrinterDto)

    func testPrinterDecodesFullJSON() throws {
        let printer = try decoder.decode(
            Printer.self,
            from: TestJSON.printer.data(using: .utf8)!
        )

        XCTAssertEqual(printer.id, UUID(uuidString: "550e8400-e29b-41d4-a716-446655440000"))
        XCTAssertEqual(printer.name, "Prusa MK4")
        XCTAssertEqual(printer.notes, "Workshop printer")
        XCTAssertEqual(printer.manufacturerName, "Prusa Research")
        XCTAssertEqual(printer.modelName, "MK4")
        XCTAssertEqual(printer.motionType, .cartesian)
        XCTAssertEqual(printer.backend, .moonraker)
        XCTAssertEqual(printer.backendPort, 7125)
        XCTAssertEqual(printer.frontendPort, 80)
        XCTAssertFalse(printer.inMaintenance)
        XCTAssertTrue(printer.isEnabled)
        XCTAssertTrue(printer.isOnline)
        XCTAssertEqual(printer.state, "printing")
        XCTAssertEqual(printer.progress, 45.5)
        XCTAssertEqual(printer.jobName, "benchy.gcode")
    }

    func testPrinterDecodesTemperatures() throws {
        let printer = try decoder.decode(
            Printer.self,
            from: TestJSON.printer.data(using: .utf8)!
        )

        XCTAssertEqual(printer.hotendTemp, 215.0)
        XCTAssertEqual(printer.bedTemp, 60.0)
        XCTAssertEqual(printer.hotendTarget, 215.0)
        XCTAssertEqual(printer.bedTarget, 60.0)
    }

    func testPrinterDecodesCoordinates() throws {
        let printer = try decoder.decode(
            Printer.self,
            from: TestJSON.printer.data(using: .utf8)!
        )

        XCTAssertEqual(printer.x, 120.0)
        XCTAssertEqual(printer.y, 85.5)
        XCTAssertEqual(printer.z, 12.3)
        XCTAssertEqual(printer.homedAxes, "xyz")
    }

    func testPrinterDecodesSpoolInfo() throws {
        let printer = try decoder.decode(
            Printer.self,
            from: TestJSON.printer.data(using: .utf8)!
        )

        XCTAssertNotNil(printer.spoolInfo)
        XCTAssertTrue(printer.spoolInfo!.hasActiveSpool)
        XCTAssertEqual(printer.spoolInfo!.activeSpoolId, 42)
        XCTAssertEqual(printer.spoolInfo!.material, "PLA")
        XCTAssertEqual(printer.spoolInfo!.colorHex, "#000000")
        XCTAssertEqual(printer.spoolInfo!.vendor, "Prusa Research")
        XCTAssertEqual(printer.spoolInfo!.remainingWeightG, 750.0)
    }

    func testPrinterDecodesLocation() throws {
        let printer = try decoder.decode(
            Printer.self,
            from: TestJSON.printer.data(using: .utf8)!
        )

        XCTAssertNotNil(printer.location)
        XCTAssertEqual(printer.location!.name, "Workshop")
        XCTAssertEqual(printer.location!.description, "Main workshop area")
    }

    func testPrinterDecodesURLs() throws {
        let printer = try decoder.decode(
            Printer.self,
            from: TestJSON.printer.data(using: .utf8)!
        )

        XCTAssertEqual(printer.backendUrl, "http://192.168.1.100:7125")
        XCTAssertEqual(printer.frontendUrl, "http://192.168.1.100")
        XCTAssertNotNil(printer.thumbnailUrl)
        XCTAssertNotNil(printer.cameraStreamUrl)
    }

    // MARK: - PrinterStatusDetail homedAxes (#276)

    private func decodeStatusDetail(_ json: String) throws -> PrinterStatusDetail {
        try decoder.decode(PrinterStatusDetail.self, from: json.data(using: .utf8)!)
    }

    func testPrinterStatusDetailDecodesHomedAxesPresent() throws {
        let json = """
        {"id":"550e8400-e29b-41d4-a716-446655440000","isOnline":true,"state":"idle","homedAxes":"xyz"}
        """
        let detail = try decodeStatusDetail(json)
        XCTAssertEqual(detail.homedAxes, "xyz")
    }

    func testPrinterStatusDetailDecodesHomedAxesAbsent() throws {
        let json = """
        {"id":"550e8400-e29b-41d4-a716-446655440000","isOnline":true,"state":"idle"}
        """
        let detail = try decodeStatusDetail(json)
        XCTAssertNil(detail.homedAxes)
    }

    func testPrinterStatusDetailDecodesHomedAxesEmpty() throws {
        let json = """
        {"id":"550e8400-e29b-41d4-a716-446655440000","isOnline":false,"state":null,"homedAxes":""}
        """
        let detail = try decodeStatusDetail(json)
        XCTAssertEqual(detail.homedAxes, "")
    }

    func testPrinterMinimalJSON() throws {
        let printer = try decoder.decode(
            Printer.self,
            from: TestJSON.printerMinimal.data(using: .utf8)!
        )

        XCTAssertEqual(printer.name, "Ender 3")
        XCTAssertFalse(printer.isOnline)
        XCTAssertNil(printer.notes)
        XCTAssertNil(printer.manufacturerName)
        XCTAssertNil(printer.modelName)
        XCTAssertNil(printer.state)
        XCTAssertNil(printer.progress)
        XCTAssertNil(printer.hotendTemp)
        XCTAssertNil(printer.spoolInfo)
        XCTAssertNil(printer.location)
    }

    func testPrinterArrayDecodes() throws {
        let printers = try decoder.decode(
            [Printer].self,
            from: TestJSON.printerArray.data(using: .utf8)!
        )

        XCTAssertEqual(printers.count, 2)
        XCTAssertEqual(printers[0].name, "Prusa MK4")
        XCTAssertEqual(printers[1].name, "Ender 3")
    }

    // MARK: - PrintJob (JobQueuePrintJobDto)

    func testPrintJobDecodesFullJSON() throws {
        let job = try decoder.decode(
            PrintJob.self,
            from: TestJSON.printJob.data(using: .utf8)!
        )

        XCTAssertEqual(job.id, UUID(uuidString: "770e8400-e29b-41d4-a716-446655440002"))
        XCTAssertEqual(job.gcodeFileName, "benchy.gcode")
        XCTAssertEqual(job.name, "benchy.gcode")
        XCTAssertEqual(job.status, .printing)
        XCTAssertEqual(job.priority, 1)
        XCTAssertEqual(job.queuePosition, 1)
        XCTAssertEqual(job.assignedPrinterName, "Prusa MK4")
    }

    func testPrintJobDecodesTimestamps() throws {
        let job = try decoder.decode(
            PrintJob.self,
            from: TestJSON.printJob.data(using: .utf8)!
        )

        XCTAssertNotNil(job.createdAt)
        XCTAssertNotNil(job.updatedAt)
        XCTAssertNotNil(job.actualStartTime)
        XCTAssertNil(job.actualEndTime)
    }

    func testPrintJobDecodesEstimates() throws {
        let job = try decoder.decode(
            PrintJob.self,
            from: TestJSON.printJob.data(using: .utf8)!
        )

        XCTAssertEqual(job.estimatedPrintTime, "01:00:00")
        XCTAssertEqual(job.estimatedFilamentUsage, 15.5)
        XCTAssertEqual(job.estimatedCost, 2.50)
    }

    func testPrintJobDecodesCopyInfo() throws {
        let job = try decoder.decode(
            PrintJob.self,
            from: TestJSON.printJob.data(using: .utf8)!
        )

        XCTAssertEqual(job.copies, 3)
        XCTAssertEqual(job.completedCopies, 1)
        XCTAssertEqual(job.remainingCopies, 2)
        XCTAssertTrue(job.isMultiCopy)
    }

    func testPrintJobDecodesFilamentInfo() throws {
        let job = try decoder.decode(
            PrintJob.self,
            from: TestJSON.printJob.data(using: .utf8)!
        )

        XCTAssertEqual(job.filamentName, "Prusament PLA")
        XCTAssertEqual(job.filamentVendor, "Prusa Research")
        XCTAssertEqual(job.filamentColor, "#000000")
    }

    func testPrintJobMinimalJSON() throws {
        let job = try decoder.decode(
            PrintJob.self,
            from: TestJSON.printJobQueued.data(using: .utf8)!
        )

        XCTAssertEqual(job.gcodeFileName, "phone_case.gcode")
        XCTAssertEqual(job.status, .queued)
        XCTAssertEqual(job.priority, 2)
        XCTAssertNil(job.assignedPrinterId)
        XCTAssertNil(job.actualStartTime)
        XCTAssertNil(job.estimatedPrintTime)
        XCTAssertFalse(job.isMultiCopy)
        XCTAssertEqual(job.remainingCopies, 1)
    }

    func testPrintJobArrayDecodes() throws {
        let jobs = try decoder.decode(
            [PrintJob].self,
            from: TestJSON.printJobArray.data(using: .utf8)!
        )

        XCTAssertEqual(jobs.count, 2)
        XCTAssertEqual(jobs[0].status, .printing)
        XCTAssertEqual(jobs[1].status, .queued)
    }

    // MARK: - Location (LocationDto)

    func testLocationDecodesFullJSON() throws {
        let location = try decoder.decode(
            Location.self,
            from: TestJSON.location.data(using: .utf8)!
        )

        XCTAssertEqual(location.id, UUID(uuidString: "c3d4e5f6-a7b8-9012-cdef-123456789012"))
        XCTAssertEqual(location.name, "Workshop")
        XCTAssertEqual(location.description, "Main workshop area")
        XCTAssertEqual(location.printerCount, 5)
        XCTAssertTrue(location.isActive)
    }

    func testLocationMinimalJSON() throws {
        let location = try decoder.decode(
            Location.self,
            from: TestJSON.locationMinimal.data(using: .utf8)!
        )

        XCTAssertEqual(location.name, "Garage")
        XCTAssertNil(location.description)
        XCTAssertEqual(location.printerCount, 0)
        XCTAssertFalse(location.isActive)
    }

    // MARK: - AuthResponse

    func testAuthResponseSuccessDecodes() throws {
        let response = try decoder.decode(
            AuthResponse.self,
            from: TestJSON.authResponseSuccess.data(using: .utf8)!
        )

        XCTAssertTrue(response.success)
        XCTAssertNotNil(response.token)
        XCTAssertNotNil(response.expiresAt)
        XCTAssertNotNil(response.user)
        XCTAssertEqual(response.user?.username, "admin")
        XCTAssertEqual(response.user?.email, "admin@printfarmer.local")
        XCTAssertEqual(response.user?.roles, ["Admin"])
    }

    func testAuthResponseFailureDecodes() throws {
        let response = try decoder.decode(
            AuthResponse.self,
            from: TestJSON.authResponseFailure.data(using: .utf8)!
        )

        XCTAssertFalse(response.success)
        XCTAssertNil(response.token)
        XCTAssertNil(response.user)
        XCTAssertEqual(response.error, "Invalid username or password")
    }

    // MARK: - UserDTO

    func testUserDTODecodes() throws {
        let user = try decoder.decode(
            UserDTO.self,
            from: TestJSON.userDTO.data(using: .utf8)!
        )

        XCTAssertEqual(user.username, "admin")
        XCTAssertEqual(user.email, "admin@printfarmer.local")
        XCTAssertEqual(user.firstName, "Admin")
        XCTAssertEqual(user.lastName, "User")
        XCTAssertTrue(user.isActive)
        XCTAssertTrue(user.emailConfirmed)
        XCTAssertNotNil(user.lastLogin)
    }

    // MARK: - Enums

    func testPrinterBackendRawValues() {
        XCTAssertEqual(PrinterBackend.unknown.rawValue, "Unknown")
        XCTAssertEqual(PrinterBackend.moonraker.rawValue, "Moonraker")
        XCTAssertEqual(PrinterBackend.prusaLink.rawValue, "PrusaLink")
        XCTAssertEqual(PrinterBackend.sdcp.rawValue, "SDCP")
        XCTAssertEqual(PrinterBackend.octoPrint.rawValue, "OctoPrint")
        XCTAssertEqual(PrinterBackend.flashForge.rawValue, "FlashForge")
    }

    func testPrintJobStatusRawValues() {
        XCTAssertEqual(PrintJobStatus.queued.rawValue, "Queued")
        XCTAssertEqual(PrintJobStatus.assigned.rawValue, "Assigned")
        XCTAssertEqual(PrintJobStatus.starting.rawValue, "Starting")
        XCTAssertEqual(PrintJobStatus.printing.rawValue, "Printing")
        XCTAssertEqual(PrintJobStatus.paused.rawValue, "Paused")
        XCTAssertEqual(PrintJobStatus.completed.rawValue, "Completed")
        XCTAssertEqual(PrintJobStatus.failed.rawValue, "Failed")
        XCTAssertEqual(PrintJobStatus.cancelled.rawValue, "Cancelled")
    }

    func testPrintJobPriorityRawValues() {
        XCTAssertEqual(PrintJobPriority.low.rawValue, "Low")
        XCTAssertEqual(PrintJobPriority.normal.rawValue, "Normal")
        XCTAssertEqual(PrintJobPriority.high.rawValue, "High")
        XCTAssertEqual(PrintJobPriority.urgent.rawValue, "Urgent")
    }

    func testMotionTypeRawValues() {
        XCTAssertEqual(MotionType.cartesian.rawValue, "Cartesian")
        XCTAssertEqual(MotionType.coreXY.rawValue, "CoreXY")
        XCTAssertEqual(MotionType.delta.rawValue, "Delta")
        XCTAssertEqual(MotionType.unknown.rawValue, "Unknown")
    }

    // MARK: - String-only enum decoder coverage (#278)
    // TODO: Add decoder tests for PrinterBackend, MotionType, PrintJobStatus, PrintJobPriority (#278 follow-up)

    func testAutoDispatchStateDecodesNone() throws {
        let state = try decoder.decode(
            AutoDispatchState.self,
            from: Data("\"None\"".utf8)
        )
        XCTAssertEqual(state, .none)
    }

    func testAutoDispatchStateDecodesPendingReady() throws {
        let state = try decoder.decode(
            AutoDispatchState.self,
            from: Data("\"PendingReady\"".utf8)
        )
        XCTAssertEqual(state, .pendingReady)
    }

    func testAutoDispatchStateDecodesReady() throws {
        let state = try decoder.decode(
            AutoDispatchState.self,
            from: Data("\"Ready\"".utf8)
        )
        XCTAssertEqual(state, .ready)
    }

    func testAutoDispatchStateDecodesDismissed() throws {
        let state = try decoder.decode(
            AutoDispatchState.self,
            from: Data("\"Dismissed\"".utf8)
        )
        XCTAssertEqual(state, .dismissed)
    }

    func testAutoDispatchStateDecodesEmptyString() throws {
        let state = try decoder.decode(
            AutoDispatchState.self,
            from: Data("\"\"".utf8)
        )
        XCTAssertEqual(state, .none)
    }

    func testAutoDispatchStateDecodesNullValue() throws {
        let state = try decoder.decode(
            AutoDispatchState.self,
            from: Data("null".utf8)
        )
        XCTAssertEqual(state, .none)
    }

    func testAutoDispatchStateRejectsNumericValue() throws {
        let state = try decoder.decode(
            AutoDispatchState.self,
            from: Data("1".utf8)
        )
        XCTAssertEqual(state, .none)
    }

    func testAutoDispatchStateLowercaseFallsBackToNone() throws {
        let state = try decoder.decode(
            AutoDispatchState.self,
            from: Data("\"none\"".utf8)
        )
        XCTAssertEqual(state, .none)
    }

    func testAutoDispatchStateUnknownStringFallsBackToNone() throws {
        let state = try decoder.decode(
            AutoDispatchState.self,
            from: Data("\"totally_unknown\"".utf8)
        )
        XCTAssertEqual(state, .none)
    }

    // MARK: - Edge Cases

    func testEmptyPrinterArrayDecodes() throws {
        let printers = try decoder.decode(
            [Printer].self,
            from: Data("[]".utf8)
        )
        XCTAssertEqual(printers.count, 0)
    }

    func testEmptyJobArrayDecodes() throws {
        let jobs = try decoder.decode(
            [PrintJob].self,
            from: Data("[]".utf8)
        )
        XCTAssertEqual(jobs.count, 0)
    }

    func testAPIErrorDecodes() throws {
        let error = try decoder.decode(
            APIError.self,
            from: TestJSON.apiError.data(using: .utf8)!
        )

        XCTAssertEqual(error.title, "Validation Error")
        XCTAssertEqual(error.status, 400)
        XCTAssertEqual(error.detail, "The printer name is required.")
        XCTAssertNotNil(error.errors)
        XCTAssertEqual(error.errors?["name"]?.first, "The Name field is required.")
    }

    // MARK: - CommandResult

    func testCommandResultDecodes() throws {
        let json = TestJSON.commandSuccess
        let result = try decoder.decode(
            CommandResult.self,
            from: json.data(using: .utf8)!
        )
        XCTAssertTrue(result.success)
        XCTAssertEqual(result.message, "Command executed")
    }

    func testCommandResultFailureDecodes() throws {
        let json = TestJSON.commandFailure
        let result = try decoder.decode(
            CommandResult.self,
            from: json.data(using: .utf8)!
        )
        XCTAssertFalse(result.success)
        XCTAssertEqual(result.message, "Printer not ready")
    }

    // MARK: - QueueOverview

    func testQueueOverviewDecodes() throws {
        let overview = try decoder.decode(
            QueueOverview.self,
            from: TestJSON.queueOverview.data(using: .utf8)!
        )

        XCTAssertEqual(overview.printerName, "Prusa MK4")
        XCTAssertEqual(overview.printerModel, "MK4")
        XCTAssertTrue(overview.isAvailable)
        XCTAssertEqual(overview.queuedJobsCount, 2)
        XCTAssertEqual(overview.currentJobName, "benchy.gcode")
        XCTAssertEqual(overview.id, UUID(uuidString: "550e8400-e29b-41d4-a716-446655440000"))
    }

    // MARK: - SignalR Models

    func testPrinterStatusUpdateDecodes() throws {
        let json = """
        {
            "id": "550e8400-e29b-41d4-a716-446655440000",
            "isOnline": true,
            "state": "printing",
            "progress": 55.0,
            "jobName": "benchy.gcode",
            "hotendTemp": 215.0,
            "bedTemp": 60.0,
            "hotendTarget": 215.0,
            "bedTarget": 60.0,
            "x": 100.0,
            "y": 50.0,
            "z": 10.0
        }
        """

        let update = try decoder.decode(
            PrinterStatusUpdate.self,
            from: json.data(using: .utf8)!
        )

        XCTAssertEqual(update.id, UUID(uuidString: "550e8400-e29b-41d4-a716-446655440000"))
        XCTAssertTrue(update.isOnline)
        XCTAssertEqual(update.state, "printing")
        XCTAssertEqual(update.progress, 55.0)
        XCTAssertEqual(update.hotendTemp, 215.0)
    }

    func testPrinterStateChangeDecodes() throws {
        let json = """
        {
            "id": "550e8400-e29b-41d4-a716-446655440000",
            "isOnline": false,
            "state": "idle"
        }
        """

        let update = try decoder.decode(
            PrinterStatusUpdate.self,
            from: json.data(using: .utf8)!
        )

        XCTAssertEqual(update.id, UUID(uuidString: "550e8400-e29b-41d4-a716-446655440000"))
        XCTAssertFalse(update.isOnline)
        XCTAssertEqual(update.state, "idle")
        XCTAssertNil(update.jobName)
    }

    // MARK: - Queue History Parity (#675)

    func testQueueHistoryEntryDecodesNewParityFieldsWhenCostIsActual() throws {
        let json = """
        {
            "id": "job-actual",
            "jobName": "multi-material.gcode",
            "printerName": "Prusa XL",
            "status": "failed",
            "completionPercentage": 42,
            "startedAtUtc": "2026-03-25T00:00:00Z",
            "completedAtUtc": "2026-03-25T00:30:00Z",
            "deadlineAtUtc": "2026-03-26T00:00:00Z",
            "actualPrintTimeSeconds": 1800,
            "materialCostUsd": 3.14,
            "totalCostUsd": 4.50,
            "costIsEstimated": false,
            "materialType": "PLA",
            "filamentName": "Prusament PLA",
            "filamentColor": "#FF6600",
            "actualFilamentUsageGrams": 156.8,
            "estimatedFilamentUsageGrams": 160.0,
            "actualCost": 3.14,
            "failureReason": "Layer shift",
            "toolheadUsages": [
                {
                    "id": "usage-1",
                    "printJobId": "job-actual",
                    "toolheadIndex": 0,
                    "spoolmanSpoolId": 10,
                    "filamentUsageGrams": 100.5,
                    "slicerEstimateGrams": 101.0,
                    "filamentName": "Prusament PLA",
                    "filamentColor": "#FF6600",
                    "materialCostUsd": 2.01
                },
                {
                    "id": "usage-2",
                    "printJobId": "job-actual",
                    "toolheadIndex": 1,
                    "spoolmanSpoolId": 11,
                    "filamentUsageGrams": 56.3,
                    "slicerEstimateGrams": 59.0,
                    "filamentName": "Prusament PETG",
                    "filamentColor": "#0066FF",
                    "materialCostUsd": 1.13
                }
            ],
            "tags": [
                {
                    "id": "550e8400-e29b-41d4-a716-446655440000",
                    "name": "urgent",
                    "category": "priority",
                    "isAutoGenerated": false,
                    "color": "#FF0000",
                    "description": "Rush job"
                }
            ]
        }
        """

        let entry = try decoder.decode(QueueHistoryEntry.self, from: json.data(using: .utf8)!)

        XCTAssertEqual(entry.id, "job-actual")
        XCTAssertEqual(entry.completionPercentage, 42)
        XCTAssertEqual(entry.statusBadgeText, "Failed @ 42%")
        XCTAssertEqual(entry.materialCostUsd, Decimal(string: "3.14"))
        XCTAssertEqual(entry.totalCostUsd, Decimal(string: "4.50"))
        XCTAssertFalse(entry.costIsEstimated ?? true)
        XCTAssertEqual(entry.materialType, "PLA")
        XCTAssertEqual(entry.filamentName, "Prusament PLA")
        XCTAssertEqual(entry.filamentColor, "#FF6600")
        XCTAssertEqual(entry.actualFilamentUsageGrams ?? 0, 156.8, accuracy: 0.001)
        XCTAssertEqual(entry.estimatedFilamentUsageGrams ?? 0, 160.0, accuracy: 0.001)
        XCTAssertEqual(entry.actualCost, Decimal(string: "3.14"))
        XCTAssertEqual(entry.failureReason, "Layer shift")
        XCTAssertNotNil(entry.startedAt)
        XCTAssertNotNil(entry.deadlineAt)
        XCTAssertEqual(entry.toolheadUsages?.count, 2)
        XCTAssertEqual(entry.toolheadUsages?.first?.toolheadIndex, 0)
        XCTAssertEqual(entry.toolheadUsages?.first?.materialCostUsd, Decimal(string: "2.01"))
        XCTAssertEqual(entry.tags?.first?.name, "urgent")
        XCTAssertEqual(entry.displayFilamentUsageGrams ?? 0, 156.8, accuracy: 0.001)
        XCTAssertEqual(entry.displayMaterialCostUsd, Decimal(string: "3.14"))
    }

    func testQueueHistoryEntryDecodesNewParityFieldsWhenCostIsEstimated() throws {
        let json = """
        {
            "id": "job-estimated",
            "jobName": "history-seeded.gcode",
            "printerName": "Moonraker",
            "status": "cancelled",
            "completionPercentage": 63.7,
            "startedAtUtc": "2026-03-25T00:00:00Z",
            "completedAtUtc": "2026-03-25T00:30:00Z",
            "actualPrintTimeSeconds": 1800,
            "materialCostUsd": 1.06,
            "costIsEstimated": true,
            "materialType": "PETG",
            "estimatedFilamentUsageGrams": 42.5,
            "toolheadUsages": [],
            "tags": []
        }
        """

        let entry = try decoder.decode(QueueHistoryEntry.self, from: json.data(using: .utf8)!)

        XCTAssertEqual(entry.statusBadgeText, "Cancelled @ 64%")
        XCTAssertTrue(entry.costIsEstimated ?? false)
        XCTAssertEqual(entry.displayFilamentUsageGrams ?? 0, 42.5, accuracy: 0.001)
        XCTAssertTrue(entry.displayFilamentUsageIsEstimated)
        XCTAssertEqual(entry.displayMaterialCostUsd, Decimal(string: "1.06"))
    }

    func testQueueHistoryEntryFallsBackToJobLevelCostAndUsageWhenToolheadsHaveNoMeasurements() throws {
        let json = """
        {
            "id": "job-fallback",
            "jobName": "fallback.gcode",
            "printerName": "Prusa XL",
            "status": "failed",
            "completionPercentage": 12,
            "completedAtUtc": "2026-03-25T00:30:00Z",
            "actualPrintTimeSeconds": 1800,
            "materialCostUsd": 2.22,
            "totalCostUsd": 5.00,
            "costIsEstimated": false,
            "actualFilamentUsageGrams": 12.3,
            "estimatedFilamentUsageGrams": 20.0,
            "toolheadUsages": [
                { "toolheadIndex": 0, "filamentName": "PLA" },
                { "toolheadIndex": 1, "filamentName": "PETG" }
            ]
        }
        """

        let entry = try decoder.decode(QueueHistoryEntry.self, from: json.data(using: .utf8)!)

        XCTAssertEqual(entry.displayMaterialCostUsd, Decimal(string: "2.22"))
        XCTAssertEqual(entry.displayFilamentUsageGrams ?? 0, 12.3, accuracy: 0.001)
        XCTAssertFalse(entry.displayFilamentUsageIsEstimated)
    }

    func testQueueHistoryEntrySumsToolheadSlicerEstimatesAndMarksUsageEstimated() throws {
        let json = """
        {
            "id": "job-toolhead-estimates",
            "jobName": "estimate-only.gcode",
            "printerName": "Prusa XL",
            "status": "cancelled",
            "completionPercentage": 37,
            "completedAtUtc": "2026-03-25T00:30:00Z",
            "actualPrintTimeSeconds": 1800,
            "materialCostUsd": 1.23,
            "costIsEstimated": true,
            "toolheadUsages": [
                { "toolheadIndex": 0, "slicerEstimateGrams": 10.5, "filamentName": "PLA" },
                { "toolheadIndex": 1, "slicerEstimateGrams": 20.0, "filamentName": "PETG" }
            ]
        }
        """

        let entry = try decoder.decode(QueueHistoryEntry.self, from: json.data(using: .utf8)!)

        XCTAssertEqual(entry.displayFilamentUsageGrams ?? 0, 30.5, accuracy: 0.001)
        XCTAssertTrue(entry.displayFilamentUsageIsEstimated)
    }

    func testQueueHistoryEntryCompletionBadgeLogicMatchesWeb() {
        func entry(status: String, completionPercentage: Double?) -> QueueHistoryEntry {
            QueueHistoryEntry(
                id: UUID().uuidString,
                jobName: "part.gcode",
                printerName: "U1-2",
                status: status,
                completedAt: Date(),
                durationSeconds: 1800,
                completionPercentage: completionPercentage
            )
        }

        XCTAssertEqual(entry(status: "failed", completionPercentage: 42).statusBadgeText, "Failed @ 42%")
        XCTAssertEqual(entry(status: "cancelled", completionPercentage: 63.7).statusBadgeText, "Cancelled @ 64%")
        XCTAssertEqual(entry(status: "completed", completionPercentage: 100).statusBadgeText, "Completed")
        XCTAssertEqual(entry(status: "failed", completionPercentage: 0).statusBadgeText, "Failed")
        XCTAssertEqual(entry(status: "cancelled", completionPercentage: 100).statusBadgeText, "Cancelled")
        XCTAssertEqual(entry(status: "failed", completionPercentage: nil).statusBadgeText, "Failed")
    }

    // MARK: - Computed Properties

    func testPrintJobRemainingCopiesComputation() throws {
        let job = try decoder.decode(
            PrintJob.self,
            from: TestJSON.printJob.data(using: .utf8)!
        )

        // copies=3, completedCopies=1 → remaining=2
        XCTAssertEqual(job.remainingCopies, 2)
    }

    func testPrintJobRemainingCopiesNeverNegative() throws {
        let json = """
        {
            "id": "990e8400-e29b-41d4-a716-446655440004",
            "status": "Completed",
            "priority": 1,
            "queuePosition": 1,
            "gcodeFileName": "test.gcode",
            "assignedPrinterName": "",
            "createdAt": "2025-07-17T09:00:00Z",
            "updatedAt": "2025-07-17T09:00:00Z",
            "copies": 2,
            "completedCopies": 5,
            "remainingCopies": 0
        }
        """

        let job = try decoder.decode(PrintJob.self, from: json.data(using: .utf8)!)
        XCTAssertEqual(job.remainingCopies, 0, "remainingCopies should be 0 when backend reports 0")
    }

    func testPrintJobIsMultiCopyFalseForSingleCopy() throws {
        let job = try decoder.decode(
            PrintJob.self,
            from: TestJSON.printJobQueued.data(using: .utf8)!
        )

        XCTAssertFalse(job.isMultiCopy)
    }
}
