import Foundation

/// Snapshot of the printer fields that the controls VM cares about when
/// deciding whether a live update should clear an in-flight command
/// (`PrinterControlsViewModel.handlePrinterUpdate`).
///
/// Two signals compare equal iff none of these fields moved: only then
/// can we safely skip forwarding the snapshot to the VM. Any drift in
/// state, online status, homing status, position or temperatures is
/// treated as evidence that the last jog/preheat/home command produced
/// an effect and the pending state should be released.
///
/// The signal deliberately excludes churn-heavy metadata (camera URLs,
/// spool info, progress %, thumbnails) so `.onChange` fires exactly
/// when it is meaningful for the controls surface.
struct PrinterControlsUpdateSignal: Hashable {
    let id: UUID
    let configurationRevision: Int64
    let rowVersion: String?
    let backend: PrinterBackend
    let isOnline: Bool
    let state: String?
    let x: Double?
    let y: Double?
    let z: Double?
    let hotendTemp: Double?
    let bedTemp: Double?
    let hotendTarget: Double?
    let bedTarget: Double?
    let fanSpeedPercent: Double?
    let liveZOffsetMm: Double?
    let speedFactorPercent: Double?
    let flowFactorPercent: Double?
    let sensors: [PrinterSensor]
    let homedAxes: String?

    init(printer: Printer) {
        self.id = printer.id
        self.configurationRevision = printer.configurationRevision
        self.rowVersion = printer.rowVersion
        self.backend = printer.backend
        self.isOnline = printer.isOnline
        self.state = printer.state
        self.x = printer.x
        self.y = printer.y
        self.z = printer.z
        self.hotendTemp = printer.hotendTemp
        self.bedTemp = printer.bedTemp
        self.hotendTarget = printer.hotendTarget
        self.bedTarget = printer.bedTarget
        self.fanSpeedPercent = printer.fanSpeedPercent
        self.liveZOffsetMm = printer.liveZOffsetMm
        self.speedFactorPercent = printer.speedFactorPercent
        self.flowFactorPercent = printer.flowFactorPercent
        self.sensors = printer.sensors
        self.homedAxes = printer.homedAxes
    }
}
