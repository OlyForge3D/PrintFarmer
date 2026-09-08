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
    /// Whether any sibling action (bind/set/clear/eject spool,
    /// pause/resume/cancel/stop/emergencyStop, mark-ready, maintenance log)
    /// currently has an in-flight, uncancellable network request. Derived
    /// from `activeActionTokens` (issue #2522, Hicks review finding — same-
    /// service/same-epoch operation overlap), not a bare settable flag:
    /// without a per-operation identity, two GENUINELY CONCURRENT actions
    /// sharing the same service and lifecycle epoch (e.g. Emergency Stop
    /// dispatched while an ordinary filament action is still in flight — no
    /// deactivate/reconfigure between them at all, so `ActionAuthority`'s
    /// epoch/service-identity pair alone is IDENTICAL for both) are
    /// indistinguishable to a shared boolean, so whichever finishes FIRST
    /// would incorrectly clear it while the other is still genuinely
    /// running. Busy stays `true` until every token any in-flight operation
    /// is holding has been released, matching a true reference count rather
    /// than "whichever operation happens to complete first says so."
    var isPerformingAction: Bool { !activeActionTokens.isEmpty }
    /// Tokens for operations currently holding a share of `isPerformingAction`.
    /// Each sibling action inserts its own via `beginBusyToken()` and
    /// removes ONLY that token via `endBusyToken(_:)` when it completes,
    /// regardless of whether its own result/refresh authority
    /// (`hasActionAuthority`) still holds — removing your own token can
    /// never affect a sibling operation's separate token.
    private var activeActionTokens: Set<UUID> = []
    /// Which run-action kinds (pause/resume/cancel/stop/emergencyStop) are
    /// CURRENTLY dispatched and awaiting a result (issue #2522, Vasquez
    /// review finding: `PrinterDetailRunActionMapping.presentation` never
    /// populated `isPending` on any descriptor, so `PrinterRunActionBar`'s
    /// own re-entrant-tap guard — which keys off `isPending`, not
    /// `isEnabled` — never actually engaged, and VoiceOver never announced
    /// a "Pending" value/hint on the button genuinely in flight). Threaded
    /// into `PrinterDetailView.runActionPresentation(for:)` so the mapping
    /// can mark exactly the in-flight kind(s) as pending, not merely
    /// disabled.
    var pendingRunActionKinds: Set<PrinterRunActionKind> = []
    var showConfirmation = false
    var pendingAction: DestructiveAction?
    /// Combined, user-facing error text (issue #2522, Hicks review finding:
    /// a single shared `actionError` string let two concurrent sibling
    /// actions — e.g. a failing Emergency Stop and a concurrently in-flight
    /// eject — silently clobber each other's message depending on
    /// completion order, discarding whichever one finished FIRST regardless
    /// of which mattered more). Backed by `operationErrorMessages`/
    /// `operationErrorOrder`, keyed by a STABLE per-method
    /// `OperationErrorSource` (not the random per-invocation busy token): a
    /// FRESH invocation of the SAME method still replaces its OWN prior
    /// stale error (matching the original "clear at start" intent), while a
    /// DIFFERENT concurrently in-flight method's error is never touched.
    /// Every currently-recorded message is combined for display, so none is
    /// ever silently lost regardless of which operation completes first.
    var actionError: String? {
        get {
            let messages = operationErrorOrder.compactMap { operationErrorMessages[$0] }
            return messages.isEmpty ? nil : messages.joined(separator: "\n\n")
        }
        set {
            if let newValue {
                setOperationError(newValue, source: .untracked)
            } else {
                // Dismissing the alert (`Button("OK") { viewModel.actionError
                // = nil }`) acknowledges every currently-displayed message
                // at once.
                operationErrorMessages.removeAll()
                operationErrorOrder.removeAll()
            }
        }
    }
    /// Identifies which sibling action produced a given `actionError`
    /// entry. One case per METHOD (not per random per-invocation token), so
    /// a fresh call to the SAME method still clears/replaces only its own
    /// prior message.
    private enum OperationErrorSource: Hashable {
        case bindToolheadSpool
        case prepareReadyConfirmation
        case markPrinterReady
        case loadSpoolById
        case ejectFilament
        case clearActiveSpoolAssignment
        case setActiveSpool
        case logMaintenanceCompletion
        case toggleMaintenance
        case runAction(PrinterRunActionKind)
        /// Any caller that assigns `actionError = "..."` directly rather
        /// than through one of the named sources above (there are none
        /// left in this file after this change, but external/preview code
        /// could still do so through the property's public setter).
        case untracked
    }
    /// Issue #2522, Hicks review finding: these two must NOT be
    /// `@ObservationIgnored`. `actionError`'s computed getter reads them
    /// directly, and SwiftUI's `.alert(isPresented: .constant(viewModel
    /// .actionError != nil))` depends on Observation tracking that read.
    /// Several call sites — `prepareReadyConfirmation()`,
    /// `markPrinterReady()`'s early-return guard, and `toggleMaintenance()`
    /// — mutate ONLY these two properties on their error path (no
    /// `activeActionTokens`/busy-token change happens alongside them, which
    /// is what masked this on every OTHER sibling action's error path: that
    /// property IS tracked, so its own mutation incidentally forced the
    /// same view body to re-evaluate and pick up the new `actionError`
    /// value too). Marking these `@ObservationIgnored` (an earlier revision
    /// did, copying the pattern from the OTHER, genuinely-internal epoch
    /// counters in this file that are never read by a UI-facing computed
    /// property) meant those three paths' alerts could silently fail to
    /// redraw at all.
    private var operationErrorMessages: [OperationErrorSource: String] = [:]
    private var operationErrorOrder: [OperationErrorSource] = []

    /// Records (or replaces) the error message for one sibling action.
    private func setOperationError(_ message: String, source: OperationErrorSource) {
        if operationErrorMessages[source] == nil {
            operationErrorOrder.append(source)
        }
        operationErrorMessages[source] = message
    }

    /// Clears ONLY this source's own message, leaving every other
    /// concurrently-recorded error untouched.
    private func clearOperationError(source: OperationErrorSource) {
        operationErrorMessages.removeValue(forKey: source)
        operationErrorOrder.removeAll { $0 == source }
    }
    var isViewActive = true {
        didSet {
            // Monotonic action-lifecycle epoch (issue #2522, Hicks review
            // finding 24): bumped on EVERY activation-state transition, not
            // only deactivation. A boolean-only `isViewActive` check has an
            // ABA gap — an in-flight, uncancellable mutation (e.g.
            // `bindToolheadSpool`) captures `isViewActive == true`, the view
            // deactivates then REACTIVATES before the mutation's network
            // await resolves, and a check against the CURRENT `isViewActive`
            // alone would see `true` again and wrongly conclude its
            // authority is still current — even though a full
            // deactivate/reactivate cycle (a materially different session)
            // occurred in between. `actionLifecycleEpoch` closes this: it is
            // captured alongside `isViewActive` at the START of such a
            // mutation and compared again afterward, and once bumped it
            // never returns to its old value, so the ABA sequence is always
            // detected regardless of what `isViewActive` reads at the
            // moment the check runs.
            //
            // This transition deliberately does NOT touch
            // `activeActionTokens` (an earlier revision force-cleared the
            // busy flag here, which is exactly the shared-flag hazard the
            // token set now replaces): a token genuinely in flight when the
            // view deactivates is released by ITS OWN eventual completion,
            // not by this transition, so `isPerformingAction` correctly
            // stays `true` for as long as that underlying network request
            // is genuinely still outstanding — including across a
            // REACTIVATION, rather than being reset early only to have a
            // later, unrelated operation's completion (mis)read as owning
            // whatever the flag was reset to.
            guard oldValue != isViewActive else { return }
            actionLifecycleEpoch &+= 1
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
    /// Issue #2522, Hicks review finding 24 — see `isViewActive`'s `didSet`
    /// and `configure(printerService:)` for where this is bumped, and
    /// `bindToolheadSpool` for the authority check that consumes it.
    @ObservationIgnored private var actionLifecycleEpoch: UInt64 = 0
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
            // Issue #2522, Hicks review finding 24: a service replacement is
            // its own kind of lifecycle transition for the sibling action
            // authority below — an in-flight action against the OLD service
            // must not treat this reconfigured session as still its own.
            // Busy-state tracking itself (`activeActionTokens`) is
            // deliberately untouched here — see `isViewActive`'s `didSet`.
            actionLifecycleEpoch &+= 1
        }

        self.printerService = printerService
    }

    /// Guided-swap toolhead bind (issue #2522, Hicks review findings 20 and
    /// 24, Bishop review finding 25 — see `ActionAuthority` above for the
    /// shared implementation every sibling `printerService` action uses).
    ///
    /// Dispatched from an unstructured `.sheet` completion closure in
    /// `PrinterDetailView`, not a `.task` the view's `onDisappear` can rely
    /// on cancelling promptly — `activeTasks.forEach { $0.cancel() }` is
    /// cooperative and this method has an uncancellable network await in
    /// the middle of it.
    func bindToolheadSpool(_ spool: SpoolmanSpool, at toolheadIndex: Int) async {
        guard isViewActive else { return }
        guard let printerService else {
            setOperationError("Printer service not available.", source: .bindToolheadSpool)
            return
        }
        let authority = beginActionAuthority(for: printerService)
        clearOperationError(source: .bindToolheadSpool)
        defer { endBusyToken(authority.busyToken) }
        do {
            _ = try await printerService.bindToolheadSpool(
                printerId: printerId,
                toolheadIndex: toolheadIndex,
                request: ToolheadSpoolBindRequest(spoolId: spool.id),
                idempotencyKey: UUID().uuidString
            )
            guard hasActionAuthority(authority) else { return }
            await loadPrinter()
        } catch {
            guard hasActionAuthority(authority) else { return }
            setOperationError(error.localizedDescription, source: .bindToolheadSpool)
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
            setOperationError(error.localizedDescription, source: .prepareReadyConfirmation)
        }
    }

    func markPrinterReady() async {
        guard isViewActive else { return }
        guard let autoDispatchService, let reviewedReadyStatus else {
            setOperationError("Refresh the auto-dispatch status before confirming.", source: .markPrinterReady)
            return
        }
        self.reviewedReadyStatus = nil
        let busyToken = beginBusyToken()
        clearOperationError(source: .markPrinterReady)
        defer { endBusyToken(busyToken) }
        do {
            _ = try await autoDispatchService.markReady(
                status: reviewedReadyStatus
            )
            guard isViewActive else { return }
            await loadPrinter()
        } catch {
            guard isViewActive else { return }
            setOperationError(error.localizedDescription, source: .markPrinterReady)
        }
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
        let authority = beginActionAuthority(for: printerService)
        clearOperationError(source: .loadSpoolById)
        defer { endBusyToken(authority.busyToken) }
        do {
            let rowVersion = try reviewedPrinterRowVersion()
            print("📡 loadSpoolById: printer=\(printerId) spool=\(id)")
            _ = try await printerService.setActiveSpool(
                printerId: printerId,
                spoolId: id,
                reviewedRowVersion: rowVersion
            )
            guard hasActionAuthority(authority) else { return }
            print("✅ loadSpoolById: success")
            lastSetSpoolInfo = PrinterSpoolInfo(
                hasActiveSpool: true,
                activeSpoolId: id
            )
            await loadPrinter()
        } catch {
            guard hasActionAuthority(authority) else { return }
            print("❌ loadSpoolById failed: \(error)")
            setOperationError(error.localizedDescription, source: .loadSpoolById)
        }
    }

    /// Physical eject: clears the active-spool assignment, then physically
    /// unloads the filament — two SEPARATE network legs against
    /// `printerService`.
    ///
    /// Safety (Bishop review finding): once the FIRST leg
    /// (`setActiveSpool(spoolId: nil, ...)`) succeeds, the assignment is
    /// ALREADY cleared server-side. Gating the SECOND leg
    /// (`unloadFilament`) behind this call's RESULT/REFRESH authority — as
    /// an earlier revision did — could silently skip the physical unload
    /// if that authority was lost between the two legs (a deactivate/
    /// reactivate ABA cycle, or a service reconfigure, mid-flight),
    /// leaving a printer that is STILL PHYSICALLY LOADED with no assignment
    /// recorded for it — a materially worse, silent state than either leg
    /// failing outright. The physical unload therefore always dispatches
    /// against the CAPTURED `printerService` local (stable regardless of
    /// any later `configure(printerService:)` reassigning `self
    /// .printerService`), never gated by `hasActionAuthority`, and any
    /// failure of that leg is surfaced explicitly rather than swallowed —
    /// this call must never silently return once the assignment has
    /// already been cleared.
    func ejectFilament() async {
        guard isViewActive else { return }
        guard let printerService else { return }
        let authority = beginActionAuthority(for: printerService)
        let capturedEmergencyStopEpoch = emergencyStopEngagedEpoch
        clearOperationError(source: .ejectFilament)
        defer { endBusyToken(authority.busyToken) }
        do {
            _ = try await printerService.setActiveSpool(
                printerId: printerId,
                spoolId: nil,
                reviewedRowVersion: try reviewedPrinterRowVersion()
            )
            // Confirmed-cleared local override (issue #2522, Bishop review
            // finding — parity with `clearActiveSpoolAssignment()`'s own
            // Hicks review finding 14 fix). The assignment is ALREADY
            // cleared server-side the moment the call above succeeds, so
            // the local snapshot must be reconciled RIGHT HERE — before any
            // of the early returns below (Emergency Stop preemption, an
            // unload failure) and before `loadPrinter()`, which can itself
            // fail without touching `printer` at all. Without this, any of
            // those paths left the UI showing an assigned spool the server
            // no longer has recorded, exactly the same stale-resurrection
            // hazard finding 14 already fixed for the assignment-only
            // clear path.
            //
            // GATED on `hasActionAuthority` (a later Hicks review finding):
            // if `configure(printerService:)` hot-swapped mid-flight, a
            // NEWER session may already have established its OWN
            // `printer.spoolInfo` truth (e.g. via its own `loadPrinter()`
            // or a live SignalR update) between this leg's success and this
            // point. A retired eject targeting the OLD service must not
            // overwrite that with stale "cleared" state — this is a purely
            // LOCAL-STATE concern, deliberately separate from the physical
            // unload leg below, which still dispatches UNCONDITIONALLY
            // regardless of authority (Bishop review finding, separately):
            // the physical operation must always reach the printer once
            // the assignment is already cleared, but the LOCAL UI model
            // must only be touched by the operation that still owns the
            // current session.
            if hasActionAuthority(authority) {
                lastSetSpoolInfo = nil
                if var updatedPrinter = printer, updatedPrinter.spoolInfo?.hasActiveSpool == true {
                    updatedPrinter.spoolInfo = PrinterSpoolInfo(hasActiveSpool: false)
                    printer = updatedPrinter
                }
            }
            // Safety precedence (issue #2522, Vasquez review finding —
            // CRITICAL): Emergency Stop is an intentional safety override
            // and must be able to preempt this already-in-flight eject
            // BEFORE it sends the physical unload command — continuing to
            // command motor movement after an Emergency Stop has been
            // engaged is exactly the hazard that override exists to
            // prevent. This is deliberately a SEPARATE check from
            // `hasActionAuthority` below: ordinary lifecycle noise (view
            // deactivate/reactivate, a service reconfigure) must NOT skip
            // the physical unload (Bishop review finding — a real, distinct
            // hazard of its own), but Emergency Stop specifically must.
            guard !hasEmergencyStopEngagedSince(capturedEmergencyStopEpoch) else {
                setOperationError(
                    "Spool assignment cleared, but the physical unload was not sent because Emergency Stop was engaged.",
                    source: .ejectFilament
                )
                return
            }
            do {
                _ = try await printerService.unloadFilament(printerId: printerId)
            } catch {
                // The assignment clear above already succeeded. The
                // operator must know the printer may still be physically
                // loaded despite that, regardless of whether this call's
                // own result/refresh authority has since been lost.
                setOperationError(
                    "Spool assignment cleared, but the physical unload failed: \(error.localizedDescription)",
                    source: .ejectFilament
                )
                return
            }
            guard hasActionAuthority(authority) else { return }
            await loadPrinter()
        } catch {
            guard hasActionAuthority(authority) else { return }
            setOperationError(error.localizedDescription, source: .ejectFilament)
        }
    }

    /// Assignment-only clear (issue #2522 / #2519 integration contract).
    ///
    /// Unlike `ejectFilament()`, this clears ONLY the active-spool
    /// assignment via `setActiveSpool(spoolId: nil, ...)` and deliberately
    /// never dispatches the physical `unloadFilament()` POST. #2519's
    /// `PrinterFilamentAction.Kind.clearAssignment` — visible copy "Clear
    /// spool assignment" — is assignment-only by contract; it must never
    /// alias the combined eject flow, which stays reachable separately
    /// (accurately labeled "Eject Filament") for the physical operation.
    func clearActiveSpoolAssignment() async {
        guard isViewActive else { return }
        guard let printerService else { return }
        let authority = beginActionAuthority(for: printerService)
        clearOperationError(source: .clearActiveSpoolAssignment)
        defer { endBusyToken(authority.busyToken) }
        do {
            _ = try await printerService.setActiveSpool(
                printerId: printerId,
                spoolId: nil,
                reviewedRowVersion: try reviewedPrinterRowVersion()
            )
            guard hasActionAuthority(authority) else { return }
            lastSetSpoolInfo = nil
            // Confirmed-cleared local override (issue #2522, Hicks review
            // finding 14): `loadPrinter()` below can itself fail (network
            // hiccup) without touching `printer` at all, leaving the
            // PRE-clear snapshot's `spoolInfo.hasActiveSpool == true` in
            // place. `effectiveSpoolInfo` prioritizes `printer?.spoolInfo`
            // over `lastSetSpoolInfo` whenever it reports an active spool,
            // so clearing only `lastSetSpoolInfo` above cannot by itself
            // prevent a failed refresh from resurrecting the very
            // assignment the server just confirmed cleared. Mutate the
            // retained snapshot directly so the clear survives a refresh
            // failure; a SUCCESSFUL `loadPrinter()` below still overwrites
            // this with the server's own current truth.
            if var updatedPrinter = printer, updatedPrinter.spoolInfo?.hasActiveSpool == true {
                updatedPrinter.spoolInfo = PrinterSpoolInfo(hasActiveSpool: false)
                printer = updatedPrinter
            }
            await loadPrinter()
        } catch {
            guard hasActionAuthority(authority) else { return }
            setOperationError(error.localizedDescription, source: .clearActiveSpoolAssignment)
        }
    }

    func setActiveSpool(_ spool: SpoolmanSpool) async {
        guard isViewActive else { return }
        showSpoolPicker = false
        guard let printerService else {
            print("⚠️ setActiveSpool: printerService is nil")
            return
        }
        let authority = beginActionAuthority(for: printerService)
        clearOperationError(source: .setActiveSpool)
        defer { endBusyToken(authority.busyToken) }
        do {
            let rowVersion = try reviewedPrinterRowVersion()
            print("📡 setActiveSpool: printer=\(printerId) spool=\(spool.id)")
            _ = try await printerService.setActiveSpool(
                printerId: printerId,
                spoolId: spool.id,
                reviewedRowVersion: rowVersion
            )
            guard hasActionAuthority(authority) else { return }
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
            guard hasActionAuthority(authority) else { return }
            print("❌ setActiveSpool failed: \(error)")
            setOperationError(error.localizedDescription, source: .setActiveSpool)
        }
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

    // MARK: - Action authority (issue #2522, Hicks/Bishop review findings
    // 24/25, generalized to every sibling action, plus a subsequent Hicks
    // review finding: per-operation busy identity)
    //
    // Every action-dispatching method below shares THREE risks
    // `bindToolheadSpool` was hardened against, one at a time:
    //
    //   * ABA (finding 24): a boolean `isViewActive` check alone cannot
    //     distinguish "this call's original activation window is still
    //     current" from "the view deactivated and REACTIVATED — or
    //     `configure(printerService:)` ran again with an instance that
    //     happens to compare identical — before this call's uncancellable
    //     network await resolved". `actionLifecycleEpoch` (bumped on every
    //     activation-state transition and every service reconfiguration,
    //     and never returning to an old value) closes this for any caller
    //     that captures it before an await and compares it again after.
    //   * Operation-owned busy-state cleanup (finding 25): cleanup must run
    //     via `defer`, not a tail line a future added return path could
    //     bypass.
    //   * Per-operation busy identity (a later Hicks review round): epoch
    //     and service identity alone are IDENTICAL for two operations that
    //     are GENUINELY CONCURRENT against the same service with no
    //     lifecycle transition between them at all — e.g. Emergency Stop
    //     dispatched while an ordinary filament action is still in flight.
    //     A shared boolean busy flag cannot tell those two apart, so
    //     whichever finishes FIRST would incorrectly clear it while the
    //     other is still genuinely running. `activeActionTokens` (see
    //     `isPerformingAction` above) replaces the boolean with a
    //     reference count: each call gets its OWN token via
    //     `beginBusyToken()`, and releases ONLY that token via
    //     `endBusyToken(_:)` — UNCONDITIONALLY, regardless of
    //     `hasActionAuthority`, since removing your own entry from a set
    //     can never affect a sibling operation's separate entry.
    //
    // `ActionAuthority` and the functions below are the single, shared
    // implementation of that pattern so every sibling action states it
    // identically rather than re-deriving its own copy.
    private struct ActionAuthority {
        let busyToken: UUID
        let epoch: UInt64
        let serviceIdentity: ObjectIdentifier?
    }

    /// Begins tracking a new in-flight operation and captures the CURRENT
    /// action authority. Call once, before the first `await` a dispatching
    /// method performs; release the returned token via `endBusyToken(_:)`
    /// (typically in a `defer`) when the method exits, regardless of
    /// outcome.
    private func beginActionAuthority(for printerService: any PrinterServiceProtocol) -> ActionAuthority {
        ActionAuthority(
            busyToken: beginBusyToken(),
            epoch: actionLifecycleEpoch,
            serviceIdentity: Self.identity(printerService)
        )
    }

    /// Whether the CURRENT lifecycle state still matches a previously
    /// captured authority — i.e. whether the operation that captured it
    /// still owns the current session. This is RESULT/REFRESH authority
    /// only (whether `loadPrinter()`/`actionError` may still be applied);
    /// it does NOT gate busy-state cleanup — see `endBusyToken(_:)`.
    private func hasActionAuthority(_ authority: ActionAuthority) -> Bool {
        isViewActive
            && actionLifecycleEpoch == authority.epoch
            && Self.identity(printerService) == authority.serviceIdentity
    }

    /// Begins tracking one more in-flight operation for `isPerformingAction`
    /// purposes and returns the token this call must release when done.
    /// Every sibling action — including ones that dispatch through a
    /// service OTHER than `printerService` (`markPrinterReady`,
    /// `logMaintenanceCompletion`) — shares this SAME reference count, so
    /// an Emergency Stop and an unrelated mark-ready/maintenance-log call
    /// overlapping in flight are equally protected from clobbering each
    /// other's busy state.
    private func beginBusyToken() -> UUID {
        let token = UUID()
        activeActionTokens.insert(token)
        return token
    }

    /// Releases exactly one token. Safe to call unconditionally — removing
    /// your own token from the set can never affect a DIFFERENT token a
    /// sibling operation is still holding, so no authority check is needed
    /// here (contrast `hasActionAuthority`, which gates a different
    /// concern).
    private func endBusyToken(_ token: UUID) {
        activeActionTokens.remove(token)
    }

    // MARK: - Emergency Stop safety precedence (issue #2522, Vasquez review
    // finding — CRITICAL)
    //
    // Distinct from `actionLifecycleEpoch`: that one is about SESSION
    // identity (view lifecycle transitions / service reconfiguration) and
    // gates RESULT/REFRESH authority for a retired session, but must NOT by
    // itself prevent an already-in-flight multi-step physical mutation
    // (`ejectFilament`'s second, physical-unload leg) from completing —
    // Bishop's review separately established that silently skipping a
    // physical unload after its assignment-clear leg already succeeded is
    // its own real safety hazard. Emergency Stop is different: it is an
    // INTENTIONAL safety override that MUST be able to preempt any other
    // in-flight action's continuation to its next physical step, so it
    // gets its own, dedicated, monotonic signal rather than overloading
    // the general lifecycle epoch (which would conflate "ordinary
    // lifecycle noise" with "Emergency Stop was just engaged" and make the
    // two findings impossible to satisfy simultaneously with one epoch).
    @ObservationIgnored private var emergencyStopEngagedEpoch: UInt64 = 0

    /// Bumped the moment Emergency Stop is confirmed — synchronously,
    /// before its own network dispatch even starts — so any OTHER
    /// concurrently in-flight multi-step action can observe it as early as
    /// possible.
    private func engageEmergencyStopSafetyOverride() {
        emergencyStopEngagedEpoch &+= 1
    }

    /// Whether Emergency Stop has been engaged since a multi-step action
    /// captured `emergencyStopEngagedEpoch` at its own start. A multi-step
    /// action must check this before dispatching its NEXT physical step.
    private func hasEmergencyStopEngagedSince(_ capturedEpoch: UInt64) -> Bool {
        emergencyStopEngagedEpoch != capturedEpoch
    }

#if DEBUG
    /// Issue #2522, Vasquez review finding: exposes
    /// `engageEmergencyStopSafetyOverride()` for tests that need to
    /// simulate Emergency Stop firing at an EXACT point mid-flight (e.g.
    /// between `ejectFilament`'s two legs) without driving the full
    /// `requestEmergencyStop()`/`confirmAction()` round trip.
    func engageEmergencyStopSafetyOverrideForTesting() {
        engageEmergencyStopSafetyOverride()
    }

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
        let busyToken = beginBusyToken()
        clearOperationError(source: .logMaintenanceCompletion)
        defer { endBusyToken(busyToken) }
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
            setOperationError(error.localizedDescription, source: .logMaintenanceCompletion)
        }
    }

    // MARK: - Actions

    func pausePrinter() async {
        await performAction(.pause) { _ = try await $0.pause(id: self.printerId) }
    }

    func resumePrinter() async {
        await performAction(.resume) { _ = try await $0.resume(id: self.printerId) }
    }

    func stopPrinter() async {
        await performAction(.stop) { _ = try await $0.stop(id: self.printerId) }
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
            await performAction(.cancel) { _ = try await $0.cancel(id: self.printerId) }
            guard isViewActive else { return }
            #if os(iOS)
            UINotificationFeedbackGenerator().notificationOccurred(.warning)
            #endif
        case .emergencyStop:
            // Safety precedence (issue #2522, Vasquez review finding —
            // CRITICAL): engage the safety override BEFORE dispatching, so
            // any OTHER already-in-flight multi-step action (e.g.
            // `ejectFilament`) observes it as early as possible and does
            // not proceed to its next physical step.
            engageEmergencyStopSafetyOverride()
            await performAction(.emergencyStop) { _ = try await $0.emergencyStop(id: self.printerId) }
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
            setOperationError(error.localizedDescription, source: .toggleMaintenance)
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

    /// Shared dispatch for pause/resume/cancel/stop/emergencyStop (issue
    /// #2522, Hicks/Bishop review findings 24/25 — see `ActionAuthority`
    /// above; Vasquez review finding — threads `kind` into
    /// `pendingRunActionKinds` so `PrinterDetailRunActionMapping.presentation`
    /// can mark exactly the in-flight kind as `isPending`, not merely
    /// disabled).
    private func performAction(
        _ kind: PrinterRunActionKind,
        _ action: @escaping (any PrinterServiceProtocol) async throws -> Void
    ) async {
        guard isViewActive else { return }
        guard let printerService else { return }
        let authority = beginActionAuthority(for: printerService)
        clearOperationError(source: .runAction(kind))
        pendingRunActionKinds.insert(kind)
        defer {
            endBusyToken(authority.busyToken)
            pendingRunActionKinds.remove(kind)
        }

        do {
            try await action(printerService)
            guard hasActionAuthority(authority) else { return }
            await loadPrinter()
        } catch {
            guard hasActionAuthority(authority) else { return }
            setOperationError(error.localizedDescription, source: .runAction(kind))
        }
    }
}
