import Foundation

enum QueueReorderDirection: Sendable, Equatable {
    case up
    case down
}

struct QueueReorderGroup: Identifiable, Sendable {
    let id: String
    let title: String
    let jobs: [QueuedPrintJobResponse]
}

@MainActor @Observable
final class JobListViewModel {
    var jobs: [QueuedPrintJobResponse] = []
    private(set) var recentFailures: [QueueHistoryEntry] = []
    var isLoading = false
    var errorMessage: String?
    private(set) var recentFailuresError: String?
    var showRecentJobs = false
    var isViewActive = true
    private(set) var queueWriteAuthorized = false
    private(set) var isNetworkReachable = false
    private(set) var hasFreshQueueSnapshot = false
    private(set) var isReorderingQueue = false
    private(set) var rerunningFailedJobIDs: Set<UUID> = []

    private var jobService: (any JobServiceProtocol)?
    private var jobAnalyticsService: (any JobAnalyticsServiceProtocol)?
    @ObservationIgnored private var queueUpdateSubscription: SignalRSubscription?
    @ObservationIgnored private var connectionStateSubscription: SignalRSubscription?
    @ObservationIgnored private var signalRServiceIdentity: ObjectIdentifier?
    @ObservationIgnored private var lastSignalRState: SignalRConnectionState?
    @ObservationIgnored private var pathObserver: (any NetworkPathObserving)?
    @ObservationIgnored private var queueStateEpoch: UInt64 = 0
    @ObservationIgnored private var loadGeneration: UInt64 = 0
    @ObservationIgnored private var activeLoadCount = 0

    func configure(jobService: any JobServiceProtocol) {
        self.jobService = jobService
    }

    func configure(jobAnalyticsService: any JobAnalyticsServiceProtocol) {
        self.jobAnalyticsService = jobAnalyticsService
    }

    var canReorderQueue: Bool {
        queueWriteAuthorized
            && isNetworkReachable
            && hasFreshQueueSnapshot
            && !isReorderingQueue
            && isViewActive
    }

    var canRerunFailedJobs: Bool {
        queueWriteAuthorized
            && isNetworkReachable
            && isViewActive
            && !DemoMode.shared.isActive
    }

    /// Keep the native List's editing session alive while an accepted move is
    /// persisted. Mutation admission still uses canReorderQueue and fresh revisions.
    var keepsQueueEditingActive: Bool {
        isReorderingQueue || canReorderQueue
    }

    func setQueueWriteAuthorization(_ isAuthorized: Bool) {
        queueWriteAuthorized = isAuthorized
    }

    func setNetworkReachability(_ isReachable: Bool) {
        isNetworkReachable = isReachable
    }

    func startObservingNetworkPath(
        with observer: (any NetworkPathObserving)? = nil
    ) {
        guard pathObserver == nil else { return }
        let observer = observer ?? NWPathMonitorObserver()
        pathObserver = observer
        observer.start { [weak self] snapshot in
            guard let self else { return }
            self.isNetworkReachable = snapshot.reachability == .satisfied
        }
    }

    func configureSignalR(_ service: any SignalRServiceProtocol) {
        guard isViewActive else { return }
        let serviceIdentity = ObjectIdentifier(service as AnyObject)
        if signalRServiceIdentity == serviceIdentity,
           queueUpdateSubscription != nil,
           connectionStateSubscription != nil {
            return
        }

        tearDownSignalR()
        signalRServiceIdentity = serviceIdentity
        queueUpdateSubscription = service.onJobQueueUpdated { [weak self] _ in
            Task { @MainActor [weak self] in
                await self?.handleQueueInvalidation()
            }
        }

        let registration = service.onConnectionStateChanged { [weak self] state in
            Task { @MainActor [weak self] in
                guard let self else { return }
                let wasConnected = self.lastSignalRState == .connected
                self.lastSignalRState = state
                if state == .connected && !wasConnected {
                    await self.handleQueueInvalidation()
                }
            }
        }
        lastSignalRState = registration.initial
        connectionStateSubscription = registration.subscription
    }

    func activate() {
        isViewActive = true
    }

    func deactivate() {
        guard isViewActive else { return }
        isViewActive = false
        queueStateEpoch &+= 1
        loadGeneration &+= 1
        tearDownSignalR()
        pathObserver?.cancel()
        pathObserver = nil
        isNetworkReachable = false
    }

    private func tearDownSignalR() {
        queueUpdateSubscription?.cancel()
        connectionStateSubscription?.cancel()
        queueUpdateSubscription = nil
        connectionStateSubscription = nil
        signalRServiceIdentity = nil
        lastSignalRState = nil
    }

    func loadJobs() async {
        _ = await refreshQueue()
    }

    @discardableResult
    private func refreshQueue() async -> Bool {
        guard let jobService, isViewActive else { return false }
        guard let jobAnalyticsService else {
            recentFailuresError = "Recent failures are unavailable."
            return false
        }
        loadGeneration &+= 1
        let requestGeneration = loadGeneration
        let stateEpoch = queueStateEpoch
        activeLoadCount += 1
        isLoading = true
        defer {
            activeLoadCount -= 1
            isLoading = activeLoadCount > 0
        }
        errorMessage = nil

        async let queueRequest = jobService.listAllJobs()
        async let historyRequest = jobAnalyticsService.getHistory(
            limit: 5,
            offset: 0,
            sortBy: "newest",
            statuses: "failed",
            dateStart: nil,
            dateEnd: nil
        )

        let queueResult: Result<[QueuedPrintJobResponse], Error>
        do {
            queueResult = .success(try await queueRequest)
        } catch {
            queueResult = .failure(error)
        }

        let historyResult: Result<QueueHistoryPage, Error>
        do {
            historyResult = .success(try await historyRequest)
        } catch {
            historyResult = .failure(error)
        }

        guard isViewActive,
              requestGeneration == loadGeneration,
              stateEpoch == queueStateEpoch else {
            return false
        }

        var queueLoaded = false
        switch queueResult {
        case .success(let result):
            jobs = result
            hasFreshQueueSnapshot = true
            queueStateEpoch &+= 1
            queueLoaded = true
        case .failure(let error):
            hasFreshQueueSnapshot = false
            errorMessage = error.localizedDescription
        }

        switch historyResult {
        case .success(let page):
            recentFailures = page.entries.filter {
                $0.status.caseInsensitiveCompare("failed") == .orderedSame
            }
            recentFailuresError = nil
        case .failure(let error):
            recentFailures = []
            recentFailuresError = error.localizedDescription
        }

        return queueLoaded
    }

    private func handleQueueInvalidation() async {
        guard isViewActive else { return }
        queueStateEpoch &+= 1
        hasFreshQueueSnapshot = false
        _ = await refreshQueue()
    }

    func cancelJob(id: UUID) async {
        guard let jobService, isViewActive else { return }
        guard let rowVersion = reviewedRowVersion(for: id) else {
            errorMessage = "Refresh and review this job before cancelling it."
            return
        }
        do {
            try await jobService.cancel(id: id, reviewedRowVersion: rowVersion)
            await loadJobs()
        } catch {
            guard isViewActive else { return }
            await handleActionError(error)
        }
    }

    func abortJob(id: UUID) async {
        guard let jobService, isViewActive else { return }
        guard let rowVersion = reviewedRowVersion(for: id) else {
            errorMessage = "Refresh and review this job before aborting it."
            return
        }
        do {
            try await jobService.abort(id: id, reviewedRowVersion: rowVersion)
            await loadJobs()
        } catch {
            guard isViewActive else { return }
            await handleActionError(error)
        }
    }

    func dispatchJob(id: UUID) async {
        guard let jobService, isViewActive else { return }
        guard let rowVersion = reviewedRowVersion(for: id) else {
            errorMessage = "Refresh and review this job before dispatching it."
            return
        }
        do {
            let result = try await jobService.dispatch(
                id: id,
                reviewedRowVersion: rowVersion
            )
            switch result {
            case .accepted:
                await loadJobs()
            case .reconciliation(let response):
                await loadJobs()
                errorMessage =
                    response.dispatchResult?.errorDetail
                    ?? "The dispatch outcome is being reconciled. Do not dispatch again."
            case .rejected(let response):
                await loadJobs()
                errorMessage =
                    response.dispatchResult?.errorDetail
                    ?? "The printer rejected the dispatch."
            }
        } catch {
            guard isViewActive else { return }
            await handleActionError(error)
        }
    }

    func rerunFailedJob(id: UUID) async {
        guard let jobService, canRerunFailedJobs,
              !rerunningFailedJobIDs.contains(id) else { return }
        rerunningFailedJobIDs.insert(id)
        defer { rerunningFailedJobIDs.remove(id) }

        do {
            let reviewedJob = try await jobService.get(id: id)
            guard isViewActive else { return }
            guard reviewedJob.id == id else {
                await loadJobs()
                errorMessage = "The selected job changed. Review the refreshed queue before retrying."
                return
            }
            guard reviewedJob.status == .failed else {
                await loadJobs()
                errorMessage = "This job is no longer failed. Review the refreshed queue before retrying."
                return
            }
            guard let rowVersion = reviewedJob.rowVersion, !rowVersion.isEmpty else {
                await loadJobs()
                errorMessage = "The failed job revision is unavailable. Refresh and review before retrying."
                return
            }
            guard canRerunFailedJobs else {
                errorMessage = "Queue.Write access or network connectivity changed. No retry was sent."
                return
            }
            try await jobService.rerun(id: id, reviewedRowVersion: rowVersion)
            await loadJobs()
        } catch {
            guard isViewActive else { return }
            await handleActionError(error)
        }
    }

    // MARK: - Grouped Jobs

    /// Jobs actively printing, starting, or paused on a printer
    var activeJobs: [QueuedPrintJobResponse] {
        jobs.filter {
            guard let status = $0.job.jobStatus else { return false }
            return [.printing, .starting, .paused].contains(status)
        }
    }

    /// Jobs waiting in the queue (queued or assigned but not yet started)
    var queuedJobs: [QueuedPrintJobResponse] {
        jobs.filter {
            guard let status = $0.job.jobStatus else { return false }
            return [.queued, .assigned].contains(status)
        }
    }

    var reorderableQueuedJobs: [QueuedPrintJobResponse] {
        jobs.filter { $0.job.jobStatus == .queued }
    }

    var assignedJobs: [QueuedPrintJobResponse] {
        queuedJobs.filter { $0.job.jobStatus == .assigned }
    }

    var reorderableQueueGroups: [QueueReorderGroup] {
        var groups: [QueueReorderGroup] = []
        var groupIndices: [String: Int] = [:]

        for item in jobs where item.job.jobStatus == .queued {
            let key = reorderGroupID(for: item)
            if let index = groupIndices[key] {
                let current = groups[index]
                groups[index] = QueueReorderGroup(
                    id: current.id,
                    title: current.title,
                    jobs: current.jobs + [item]
                )
            } else {
                groupIndices[key] = groups.count
                groups.append(
                    QueueReorderGroup(
                        id: key,
                        title: reorderGroupTitle(for: item),
                        jobs: [item]
                    )
                )
            }
        }
        return groups
    }

    func canMoveQueuedJob(
        id: UUID,
        direction: QueueReorderDirection,
        inGroup groupID: String
    ) -> Bool {
        guard canReorderQueue,
              let group = reorderableQueueGroups.first(where: { $0.id == groupID }),
              let index = group.jobs.firstIndex(where: { $0.job.jobUUID == id }) else {
            return false
        }
        let item = group.jobs[index]
        switch direction {
        case .up:
            return index > 0 && group.jobs[index - 1].job.priority == item.job.priority
        case .down:
            return index + 1 < group.jobs.count
                && group.jobs[index + 1].job.priority == item.job.priority
        }
    }

    func moveQueuedJob(
        id: UUID,
        direction: QueueReorderDirection,
        inGroup groupID: String
    ) async {
        guard let group = reorderableQueueGroups.first(where: { $0.id == groupID }),
              let index = group.jobs.firstIndex(where: { $0.job.jobUUID == id }) else {
            return
        }
        let destination = direction == .up ? index - 1 : index + 2
        await moveQueuedJobs(
            fromOffsets: IndexSet(integer: index),
            toOffset: destination,
            inGroup: groupID
        )
    }

    func moveQueuedJobs(fromOffsets offsets: IndexSet, toOffset destination: Int) async {
        let orderedJobs = reorderableQueuedJobs
        guard offsets.count == 1,
              let source = offsets.first,
              orderedJobs.indices.contains(source),
              (0...orderedJobs.count).contains(destination) else {
            return
        }

        let moved = orderedJobs[source]
        guard let group = reorderableQueueGroups.first(where: { candidate in
            candidate.jobs.contains(where: { $0.id == moved.id })
        }) else {
            return
        }

        var remaining = orderedJobs
        remaining.remove(at: source)
        let insertionIndex = min(
            max(destination > source ? destination - 1 : destination, 0),
            remaining.count
        )
        let beforeSharesGroup = insertionIndex < remaining.count
            && reorderGroupID(for: remaining[insertionIndex]) == group.id
        let afterSharesGroup = insertionIndex > 0
            && reorderGroupID(for: remaining[insertionIndex - 1]) == group.id
        guard beforeSharesGroup || afterSharesGroup else {
            errorMessage = "Jobs can only be reordered within the same printer and priority group."
            return
        }

        guard let localSource = group.jobs.firstIndex(where: { $0.id == moved.id }) else { return }
        let localInsertionIndex = remaining[..<insertionIndex].filter {
            reorderGroupID(for: $0) == group.id
        }.count
        let localDestination = localInsertionIndex > localSource
            ? localInsertionIndex + 1
            : localInsertionIndex
        await moveQueuedJobs(
            fromOffsets: IndexSet(integer: localSource),
            toOffset: localDestination,
            inGroup: group.id
        )
    }

    func moveQueuedJobs(
        fromOffsets offsets: IndexSet,
        toOffset destination: Int,
        inGroup groupID: String
    ) async {
        guard isViewActive else { return }
        guard queueWriteAuthorized else {
            errorMessage = "Queue.Write permission is required to reorder jobs."
            return
        }
        guard isNetworkReachable else {
            errorMessage = "Reordering is unavailable while offline. Connect to the network before moving jobs."
            return
        }
        guard hasFreshQueueSnapshot else {
            errorMessage = "Refresh the queue before reordering to load current revisions."
            return
        }
        guard !isReorderingQueue,
              offsets.count == 1,
              let service = jobService,
              let group = reorderableQueueGroups.first(where: { $0.id == groupID }),
              let source = offsets.first,
              group.jobs.indices.contains(source),
              (0...group.jobs.count).contains(destination) else {
            return
        }

        let moved = group.jobs[source]
        guard moved.job.jobStatus == .queued,
              let movedID = moved.job.jobUUID,
              let movedRowVersion = nonempty(moved.job.rowVersion) else {
            errorMessage = "Refresh the queue to get a current revision before moving this job."
            return
        }

        var reordered = group.jobs
        let item = reordered.remove(at: source)
        let insertionIndex = destination > source ? destination - 1 : destination
        reordered.insert(item, at: min(max(insertionIndex, 0), reordered.count))
        guard reordered.map(\.id) != group.jobs.map(\.id) else { return }

        guard let movedIndex = reordered.firstIndex(where: { $0.id == moved.id }) else { return }
        let priorityIndices = group.jobs.indices.filter {
            group.jobs[$0].job.priority == moved.job.priority
        }
        guard let firstPriorityIndex = priorityIndices.first,
              let lastPriorityIndex = priorityIndices.last,
              priorityIndices.count == lastPriorityIndex - firstPriorityIndex + 1 else {
            errorMessage = "Refresh the queue before reordering jobs across priority groups."
            return
        }
        guard (firstPriorityIndex...lastPriorityIndex).contains(movedIndex) else {
            errorMessage = "Jobs can only be reordered within their current priority group."
            return
        }

        let neighbor: QueuePositionNeighbor
        if movedIndex < lastPriorityIndex {
            let next = reordered[movedIndex + 1]
            guard let neighborID = next.job.jobUUID,
                  let rowVersion = nonempty(next.job.rowVersion) else {
                errorMessage = "Refresh the queue to get current neighbor revisions before moving."
                return
            }
            neighbor = .before(id: neighborID, rowVersion: rowVersion)
        } else if movedIndex > firstPriorityIndex {
            let previous = reordered[movedIndex - 1]
            guard let neighborID = previous.job.jobUUID,
                  let rowVersion = nonempty(previous.job.rowVersion) else {
                errorMessage = "Refresh the queue to get current neighbor revisions before moving."
                return
            }
            neighbor = .after(id: neighborID, rowVersion: rowVersion)
        } else {
            return
        }

        let previousJobs = jobs
        queueStateEpoch &+= 1
        let mutationEpoch = queueStateEpoch
        loadGeneration &+= 1
        jobs = replacingGroup(groupID, with: reordered, in: jobs)
        hasFreshQueueSnapshot = false
        isReorderingQueue = true
        defer { isReorderingQueue = false }

        do {
            _ = try await service.moveQueuedJob(
                id: movedID,
                reviewedRowVersion: movedRowVersion,
                neighbor: neighbor
            )
            guard isViewActive else { return }
            let refreshed = await refreshQueue()
            if !refreshed {
                errorMessage =
                    "The job moved, but the current queue couldn't be confirmed. Refresh before moving another job."
            }
        } catch {
            guard isViewActive else { return }
            if queueStateEpoch == mutationEpoch {
                jobs = previousJobs
                queueStateEpoch &+= 1
            }
            hasFreshQueueSnapshot = false
            if case .forbidden? = error as? NetworkError {
                queueWriteAuthorized = false
            }
            let refreshed = await refreshQueue()
            errorMessage = queueMoveErrorMessage(error, refreshed: refreshed)
        }
    }

    private func replacingGroup(
        _ groupID: String,
        with reordered: [QueuedPrintJobResponse],
        in values: [QueuedPrintJobResponse]
    ) -> [QueuedPrintJobResponse] {
        var result = values
        let ids = Set(reordered.map(\.id))
        let indices = result.indices.filter {
            ids.contains(result[$0].id) && reorderGroupID(for: result[$0]) == groupID
        }
        for (index, item) in zip(indices, reordered) {
            result[index] = item
        }
        return result
    }

    func reorderGroupID(for item: QueuedPrintJobResponse) -> String {
        "\(item.job.assignedPrinterId ?? "unassigned")|\(item.job.priority.rawValue)"
    }

    private func reorderGroupTitle(for item: QueuedPrintJobResponse) -> String {
        let scope = item.job.assignedPrinterId == nil
            ? "Any printer"
            : (item.job.printerName ?? "Assigned printer")
        let priority: String
        switch item.job.priority {
        case .low: priority = "Low"
        case .normal: priority = "Normal"
        case .high: priority = "High"
        case .urgent: priority = "Urgent"
        }
        return "\(scope) · \(priority) priority"
    }

    private func nonempty(_ value: String?) -> String? {
        guard let value, !value.isEmpty else { return nil }
        return value
    }

    private func queueMoveErrorMessage(_ error: Error, refreshed: Bool) -> String {
        let suffix = refreshed
            ? "The queue was refreshed."
            : "Pull to refresh before moving jobs again."
        guard let networkError = error as? NetworkError else {
            return "Couldn't reorder the queue: \(error.localizedDescription) \(suffix)"
        }
        switch networkError {
        case .conflict, .preconditionFailed, .notFound:
            return refreshed
                ? "Queue changed — refreshed."
                : "Queue changed, but the current order couldn't be loaded. \(suffix)"
        case .preconditionRequired:
            return "The server requires a current job revision. \(suffix)"
        case .clientError(400, _):
            return "The queue move was rejected. \(suffix)"
        case .forbidden:
            return "Queue.Write access was revoked. Reordering is unavailable."
        default:
            return "Couldn't reorder the queue: \(networkError.localizedDescription) \(suffix)"
        }
    }

    /// Recent failures only; completed and cancelled jobs belong in history.
    /// Completed and cancelled jobs remain reachable from secondary history,
    /// outside the approved three-section queue composition.
    var completedHistoryJobs: [QueuedPrintJobResponse] {
        jobs.filter { item in
            guard let status = item.job.jobStatus else { return false }
            return [.completed, .cancelled].contains(status)
        }
        .sorted { ($0.job.actualEndTimeUtc ?? $0.job.createdAtUtc) > ($1.job.actualEndTimeUtc ?? $1.job.createdAtUtc) }
    }

    var hasAnyJobs: Bool {
        !jobs.isEmpty || !recentFailures.isEmpty || recentFailuresError != nil
    }

    private func reviewedRowVersion(for id: UUID) -> String? {
        jobs.first(where: { $0.job.jobUUID == id })?.job.rowVersion
    }

    private func handleActionError(_ error: Error) async {
        if let networkError = error as? NetworkError,
           networkError.requiresReview {
            await loadJobs()
            errorMessage =
                "This job changed after you reviewed it. Review the refreshed row and confirm again."
            return
        }
        errorMessage = error.localizedDescription
    }
}

private extension NetworkError {
    var requiresReview: Bool {
        switch self {
        case .preconditionFailed, .preconditionRequired:
            return true
        case .clientError(let code, _):
            return code == 412 || code == 428
        default:
            return false
        }
    }
}
