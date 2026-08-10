import Foundation
import SwiftUI
import os

@MainActor @Observable
final class PrinterDetailViewModel {
    typealias CallbackEnqueuer = @Sendable (
        @escaping @MainActor @Sendable () async -> Void
    ) -> Void

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
    var isViewActive = true {
        didSet {
            if oldValue && !isViewActive {
                invalidateCanonicalLoad()
                tearDownSignalR()
                invalidateSnapshotLifecycle()
            }

        }
    }
    var activeAlerts: [PredictiveAlert] = []
    var failureDetectionStatus: FailureDetectionPrinterStatus?

    // MARK: - F7 Printer Detail v2 (issue #712)
    /// F6 toolhead/slot roster for this printer (`GET /api/printers/{id}/details`).
    var toolheads: [Toolhead] = []
    /// Next jobs explicitly assigned to this printer (current + queued), capped
    /// downstream to three for the operator queue section.
    var assignedQueue: [QueuedPrintJobResponse] = []
    /// Cumulative odometer reading for the maintenance section.
    var printerStatistics: PrinterMaintenanceStatistics?
    /// Upcoming/overdue maintenance tasks scoped to this printer.
    var upcomingMaintenance: [UpcomingMaintenanceTask] = []
    /// Recent job history (newest first) for the history tail.
    var history: [PrinterHistoryJob] = []

    // Dispatch-to flow state.
    var dispatchTargetJob: QueuedPrintJobResponse?
    var dispatchCandidates: [DispatchCandidate] = []
    var isLoadingCandidates = false
    var isDispatching = false
    var dispatchError: String?
    /// Set after a successful maintenance log so the view can surface a
    /// transient confirmation without color-only signalling.
    var lastLoggedMaintenanceTaskId: UUID?

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
    var reviewedReadyStatus: AutoDispatchStatus?

    private var nfcScanner: (any SpoolScannerProtocol)?
    private var autoDispatchService: (any AutoDispatchServiceProtocol)?
    private var signalRService: (any SignalRServiceProtocol)?
    @ObservationIgnored private var signalRSubscriptions: [SignalRSubscription] = []
    @ObservationIgnored private var signalRServiceIdentity: ObjectIdentifier?
    @ObservationIgnored private var signalRAuthorityEpoch: UInt64 = 0
    @ObservationIgnored private var lastObservedConnectionState: SignalRConnectionState?
    @ObservationIgnored private let callbackEnqueuer: CallbackEnqueuer
    @ObservationIgnored private var canonicalLifecycleEpoch: UInt64 = 0
    @ObservationIgnored private var canonicalLoadToken: UUID?
    @ObservationIgnored private var canonicalLoadTask: Task<Void, Never>?
    @ObservationIgnored private var canonicalLoadRequested = false
    @ObservationIgnored private var canonicalPassHasRecoveryDemand = false
    @ObservationIgnored private var canonicalPendingRecoveryDemand = false
    @ObservationIgnored private let canonicalLoadWaiters = CanonicalLoadWaiterRegistry()
    @ObservationIgnored private let canonicalTaskTracker = CanonicalLoadTaskTracker()
    private var predictiveService: (any PredictiveServiceProtocol)?
    private var failureDetectionService: (any FailureDetectionServiceProtocol)?
    @ObservationIgnored private var snapshotPollingTask: Task<Void, Never>?
    @ObservationIgnored private var snapshotPollingGeneration: UInt64 = 0
    @ObservationIgnored private var snapshotLifecycleEpoch: UInt64 = 0
    @ObservationIgnored private var snapshotRequestToken: UUID?
    @ObservationIgnored private let snapshotTaskTracker = CanonicalLoadTaskTracker()
    private var isSnapshotPollingAllowed = true
    private let snapshotPollInterval: Duration
    private let snapshotErrorBackoffBaseSeconds: Int
    private let snapshotErrorBackoffMaxSeconds: Int

    let printerId: UUID
    private var printerService: (any PrinterServiceProtocol)?
    private var jobService: (any JobServiceProtocol)?
    private var maintenanceService: (any MaintenanceServiceProtocol)?
    /// Injectable clock so absolute-ETA formatting is deterministic in tests.
    private let nowProvider: @Sendable () -> Date

    var cameraRotation: Int = 0

    enum CameraPreviewMode: Equatable {
        case none
        case directSnapshot
        case snapshotPolling
        case mjpegStream
        case unsupported
    }

    init(
        printerId: UUID,
        snapshotPollInterval: Duration = .seconds(5),
        snapshotErrorBackoffBaseSeconds: Int = 5,
        snapshotErrorBackoffMaxSeconds: Int = 30,
        now: @escaping @Sendable () -> Date = { Date() },
        callbackEnqueuer: @escaping CallbackEnqueuer = { operation in
            Task { @MainActor in await operation() }
        }
    ) {
        self.printerId = printerId
        self.snapshotPollInterval = snapshotPollInterval
        self.snapshotErrorBackoffBaseSeconds = snapshotErrorBackoffBaseSeconds
        self.snapshotErrorBackoffMaxSeconds = snapshotErrorBackoffMaxSeconds
        self.nowProvider = now
        self.callbackEnqueuer = callbackEnqueuer
        self.cameraRotation = UserDefaults.standard.integer(forKey: "cameraRotation-\(printerId.uuidString)")
    }

    deinit {
        canonicalLoadTask?.cancel()
        snapshotPollingTask?.cancel()
        canonicalLoadWaiters.completeAll()
    }

    func configure(printerService: any PrinterServiceProtocol) {
        if !Self.identical(self.printerService, printerService) {
            invalidateCanonicalLoad()
            invalidateSnapshotLifecycle()
        }

        self.printerService = printerService
    }

    func bindToolheadSpool(_ spool: SpoolmanSpool, at toolheadIndex: Int) async {
        guard let printerService else {
            actionError = "Printer service not available."
            return
        }

        do {
            _ = try await printerService.bindToolheadSpool(
                printerId: printerId,
                toolheadIndex: toolheadIndex,
                request: ToolheadSpoolBindRequest(spoolId: spool.id),
                idempotencyKey: UUID().uuidString
            )
            await loadPrinter()
        } catch {
            actionError = error.localizedDescription
        }
    }

    /// Injects the job and maintenance services used by the F7 operator
    /// sections (queue/dispatch, odometer, history). Optional so existing
    /// call sites and previews that only need printer control keep working.
    func configureOperatorServices(
        jobService: any JobServiceProtocol,
        maintenanceService: any MaintenanceServiceProtocol
    ) {
        let changed = !Self.identical(self.jobService, jobService)
            || !Self.identical(self.maintenanceService, maintenanceService)
        if changed {
            invalidateCanonicalLoad()
        }
        self.jobService = jobService
        self.maintenanceService = maintenanceService
    }

    func configureNFCScanner(_ scanner: any SpoolScannerProtocol) {
        self.nfcScanner = scanner
    }

    func configureAutoDispatch(_ service: any AutoDispatchServiceProtocol) {
        self.autoDispatchService = service
    }

    func configurePredictive(_ service: any PredictiveServiceProtocol) {
        if !Self.identical(predictiveService, service) {
            invalidateCanonicalLoad()
        }
        self.predictiveService = service
    }

    func configureFailureDetection(_ service: any FailureDetectionServiceProtocol) {
        if !Self.identical(failureDetectionService, service) {
            invalidateCanonicalLoad()
        }
        self.failureDetectionService = service
    }

    func configureSignalR(_ service: any SignalRServiceProtocol) {
        let serviceIdentity = ObjectIdentifier(service as AnyObject)
        if signalRServiceIdentity == serviceIdentity, !signalRSubscriptions.isEmpty {
            return
        }
        invalidateCanonicalLoad()
        tearDownSignalR()
        self.signalRService = service
        signalRAuthorityEpoch &+= 1
        let authorityEpoch = signalRAuthorityEpoch
        signalRServiceIdentity = serviceIdentity
        let enqueue = callbackEnqueuer
        signalRSubscriptions.append(service.onPrinterUpdated { [weak self] update in
            enqueue { [weak self] in
                guard let self,
                      self.hasSignalRAuthority(
                        epoch: authorityEpoch,
                        serviceIdentity: serviceIdentity
                      ),
                      update.id == self.printerId else {
                    return
                }
                self.applyLiveUpdate(update)
            }
        })
        let connectionRegistration = service.onConnectionStateChanged { [weak self] state in
            enqueue { [weak self] in
                guard let self,
                      self.hasSignalRAuthority(
                        epoch: authorityEpoch,
                        serviceIdentity: serviceIdentity
                      ) else {
                    return
                }
                let previous = self.lastObservedConnectionState
                self.lastObservedConnectionState = state
                guard previous == .reconnecting, state == .connected else {
                    return
                }
                self.requestCanonicalLoad(isRecovery: true)
            }
        }
        lastObservedConnectionState = connectionRegistration.initial
        signalRSubscriptions.append(connectionRegistration.subscription)
    }

    private func tearDownSignalR() {
        signalRAuthorityEpoch &+= 1
        for subscription in signalRSubscriptions { subscription.cancel() }
        signalRSubscriptions.removeAll(keepingCapacity: true)
        signalRService = nil
        signalRServiceIdentity = nil
        lastObservedConnectionState = nil
    }

    private func hasSignalRAuthority(
        epoch: UInt64,
        serviceIdentity: ObjectIdentifier
    ) -> Bool {
        isViewActive
            && signalRAuthorityEpoch == epoch
            && signalRServiceIdentity == serviceIdentity
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
            if let homed = update.homedAxes { p.homedAxes = homed }
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
            startSnapshotPollingIfNeeded()
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
            homedAxes: update.homedAxes ?? statusDetail?.homedAxes,
            spoolInfo: update.spoolInfo ?? statusDetail?.spoolInfo,
            mmuStatus: update.mmuStatus ?? statusDetail?.mmuStatus,
            printTimeLeftSeconds: statusDetail?.printTimeLeftSeconds
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

    func prepareReadyConfirmation() async {
        guard isViewActive, let autoDispatchService else { return }
        do {
            reviewedReadyStatus = try await autoDispatchService.getStatus(
                printerId: printerId
            )
            showNFCReadyConfirmation = true
        } catch {
            actionError = error.localizedDescription
        }
    }

    func markPrinterReady() async {
        guard isViewActive else { return }
        guard let autoDispatchService, let reviewedReadyStatus else {
            actionError = "Refresh the auto-dispatch status before confirming."
            return
        }
        self.reviewedReadyStatus = nil
        isPerformingAction = true
        actionError = nil
        do {
            _ = try await autoDispatchService.markReady(
                status: reviewedReadyStatus
            )
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
            let rowVersion = try reviewedPrinterRowVersion()
            print("📡 loadSpoolById: printer=\(printerId) spool=\(id)")
            _ = try await printerService.setActiveSpool(
                printerId: printerId,
                spoolId: id,
                reviewedRowVersion: rowVersion
            )
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
            _ = try await printerService.setActiveSpool(
                printerId: printerId,
                spoolId: nil,
                reviewedRowVersion: try reviewedPrinterRowVersion()
            )
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
            let rowVersion = try reviewedPrinterRowVersion()
            print("📡 setActiveSpool: printer=\(printerId) spool=\(spool.id)")
            _ = try await printerService.setActiveSpool(
                printerId: printerId,
                spoolId: spool.id,
                reviewedRowVersion: rowVersion
            )
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
        guard canLoadPrinter else {
            if isViewActive, printerService == nil {
                errorMessage = "Printer service not available"
            }
            return
        }
        let waiter = beginCanonicalLoad()
        await waiter.wait()
    }

    private func beginCanonicalLoad() -> CanonicalLoadWaiter {
        let enqueue = callbackEnqueuer
        let waiter = canonicalLoadWaiters.registerWaiter { [weak self] in
            enqueue { [weak self] in
                self?.canonicalWaiterCancelled()
            }
        }
        requestCanonicalLoad()
        return waiter
    }

    private var canLoadPrinter: Bool {
        isViewActive && printerService != nil
    }

    private func requestCanonicalLoad(isRecovery: Bool = false) {
        guard canLoadPrinter else { return }
        canonicalLoadRequested = true
        if isRecovery {
            canonicalPendingRecoveryDemand = true
        }
        guard canonicalLoadTask == nil else { return }

        let token = UUID()
        canonicalLoadToken = token
        canonicalLoadRequested = false
        canonicalPassHasRecoveryDemand = canonicalPendingRecoveryDemand
        canonicalPendingRecoveryDemand = false
        isLoading = true
        errorMessage = nil
        let authority = makeCanonicalAuthority(token: token)
        startCanonicalPass(authority: authority)
    }

    private func startCanonicalPass(authority: CanonicalAuthority) {
        guard isCanonicalLoadCurrent(authority),
              let printerService else {
            return
        }
        stopSnapshotPolling()
        guard let snapshotAuthority = beginSnapshotRequest(
            using: printerService,
            pollGeneration: nil,
            publishesLoading: false
        ) else {
            return
        }
        let input = CanonicalLoadInput(
            printerId: printerId,
            printerService: printerService,
            jobService: jobService,
            maintenanceService: maintenanceService,
            predictiveService: predictiveService,
            failureDetectionService: failureDetectionService,
            statusDetail: statusDetail,
            currentJob: currentJob,
            snapshotData: snapshotData,
            activeAlerts: activeAlerts,
            failureDetectionStatus: failureDetectionStatus,
            toolheads: toolheads,
            assignedQueue: assignedQueue,
            printerStatistics: printerStatistics,
            upcomingMaintenance: upcomingMaintenance,
            history: history,
            cameraRotation: cameraRotation,
            logger: logger
        )
        let taskTracker = canonicalTaskTracker
        taskTracker.taskStarted()
        canonicalLoadTask = Task { [weak self, taskTracker, input] in
            defer { taskTracker.taskFinished() }
            let result = await Self.loadCanonicalPrinter(input: input)
            self?.completeCanonicalPass(
                result,
                authority: authority,
                snapshotAuthority: snapshotAuthority
            )
        }
    }

    private func completeCanonicalPass(
        _ result: CanonicalLoadResult,
        authority: CanonicalAuthority,
        snapshotAuthority: SnapshotRequestAuthority
    ) {
        guard isCanonicalLoadCurrent(authority) else { return }
        if canonicalLoadRequested {
            canonicalLoadRequested = false
            canonicalPassHasRecoveryDemand = canonicalPendingRecoveryDemand
            canonicalPendingRecoveryDemand = false
            startCanonicalPass(authority: authority)
            return
        }

        switch result {
        case .success(let snapshot):
            publish(
                snapshot,
                includesAuthoritativeSnapshot: isSnapshotRequestCurrent(snapshotAuthority)
            )
        case .failure(let error):
            errorMessage = error.localizedDescription
        case .superseded:
            break
        }
        finishCanonicalLoad(authority: authority)
    }

    nonisolated private static func loadCanonicalPrinter(
        input: CanonicalLoadInput
    ) async -> CanonicalLoadResult {
        let loadedPrinter: Printer
        do {
            loadedPrinter = try await input.printerService.get(id: input.printerId)
        } catch {
            guard !Task.isCancelled else { return .superseded }
            return .failure(error)
        }
        guard !Task.isCancelled else { return .superseded }

        async let statusTask = fetchStatus(
            using: input.printerService,
            printerId: input.printerId,
            logger: input.logger
        )
        async let cameraTask = fetchCamera(
            using: input.printerService,
            printerId: input.printerId,
            logger: input.logger
        )
        async let currentJobTask = fetchCurrentJob(
            using: input.printerService,
            printerId: input.printerId,
            logger: input.logger
        )
        async let operatorTask = fetchOperatorSnapshot(
            printerId: input.printerId,
            printerService: input.printerService,
            jobService: input.jobService,
            maintenanceService: input.maintenanceService,
            logger: input.logger
        )
        let (statusResult, cameraResult, jobResult, operatorSnapshot) = await (
            statusTask,
            cameraTask,
            currentJobTask,
            operatorTask
        )
        guard !Task.isCancelled else { return .superseded }

        var canonicalPrinter = loadedPrinter
        var loadedStatus = input.statusDetail
        if case .value(let detail) = statusResult {
            loadedStatus = detail
            canonicalPrinter = Self.applying(detail, to: canonicalPrinter)
        }
        if case .value(let camera) = cameraResult {
            canonicalPrinter = Self.applying(camera, to: canonicalPrinter)
        }

        let previewMode = Self.cameraPreviewMode(for: canonicalPrinter)
        var loadedSnapshotData = input.snapshotData
        let shouldFetchSnapshot = previewMode == .snapshotPolling
            || (previewMode == .directSnapshot && loadedSnapshotData == nil)
        if shouldFetchSnapshot {
            do {
                loadedSnapshotData = try await input.printerService.getSnapshot(id: input.printerId)
            } catch {
                guard !Task.isCancelled else { return .superseded }
                input.logger.warning("Failed to refresh snapshot: \(error.localizedDescription)")
            }
            guard !Task.isCancelled else { return .superseded }
        }

        var loadedFailureStatus = input.failureDetectionStatus
        var loadedAlerts = input.activeAlerts
        if canonicalPrinter.obicoEnabled && Self.isActivelyPrinting(canonicalPrinter) {
            async let failureTask = fetchFailureDetectionStatus(
                using: input.failureDetectionService,
                printerId: input.printerId,
                logger: input.logger
            )
            async let alertsTask = fetchActiveAlerts(
                using: input.predictiveService,
                printerId: input.printerId,
                logger: input.logger
            )
            let (failureResult, alertsResult) = await (failureTask, alertsTask)
            guard !Task.isCancelled else { return .superseded }
            if case .value(let status) = failureResult {
                loadedFailureStatus = status
            }
            if case .value(let alerts) = alertsResult {
                loadedAlerts = alerts
            }
        } else {
            loadedFailureStatus = nil
            loadedAlerts = []
        }

        return .success(
            CanonicalSnapshot(
                printer: canonicalPrinter,
                statusDetail: loadedStatus,
                currentJob: jobResult.value(or: input.currentJob),
                snapshotData: loadedSnapshotData,
                showLivestream: Self.isActivelyPrinting(canonicalPrinter)
                    && previewMode == .mjpegStream,
                activeAlerts: loadedAlerts,
                failureDetectionStatus: loadedFailureStatus,
                toolheads: operatorSnapshot.toolheads ?? input.toolheads,
                assignedQueue: operatorSnapshot.assignedQueue ?? input.assignedQueue,
                printerStatistics: operatorSnapshot.printerStatistics ?? input.printerStatistics,
                upcomingMaintenance: operatorSnapshot.upcomingMaintenance ?? input.upcomingMaintenance,
                history: operatorSnapshot.history ?? input.history,
                cameraRotation: input.cameraRotation
            )
        )
    }

    private func publish(
        _ snapshot: CanonicalSnapshot,
        includesAuthoritativeSnapshot: Bool
    ) {
        printer = snapshot.printer
        statusDetail = snapshot.statusDetail
        currentJob = snapshot.currentJob
        if includesAuthoritativeSnapshot {
            snapshotData = snapshot.snapshotData
#if DEBUG
            if let snapshotData = snapshot.snapshotData {
                snapshotPublicationsForTesting.append(snapshotData)
            }
#endif
        }
        showLivestream = snapshot.showLivestream
        activeAlerts = snapshot.activeAlerts
        failureDetectionStatus = snapshot.failureDetectionStatus
        toolheads = snapshot.toolheads
        assignedQueue = snapshot.assignedQueue
        printerStatistics = snapshot.printerStatistics
        upcomingMaintenance = snapshot.upcomingMaintenance
        history = snapshot.history
        cameraRotation = snapshot.cameraRotation
        errorMessage = nil
        if includesAuthoritativeSnapshot {
            startSnapshotPollingIfNeeded()
        }
    }

    private func finishCanonicalLoad(authority: CanonicalAuthority) {
        guard isCanonicalLoadCurrent(authority) else { return }
        canonicalLoadTask = nil
        canonicalLoadToken = nil
        canonicalLoadRequested = false
        canonicalPassHasRecoveryDemand = false
        canonicalPendingRecoveryDemand = false
        isLoading = false
        canonicalLoadWaiters.completeAll()
    }

    private func invalidateCanonicalLoad() {
        canonicalLifecycleEpoch &+= 1
        canonicalLoadTask?.cancel()
        canonicalLoadTask = nil
        canonicalLoadToken = nil
        canonicalLoadRequested = false
        canonicalPassHasRecoveryDemand = false
        canonicalPendingRecoveryDemand = false
        isLoading = false
        canonicalLoadWaiters.completeAll()
    }

    private func canonicalWaiterCancelled() {
        guard canonicalLoadWaiters.activeCount == 0,
              !canonicalPassHasRecoveryDemand,
              !canonicalPendingRecoveryDemand else {
            return
        }
        invalidateCanonicalLoad()
    }

    private func makeCanonicalAuthority(token: UUID) -> CanonicalAuthority {
        CanonicalAuthority(
            token: token,
            lifecycleEpoch: canonicalLifecycleEpoch,
            printerServiceIdentity: Self.identity(printerService),
            jobServiceIdentity: Self.identity(jobService),
            maintenanceServiceIdentity: Self.identity(maintenanceService),
            predictiveServiceIdentity: Self.identity(predictiveService),
            failureDetectionServiceIdentity: Self.identity(failureDetectionService)
        )
    }

    private func isCanonicalLoadCurrent(_ authority: CanonicalAuthority) -> Bool {
        isViewActive
            && canonicalLoadToken == authority.token
            && canonicalLifecycleEpoch == authority.lifecycleEpoch
            && Self.identity(printerService) == authority.printerServiceIdentity
            && Self.identity(jobService) == authority.jobServiceIdentity
            && Self.identity(maintenanceService) == authority.maintenanceServiceIdentity
            && Self.identity(predictiveService) == authority.predictiveServiceIdentity
            && Self.identity(failureDetectionService) == authority.failureDetectionServiceIdentity
    }

    private static func identity<T>(_ value: T?) -> ObjectIdentifier? {
        value.map { ObjectIdentifier($0 as AnyObject) }
    }

    private static func identical<T>(_ lhs: T?, _ rhs: T?) -> Bool {
        identity(lhs) == identity(rhs)
    }

#if DEBUG
    func beginCanonicalLoadForTesting() -> CanonicalLoadWaiter? {
        guard canLoadPrinter else { return nil }
        return beginCanonicalLoad()
    }

    var canonicalWaiterCountForTesting: Int {
        canonicalLoadWaiters.activeCount
    }

    func waitForCanonicalWaiterCount(_ count: Int) async {
        await canonicalLoadWaiters.waitForActiveCount(count)
    }

    private(set) var snapshotPublicationsForTesting: [Data] = []

    func waitForSnapshotRequestsToBecomeIdle() async {
        await snapshotTaskTracker.waitForIdle()
    }

    func waitForCanonicalLoadToBecomeIdle() async {
        await canonicalTaskTracker.waitForIdle()
    }

    func waitForSupersededCanonicalLoads() async {
        await canonicalTaskTracker.waitForIdle()
    }
#endif

    nonisolated private static func fetchStatus(
        using service: any PrinterServiceProtocol,
        printerId: UUID,
        logger: Logger
    ) async -> CanonicalValue<PrinterStatusDetail> {
        do {
            return .value(try await service.getStatus(id: printerId))
        } catch {
            logger.warning("Failed to load printer status: \(error.localizedDescription)")
            return .unavailable
        }
    }

    nonisolated private static func fetchCamera(
        using service: any PrinterServiceProtocol,
        printerId: UUID,
        logger: Logger
    ) async -> CanonicalValue<PrinterCameraUrl> {
        do {
            return .value(try await service.getCameraUrl(id: printerId))
        } catch {
            logger.warning("Failed to load camera URL metadata: \(error.localizedDescription)")
            return .unavailable
        }
    }

    nonisolated private static func fetchCurrentJob(
        using service: any PrinterServiceProtocol,
        printerId: UUID,
        logger: Logger
    ) async -> CanonicalValue<PrintJobStatusInfo?> {
        do {
            return .value(try await service.getCurrentJob(id: printerId))
        } catch {
            logger.warning("Failed to load current job: \(error.localizedDescription)")
            return .unavailable
        }
    }

    nonisolated private static func fetchOperatorSnapshot(
        printerId: UUID,
        printerService: any PrinterServiceProtocol,
        jobService: (any JobServiceProtocol)?,
        maintenanceService: (any MaintenanceServiceProtocol)?,
        logger: Logger
    ) async -> OperatorSnapshot {
        async let toolheadsTask = fetchCanonicalToolheads(
            using: printerService,
            printerId: printerId,
            logger: logger
        )
        async let queueTask = fetchCanonicalQueue(
            using: jobService,
            printerId: printerId,
            logger: logger
        )
        async let maintenanceTask = fetchCanonicalMaintenance(
            using: maintenanceService,
            printerId: printerId,
            logger: logger
        )
        async let historyTask = fetchCanonicalHistory(
            using: printerService,
            printerId: printerId,
            logger: logger
        )
        let (loadedToolheads, loadedQueue, loadedMaintenance, loadedHistory) = await (
            toolheadsTask,
            queueTask,
            maintenanceTask,
            historyTask
        )
        return OperatorSnapshot(
            toolheads: loadedToolheads,
            assignedQueue: loadedQueue,
            printerStatistics: loadedMaintenance.statistics,
            upcomingMaintenance: loadedMaintenance.upcoming,
            history: loadedHistory
        )
    }

    nonisolated private static func fetchCanonicalToolheads(
        using service: any PrinterServiceProtocol,
        printerId: UUID,
        logger: Logger
    ) async -> [Toolhead]? {
        do {
            return try await service.getDetails(id: printerId).toolheads
        } catch {
            logger.warning("Failed to load toolheads: \(error.localizedDescription)")
            return nil
        }
    }

    nonisolated private static func fetchCanonicalQueue(
        using service: (any JobServiceProtocol)?,
        printerId: UUID,
        logger: Logger
    ) async -> [QueuedPrintJobResponse]? {
        guard let service else { return nil }
        do {
            return Self.filterAssignedQueue(
                try await service.listAllJobs(),
                printerId: printerId
            )
        } catch {
            logger.warning("Failed to load assigned queue: \(error.localizedDescription)")
            return nil
        }
    }

    nonisolated private static func fetchCanonicalMaintenance(
        using service: (any MaintenanceServiceProtocol)?,
        printerId: UUID,
        logger: Logger
    ) async -> MaintenanceSnapshot {
        guard let service else { return MaintenanceSnapshot() }
        async let statisticsTask = fetchCanonicalStatistics(
            using: service,
            printerId: printerId,
            logger: logger
        )
        async let upcomingTask = fetchCanonicalUpcomingMaintenance(
            using: service,
            printerId: printerId,
            logger: logger
        )
        let (statistics, upcoming) = await (statisticsTask, upcomingTask)
        return MaintenanceSnapshot(statistics: statistics, upcoming: upcoming)
    }

    nonisolated private static func fetchCanonicalStatistics(
        using service: any MaintenanceServiceProtocol,
        printerId: UUID,
        logger: Logger
    ) async -> PrinterMaintenanceStatistics? {
        do {
            return try await service.getPrinterStatistics(printerId: printerId)
        } catch {
            logger.warning("Failed to load printer statistics: \(error.localizedDescription)")
            return nil
        }
    }

    nonisolated private static func fetchCanonicalUpcomingMaintenance(
        using service: any MaintenanceServiceProtocol,
        printerId: UUID,
        logger: Logger
    ) async -> [UpcomingMaintenanceTask]? {
        do {
            return try await service.getUpcoming(
                lookaheadDays: nil,
                includeOverdue: true,
                printerId: printerId
            )
        } catch {
            logger.warning("Failed to load upcoming maintenance: \(error.localizedDescription)")
            return nil
        }
    }

    nonisolated private static func fetchCanonicalHistory(
        using service: any PrinterServiceProtocol,
        printerId: UUID,
        logger: Logger
    ) async -> [PrinterHistoryJob]? {
        do {
            return Self.sortedHistory(try await service.getHistory(id: printerId, limit: nil).jobs)
        } catch {
            logger.warning("Failed to load printer history: \(error.localizedDescription)")
            return nil
        }
    }

    nonisolated private static func fetchFailureDetectionStatus(
        using service: (any FailureDetectionServiceProtocol)?,
        printerId: UUID,
        logger: Logger
    ) async -> CanonicalValue<FailureDetectionPrinterStatus?> {
        guard let service else { return .unavailable }
        do {
            let status = try await service.getStatus()
            return .value(status.printers.first { $0.printerId == printerId })
        } catch {
            logger.warning("Failed to load failure detection status: \(error.localizedDescription)")
            return .unavailable
        }
    }

    nonisolated private static func fetchActiveAlerts(
        using service: (any PredictiveServiceProtocol)?,
        printerId: UUID,
        logger: Logger
    ) async -> CanonicalValue<[PredictiveAlert]> {
        guard let service else { return .unavailable }
        do {
            return .value(try await service.getActiveAlerts(printerId: printerId))
        } catch {
            logger.warning("Failed to load active alerts: \(error.localizedDescription)")
            return .unavailable
        }
    }

    private struct CanonicalAuthority {
        let token: UUID
        let lifecycleEpoch: UInt64
        let printerServiceIdentity: ObjectIdentifier?
        let jobServiceIdentity: ObjectIdentifier?
        let maintenanceServiceIdentity: ObjectIdentifier?
        let predictiveServiceIdentity: ObjectIdentifier?
        let failureDetectionServiceIdentity: ObjectIdentifier?
    }

    private struct CanonicalSnapshot {
        let printer: Printer
        let statusDetail: PrinterStatusDetail?
        let currentJob: PrintJobStatusInfo?
        let snapshotData: Data?
        let showLivestream: Bool
        let activeAlerts: [PredictiveAlert]
        let failureDetectionStatus: FailureDetectionPrinterStatus?
        let toolheads: [Toolhead]
        let assignedQueue: [QueuedPrintJobResponse]
        let printerStatistics: PrinterMaintenanceStatistics?
        let upcomingMaintenance: [UpcomingMaintenanceTask]
        let history: [PrinterHistoryJob]
        let cameraRotation: Int
    }

    private struct CanonicalLoadInput: Sendable {
        let printerId: UUID
        let printerService: any PrinterServiceProtocol
        let jobService: (any JobServiceProtocol)?
        let maintenanceService: (any MaintenanceServiceProtocol)?
        let predictiveService: (any PredictiveServiceProtocol)?
        let failureDetectionService: (any FailureDetectionServiceProtocol)?
        let statusDetail: PrinterStatusDetail?
        let currentJob: PrintJobStatusInfo?
        let snapshotData: Data?
        let activeAlerts: [PredictiveAlert]
        let failureDetectionStatus: FailureDetectionPrinterStatus?
        let toolheads: [Toolhead]
        let assignedQueue: [QueuedPrintJobResponse]
        let printerStatistics: PrinterMaintenanceStatistics?
        let upcomingMaintenance: [UpcomingMaintenanceTask]
        let history: [PrinterHistoryJob]
        let cameraRotation: Int
        let logger: Logger
    }

    private struct OperatorSnapshot {
        let toolheads: [Toolhead]?
        let assignedQueue: [QueuedPrintJobResponse]?
        let printerStatistics: PrinterMaintenanceStatistics?
        let upcomingMaintenance: [UpcomingMaintenanceTask]?
        let history: [PrinterHistoryJob]?
    }

    private struct MaintenanceSnapshot {
        var statistics: PrinterMaintenanceStatistics?
        var upcoming: [UpcomingMaintenanceTask]?
    }

    private enum CanonicalValue<Value: Sendable>: Sendable {
        case value(Value)
        case unavailable

        func value(or fallback: Value) -> Value {
            if case .value(let value) = self {
                return value
            }
            return fallback
        }
    }

    private enum CanonicalLoadResult {
        case success(CanonicalSnapshot)
        case failure(Error)
        case superseded
    }

    private struct SnapshotRequestAuthority: Sendable {
        let token: UUID
        let lifecycleEpoch: UInt64
        let printerId: UUID
        let printerServiceIdentity: ObjectIdentifier?
        let pollGeneration: UInt64?
    }

    /// Keep the UI-facing printer state aligned with the dedicated status endpoint.
    /// This prevents detail/list mismatches when `/api/printers/{id}` and `/status` are briefly out of sync.
    private func applyStatusDetail(_ detail: PrinterStatusDetail) {
        guard let current = printer else { return }
        printer = Self.applying(detail, to: current)
    }

    nonisolated private static func applying(_ detail: PrinterStatusDetail, to printer: Printer) -> Printer {
        var current = printer
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
        if let homed = detail.homedAxes { current.homedAxes = homed }
        current.spoolInfo = detail.spoolInfo
        return current
    }

    private func applyCameraUrl(_ cameraUrl: PrinterCameraUrl) {
        guard let current = printer else { return }
        printer = Self.applying(cameraUrl, to: current)
    }

    nonisolated private static func applying(_ cameraUrl: PrinterCameraUrl, to printer: Printer) -> Printer {
        var current = printer
        current.cameraStreamUrl = cameraUrl.streamUrl
        current.cameraSnapshotUrl = cameraUrl.snapshotUrl
        current.cameraAccessMode = cameraUrl.accessMode
        current.cameraStreamFormat = cameraUrl.streamFormat
        current.cameraSnapshotStrategy = cameraUrl.snapshotStrategy
        return current
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

    // MARK: - F7 Operator Sections (issue #712)

    /// Loads the operator-facing sections (toolhead slots, assigned queue,
    /// maintenance odometer, history) in parallel. Every load is independent
    /// and non-fatal: a failure clears only its own slice and is logged, so a
    /// single degraded endpoint never blanks the whole screen. Deterministic —
    /// all child loads are awaited before returning.
    func loadOperatorSections() async {
        guard isViewActive else { return }

        async let toolheadsResult = loadToolheads()
        async let queueResult = loadAssignedQueue()
        async let statsResult = loadMaintenance()
        async let historyResult = loadHistory()

        _ = await (toolheadsResult, queueResult, statsResult, historyResult)
    }

    private func loadToolheads() async {
        guard let printerService else { return }
        do {
            let details = try await printerService.getDetails(id: printerId)
            guard isViewActive else { return }
            toolheads = details.toolheads
        } catch {
            logger.warning("Failed to load toolheads: \(error.localizedDescription)")
        }
    }

    private func loadAssignedQueue() async {
        guard let jobService else { return }
        do {
            let all = try await jobService.listAllJobs()
            guard isViewActive else { return }
            assignedQueue = Self.filterAssignedQueue(all, printerId: printerId)
        } catch {
            logger.warning("Failed to load assigned queue: \(error.localizedDescription)")
        }
    }

    private func loadMaintenance() async {
        guard let maintenanceService else { return }
        do {
            printerStatistics = try await maintenanceService.getPrinterStatistics(printerId: printerId)
        } catch {
            logger.warning("Failed to load printer statistics: \(error.localizedDescription)")
        }
        guard isViewActive else { return }
        do {
            upcomingMaintenance = try await maintenanceService.getUpcoming(
                lookaheadDays: nil,
                includeOverdue: true,
                printerId: printerId
            )
        } catch {
            logger.warning("Failed to load upcoming maintenance: \(error.localizedDescription)")
        }
    }

    private func loadHistory() async {
        guard let printerService else { return }
        do {
            let list = try await printerService.getHistory(id: printerId, limit: nil)
            guard isViewActive else { return }
            history = Self.sortedHistory(list.jobs)
        } catch {
            logger.warning("Failed to load printer history: \(error.localizedDescription)")
        }
    }

    /// Queue scope per triage: jobs explicitly assigned to *this* printer that
    /// are still active or waiting (not terminal), ordered by queue position.
    /// Pure/static so it is unit-testable without a live service.
    nonisolated static func filterAssignedQueue(
        _ all: [QueuedPrintJobResponse],
        printerId: UUID
    ) -> [QueuedPrintJobResponse] {
        let target = printerId.uuidString.lowercased()
        let terminal: Set<String> = ["completed", "failed", "cancelled", "canceled", "aborted"]
        return all
            .filter { response in
                let assigned = (response.job.assignedPrinterId ?? response.assignedPrinter?.id)?.lowercased()
                guard assigned == target else { return false }
                return !terminal.contains(response.job.status.lowercased())
            }
            .sorted { $0.job.queuePosition < $1.job.queuePosition }
    }

    nonisolated static func sortedHistory(_ jobs: [PrinterHistoryJob]) -> [PrinterHistoryJob] {
        jobs.sorted { lhs, rhs in
            (lhs.endTime ?? lhs.startTime) > (rhs.endTime ?? rhs.startTime)
        }
    }

    // MARK: - Dispatch-to

    func beginDispatch(for job: QueuedPrintJobResponse) async {
        guard isViewActive, let jobService, let jobUUID = job.job.jobUUID else { return }
        dispatchTargetJob = job
        dispatchCandidates = []
        dispatchError = nil
        isLoadingCandidates = true
        do {
            let candidates = try await jobService.getCandidates(jobId: jobUUID)
            guard isViewActive else { return }
            dispatchCandidates = candidates.sorted { $0.score > $1.score }
        } catch {
            guard isViewActive else { return }
            dispatchError = error.localizedDescription
        }
        guard isViewActive else { return }
        isLoadingCandidates = false
    }

    func dispatch(_ job: QueuedPrintJobResponse, to targetPrinterId: UUID) async {
        guard isViewActive, let jobService, let jobUUID = job.job.jobUUID else { return }
        isDispatching = true
        dispatchError = nil
        do {
            try await jobService.dispatchTo(jobId: jobUUID, printerId: targetPrinterId)
            guard isViewActive else { return }
            dispatchTargetJob = nil
            dispatchCandidates = []
            await loadAssignedQueue()
        } catch {
            guard isViewActive else { return }
            dispatchError = error.localizedDescription
        }
        guard isViewActive else { return }
        isDispatching = false
    }

    func cancelDispatch() {
        dispatchTargetJob = nil
        dispatchCandidates = []
        dispatchError = nil
        isLoadingCandidates = false
    }

    // MARK: - Maintenance log completion

    func logMaintenanceCompletion(_ row: OdometerRow, performedBy: String) async {
        guard isViewActive, let maintenanceService else { return }
        isPerformingAction = true
        actionError = nil
        let request = CreateMaintenanceLogRequest(
            printerId: printerId,
            performedBy: performedBy,
            taskId: row.taskId,
            taskName: row.title,
            componentName: row.component
        )
        do {
            _ = try await maintenanceService.createLog(request)
            guard isViewActive else { return }
            lastLoggedMaintenanceTaskId = row.taskId
            await loadMaintenance()
        } catch {
            guard isViewActive else { return }
            actionError = error.localizedDescription
        }
        guard isViewActive else { return }
        isPerformingAction = false
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
                inMaintenance: !printer.inMaintenance,
                reviewedRowVersion: try reviewedPrinterRowVersion()
            )
            guard isViewActive else { return }
            self.printer = updated
        } catch {
            guard isViewActive else { return }
            actionError = error.localizedDescription
        }
    }

    private func reviewedPrinterRowVersion() throws -> String {
        guard let rowVersion = printer?.rowVersion, !rowVersion.isEmpty else {
            throw NetworkError.invalidResponse
        }
        return rowVersion
    }

    @discardableResult
    func refreshSnapshot() async -> Bool {
        guard let printerService,
              let request = beginSnapshotRequest(
                using: printerService,
                pollGeneration: nil,
                publishesLoading: true
              ) else {
            return false
        }
        let taskTracker = snapshotTaskTracker
        taskTracker.taskStarted()
        defer { taskTracker.taskFinished() }
        let result = await Self.fetchSnapshot(
            service: printerService,
            printerId: printerId
        )
        return completeSnapshotRequest(result, authority: request) ?? false
    }

    func startSnapshotPollingIfNeeded() {
        guard isViewActive, isSnapshotPollingAllowed, shouldPollSnapshot else {
            stopSnapshotPolling()
            return
        }
        guard snapshotPollingTask == nil else { return }
        snapshotPollingGeneration &+= 1
        let generation = snapshotPollingGeneration
        let pollInterval = snapshotPollInterval
        let backoffBase = snapshotErrorBackoffBaseSeconds
        let backoffMaximum = snapshotErrorBackoffMaxSeconds
        snapshotPollingTask = Task { [weak self] in
            var consecutiveFailures = 0
            while !Task.isCancelled {
                guard let service = self?.printerService,
                      let request = self?.beginSnapshotRequest(
                        using: service,
                        pollGeneration: generation,
                        publishesLoading: true
                      ),
                      let taskTracker = self?.snapshotTaskTracker else {
                    break
                }
                taskTracker.taskStarted()
                let result = await Self.fetchSnapshot(
                    service: service,
                    printerId: request.printerId
                )
                taskTracker.taskFinished()
                guard let succeeded = self?.completeSnapshotRequest(
                    result,
                    authority: request
                ) else {
                    break
                }
                consecutiveFailures = succeeded ? 0 : min(consecutiveFailures + 1, 6)
                let delay = succeeded
                    ? pollInterval
                    : Self.snapshotBackoffDuration(
                        afterFailures: consecutiveFailures,
                        baseSeconds: backoffBase,
                        maximumSeconds: backoffMaximum
                    )
                do {
                    try await Task.sleep(for: delay)
                } catch {
                    break
                }
            }
            self?.finishSnapshotPolling(generation: generation)
        }
    }

    func stopSnapshotPolling() {
        snapshotPollingGeneration &+= 1
        snapshotRequestToken = nil
        snapshotPollingTask?.cancel()
        snapshotPollingTask = nil
        isLoadingSnapshot = false
    }

    func setSnapshotPollingAllowed(_ allowed: Bool) {
        isSnapshotPollingAllowed = allowed
        if allowed {
            startSnapshotPollingIfNeeded()
        } else {
            stopSnapshotPolling()
        }
    }

    private func finishSnapshotPolling(generation: UInt64) {
        if snapshotPollingGeneration == generation {
            snapshotPollingTask = nil
        }
    }

    private func invalidateSnapshotLifecycle() {
        snapshotLifecycleEpoch &+= 1
        stopSnapshotPolling()
    }

    private func beginSnapshotRequest(
        using service: any PrinterServiceProtocol,
        pollGeneration: UInt64?,
        publishesLoading: Bool
    ) -> SnapshotRequestAuthority? {
        guard isViewActive,
              Self.identity(printerService) == Self.identity(service) else {
            return nil
        }
        if let pollGeneration {
            guard snapshotPollingGeneration == pollGeneration,
                  snapshotPollingTask != nil,
                  shouldPollSnapshot else {
                return nil
            }
        }
        let token = UUID()
        snapshotRequestToken = token
        if publishesLoading {
            isLoadingSnapshot = true
        }
        return SnapshotRequestAuthority(
            token: token,
            lifecycleEpoch: snapshotLifecycleEpoch,
            printerId: printerId,
            printerServiceIdentity: Self.identity(service),
            pollGeneration: pollGeneration
        )
    }

    private func completeSnapshotRequest(
        _ result: Result<Data, Error>,
        authority: SnapshotRequestAuthority
    ) -> Bool? {
        guard isSnapshotRequestCurrent(authority), !Task.isCancelled else {
            return nil
        }
        isLoadingSnapshot = false
        switch result {
        case .success(let data):
            snapshotData = data
#if DEBUG
            snapshotPublicationsForTesting.append(data)
#endif
            return true
        case .failure(let error):
            logger.warning("Failed to refresh snapshot: \(error.localizedDescription)")
            return false
        }
    }

    private func isSnapshotRequestCurrent(
        _ authority: SnapshotRequestAuthority
    ) -> Bool {
        isViewActive
            && snapshotLifecycleEpoch == authority.lifecycleEpoch
            && snapshotRequestToken == authority.token
            && printerId == authority.printerId
            && Self.identity(printerService) == authority.printerServiceIdentity
            && authority.pollGeneration.map { snapshotPollingGeneration == $0 } != false
    }

    nonisolated private static func fetchSnapshot(
        service: any PrinterServiceProtocol,
        printerId: UUID
    ) async -> Result<Data, Error> {
        do {
            return .success(try await service.getSnapshot(id: printerId))
        } catch {
            return .failure(error)
        }
    }

    nonisolated private static func snapshotBackoffDuration(
        afterFailures failures: Int,
        baseSeconds: Int,
        maximumSeconds: Int
    ) -> Duration {
        let exponent = max(0, min(failures - 1, 4))
        let seconds = min(maximumSeconds, baseSeconds * (1 << exponent))
        return .seconds(seconds)
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
        isActivelyPrinting && cameraPreviewMode == .mjpegStream
    }

    var cameraPreviewMode: CameraPreviewMode {
        guard let printer else { return .none }
        return Self.cameraPreviewMode(for: printer)
    }

    nonisolated private static func cameraPreviewMode(for printer: Printer) -> CameraPreviewMode {
        switch printer.cameraAccessMode {
        case .snapshotOnly:
            if printer.cameraSnapshotStrategy == .snapmakerU1MonitorJpeg {
                return .snapshotPolling
            }
            if hasDirectSnapshot(printer) {
                return .directSnapshot
            }
            return .snapshotPolling
        case .streamOnly:
            if hasUsableMjpegStream(printer) { return .mjpegStream }
            return snapshotFallbackMode(for: printer) ?? .unsupported
        case .streamAndSnapshot:
            if hasUsableMjpegStream(printer) { return .mjpegStream }
            return snapshotFallbackMode(for: printer) ?? .unsupported
        case .unsupportedStream:
            return snapshotFallbackMode(for: printer) ?? .unsupported
        case .unknown:
            if hasUsableMjpegStream(printer) { return .mjpegStream }
            return snapshotFallbackMode(for: printer) ?? .none
        }
    }

    var shouldPollSnapshot: Bool {
        cameraPreviewMode == .snapshotPolling
    }

    var isSnapshotPollingActive: Bool {
        snapshotPollingTask != nil
    }

    private var shouldLoadInitialSnapshot: Bool {
        switch cameraPreviewMode {
        case .snapshotPolling:
            return true
        case .directSnapshot:
            return snapshotData == nil
        case .mjpegStream, .none, .unsupported:
            return false
        }
    }

    nonisolated private static func hasUsableMjpegStream(_ printer: Printer) -> Bool {
        guard printer.cameraStreamUrl != nil else { return false }
        return printer.cameraStreamFormat == .mjpeg || printer.cameraStreamFormat == .unknown
    }

    nonisolated private static func snapshotFallbackMode(for printer: Printer) -> CameraPreviewMode? {
        if printer.cameraSnapshotStrategy == .snapmakerU1MonitorJpeg {
            return .snapshotPolling
        }
        if hasDirectSnapshot(printer) {
            return .directSnapshot
        }
        return nil
    }

    nonisolated private static func hasDirectSnapshot(_ printer: Printer) -> Bool {
        guard let snapshotUrl = printer.cameraSnapshotUrl else { return false }
        return !snapshotUrl.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
    }

    nonisolated private static func isActivelyPrinting(_ printer: Printer) -> Bool {
        guard let state = printer.state?.lowercased() else { return false }
        return ["printing", "starting", "paused"].contains(state)
    }

    var isIdle: Bool {
        guard let state = printer?.state?.lowercased() else { return false }
        return ["ready", "idle", "operational"].contains(state)
    }

    var isOnline: Bool {
        printer?.isOnline ?? false
    }

    // MARK: - F7 Computed State (issue #712)

    /// Remaining print seconds surfaced additively from the backend
    /// `printTimeLeftSeconds`. Never recomputed on-device — nil unless the
    /// printer is actively printing and the backend supplied a positive value.
    var currentJobRemainingSeconds: Double? {
        guard isActivelyPrinting,
              let seconds = statusDetail?.printTimeLeftSeconds,
              seconds > 0 else { return nil }
        return seconds
    }

    /// Absolute estimated completion instant (now + remaining). Uses the
    /// injected clock so tests are deterministic.
    var currentJobEtaDate: Date? {
        guard let seconds = currentJobRemainingSeconds else { return nil }
        return nowProvider().addingTimeInterval(seconds)
    }

    /// Human "time remaining" string (e.g. "1h 12m"). Deterministic — depends
    /// only on the remaining seconds, not wall-clock time.
    var formattedTimeRemaining: String? {
        guard let seconds = currentJobRemainingSeconds else { return nil }
        return Self.remainingFormatter.string(from: seconds)
    }

    /// Absolute completion clock time (e.g. "3:45 PM").
    var formattedEtaClock: String? {
        guard let eta = currentJobEtaDate else { return nil }
        return eta.formatted(date: .omitted, time: .shortened)
    }

    /// Thumbnail for the running job, if the model file carries one; falls
    /// back to the printer thumbnail, and is omitted gracefully when absent.
    var currentJobThumbnailUrl: String? {
        let candidates = [currentJob?.thumbnailUrl, printer?.thumbnailUrl]
        return candidates
            .compactMap { $0 }
            .first { !$0.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty }
    }

    /// The operator queue section shows at most three assigned jobs.
    var nextQueuedJobs: [QueuedPrintJobResponse] {
        Array(assignedQueue.prefix(3))
    }

    /// Per-job compatibility verdict derived read-only from the loaded
    /// toolheads vs the job's required material/nozzle. No per-tool candidate
    /// DTO exists; this is advisory only and never blocks dispatch.
    enum QueueMatchState: String {
        case match
        case mismatch
        case unknown

        var label: String {
            switch self {
            case .match: "Loaded filament matches"
            case .mismatch: "Filament mismatch"
            case .unknown: "Match unknown"
            }
        }

        var systemImage: String {
            switch self {
            case .match: "checkmark.circle"
            case .mismatch: "exclamationmark.triangle"
            case .unknown: "questionmark.circle"
            }
        }
    }

    func matchState(for job: QueuedPrintJobResponse) -> QueueMatchState {
        guard let required = job.gcodeFile?.materialType?.trimmingCharacters(in: .whitespacesAndNewlines),
              !required.isEmpty else { return .unknown }
        let loaded = toolheads.compactMap {
            $0.currentMaterial?.trimmingCharacters(in: .whitespacesAndNewlines).lowercased()
        }.filter { !$0.isEmpty }
        guard !loaded.isEmpty else { return .unknown }
        return loaded.contains(required.lowercased()) ? .match : .mismatch
    }

    /// Maintenance odometer rows: current hours vs the derived threshold, with
    /// due/overdue state announced as text.
    struct OdometerRow: Identifiable, Equatable {
        let id: String
        let title: String
        let component: String?
        let taskId: UUID?
        let currentHours: Double
        let thresholdHours: Double?
        let hoursUntilDue: Double?
        let isOverdue: Bool
        let isDueToday: Bool

        var isDue: Bool {
            isOverdue || isDueToday || (hoursUntilDue.map { $0 <= 0 } ?? false)
        }

        /// State announced without relying on color alone.
        var stateLabel: String {
            if isOverdue { return "Overdue" }
            if isDueToday { return "Due today" }
            if let hours = hoursUntilDue {
                if hours <= 0 { return "Due now" }
                return "In \(Int(hours.rounded())) h"
            }
            return "On schedule"
        }
    }

    var odometerRows: [OdometerRow] {
        let currentHours = printerStatistics?.totalPrintHours ?? 0
        return upcomingMaintenance
            .sorted { lhs, rhs in
                if lhs.isOverdue != rhs.isOverdue { return lhs.isOverdue }
                return (lhs.hoursUntilDue ?? .greatestFiniteMagnitude) < (rhs.hoursUntilDue ?? .greatestFiniteMagnitude)
            }
            .map { task in
                let threshold = task.hoursUntilDue.map { currentHours + $0 }
                return OdometerRow(
                    id: task.id,
                    title: task.taskName,
                    component: task.component,
                    taskId: task.taskId,
                    currentHours: currentHours,
                    thresholdHours: threshold,
                    hoursUntilDue: task.hoursUntilDue,
                    isOverdue: task.isOverdue,
                    isDueToday: task.isDueToday
                )
            }
    }

    /// Last five completed/terminal jobs for the history tail.
    var historyTail: [PrinterHistoryJob] {
        Array(history.prefix(5))
    }

    /// Deep link to the printer's native web UI (Mainsail/Fluidd/etc.), shown
    /// only when the backend URL is present and parseable.
    var mainsailUrl: URL? {
        guard let raw = printer?.backendUrl?.trimmingCharacters(in: .whitespacesAndNewlines),
              !raw.isEmpty,
              let url = URL(string: raw),
              url.scheme?.hasPrefix("http") == true else { return nil }
        return url
    }

    private static let remainingFormatter: DateComponentsFormatter = {
        let formatter = DateComponentsFormatter()
        formatter.allowedUnits = [.hour, .minute]
        formatter.unitsStyle = .abbreviated
        formatter.zeroFormattingBehavior = .dropLeading
        return formatter
    }()

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
