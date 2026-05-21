import Foundation

// MARK: - Demo Printer Service

final class DemoPrinterService: PrinterServiceProtocol, @unchecked Sendable {
    private let printers = DemoData.printers

    func list(includeDisabled: Bool) async throws -> [Printer] {
        printers
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
            spoolInfo: p.spoolInfo, mmuStatus: nil)
    }

    func getSnapshot(id: UUID) async throws -> Data {
        Data()
    }

    func getCurrentJob(id: UUID) async throws -> PrintJobStatusInfo? {
        guard let p = printers.first(where: { $0.id == id }), p.state == "printing" || p.state == "paused" else {
            return nil
        }
        return PrintJobStatusInfo(
            state: p.state, progress: p.progress,
            jobName: p.jobName, thumbnailUrl: p.thumbnailUrl, error: nil)
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
}
