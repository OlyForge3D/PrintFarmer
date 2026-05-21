import Foundation

// MARK: - Demo Data Factory

/// Central mock data factory with consistent UUIDs so relationships
/// (printer→job→spool→location) are coherent across all demo services.
enum DemoData {

    // MARK: - Consistent UUIDs

    // Printers
    static let prusaMK4_1_ID  = UUID(uuidString: "10000000-0001-0000-0000-000000000001")!
    static let prusaMK4_2_ID  = UUID(uuidString: "10000000-0001-0000-0000-000000000002")!
    static let bambuX1C_ID    = UUID(uuidString: "10000000-0001-0000-0000-000000000003")!
    static let bambuP1S_ID    = UUID(uuidString: "10000000-0001-0000-0000-000000000004")!
    static let voron24_ID     = UUID(uuidString: "10000000-0001-0000-0000-000000000005")!
    static let ender3V3_ID    = UUID(uuidString: "10000000-0001-0000-0000-000000000006")!

    // Locations
    static let workshopID = UUID(uuidString: "20000000-0002-0000-0000-000000000001")!
    static let officeID   = UUID(uuidString: "20000000-0002-0000-0000-000000000002")!
    static let garageID   = UUID(uuidString: "20000000-0002-0000-0000-000000000003")!

    // Jobs
    static let job1ID  = UUID(uuidString: "30000000-0003-0000-0000-000000000001")!
    static let job2ID  = UUID(uuidString: "30000000-0003-0000-0000-000000000002")!
    static let job3ID  = UUID(uuidString: "30000000-0003-0000-0000-000000000003")!
    static let job4ID  = UUID(uuidString: "30000000-0003-0000-0000-000000000004")!
    static let job5ID  = UUID(uuidString: "30000000-0003-0000-0000-000000000005")!
    static let job6ID  = UUID(uuidString: "30000000-0003-0000-0000-000000000006")!
    static let job7ID  = UUID(uuidString: "30000000-0003-0000-0000-000000000007")!
    static let job8ID  = UUID(uuidString: "30000000-0003-0000-0000-000000000008")!
    static let job9ID  = UUID(uuidString: "30000000-0003-0000-0000-000000000009")!
    static let job10ID = UUID(uuidString: "30000000-0003-0000-0000-000000000010")!
    static let job11ID = UUID(uuidString: "30000000-0003-0000-0000-000000000011")!
    static let job12ID = UUID(uuidString: "30000000-0003-0000-0000-000000000012")!

    // User
    static let demoUserID = UUID(uuidString: "40000000-0004-0000-0000-000000000001")!

    // Manufacturer IDs
    static let prusaManufID = UUID(uuidString: "50000000-0005-0000-0000-000000000001")!
    static let bambuManufID = UUID(uuidString: "50000000-0005-0000-0000-000000000002")!
    static let voronManufID = UUID(uuidString: "50000000-0005-0000-0000-000000000003")!
    static let crealManufID = UUID(uuidString: "50000000-0005-0000-0000-000000000004")!

    // MARK: - Demo User

    static let demoUser = UserDTO(
        id: demoUserID,
        username: "demo_user",
        email: "demo@printfarmer.app",
        firstName: "Demo",
        lastName: "User",
        isActive: true,
        emailConfirmed: true,
        lastLogin: Date(),
        createdAt: Date().addingTimeInterval(-86400 * 90),
        roles: ["admin"],
        permissions: ["read", "write", "manage"]
    )

    // MARK: - Auth Response

    static let demoAuthResponse = AuthResponse(
        success: true,
        token: "demo-jwt-token-not-real",
        expiresAt: Date().addingTimeInterval(86400),
        user: demoUser,
        error: nil
    )

    // MARK: - Location Summaries

    static let workshopSummary = LocationSummary(id: workshopID, name: "Workshop", description: "Main workshop area")
    static let officeSummary = LocationSummary(id: officeID, name: "Office", description: "Office print station")
    static let garageSummary = LocationSummary(id: garageID, name: "Garage", description: "Garage build area")

    // MARK: - Locations

    static let locations: [Location] = [
        Location(id: workshopID, name: "Workshop", description: "Main workshop area",
                 printerCount: 3, createdAt: Date().addingTimeInterval(-86400 * 180),
                 modifiedAt: Date().addingTimeInterval(-86400 * 2), isActive: true),
        Location(id: officeID, name: "Office", description: "Office print station",
                 printerCount: 2, createdAt: Date().addingTimeInterval(-86400 * 120),
                 modifiedAt: Date().addingTimeInterval(-86400 * 5), isActive: true),
        Location(id: garageID, name: "Garage", description: "Garage build area",
                 printerCount: 1, createdAt: Date().addingTimeInterval(-86400 * 60),
                 modifiedAt: Date().addingTimeInterval(-86400 * 10), isActive: true),
    ]

    // MARK: - Spool Info

    static let plaBlackSpool = PrinterSpoolInfo(
        hasActiveSpool: true, activeSpoolId: 1, spoolName: "PLA Basic Black",
        material: "PLA", colorHex: "#000000", filamentName: "Prusament PLA",
        vendor: "Prusa Research", remainingWeightG: 750.0, spoolInUse: true)

    static let petgClearSpool = PrinterSpoolInfo(
        hasActiveSpool: true, activeSpoolId: 3, spoolName: "PETG Clear",
        material: "PETG", colorHex: "#FFFFFF", filamentName: "eSun PETG",
        vendor: "eSun", remainingWeightG: 620.0, spoolInUse: true)

    static let plaRedSpool = PrinterSpoolInfo(
        hasActiveSpool: true, activeSpoolId: 5, spoolName: "PLA Red",
        material: "PLA", colorHex: "#FF0000", filamentName: "Prusament PLA",
        vendor: "Prusa Research", remainingWeightG: 430.0, spoolInUse: true)

    static let absWhiteSpool = PrinterSpoolInfo(
        hasActiveSpool: true, activeSpoolId: 6, spoolName: "ABS White",
        material: "ABS", colorHex: "#FFFFFF", filamentName: "Hatchbox ABS",
        vendor: "Hatchbox", remainingWeightG: 890.0, spoolInUse: true)

    // MARK: - JSON Printer Factory

    /// Creates Printer instances via JSON decoding (Printer only has init(from:))
    static func decodePrinter(from json: String) -> Printer {
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601
        // swiftlint:disable:next force_try
        return try! decoder.decode(Printer.self, from: Data(json.utf8))
    }

    // MARK: - Printers

    static let printers: [Printer] = [
        decodePrinter(from: """
        {
            "id": "\(prusaMK4_1_ID.uuidString)",
            "name": "Prusa MK4 #1",
            "notes": "Primary production printer",
            "manufacturerId": "\(prusaManufID.uuidString)",
            "manufacturerName": "Prusa Research",
            "modelName": "MK4",
            "motionType": "Cartesian",
            "backend": "Moonraker",
            "backendPort": 7125,
            "frontendPort": 80,
            "inMaintenance": false,
            "isEnabled": true,
            "isOnline": true,
            "state": "printing",
            "progress": 67.3,
            "jobName": "phone_case_v3.gcode",
            "hotendTemp": 215.0,
            "bedTemp": 60.0,
            "hotendTarget": 215.0,
            "bedTarget": 60.0,
            "homedAxes": "xyz",
            "x": 120.5, "y": 85.3, "z": 14.2,
            "spoolInfo": {
                "hasActiveSpool": true,
                "activeSpoolId": 1,
                "spoolName": "PLA Basic Black",
                "material": "PLA",
                "colorHex": "#000000",
                "filamentName": "Prusament PLA",
                "vendor": "Prusa Research",
                "remainingWeightG": 750.0,
                "spoolInUse": true
            },
            "location": { "id": "\(workshopID.uuidString)", "name": "Workshop", "description": "Main workshop area" },
            "obicoEnabled": true
        }
        """),
        decodePrinter(from: """
        {
            "id": "\(prusaMK4_2_ID.uuidString)",
            "name": "Prusa MK4 #2",
            "notes": "Secondary printer — recently calibrated",
            "manufacturerId": "\(prusaManufID.uuidString)",
            "manufacturerName": "Prusa Research",
            "modelName": "MK4",
            "motionType": "Cartesian",
            "backend": "Moonraker",
            "backendPort": 7125,
            "inMaintenance": false,
            "isEnabled": true,
            "isOnline": true,
            "state": "idle",
            "hotendTemp": 25.0,
            "bedTemp": 23.0,
            "location": { "id": "\(workshopID.uuidString)", "name": "Workshop", "description": "Main workshop area" }
        }
        """),
        decodePrinter(from: """
        {
            "id": "\(bambuX1C_ID.uuidString)",
            "name": "Bambu X1C",
            "notes": "High-speed enclosed printer",
            "manufacturerId": "\(bambuManufID.uuidString)",
            "manufacturerName": "Bambu Lab",
            "modelName": "X1 Carbon",
            "motionType": "CoreXY",
            "backend": "Unknown",
            "backendPort": 80,
            "inMaintenance": false,
            "isEnabled": true,
            "isOnline": true,
            "state": "printing",
            "progress": 23.1,
            "jobName": "gear_housing_x4.gcode",
            "hotendTemp": 250.0,
            "bedTemp": 80.0,
            "hotendTarget": 250.0,
            "bedTarget": 80.0,
            "homedAxes": "xyz",
            "x": 64.0, "y": 30.0, "z": 8.5,
            "spoolInfo": {
                "hasActiveSpool": true,
                "activeSpoolId": 3,
                "spoolName": "PETG Clear",
                "material": "PETG",
                "colorHex": "#FFFFFF",
                "filamentName": "eSun PETG",
                "vendor": "eSun",
                "remainingWeightG": 620.0,
                "spoolInUse": true
            },
            "location": { "id": "\(officeID.uuidString)", "name": "Office", "description": "Office print station" },
            "obicoEnabled": true
        }
        """),
        decodePrinter(from: """
        {
            "id": "\(bambuP1S_ID.uuidString)",
            "name": "Bambu P1S",
            "notes": "Enclosed, good for ABS",
            "manufacturerId": "\(bambuManufID.uuidString)",
            "manufacturerName": "Bambu Lab",
            "modelName": "P1S",
            "motionType": "CoreXY",
            "backend": "Unknown",
            "backendPort": 80,
            "inMaintenance": false,
            "isEnabled": true,
            "isOnline": true,
            "state": "paused",
            "progress": 41.0,
            "jobName": "enclosure_panel_left.gcode",
            "hotendTemp": 240.0,
            "bedTemp": 100.0,
            "hotendTarget": 240.0,
            "bedTarget": 100.0,
            "spoolInfo": {
                "hasActiveSpool": true,
                "activeSpoolId": 6,
                "spoolName": "ABS White",
                "material": "ABS",
                "colorHex": "#FFFFFF",
                "filamentName": "Hatchbox ABS",
                "vendor": "Hatchbox",
                "remainingWeightG": 890.0,
                "spoolInUse": true
            },
            "location": { "id": "\(officeID.uuidString)", "name": "Office", "description": "Office print station" }
        }
        """),
        decodePrinter(from: """
        {
            "id": "\(voron24_ID.uuidString)",
            "name": "Voron 2.4",
            "notes": "Custom CoreXY build",
            "manufacturerId": "\(voronManufID.uuidString)",
            "manufacturerName": "Voron Design",
            "modelName": "Voron 2.4",
            "motionType": "CoreXY",
            "backend": "Moonraker",
            "backendPort": 7125,
            "inMaintenance": false,
            "isEnabled": true,
            "isOnline": true,
            "state": "error",
            "progress": 88.5,
            "jobName": "toolhead_mount_v2.gcode",
            "hotendTemp": 195.0,
            "bedTemp": 60.0,
            "hotendTarget": 0.0,
            "bedTarget": 0.0,
            "spoolInfo": {
                "hasActiveSpool": true,
                "activeSpoolId": 5,
                "spoolName": "PLA Red",
                "material": "PLA",
                "colorHex": "#FF0000",
                "filamentName": "Prusament PLA",
                "vendor": "Prusa Research",
                "remainingWeightG": 430.0,
                "spoolInUse": true
            },
            "location": { "id": "\(garageID.uuidString)", "name": "Garage", "description": "Garage build area" }
        }
        """),
        decodePrinter(from: """
        {
            "id": "\(ender3V3_ID.uuidString)",
            "name": "Ender 3 V3",
            "notes": "Budget workhorse",
            "manufacturerId": "\(crealManufID.uuidString)",
            "manufacturerName": "Creality",
            "modelName": "Ender 3 V3",
            "motionType": "Cartesian",
            "backend": "Moonraker",
            "backendPort": 7125,
            "inMaintenance": false,
            "isEnabled": true,
            "isOnline": false
        }
        """),
    ]

    // MARK: - Spoolman Spools

    static let spools: [SpoolmanSpool] = [
        SpoolmanSpool(id: 1, filamentId: 1, name: "PLA Black", material: "PLA", colorHex: "#000000", inUse: true,
                      filamentName: "Prusament PLA", vendor: "Prusa Research",
                      registeredAt: "2024-01-15", firstUsedAt: "2024-01-16", lastUsedAt: "2024-03-10",
                      remainingWeightG: 750.0, initialWeightG: 1000.0, usedWeightG: 250.0,
                      spoolWeightG: 200.0, remainingLengthMm: 249000, usedLengthMm: 83000,
                      location: "Workshop", lotNumber: "PR-2024-001", archived: false, price: 24.99,
                      comment: nil, hasNfcTag: true, usedPercent: 25.0, remainingPercent: 75.0),
        SpoolmanSpool(id: 2, filamentId: 1, name: "PLA White", material: "PLA", colorHex: "#FFFFFF", inUse: false,
                      filamentName: "Prusament PLA", vendor: "Prusa Research",
                      registeredAt: "2024-02-01", firstUsedAt: "2024-02-05", lastUsedAt: "2024-03-08",
                      remainingWeightG: 620.0, initialWeightG: 1000.0, usedWeightG: 380.0,
                      spoolWeightG: 200.0, remainingLengthMm: 206000, usedLengthMm: 126000,
                      location: "Workshop", lotNumber: "PR-2024-015", archived: false, price: 24.99,
                      comment: nil, hasNfcTag: true, usedPercent: 38.0, remainingPercent: 62.0),
        SpoolmanSpool(id: 3, filamentId: 2, name: "PETG Clear", material: "PETG", colorHex: "#FFFFFF", inUse: true,
                      filamentName: "eSun PETG", vendor: "eSun",
                      registeredAt: "2024-01-20", firstUsedAt: "2024-01-22", lastUsedAt: "2024-03-10",
                      remainingWeightG: 620.0, initialWeightG: 1000.0, usedWeightG: 380.0,
                      spoolWeightG: 180.0, remainingLengthMm: 190000, usedLengthMm: 117000,
                      location: "Office", lotNumber: nil, archived: false, price: 19.99,
                      comment: nil, hasNfcTag: false, usedPercent: 38.0, remainingPercent: 62.0),
        SpoolmanSpool(id: 4, filamentId: 2, name: "PETG Blue", material: "PETG", colorHex: "#0066CC", inUse: false,
                      filamentName: "eSun PETG", vendor: "eSun",
                      registeredAt: "2024-02-10", firstUsedAt: "2024-02-12", lastUsedAt: "2024-03-05",
                      remainingWeightG: 480.0, initialWeightG: 1000.0, usedWeightG: 520.0,
                      spoolWeightG: 180.0, remainingLengthMm: 148000, usedLengthMm: 160000,
                      location: "Office", lotNumber: nil, archived: false, price: 19.99,
                      comment: nil, hasNfcTag: false, usedPercent: 52.0, remainingPercent: 48.0),
        SpoolmanSpool(id: 5, filamentId: 1, name: "PLA Red", material: "PLA", colorHex: "#FF0000", inUse: true,
                      filamentName: "Prusament PLA", vendor: "Prusa Research",
                      registeredAt: "2024-01-10", firstUsedAt: "2024-01-11", lastUsedAt: "2024-03-10",
                      remainingWeightG: 430.0, initialWeightG: 1000.0, usedWeightG: 570.0,
                      spoolWeightG: 200.0, remainingLengthMm: 143000, usedLengthMm: 190000,
                      location: "Garage", lotNumber: "PR-2024-008", archived: false, price: 24.99,
                      comment: nil, hasNfcTag: true, usedPercent: 57.0, remainingPercent: 43.0),
        SpoolmanSpool(id: 6, filamentId: 3, name: "ABS White", material: "ABS", colorHex: "#FFFFFF", inUse: true,
                      filamentName: "Hatchbox ABS", vendor: "Hatchbox",
                      registeredAt: "2024-02-15", firstUsedAt: "2024-02-16", lastUsedAt: "2024-03-10",
                      remainingWeightG: 890.0, initialWeightG: 1000.0, usedWeightG: 110.0,
                      spoolWeightG: 220.0, remainingLengthMm: 268000, usedLengthMm: 33000,
                      location: "Office", lotNumber: "HB-2024-002", archived: false, price: 22.99,
                      comment: "Good for enclosures", hasNfcTag: false, usedPercent: 11.0, remainingPercent: 89.0),
        SpoolmanSpool(id: 7, filamentId: 4, name: "TPU 95A Black", material: "TPU", colorHex: "#1A1A1A", inUse: false,
                      filamentName: "NinjaFlex TPU", vendor: "NinjaTek",
                      registeredAt: "2024-03-01", firstUsedAt: "2024-03-02", lastUsedAt: "2024-03-07",
                      remainingWeightG: 680.0, initialWeightG: 750.0, usedWeightG: 70.0,
                      spoolWeightG: 150.0, remainingLengthMm: 180000, usedLengthMm: 18500,
                      location: "Workshop", lotNumber: nil, archived: false, price: 45.99,
                      comment: "Flexible, print slow", hasNfcTag: false, usedPercent: 9.3, remainingPercent: 90.7),
        SpoolmanSpool(id: 8, filamentId: 5, name: "ASA Orange", material: "ASA", colorHex: "#FF6600", inUse: false,
                      filamentName: "PolyLite ASA", vendor: "Polymaker",
                      registeredAt: "2024-02-20", firstUsedAt: "2024-02-22", lastUsedAt: "2024-03-04",
                      remainingWeightG: 550.0, initialWeightG: 1000.0, usedWeightG: 450.0,
                      spoolWeightG: 190.0, remainingLengthMm: 168000, usedLengthMm: 137000,
                      location: "Garage", lotNumber: "PM-ASA-012", archived: false, price: 27.99,
                      comment: "UV resistant", hasNfcTag: false, usedPercent: 45.0, remainingPercent: 55.0),
    ]

    // MARK: - Notifications

    static let notifications: [AppNotification] = {
        let now = Date()
        return [
            AppNotification(id: "notif-001", userId: demoUserID, jobId: job1ID,
                            type: .jobStarted, subject: "Print Started",
                            body: "phone_case_v3.gcode started on Prusa MK4 #1",
                            isRead: true, createdAt: now.addingTimeInterval(-3600 * 8), readAt: now.addingTimeInterval(-3600 * 7), expiresAt: nil),
            AppNotification(id: "notif-002", userId: demoUserID, jobId: job7ID,
                            type: .jobCompleted, subject: "Print Completed",
                            body: "benchy_calibration.gcode completed successfully on Prusa MK4 #2",
                            isRead: true, createdAt: now.addingTimeInterval(-3600 * 6), readAt: now.addingTimeInterval(-3600 * 5), expiresAt: nil),
            AppNotification(id: "notif-003", userId: demoUserID, jobId: job9ID,
                            type: .jobFailed, subject: "Print Failed",
                            body: "vase_mode_spiral.gcode failed on Voron 2.4: Layer adhesion failure",
                            isRead: true, createdAt: now.addingTimeInterval(-3600 * 5), readAt: now.addingTimeInterval(-3600 * 4), expiresAt: nil),
            AppNotification(id: "notif-004", userId: demoUserID, jobId: nil,
                            type: .queueAlert, subject: "Queue Alert",
                            body: "3 jobs queued with no available printers — consider enabling Ender 3 V3",
                            isRead: true, createdAt: now.addingTimeInterval(-3600 * 4), readAt: now.addingTimeInterval(-3600 * 3), expiresAt: nil),
            AppNotification(id: "notif-005", userId: demoUserID, jobId: job8ID,
                            type: .jobCompleted, subject: "Print Completed",
                            body: "bracket_mount_x2.gcode completed on Bambu X1C",
                            isRead: true, createdAt: now.addingTimeInterval(-3600 * 3), readAt: now.addingTimeInterval(-3600 * 2), expiresAt: nil),
            AppNotification(id: "notif-006", userId: demoUserID, jobId: nil,
                            type: .systemAlert, subject: "Maintenance Due",
                            body: "Voron 2.4 nozzle replacement is overdue by 120 print hours",
                            isRead: true, createdAt: now.addingTimeInterval(-3600 * 2), readAt: now.addingTimeInterval(-3600), expiresAt: nil),
            AppNotification(id: "notif-007", userId: demoUserID, jobId: job3ID,
                            type: .jobStarted, subject: "Print Started",
                            body: "gear_housing_x4.gcode started on Bambu X1C",
                            isRead: false, createdAt: now.addingTimeInterval(-3600), readAt: nil, expiresAt: nil),
            AppNotification(id: "notif-008", userId: demoUserID, jobId: nil,
                            type: .bedClearRequired, subject: "Bed Clear Required",
                            body: "Prusa MK4 #2 needs bed cleared before next job can start",
                            isRead: false, createdAt: now.addingTimeInterval(-1800), readAt: nil, expiresAt: nil),
            AppNotification(id: "notif-009", userId: demoUserID, jobId: job4ID,
                            type: .jobPaused, subject: "Print Paused",
                            body: "enclosure_panel_left.gcode paused on Bambu P1S — filament runout detected",
                            isRead: false, createdAt: now.addingTimeInterval(-900), readAt: nil, expiresAt: nil),
            AppNotification(id: "notif-010", userId: demoUserID, jobId: nil,
                            type: .systemAlert, subject: "Error Detected",
                            body: "Voron 2.4 reported thermal runaway — printer halted for safety",
                            isRead: false, createdAt: now.addingTimeInterval(-300), readAt: nil, expiresAt: nil),
        ]
    }()

    // MARK: - Statistics

    static let statisticsSummary: StatisticsSummary = {
        let json = """
        {
            "totalJobs": 847,
            "completedJobs": 772,
            "failedJobs": 41,
            "cancelledJobs": 34,
            "successRate": 91.2,
            "totalCost": 2847.50,
            "totalFilamentGrams": 14230.0,
            "totalPrintHours": 2340.0
        }
        """
        let decoder = JSONDecoder()
        // swiftlint:disable:next force_try
        return try! decoder.decode(StatisticsSummary.self, from: Data(json.utf8))
    }()
}
