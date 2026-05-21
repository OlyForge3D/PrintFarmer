import Foundation

// MARK: - Demo AutoDispatch Service

final class DemoAutoDispatchService: AutoDispatchServiceProtocol, @unchecked Sendable {

    func getAllStatus() async throws -> AutoDispatchGlobalStatus {
        let printers = [
            AutoDispatchStatus(printerId: DemoData.prusaMK4_1_ID, printerName: "Prusa MK4 #1",
                               enabled: true, isReady: true, currentJobName: nil, queueDepth: 1,
                               readyGateChecks: [], lastActivity: nil,
                               state: AutoDispatchState.ready.rawValue,
                               bedPreConfirmed: false, attentionMessage: nil),
            AutoDispatchStatus(printerId: DemoData.prusaMK4_2_ID, printerName: "Prusa MK4 #2",
                               enabled: true, isReady: true, currentJobName: nil, queueDepth: 0,
                               readyGateChecks: [], lastActivity: nil,
                               state: AutoDispatchState.ready.rawValue,
                               bedPreConfirmed: false, attentionMessage: nil),
            AutoDispatchStatus(printerId: DemoData.bambuX1C_ID, printerName: "Bambu X1C",
                               enabled: true, isReady: false, currentJobName: nil, queueDepth: 1,
                               readyGateChecks: [], lastActivity: nil,
                               state: AutoDispatchState.pendingReady.rawValue,
                               bedPreConfirmed: false, attentionMessage: nil),
            AutoDispatchStatus(printerId: DemoData.bambuP1S_ID, printerName: "Bambu P1S",
                               enabled: false, isReady: false, currentJobName: nil, queueDepth: 1,
                               readyGateChecks: [], lastActivity: nil,
                               state: AutoDispatchState.none.rawValue,
                               bedPreConfirmed: false, attentionMessage: nil),
            AutoDispatchStatus(printerId: DemoData.voron24_ID, printerName: "Voron 2.4",
                               enabled: true, isReady: false, currentJobName: nil, queueDepth: 0,
                               readyGateChecks: [], lastActivity: nil,
                               state: AutoDispatchState.none.rawValue,
                               bedPreConfirmed: false, attentionMessage: nil),
            AutoDispatchStatus(printerId: DemoData.ender3V3_ID, printerName: "Ender 3 V3",
                               enabled: false, isReady: false, currentJobName: nil, queueDepth: 0,
                               readyGateChecks: [], lastActivity: nil,
                               state: AutoDispatchState.none.rawValue,
                               bedPreConfirmed: false, attentionMessage: nil),
        ]
        return AutoDispatchGlobalStatus(globalEnabled: true, printers: printers)
    }

    func getStatus(printerId: UUID) async throws -> AutoDispatchStatus {
        let all = try await getAllStatus()
        guard let status = all.printers.first(where: { $0.printerId == printerId }) else {
            return AutoDispatchStatus(printerId: printerId, printerName: "Unknown",
                                      enabled: false, isReady: false, currentJobName: nil, queueDepth: 0,
                                      readyGateChecks: [], lastActivity: nil,
                                      state: AutoDispatchState.none.rawValue,
                                      bedPreConfirmed: false, attentionMessage: nil)
        }
        return status
    }

    func markReady(printerId: UUID) async throws -> AutoDispatchReadyResult {
        let status = AutoDispatchStatus(printerId: printerId, printerName: "Demo Printer",
                                        enabled: true, isReady: true, currentJobName: nil, queueDepth: 1,
                                        readyGateChecks: [], lastActivity: nil,
                                        state: AutoDispatchState.ready.rawValue,
                                        bedPreConfirmed: false, attentionMessage: nil)
        let nextJob = AutoDispatchNextJob(id: DemoData.job5ID, name: "raspberry_pi_case.gcode",
                                          estimatedFilamentUsageG: 35.0, requiredMaterialType: "PLA",
                                          estimatedPrintTime: 8100)
        let filamentCheck = FilamentCheckResult(sufficient: true, remainingWeightG: 750.0,
                                                requiredWeightG: 35.0, loadedMaterial: "PLA",
                                                requiredMaterial: "PLA", materialMismatch: false,
                                                message: "Sufficient filament available")
        return AutoDispatchReadyResult(status: status, nextJob: nextJob, filamentCheck: filamentCheck)
    }

    func skip(printerId: UUID) async throws -> AutoDispatchStatus {
        AutoDispatchStatus(printerId: printerId, printerName: "Demo Printer",
                           enabled: true, isReady: false, currentJobName: nil, queueDepth: 0,
                           readyGateChecks: [], lastActivity: nil,
                           state: AutoDispatchState.none.rawValue,
                           bedPreConfirmed: false, attentionMessage: nil)
    }

    func cancel(printerId: UUID) async throws -> AutoDispatchStatus {
        AutoDispatchStatus(printerId: printerId, printerName: "Demo Printer",
                           enabled: true, isReady: false, currentJobName: nil, queueDepth: 0,
                           readyGateChecks: [], lastActivity: nil,
                           state: AutoDispatchState.none.rawValue,
                           bedPreConfirmed: false, attentionMessage: nil)
    }

    func preClear(printerId: UUID) async throws -> AutoDispatchStatus {
        AutoDispatchStatus(printerId: printerId, printerName: "Demo Printer",
                           enabled: true, isReady: false, currentJobName: nil, queueDepth: 0,
                           readyGateChecks: [], lastActivity: nil,
                           state: AutoDispatchState.none.rawValue,
                           bedPreConfirmed: true, attentionMessage: nil)
    }

    func setEnabled(printerId: UUID, request: SetAutoDispatchEnabledRequest) async throws -> AutoDispatchStatus {
        AutoDispatchStatus(printerId: printerId, printerName: "Demo Printer",
                           enabled: request.enabled, isReady: false, currentJobName: nil, queueDepth: 0,
                           readyGateChecks: [], lastActivity: nil,
                           state: AutoDispatchState.none.rawValue,
                           bedPreConfirmed: false, attentionMessage: nil)
    }
}
