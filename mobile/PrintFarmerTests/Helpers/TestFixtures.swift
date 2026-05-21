import Foundation
@testable import PrintFarmer

// MARK: - JSON Fixtures

/// Realistic JSON payloads derived from the Printfarmer backend DTOs.
enum TestJSON {

    // MARK: Printer (CompletePrinterDto)

    static let printer = """
    {
        "id": "550e8400-e29b-41d4-a716-446655440000",
        "name": "Prusa MK4",
        "notes": "Workshop printer",
        "manufacturerId": "a1b2c3d4-e5f6-7890-abcd-ef1234567890",
        "manufacturerName": "Prusa Research",
        "modelId": "b2c3d4e5-f6a7-8901-bcde-f12345678901",
        "modelName": "MK4",
        "motionType": "Cartesian",
        "backend": "Moonraker",
        "apiKey": "test-api-key",
        "originalServerUrl": "http://192.168.1.100",
        "backendPort": 7125,
        "frontendPort": 80,
        "inMaintenance": false,
        "isEnabled": true,
        "isOnline": true,
        "state": "printing",
        "progress": 45.5,
        "jobName": "benchy.gcode",
        "thumbnailUrl": "http://192.168.1.100/thumb/benchy.png",
        "cameraStreamUrl": "http://192.168.1.100:8080/?action=stream",
        "x": 120.0,
        "y": 85.5,
        "z": 12.3,
        "hotendTemp": 215.0,
        "bedTemp": 60.0,
        "hotendTarget": 215.0,
        "bedTarget": 60.0,
        "homedAxes": "xyz",
        "spoolInfo": {
            "hasActiveSpool": true,
            "activeSpoolId": 42,
            "spoolName": "PLA Basic Black",
            "material": "PLA",
            "colorHex": "#000000",
            "filamentName": "Prusament PLA",
            "vendor": "Prusa Research",
            "remainingWeightG": 750.0,
            "spoolInUse": true
        },
        "backendUrl": "http://192.168.1.100:7125",
        "frontendUrl": "http://192.168.1.100",
        "location": {
            "id": "c3d4e5f6-a7b8-9012-cdef-123456789012",
            "name": "Workshop",
            "description": "Main workshop area"
        }
    }
    """

    static let printerMinimal = """
    {
        "id": "660e8400-e29b-41d4-a716-446655440001",
        "name": "Ender 3",
        "backend": "Moonraker",
        "backendPort": 7125,
        "inMaintenance": false,
        "isEnabled": true,
        "isOnline": false
    }
    """

    static let printerArray = "[\(printer), \(printerMinimal)]"

    // MARK: PrintJob (JobQueuePrintJobDto)

    static let printJob = """
    {
        "id": "770e8400-e29b-41d4-a716-446655440002",
        "status": "Printing",
        "priority": 1,
        "queuePosition": 1,
        "gcodeFileId": "880e8400-e29b-41d4-a716-446655440003",
        "gcodeFileName": "benchy.gcode",
        "assignedPrinterId": "550e8400-e29b-41d4-a716-446655440000",
        "assignedPrinterName": "Prusa MK4",
        "createdAt": "2025-07-17T10:00:00Z",
        "updatedAt": "2025-07-17T10:30:00Z",
        "actualStartTime": "2025-07-17T10:15:00Z",
        "estimatedPrintTime": "01:00:00",
        "estimatedFilamentUsage": 15.5,
        "estimatedCost": 2.50,
        "copies": 3,
        "completedCopies": 1,
        "remainingCopies": 2,
        "filamentName": "Prusament PLA",
        "filamentVendor": "Prusa Research",
        "filamentColor": "#000000"
    }
    """

    static let printJobQueued = """
    {
        "id": "990e8400-e29b-41d4-a716-446655440004",
        "status": "Queued",
        "priority": 2,
        "queuePosition": 2,
        "gcodeFileName": "phone_case.gcode",
        "assignedPrinterName": "",
        "createdAt": "2025-07-17T09:00:00Z",
        "updatedAt": "2025-07-17T09:00:00Z",
        "copies": 1,
        "completedCopies": 0,
        "remainingCopies": 1
    }
    """

    static let printJobArray = "[\(printJob), \(printJobQueued)]"

    // MARK: QueueOverview (QueueOverviewDto)

    static let queueOverview = """
    {
        "printerId": "550e8400-e29b-41d4-a716-446655440000",
        "printerName": "Prusa MK4",
        "printerModel": "MK4",
        "isAvailable": true,
        "queuedJobsCount": 2,
        "currentJobId": "770e8400-e29b-41d4-a716-446655440002",
        "currentJobName": "benchy.gcode"
    }
    """

    static let queueOverviewArray = "[\(queueOverview)]"

    // MARK: Location (LocationDto)

    static let location = """
    {
        "id": "c3d4e5f6-a7b8-9012-cdef-123456789012",
        "name": "Workshop",
        "description": "Main workshop area",
        "printerCount": 5,
        "createdAt": "2025-01-01T00:00:00Z",
        "modifiedAt": "2025-07-17T12:00:00Z",
        "isActive": true
    }
    """

    static let locationMinimal = """
    {
        "id": "d4e5f6a7-b8c9-0123-def0-234567890123",
        "name": "Garage",
        "printerCount": 0,
        "createdAt": "2025-06-01T00:00:00Z",
        "modifiedAt": "2025-06-01T00:00:00Z",
        "isActive": false
    }
    """

    // MARK: Auth

    static let authResponseSuccess = """
    {
        "success": true,
        "token": "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.test.signature",
        "expiresAt": "2099-07-18T10:00:00Z",
        "user": {
            "id": "aab2c3d4-e5f6-7890-abcd-ef1234567890",
            "username": "admin",
            "email": "admin@printfarmer.local",
            "firstName": "Admin",
            "lastName": "User",
            "isActive": true,
            "emailConfirmed": true,
            "lastLogin": "2025-07-17T09:00:00Z",
            "createdAt": "2025-01-01T00:00:00Z",
            "roles": ["Admin"],
            "permissions": ["printers.manage", "jobs.manage"]
        }
    }
    """

    static let authResponseFailure = """
    {
        "success": false,
        "error": "Invalid username or password"
    }
    """

    static let userDTO = """
    {
        "id": "aab2c3d4-e5f6-7890-abcd-ef1234567890",
        "username": "admin",
        "email": "admin@printfarmer.local",
        "firstName": "Admin",
        "lastName": "User",
        "isActive": true,
        "emailConfirmed": true,
        "lastLogin": "2025-07-17T09:00:00Z",
        "createdAt": "2025-01-01T00:00:00Z",
        "roles": ["Admin"],
        "permissions": ["printers.manage"]
    }
    """

    // MARK: CommandResult

    static let commandSuccess = """
    {"success": true, "message": "Command executed"}
    """

    static let commandFailure = """
    {"success": false, "message": "Printer not ready"}
    """

    // MARK: QueuedPrintJobResponse

    static let queuedPrintJobResponsePrinting = """
    {
        "job": {
            "id": "770e8400-e29b-41d4-a716-446655440002",
            "name": "benchy.gcode",
            "fileName": "benchy.gcode",
            "assignedPrinterId": "550e8400-e29b-41d4-a716-446655440000",
            "printerName": "Prusa MK4",
            "printerModel": "MK4",
            "status": "Printing",
            "priority": 1,
            "queuePosition": 1,
            "estimatedPrintTimeSeconds": 3600,
            "actualStartTimeUtc": "2025-07-17T10:15:00Z",
            "createdAtUtc": "2025-07-17T10:00:00Z",
            "updatedAtUtc": "2025-07-17T10:30:00Z",
            "copies": 1,
            "completedCopies": 0,
            "remainingCopies": 1
        },
        "gcodeFile": null,
        "assignedPrinter": null,
        "estimatedStartTime": null,
        "estimatedCompletionTime": null
    }
    """

    static let queuedPrintJobResponseQueued = """
    {
        "job": {
            "id": "880e8400-e29b-41d4-a716-446655440003",
            "name": "phone_case.gcode",
            "fileName": "phone_case.gcode",
            "status": "Queued",
            "priority": 2,
            "queuePosition": 2,
            "createdAtUtc": "2025-07-17T09:00:00Z",
            "copies": 1,
            "completedCopies": 0,
            "remainingCopies": 1
        },
        "gcodeFile": null,
        "assignedPrinter": null,
        "estimatedStartTime": null,
        "estimatedCompletionTime": null
    }
    """

    static let queuedPrintJobResponseCompleted = """
    {
        "job": {
            "id": "990e8400-e29b-41d4-a716-446655440004",
            "name": "vase.gcode",
            "fileName": "vase.gcode",
            "status": "Completed",
            "priority": 1,
            "queuePosition": 0,
            "actualStartTimeUtc": "2025-07-17T08:00:00Z",
            "actualEndTimeUtc": "2025-07-17T09:00:00Z",
            "createdAtUtc": "2025-07-17T07:00:00Z",
            "copies": 1,
            "completedCopies": 1,
            "remainingCopies": 0
        },
        "gcodeFile": null,
        "assignedPrinter": null,
        "estimatedStartTime": null,
        "estimatedCompletionTime": null
    }
    """

    static let queuedPrintJobResponseFailed = """
    {
        "job": {
            "id": "aa0e8400-e29b-41d4-a716-446655440005",
            "name": "failed_part.gcode",
            "fileName": "failed_part.gcode",
            "status": "Failed",
            "priority": 1,
            "queuePosition": 0,
            "failureReason": "Filament runout",
            "createdAtUtc": "2025-07-17T06:00:00Z",
            "copies": 1,
            "completedCopies": 0,
            "remainingCopies": 1
        },
        "gcodeFile": null,
        "assignedPrinter": null,
        "estimatedStartTime": null,
        "estimatedCompletionTime": null
    }
    """

    static let queuedPrintJobResponsePaused = """
    {
        "job": {
            "id": "bb0e8400-e29b-41d4-a716-446655440006",
            "name": "paused_model.gcode",
            "fileName": "paused_model.gcode",
            "status": "Paused",
            "priority": 1,
            "queuePosition": 1,
            "actualStartTimeUtc": "2025-07-17T11:00:00Z",
            "createdAtUtc": "2025-07-17T10:00:00Z",
            "copies": 1,
            "completedCopies": 0,
            "remainingCopies": 1
        },
        "gcodeFile": null,
        "assignedPrinter": null,
        "estimatedStartTime": null,
        "estimatedCompletionTime": null
    }
    """

    static let queuedPrintJobResponseAssigned = """
    {
        "job": {
            "id": "cc0e8400-e29b-41d4-a716-446655440007",
            "name": "assigned_job.gcode",
            "fileName": "assigned_job.gcode",
            "assignedPrinterId": "550e8400-e29b-41d4-a716-446655440000",
            "printerName": "Prusa MK4",
            "status": "Assigned",
            "priority": 1,
            "queuePosition": 1,
            "createdAtUtc": "2025-07-17T12:00:00Z",
            "copies": 1,
            "completedCopies": 0,
            "remainingCopies": 1
        },
        "gcodeFile": null,
        "assignedPrinter": null,
        "estimatedStartTime": null,
        "estimatedCompletionTime": null
    }
    """

    // MARK: AppNotification

    static let appNotificationUnread = """
    {
        "id": "notif-001",
        "userId": "aab2c3d4-e5f6-7890-abcd-ef1234567890",
        "jobId": "770e8400-e29b-41d4-a716-446655440002",
        "type": "JobCompleted",
        "subject": "Print Complete",
        "body": "benchy.gcode finished printing on Prusa MK4",
        "isRead": false,
        "createdAt": "2025-07-17T11:00:00Z"
    }
    """

    static let appNotificationRead = """
    {
        "id": "notif-002",
        "userId": "aab2c3d4-e5f6-7890-abcd-ef1234567890",
        "type": "SystemAlert",
        "subject": "System Update",
        "body": "Printfarmer has been updated to v2.5",
        "isRead": true,
        "createdAt": "2025-07-17T09:00:00Z",
        "readAt": "2025-07-17T09:30:00Z"
    }
    """

    static let appNotificationFailed = """
    {
        "id": "notif-003",
        "userId": "aab2c3d4-e5f6-7890-abcd-ef1234567890",
        "jobId": "aa0e8400-e29b-41d4-a716-446655440005",
        "type": "JobFailed",
        "subject": "Print Failed",
        "body": "failed_part.gcode failed on Prusa MK4: Filament runout",
        "isRead": false,
        "createdAt": "2025-07-17T10:30:00Z"
    }
    """

    // MARK: StatisticsSummary

    static let statisticsSummary = """
    {
        "totalJobs": 150,
        "completedJobs": 120,
        "failedJobs": 15,
        "cancelledJobs": 15,
        "successRate": 80.0,
        "totalCost": 450.50,
        "totalFilamentGrams": 5000.0,
        "totalPrintHours": 320.5
    }
    """

    static let statisticsSummaryEmpty = """
    {
        "totalJobs": 0,
        "completedJobs": 0,
        "failedJobs": 0,
        "cancelledJobs": 0,
        "successRate": 0.0,
        "totalCost": 0,
        "totalFilamentGrams": 0.0,
        "totalPrintHours": 0.0
    }
    """

    // MARK: Spoolman Spool

    static let spoolmanSpool = """
    {
        "id": 1,
        "name": "PLA Basic Black",
        "material": "PLA",
        "colorHex": "#000000",
        "inUse": true,
        "filamentName": "Prusament PLA",
        "vendor": "Prusa Research",
        "registeredAt": "2025-01-15T10:00:00Z",
        "firstUsedAt": "2025-01-20T14:00:00Z",
        "lastUsedAt": "2025-07-17T10:00:00Z",
        "remainingWeightG": 750.0,
        "initialWeightG": 1000.0,
        "usedWeightG": 250.0,
        "spoolWeightG": 200.0,
        "remainingLengthMm": 250000.0,
        "usedLengthMm": 83000.0,
        "archived": false,
        "price": 25.99,
        "usedPercent": 25.0,
        "remainingPercent": 75.0
    }
    """

    static let spoolmanSpoolMinimal = """
    {
        "id": 2,
        "name": "PETG Orange",
        "material": "PETG",
        "colorHex": "#FF6600",
        "inUse": false,
        "filamentName": "eSUN PETG",
        "vendor": "eSUN",
        "remainingWeightG": 1000.0,
        "initialWeightG": 1000.0,
        "usedWeightG": 0.0,
        "archived": false
    }
    """

    static let spoolmanSpoolArray = "[\(spoolmanSpool), \(spoolmanSpoolMinimal)]"

    static let spoolmanPagedResult = """
    {"items": [\(spoolmanSpool), \(spoolmanSpoolMinimal)], "totalCount": 2}
    """

    static let spoolmanPagedResultEmpty = """
    {"items": [], "totalCount": 0}
    """

    // MARK: Errors

    static let apiError = """
    {
        "title": "Validation Error",
        "status": 400,
        "detail": "The printer name is required.",
        "errors": {
            "name": ["The Name field is required."]
        }
    }
    """
}

// MARK: - Model Factories

enum TestData {
    static let testUUID = UUID(uuidString: "550e8400-e29b-41d4-a716-446655440000")!
    static let testUUID2 = UUID(uuidString: "660e8400-e29b-41d4-a716-446655440001")!
    static let testUUID3 = UUID(uuidString: "770e8400-e29b-41d4-a716-446655440002")!
    static let testBaseURL = URL(string: "https://print.example.com")!

    static let decoder: JSONDecoder = {
        let d = JSONDecoder()
        d.dateDecodingStrategy = .iso8601
        return d
    }()

    static func decodePrinter(from json: String = TestJSON.printer) throws -> Printer {
        try decoder.decode(Printer.self, from: json.data(using: .utf8)!)
    }

    static func decodePrintJob(from json: String = TestJSON.printJob) throws -> PrintJob {
        try decoder.decode(PrintJob.self, from: json.data(using: .utf8)!)
    }

    static func decodeLocation(from json: String = TestJSON.location) throws -> Location {
        try decoder.decode(Location.self, from: json.data(using: .utf8)!)
    }

    static func decodeQueuedPrintJobResponse(from json: String) throws -> QueuedPrintJobResponse {
        try decoder.decode(QueuedPrintJobResponse.self, from: json.data(using: .utf8)!)
    }

    static func decodeAppNotification(from json: String) throws -> AppNotification {
        try decoder.decode(AppNotification.self, from: json.data(using: .utf8)!)
    }

    static func decodeStatisticsSummary(from json: String = TestJSON.statisticsSummary) throws -> StatisticsSummary {
        try decoder.decode(StatisticsSummary.self, from: json.data(using: .utf8)!)
    }

    static func decodeSpoolmanSpool(from json: String = TestJSON.spoolmanSpool) throws -> SpoolmanSpool {
        try decoder.decode(SpoolmanSpool.self, from: json.data(using: .utf8)!)
    }

    static func decodeSpoolmanPagedResult(from json: String = TestJSON.spoolmanPagedResult) throws -> SpoolmanPagedResult<SpoolmanSpool> {
        try decoder.decode(SpoolmanPagedResult<SpoolmanSpool>.self, from: json.data(using: .utf8)!)
    }

    static func decodeAuthResponse(from json: String = TestJSON.authResponseSuccess) throws -> AuthResponse {
        try decoder.decode(AuthResponse.self, from: json.data(using: .utf8)!)
    }

    static func decodeUser(from json: String = TestJSON.userDTO) throws -> UserDTO {
        try decoder.decode(UserDTO.self, from: json.data(using: .utf8)!)
    }

    static func httpResponse(url: URL? = nil, statusCode: Int = 200) -> HTTPURLResponse {
        HTTPURLResponse(
            url: url ?? testBaseURL,
            statusCode: statusCode,
            httpVersion: "HTTP/1.1",
            headerFields: ["Content-Type": "application/json"]
        )!
    }
}
