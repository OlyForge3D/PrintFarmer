import Foundation

// MARK: - Demo SignalR Service

/// Simulates real-time printer updates without a WebSocket connection.
/// Uses Task.sleep timers to emit progress updates every few seconds.
final class DemoSignalRService: SignalRServiceProtocol, @unchecked Sendable {
    private(set) var connectionState: SignalRConnectionState = .disconnected
    private var printerHandler: (@Sendable (PrinterStatusUpdate) -> Void)?
    private var jobQueueHandler: (@Sendable (JobQueueUpdate) -> Void)?
    private var simulationTask: Task<Void, Never>?

    func connect() async throws {
        connectionState = .connected
        startSimulation()
    }

    func disconnect() async {
        simulationTask?.cancel()
        simulationTask = nil
        connectionState = .disconnected
    }

    func onPrinterUpdated(_ handler: @escaping @Sendable (PrinterStatusUpdate) -> Void) {
        printerHandler = handler
    }

    func onJobQueueUpdated(_ handler: @escaping @Sendable (JobQueueUpdate) -> Void) {
        jobQueueHandler = handler
    }

    // MARK: - Simulation

    private func startSimulation() {
        simulationTask?.cancel()

        // Capture handlers and initial state before entering the Task
        let printerHandler = self.printerHandler
        let jobQueueHandler = self.jobQueueHandler

        simulationTask = Task.detached { [weak self] in
            var printerProgress: [UUID: Double] = [
                DemoData.prusaMK4_1_ID: 67.3,
                DemoData.bambuX1C_ID: 23.1,
            ]
            var printerStates: [UUID: String] = [
                DemoData.prusaMK4_1_ID: "printing",
                DemoData.bambuX1C_ID: "printing",
            ]

            let printerConfigs: [UUID: (jobName: String, hotend: Double, bed: Double)] = [
                DemoData.prusaMK4_1_ID: ("phone_case_v3.gcode", 215.0, 60.0),
                DemoData.bambuX1C_ID: ("gear_housing_x4.gcode", 250.0, 80.0),
            ]

            var tick = 0
            while !Task.isCancelled {
                try? await Task.sleep(nanoseconds: 5_000_000_000)
                guard self != nil, !Task.isCancelled else { return }
                tick += 1

                for id in [DemoData.prusaMK4_1_ID, DemoData.bambuX1C_ID] {
                    guard printerStates[id] == "printing", var progress = printerProgress[id] else { continue }

                    progress = min(progress + Double.random(in: 0.3...0.7), 100.0)
                    printerProgress[id] = progress

                    if progress >= 100.0 {
                        printerStates[id] = "idle"
                        printerProgress[id] = nil
                    }

                    let config = printerConfigs[id]!
                    let state = printerStates[id] ?? "idle"
                    let tempVariation = Double.random(in: -0.5...0.5)
                    let isPrinting = state == "printing"

                    let update = PrinterStatusUpdate(
                        id: id, isOnline: true, state: state,
                        progress: printerProgress[id].map { $0 / 100.0 },
                        jobName: isPrinting ? config.jobName : nil,
                        fileName: nil, thumbnailUrl: nil, cameraStreamUrl: nil,
                        x: nil, y: nil, z: nil,
                        hotendTemp: isPrinting ? config.hotend + tempVariation : 25.0,
                        bedTemp: isPrinting ? config.bed + tempVariation * 0.3 : 23.0,
                        hotendTarget: isPrinting ? config.hotend : 0.0,
                        bedTarget: isPrinting ? config.bed : 0.0,
                        homedAxes: "xyz", spoolInfo: nil, mmuStatus: nil)
                    printerHandler?(update)
                }

                // Every 6th tick (~30s), emit a job queue update
                if tick % 6 == 0 {
                    let queueUpdate = JobQueueUpdate(
                        printerId: DemoData.prusaMK4_1_ID,
                        jobs: [
                            JobQueueUpdateEntry(
                                id: DemoData.job1ID, name: "phone_case_v3.gcode",
                                status: .printing, priority: 1, queuedAt: Date().addingTimeInterval(-86400),
                                actualStartTime: Date().addingTimeInterval(-7200), actualEndTime: nil),
                        ])
                    jobQueueHandler?(queueUpdate)
                }
            }
        }
    }
}
