import Foundation
import SwiftUI
import os

@MainActor @Observable
final class PrinterDetailViewModel {
    var printer: Printer?
    var statusDetail: PrinterStatusDetail?
    var currentJob: PrintJobStatusInfo?
    var snapshotData: Data?
    var isLoadingSnapshot = false
    var showLivestream = false
    var isLoading = false
    var errorMessage: String?
    var isPerformingAction = false
    var showConfirmation = false
    var pendingAction: DestructiveAction?
    var actionError: String?
    var isViewActive = true
    var activeAlerts: [PredictiveAlert] = []
    var failureDetectionStatus: FailureDetectionPrinterStatus?

    private let logger = Logger(subsystem: "com.printfarmer.ios", category: "PrinterDetail")

    enum DestructiveAction: Identifiable {
        case cancelPrint
        case emergencyStop

        var id: String {
            switch self {
            case .cancelPrint: "cancel"
            case .emergencyStop: "emergencyStop"
            }
        }

        var title: String {
            switch self {
            case .cancelPrint: "Cancel Print"
            case .emergencyStop: "Emergency Stop"
            }
        }

        var message: String {
            switch self {
            case .cancelPrint: "This will cancel the current print job. This action cannot be undone."
            case .emergencyStop: "This will immediately stop all printer operations. Use only in emergencies."
            }
        }
    }

    var showSpoolPicker = false
    private var lastSetSpoolInfo: PrinterSpoolInfo?
    var nfcScanError: String?
    var nfcScannedData: ScannedSpoolData?
    var showScannedDataSheet = false
    var showNFCReadyConfirmation = false

    private var nfcScanner: (any SpoolScannerProtocol)?
    private var autoDispatchService: (any AutoDispatchServiceProtocol)?
    private var signalRService: (any SignalRServiceProtocol)?
    private var predictiveService: (any PredictiveServiceProtocol)?
    private var failureDetectionService: (any FailureDetectionServiceProtocol)?

    let printerId: UUID
    private var printerService: (any PrinterServiceProtocol)?
    
    var cameraRotation: Int = 0

    init(printerId: UUID) {
        self.printerId = printerId
        self.cameraRotation = UserDefaults.standard.integer(forKey: "cameraRotation-\(printerId.uuidString)")
    }

    func configure(printerService: any PrinterServiceProtocol) {
        self.printerService = printerService
    }

    func configureNFCScanner(_ scanner: any SpoolScannerProtocol) {
        self.nfcScanner = scanner
    }

    func configureAutoDispatch(_ service: any AutoDispatchServiceProtocol) {
        self.autoDispatchService = service
    }

    func configurePredictive(_ service: any PredictiveServiceProtocol) {
        self.predictiveService = service
    }

    func configureFailureDetection(_ service: any FailureDetectionServiceProtocol) {
        self.failureDetectionService = service
    }

    func configureSignalR(_ service: any SignalRServiceProtocol) {
        self.signalRService = service
        service.onPrinterUpdated { [weak self] update in
            guard update.id == self?.printerId else { return }
            Task { @MainActor [weak self] in
                guard let self, self.isViewActive else { return }
                self.applyLiveUpdate(update)
            }
        }
    }

    private func applyLiveUpdate(_ update: PrinterStatusUpdate) {
        guard isViewActive else { return }
        if var p = printer {
            p.isOnline = update.isOnline
            if let s = update.state { p.state = s }
            // Backend sends progress as 0-100; normalize to 0-1.0 for SwiftUI
            if let prog = update.progress { p.progress = prog / 100.0 }
            if let name = update.jobName { p.jobName = name }
            if let fn = update.fileName { p.fileName = fn }
            if let thumb = update.thumbnailUrl { p.thumbnailUrl = thumb }
            if let cam = update.cameraStreamUrl { p.cameraStreamUrl = cam }
            if let hotend = update.hotendTemp { p.hotendTemp = hotend }
            if let bed = update.bedTemp { p.bedTemp = bed }
            if let ht = update.hotendTarget { p.hotendTarget = ht }
            if let bt = update.bedTarget { p.bedTarget = bt }
            if let x = update.x { p.x = x }
            if let y = update.y { p.y = y }
            if let z = update.z { p.z = z }
            if let spool = update.spoolInfo { p.spoolInfo = spool }
            printer = p

            // Auto-toggle livestream based on printer state
            if let state = p.state?.lowercased() {
                let isPrinterActive = ["printing", "starting", "paused"].contains(state)
                if isPrinterActive && p.cameraStreamUrl != nil && !showLivestream {
                    showLivestream = true
                } else if !isPrinterActive && showLivestream {
                    showLivestream = false
                }
            }
        }

        statusDetail = PrinterStatusDetail(
            id: update.id,
            isOnline: update.isOnline,
            state: update.state ?? statusDetail?.state,
            progress: update.progress.map { $0 / 100.0 } ?? statusDetail?.progress,
            jobName: update.jobName ?? statusDetail?.jobName,
            thumbnailUrl: update.thumbnailUrl ?? statusDetail?.thumbnailUrl,
            cameraStreamUrl: update.cameraStreamUrl ?? statusDetail?.cameraStreamUrl,
            cameraSnapshotUrl: statusDetail?.cameraSnapshotUrl,
            x: update.x ?? statusDetail?.x,
            y: update.y ?? statusDetail?.y,
            z: update.z ?? statusDetail?.z,
            hotendTemp: update.hotendTemp ?? statusDetail?.hotendTemp,
            bedTemp: update.bedTemp ?? statusDetail?.bedTemp,
            hotendTarget: update.hotendTarget ?? statusDetail?.hotendTarget,
            bedTarget: update.bedTarget ?? statusDetail?.bedTarget,
            spoolInfo: update.spoolInfo ?? statusDetail?.spoolInfo,
            mmuStatus: update.mmuStatus ?? statusDetail?.mmuStatus
        )
    }

    // MARK: - NFC Printer Tag Writing

    #if canImport(UIKit)
    func writeNFCPrinterTag() {
        guard isViewActive else { return }
        guard let printer else { return }
        guard let nfcService = nfcScanner as? NFCService else {
            nfcScanError = "NFC writing is not available on this device."
            return
        }
        Task {
            do {
                try await nfcService.writePrinterTag(printerId: printer.id, printerName: printer.name)
            } catch SpoolScanError.cancelled {
                // User cancelled — do nothing
            } catch {
                guard self.isViewActive else { return }
                self.nfcScanError = error.localizedDescription
            }
        }
    }
    #endif

    // MARK: - Mark Ready (NFC Deep Link)

    func markPrinterReady() async {
        guard isViewActive else { return }
        guard let autoDispatchService else {
            actionError = "Auto-dispatch service not available."
            return
        }
        isPerformingAction = true
        actionError = nil
        do {
            _ = try await autoDispatchService.markReady(printerId: printerId)
            guard isViewActive else { return }
            await loadPrinter()
        } catch {
            guard isViewActive else { return }
            actionError = error.localizedDescription
        }
        guard isViewActive else { return }
        isPerformingAction = false
    }

    // MARK: - Filament / Spool

    func loadFilament() {
        showSpoolPicker = true
    }

    // MARK: - NFC Scan to Load

    func handleNFCScanToLoad() {
        guard isViewActive else { return }
        guard let nfcScanner, nfcScanner.isAvailable else {
            nfcScanError = "NFC scanning is not available on this device."
            return
        }

        Task {
            let result = await nfcScanner.scan()
            guard self.isViewActive else { return }
            switch result {
            case .spoolId(let id):
                await loadSpoolById(id)
            case .newSpoolData(let data):
                nfcScannedData = data
                showScannedDataSheet = true
            case .cancelled:
                break
            case .error(let error):
                nfcScanError = error.localizedDescription
            }
        }
    }

    private func loadSpoolById(_ id: Int) async {
        guard isViewActive else { return }
        guard let printerService else {
            print("⚠️ loadSpoolById: printerService is nil")
            return
        }
        isPerformingAction = true
        actionError = nil
        do {
            print("📡 loadSpoolById: printer=\(printerId) spool=\(id)")
            _ = try await printerService.setActiveSpool(printerId: printerId, spoolId: id)
            guard isViewActive else { return }
            print("✅ loadSpoolById: success")
            lastSetSpoolInfo = PrinterSpoolInfo(
                hasActiveSpool: true,
                activeSpoolId: id
            )
            await loadPrinter()
        } catch {
            guard isViewActive else { return }
            print("❌ loadSpoolById failed: \(error)")
            actionError = error.localizedDescription
        }
        guard isViewActive else { return }
        isPerformingAction = false
    }

    func ejectFilament() async {
        guard isViewActive else { return }
        guard let printerService else { return }
        isPerformingAction = true
        actionError = nil
        do {
            _ = try await printerService.setActiveSpool(printerId: printerId, spoolId: nil)
            guard isViewActive else { return }
            _ = try await printerService.unloadFilament(printerId: printerId)
            guard isViewActive else { return }
            lastSetSpoolInfo = nil
            await loadPrinter()
        } catch {
            guard isViewActive else { return }
            actionError = error.localizedDescription
        }
        guard isViewActive else { return }
        isPerformingAction = false
    }

    func setActiveSpool(_ spool: SpoolmanSpool) async {
        guard isViewActive else { return }
        showSpoolPicker = false
        guard let printerService else {
            print("⚠️ setActiveSpool: printerService is nil")
            return
        }
        isPerformingAction = true
        actionError = nil
        do {
            print("📡 setActiveSpool: printer=\(printerId) spool=\(spool.id)")
            _ = try await printerService.setActiveSpool(printerId: printerId, spoolId: spool.id)
            guard isViewActive else { return }
            print("✅ setActiveSpool: success")
            lastSetSpoolInfo = PrinterSpoolInfo(
                hasActiveSpool: true,
                activeSpoolId: spool.id,
                spoolName: spool.name,
                material: spool.material,
                colorHex: spool.colorHex,
                filamentName: spool.filamentName,
                vendor: spool.vendor,
                remainingWeightG: spool.remainingWeightG,
                spoolInUse: true
            )
            await loadPrinter()
        } catch {
            guard isViewActive else { return }
            print("❌ setActiveSpool failed: \(error)")
            actionError = error.localizedDescription
        }
        guard isViewActive else { return }
        isPerformingAction = false
    }

    func loadPrinter() async {
        guard isViewActive else { return }
        isLoading = true
        errorMessage = nil
        
        cameraRotation = UserDefaults.standard.integer(forKey: "cameraRotation-\(printerId.uuidString)")

        guard let printerService else {
            errorMessage = "Printer service not available"
            isLoading = false
            return
        }

        do {
            printer = try await printerService.get(id: printerId)
        } catch {
            errorMessage = error.localizedDescription
            isLoading = false
            return
        }

        do {
            statusDetail = try await printerService.getStatus(id: printerId)
            if let detail = statusDetail {
                applyStatusDetail(detail)
            }
        } catch {
            logger.warning("Failed to load printer status: \(error.localizedDescription)")
        }

        do {
            currentJob = try await printerService.getCurrentJob(id: printerId)
        } catch {
            logger.warning("Failed to load current job: \(error.localizedDescription)")
        }

        do {
            snapshotData = try await printerService.getSnapshot(id: printerId)
        } catch {
            logger.warning("Failed to load snapshot: \(error.localizedDescription)")
        }

        // Auto-enable livestream when printer is actively printing
        if let state = printer?.state?.lowercased(),
           ["printing", "starting", "paused"].contains(state),
           printer?.cameraStreamUrl != nil {
            showLivestream = true
        }

        // Load failure detection status when printing with Obico enabled
        if printer?.obicoEnabled == true, isActivelyPrinting {
            await loadFailureDetection()
        } else {
            activeAlerts = []
            failureDetectionStatus = nil
        }

        isLoading = false
    }

    /// Keep the UI-facing printer state aligned with the dedicated status endpoint.
    /// This prevents detail/list mismatches when `/api/printers/{id}` and `/status` are briefly out of sync.
    private func applyStatusDetail(_ detail: PrinterStatusDetail) {
        guard var current = printer else { return }
        current.isOnline = detail.isOnline
        current.state = detail.state
        current.progress = detail.progress
        current.jobName = detail.jobName
        current.thumbnailUrl = detail.thumbnailUrl
        current.cameraStreamUrl = detail.cameraStreamUrl
        current.cameraSnapshotUrl = detail.cameraSnapshotUrl
        current.x = detail.x
        current.y = detail.y
        current.z = detail.z
        current.hotendTemp = detail.hotendTemp
        current.bedTemp = detail.bedTemp
        current.hotendTarget = detail.hotendTarget
        current.bedTarget = detail.bedTarget
        current.spoolInfo = detail.spoolInfo
        printer = current
    }

    func loadFailureDetection() async {
        guard isViewActive else { return }
        if let failureDetectionService {
            do {
                let monitorStatus = try await failureDetectionService.getStatus()
                failureDetectionStatus = monitorStatus.printers.first { $0.printerId == printerId }
            } catch {
                logger.warning("Failed to load failure detection status: \(error.localizedDescription)")
            }
        }
        if let predictiveService {
            do {
                activeAlerts = try await predictiveService.getActiveAlerts(printerId: printerId)
            } catch {
                logger.warning("Failed to load active alerts: \(error.localizedDescription)")
            }
        }
    }

    // MARK: - Actions

    func pausePrinter() async {
        await performAction { _ = try await $0.pause(id: self.printerId) }
    }

    func resumePrinter() async {
        await performAction { _ = try await $0.resume(id: self.printerId) }
    }

    func stopPrinter() async {
        await performAction { _ = try await $0.stop(id: self.printerId) }
    }

    func requestCancel() {
        pendingAction = .cancelPrint
        showConfirmation = true
    }

    func requestEmergencyStop() {
        pendingAction = .emergencyStop
        showConfirmation = true
    }

    func confirmAction() async {
        guard isViewActive else { return }
        guard let action = pendingAction else { return }
        showConfirmation = false
        pendingAction = nil

        switch action {
        case .cancelPrint:
            await performAction { _ = try await $0.cancel(id: self.printerId) }
            guard isViewActive else { return }
            #if os(iOS)
            UINotificationFeedbackGenerator().notificationOccurred(.warning)
            #endif
        case .emergencyStop:
            await performAction { _ = try await $0.emergencyStop(id: self.printerId) }
            guard isViewActive else { return }
            #if os(iOS)
            UINotificationFeedbackGenerator().notificationOccurred(.error)
            #endif
        }
    }

    func toggleMaintenance() async {
        guard isViewActive else { return }
        guard let printerService, let printer else { return }
        do {
            let updated = try await printerService.setMaintenanceMode(
                id: printerId,
                inMaintenance: !printer.inMaintenance
            )
            guard isViewActive else { return }
            self.printer = updated
        } catch {
            guard isViewActive else { return }
            actionError = error.localizedDescription
        }
    }

    func refreshSnapshot() async {
        guard isViewActive else { return }
        guard let printerService else { return }
        isLoadingSnapshot = true
        do {
            snapshotData = try await printerService.getSnapshot(id: printerId)
        } catch {
            logger.warning("Failed to refresh snapshot: \(error.localizedDescription)")
        }
        guard isViewActive else { return }
        isLoadingSnapshot = false
    }
    
    func rotateCameraView() {
        cameraRotation = (cameraRotation + 90) % 360
        UserDefaults.standard.set(cameraRotation, forKey: "cameraRotation-\(printerId.uuidString)")
    }

    // MARK: - Computed State

    /// Merges server-returned spoolInfo with local override from recent setActiveSpool
    var effectiveSpoolInfo: PrinterSpoolInfo? {
        if let serverInfo = printer?.spoolInfo, serverInfo.hasActiveSpool {
            return serverInfo
        }
        return lastSetSpoolInfo ?? printer?.spoolInfo
    }

    var isPrinting: Bool {
        printer?.state?.lowercased() == "printing"
    }

    var isPaused: Bool {
        printer?.state?.lowercased() == "paused"
    }

    var isActivelyPrinting: Bool {
        guard let state = printer?.state?.lowercased() else { return false }
        return ["printing", "starting", "paused"].contains(state)
    }

    var canShowLivestream: Bool {
        isActivelyPrinting && printer?.cameraStreamUrl != nil
    }

    var isIdle: Bool {
        guard let state = printer?.state?.lowercased() else { return false }
        return ["ready", "idle", "operational"].contains(state)
    }

    var isOnline: Bool {
        printer?.isOnline ?? false
    }

    // MARK: - Private

    private func performAction(_ action: @escaping (any PrinterServiceProtocol) async throws -> Void) async {
        guard isViewActive else { return }
        guard let printerService else { return }
        isPerformingAction = true
        actionError = nil

        do {
            try await action(printerService)
            guard isViewActive else { return }
            await loadPrinter()
        } catch {
            guard isViewActive else { return }
            actionError = error.localizedDescription
        }

        guard isViewActive else { return }
        isPerformingAction = false
    }
}
