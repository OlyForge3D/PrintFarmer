import Foundation

// MARK: - Demo Printer Service

final class DemoPrinterService: PrinterServiceProtocol, @unchecked Sendable {
    private let printers: [Printer]
    /// When set, `list(...)` throws it. Used by the cold-offline UI-test mode
    /// (#817) to force a canonical load failure so the cached read-only shell
    /// persists. Non-Farm demo behavior is unaffected.
    private let listError: Error?
    private let snapshots: [UUID: Data]

    /// Default demo constructor (all callers except UI-test bootstrap):
    /// exposes exactly the demo fleet from `DemoData.printers`.
    init() {
        self.printers = DemoData.printers
        self.listError = nil
        self.snapshots = [:]
    }

    /// UI-test bootstrap constructor (F4-M / #778 cycle-3): appends
    /// scenario-only printers to the base demo fleet without mutating
    /// `DemoData`. Used by `--uitesting-filament-coverage-scenario`
    /// to seed a duplicate display-name pair so XCUI can prove stable-
    /// id scoping. Non-Farm demo behavior is unaffected.
    init(
        additionalPrinters: [Printer],
        snapshots: [UUID: Data] = [:]
    ) {
        self.printers = DemoData.printers + additionalPrinters
        self.listError = nil
        self.snapshots = snapshots
    }

    /// UI-test offline constructor (#817): `list(...)` throws `offlineError`
    /// so `DashboardViewModel.loadDashboard()` fails and the cold-offline
    /// cached shell stays read-only/stale.
    init(offlineError: Error) {
        self.printers = DemoData.printers
        self.listError = offlineError
        self.snapshots = [:]
    }

    func list(includeDisabled: Bool) async throws -> [Printer] {
        if let listError { throw listError }
        return printers
    }

    func get(id: UUID) async throws -> Printer {
        guard let printer = printers.first(where: { $0.id == id }) else {
            throw ServiceError.notImplemented("Printer not found in demo data")
        }
        return printer
    }

    func getStatus(id: UUID) async throws -> PrinterStatusDetail {
        guard let p = printers.first(where: { $0.id == id }) else {
            throw ServiceError.notImplemented("Printer not found")
        }
        return PrinterStatusDetail(
            id: p.id, isOnline: p.isOnline, state: p.state,
            progress: p.progress, jobName: p.jobName,
            thumbnailUrl: p.thumbnailUrl,
            cameraStreamUrl: p.cameraStreamUrl,
            cameraSnapshotUrl: p.cameraSnapshotUrl,
            x: p.x, y: p.y, z: p.z,
            hotendTemp: p.hotendTemp, bedTemp: p.bedTemp,
            hotendTarget: p.hotendTarget, bedTarget: p.bedTarget,
            homedAxes: p.homedAxes,
            spoolInfo: p.spoolInfo, mmuStatus: nil)
    }

    func listCameraUrls() async throws -> [PrinterCameraUrls] {
        printers.map { printer in
            PrinterCameraUrls(
                id: printer.id,
                name: printer.name,
                cameraStreamUrl: printer.cameraStreamUrl,
                cameraSnapshotUrl: printer.cameraSnapshotUrl,
                cameraAccessMode: printer.cameraAccessMode,
                cameraStreamFormat: printer.cameraStreamFormat,
                cameraSnapshotStrategy: printer.cameraSnapshotStrategy
            )
        }
    }

    func getCameraUrl(id: UUID) async throws -> PrinterCameraUrl {
        guard let p = printers.first(where: { $0.id == id }) else {
            throw ServiceError.notImplemented("Printer not found")
        }
        return PrinterCameraUrl(
            streamUrl: p.cameraStreamUrl,
            snapshotUrl: p.cameraSnapshotUrl,
            accessMode: p.cameraAccessMode,
            streamFormat: p.cameraStreamFormat,
            snapshotStrategy: p.cameraSnapshotStrategy
        )
    }

    func getSnapshot(id: UUID) async throws -> Data {
        snapshots[id] ?? Data()
    }

    func getCurrentJob(id: UUID) async throws -> PrintJobStatusInfo? {
        guard let p = printers.first(where: { $0.id == id }), p.state == "printing" || p.state == "paused" else {
            return nil
        }
        return PrintJobStatusInfo(
            state: p.state, progress: p.progress,
            jobName: p.jobName, thumbnailUrl: p.thumbnailUrl, error: nil)
    }

    func getHistory(id: UUID, limit: Int? = nil) async throws -> PrinterHistoryList {
        let now = Date().timeIntervalSince1970
        let jobs = [
            PrinterHistoryJob(
                jobId: UUID().uuidString, status: "completed", filename: "bracket_v3.gcode",
                startTime: now - 7200, endTime: now - 3600,
                printDuration: 3300, totalDuration: 3600, filamentUsed: 12.4),
            PrinterHistoryJob(
                jobId: UUID().uuidString, status: "completed", filename: "gear_mount.gcode",
                startTime: now - 18000, endTime: now - 12600,
                printDuration: 5100, totalDuration: 5400, filamentUsed: 24.1),
            PrinterHistoryJob(
                jobId: UUID().uuidString, status: "cancelled", filename: "prototype.gcode",
                startTime: now - 90000, endTime: now - 88200,
                printDuration: 1500, totalDuration: 1800, filamentUsed: 4.2),
        ]
        let limited = limit.map { Array(jobs.prefix($0)) } ?? jobs
        return PrinterHistoryList(count: limited.count, jobs: limited)
    }

    func pause(id: UUID) async throws -> CommandResult {
        CommandResult(success: true, message: "Printer paused (demo)")
    }

    func resume(id: UUID) async throws -> CommandResult {
        CommandResult(success: true, message: "Printer resumed (demo)")
    }

    func cancel(id: UUID) async throws -> CommandResult {
        CommandResult(success: true, message: "Print cancelled (demo)")
    }

    func stop(id: UUID) async throws -> CommandResult {
        CommandResult(success: true, message: "Printer stopped (demo)")
    }

    func emergencyStop(id: UUID) async throws -> CommandResult {
        CommandResult(success: true, message: "Emergency stop executed (demo)")
    }

    func setMaintenanceMode(id: UUID, inMaintenance: Bool) async throws -> Printer {
        guard let printer = printers.first(where: { $0.id == id }) else {
            throw ServiceError.notImplemented("Printer not found")
        }
        return printer
    }

    func getQueueOverview(model: String?, nozzle: Double?, material: String?) async throws -> [QueueOverview] {
        printers.map { p in
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

    func setActiveSpool(printerId: UUID, spoolId: Int?) async throws -> CommandResult {
        CommandResult(success: true, message: "Spool set (demo)")
    }

    func bindToolheadSpool(printerId: UUID, toolheadIndex: Int, request: ToolheadSpoolBindRequest, idempotencyKey: String) async throws -> CommandResult {
        CommandResult(success: true, message: "Toolhead spool bound (demo)")
    }

    func listAvailableSpools(printerId: UUID) async throws -> [SpoolmanSpool] {
        DemoData.spools
    }

    func loadFilament(printerId: UUID) async throws -> CommandResult {
        CommandResult(success: true, message: "Filament loaded (demo)")
    }

    func unloadFilament(printerId: UUID) async throws -> CommandResult {
        CommandResult(success: true, message: "Filament unloaded (demo)")
    }

    func changeFilament(printerId: UUID) async throws -> CommandResult {
        CommandResult(success: true, message: "Filament changed (demo)")
    }

    func getBackendCapabilities(printerId: UUID) async throws -> PrinterBackendCapabilities {
        let backend = printers.first(where: { $0.id == printerId })?.backend ?? .moonraker
        return PrinterBackendCapabilities.fallback(for: backend)
    }

    func setTemperatures(printerId: UUID, hotend: Double?, bed: Double?) async throws {
        // Demo no-op
    }

    func home(printerId: UUID, axes: [String]) async throws {
        // Demo no-op
    }

    func homeXY(printerId: UUID) async throws {
        // Demo no-op
    }

    func homeZ(printerId: UUID) async throws {
        // Demo no-op
    }

    func move(printerId: UUID, axis: String, distanceMm: Double, feedrateMmMin: Int) async throws {
        // Demo no-op
    }

    // MARK: - Details + fallback groups (issue #711, F6 demo stubs)

    func getDetails(id: UUID) async throws -> PrinterDetails {
        guard let p = printers.first(where: { $0.id == id }) else {
            throw ServiceError.notImplemented("Printer not found in demo data")
        }
        return PrinterDetails(
            id: p.id,
            name: p.name,
            backend: p.backend,
            hasMmu: nil,
            manufacturerName: p.manufacturerName,
            modelName: p.modelName,
            toolheads: [],
            fallbackGroups: [],
            supportsPerToolAttribution: false
        )
    }

    func listFallbackGroups(printerId: UUID) async throws -> [FilamentFallbackGroup] { [] }

    func getFallbackGroup(printerId: UUID, groupId: UUID) async throws -> FilamentFallbackGroup {
        throw ServiceError.notImplemented("Fallback groups not available in demo mode")
    }

    func createFallbackGroup(
        printerId: UUID,
        _ request: CreateFilamentFallbackGroupRequest
    ) async throws -> FilamentFallbackGroup {
        throw ServiceError.notImplemented("Fallback groups not available in demo mode")
    }

    func updateFallbackGroup(
        printerId: UUID,
        groupId: UUID,
        _ request: UpdateFilamentFallbackGroupRequest
    ) async throws -> FilamentFallbackGroup {
        throw ServiceError.notImplemented("Fallback groups not available in demo mode")
    }

    func deleteFallbackGroup(printerId: UUID, groupId: UUID) async throws {
        throw ServiceError.notImplemented("Fallback groups not available in demo mode")
    }

    func getAvailableFallback(
        printerId: UUID,
        sourceToolheadId: UUID,
        material: String
    ) async throws -> AvailableFallbackMember? {
        nil
    }

}
