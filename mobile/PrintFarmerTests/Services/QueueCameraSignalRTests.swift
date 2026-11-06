import Foundation
import XCTest
@testable import PrintFarmer

/// #3684: queue reorder `jobqueueupdate`, `queueevent` and `camerasupdated`
/// SignalR payloads decode against the real backend shapes and drive the
/// queue / camera-metadata refresh paths.
@MainActor
final class QueueCameraSignalRTests: XCTestCase {

    // MARK: - Payload decoding

    func testReorderJobQueueUpdateDecodesQueuedAtUtcAndNullPrinterId() throws {
        let jobId = UUID()
        let json = """
        {
          "printerId": null,
          "jobs": [{
            "id": "\(jobId.uuidString)",
            "name": "Bracket",
            "status": "Queued",
            "priority": "High",
            "queuedAtUtc": "2026-11-04T16:38:00.123Z",
            "actualStartTimeUtc": null,
            "actualEndTimeUtc": null,
            "assignedPrinterId": null,
            "rowVersion": "AQIDBA==",
            "copies": 1
          }]
        }
        """
        let update = try signalRDecoder.decode(JobQueueUpdate.self, from: Data(json.utf8))

        XCTAssertNil(update.printerId)
        XCTAssertEqual(update.jobs.map(\.id), [jobId])
        XCTAssertEqual(update.jobs.first?.priority, .high)
        XCTAssertEqual(update.jobs.first?.status, .queued)
        let expected = try XCTUnwrap(ISO8601DateFormatter().date(from: "2026-11-04T16:38:00Z"))
        let queuedAt = try XCTUnwrap(update.jobs.first?.queuedAt)
        XCTAssertEqual(queuedAt.timeIntervalSince(expected), 0.123, accuracy: 0.001)
    }

    func testLegacyJobQueueUpdateDecodesQueuedAtAndPrinterId() throws {
        let printerId = UUID()
        let json = """
        {
          "printerId": "\(printerId.uuidString)",
          "jobs": [{
            "id": "\(UUID().uuidString)",
            "name": "Legacy",
            "status": "Printing",
            "priority": "Normal",
            "queuedAt": "2026-11-04T10:00:00Z",
            "actualStartTime": "2026-11-04T10:05:00Z",
            "actualEndTime": null
          }]
        }
        """
        let update = try signalRDecoder.decode(JobQueueUpdate.self, from: Data(json.utf8))

        XCTAssertEqual(update.printerId, printerId)
        XCTAssertEqual(update.jobs.count, 1)
        XCTAssertEqual(update.jobs.first?.status, .printing)
        XCTAssertNotNil(update.jobs.first?.actualStartTime)
        XCTAssertNil(update.jobs.first?.actualEndTime)
    }

    func testJobQueueUpdateSkipsUndecodableEntriesInsteadOfFailingFrame() throws {
        let goodId = UUID()
        let json = """
        {
          "jobs": [
            { "id": "not-a-guid", "queuedAtUtc": "2026-11-04T10:00:00Z" },
            { "id": "\(goodId.uuidString)", "queuedAtUtc": "2026-11-04T10:00:00Z" }
          ]
        }
        """
        let update = try signalRDecoder.decode(JobQueueUpdate.self, from: Data(json.utf8))

        XCTAssertNil(update.printerId)
        XCTAssertEqual(update.jobs.map(\.id), [goodId])
        XCTAssertEqual(update.jobs.first?.name, "")
        XCTAssertEqual(update.jobs.first?.priority, .normal)
    }

    func testQueueEventDecodesRealisticEnvelopeIgnoringUnknownFields() throws {
        let eventId = UUID()
        let jobId = UUID()
        let printerId = UUID()
        let json = """
        {
          "schemaVersion": "1",
          "eventId": "\(eventId.uuidString)",
          "sequence": 9007199254740993,
          "eventType": "PrintFarmer.Queue.JobAssigned.v1",
          "occurredAtUtc": "2026-11-04T16:38:00.5Z",
          "jobId": "\(jobId.uuidString)",
          "printerId": "\(printerId.uuidString)",
          "projectId": null,
          "calibrationAttemptId": null,
          "jobStatus": "Assigned",
          "jobKind": "Print",
          "jobRevision": 4,
          "dispatchStateRevision": 2,
          "attemptNumber": 1,
          "failureRetryable": null,
          "payloadJson": "{\\"k\\":1}"
        }
        """
        let event = try signalRDecoder.decode(QueueEvent.self, from: Data(json.utf8))

        XCTAssertEqual(event.eventId, eventId)
        XCTAssertEqual(event.sequence, 9_007_199_254_740_993)
        XCTAssertEqual(event.eventType, "PrintFarmer.Queue.JobAssigned.v1")
        XCTAssertEqual(event.jobId, jobId)
        XCTAssertEqual(event.printerId, printerId)
        XCTAssertNil(event.projectId)
        XCTAssertEqual(event.jobStatus, "Assigned")
        XCTAssertNotNil(event.occurredAtUtc)
    }

    func testQueueEventDecodesRedactedPrinterGroupEnvelope() throws {
        let json = #"{"schemaVersion":"1","eventType":"PrintFarmer.Queue.PrinterStateChanged.v1"}"#
        let event = try signalRDecoder.decode(QueueEvent.self, from: Data(json.utf8))
        XCTAssertEqual(event.eventType, "PrintFarmer.Queue.PrinterStateChanged.v1")
        XCTAssertNil(event.jobId)
        XCTAssertNil(event.sequence)
    }

    func testCamerasUpdatedDecodesPrinterAndCameraIds() throws {
        let printerId = UUID()
        let cameraIds = [UUID(), UUID()]
        let json = """
        {"printerId":"\(printerId.uuidString)","cameraIds":["\(cameraIds[0].uuidString)","\(cameraIds[1].uuidString)"]}
        """
        let event = try signalRDecoder.decode(CamerasUpdatedEvent.self, from: Data(json.utf8))
        XCTAssertEqual(event, CamerasUpdatedEvent(printerId: printerId, cameraIds: cameraIds))

        let empty = try signalRDecoder.decode(
            CamerasUpdatedEvent.self,
            from: Data(#"{"printerId":"\#(printerId.uuidString)","cameraIds":[]}"#.utf8)
        )
        XCTAssertEqual(empty.cameraIds, [])
    }

    // MARK: - Raw SignalR frames

    #if DEBUG
    func testQueueEventAndCamerasUpdatedFramesRouteToLowercaseHubs() async throws {
        let service = makeService()
        let printerId = UUID()
        let queueEvents = expectation(description: "queueevent delivered")
        let cameras = expectation(description: "camerasupdated delivered")
        let received = LockedValues<String>()
        let queueSub = service.onQueueEvent { event in
            received.append(event.eventType)
            queueEvents.fulfill()
        }
        let cameraSub = service.onCamerasUpdated { event in
            received.append("cameras:\(event.printerId == printerId)")
            cameras.fulfill()
        }
        defer {
            queueSub.cancel()
            cameraSub.cancel()
        }

        service.processIncomingDataForTesting(invocation(
            target: "queueevent",
            argument: #"{"schemaVersion":"1","eventType":"PrintFarmer.Queue.JobQueued.v1","sequence":7}"#
        ))
        service.processIncomingDataForTesting(invocation(
            target: "camerasupdated",
            argument: #"{"printerId":"\#(printerId.uuidString)","cameraIds":[]}"#
        ))

        await fulfillment(of: [queueEvents, cameras], timeout: 5)
        XCTAssertEqual(
            received.values.sorted(),
            ["PrintFarmer.Queue.JobQueued.v1", "cameras:true"].sorted()
        )
    }

    func testUndecodableQueueFramesStillDeliverInvalidationHints() async throws {
        let service = makeService()
        let queueUpdate = expectation(description: "jobqueueupdate fallback")
        let queueEvent = expectation(description: "queueevent fallback")
        let updates = LockedValues<String>()
        let updateSub = service.onJobQueueUpdated { update in
            updates.append("update:\(update.printerId == nil):\(update.jobs.count)")
            queueUpdate.fulfill()
        }
        let eventSub = service.onQueueEvent { event in
            updates.append("event:\(event == .undecodable)")
            queueEvent.fulfill()
        }
        defer {
            updateSub.cancel()
            eventSub.cancel()
        }

        service.processIncomingDataForTesting(invocation(target: "jobqueueupdate", argument: "42"))
        service.processIncomingDataForTesting(invocation(target: "queueevent", argument: #"{"sequence":"x"}"#))

        await fulfillment(of: [queueUpdate, queueEvent], timeout: 5)
        XCTAssertEqual(updates.values.sorted(), ["event:true", "update:true:0"])
    }

    func testUndecodableCamerasUpdatedFrameIsDropped() async throws {
        let service = makeService()
        let received = LockedValues<String>()
        let sub = service.onCamerasUpdated { _ in received.append("cameras") }
        defer { sub.cancel() }

        service.processIncomingDataForTesting(invocation(target: "camerasupdated", argument: #"{"cameraIds":[]}"#))
        await service.drainHubCoordinatorForTesting()

        XCTAssertTrue(received.values.isEmpty)
    }
    #endif

    // MARK: - JobListViewModel refresh

    func testQueueEventRefreshesJobListAndTearsDownOnDeactivate() async throws {
        let jobService = MockJobService()
        let viewModel = JobListViewModel()
        viewModel.configure(jobService: jobService)
        viewModel.configure(jobAnalyticsService: MockJobAnalyticsService())
        await viewModel.loadJobs()
        let baseline = jobService.listAllJobsCallCount
        let signalR = MockSignalRService()
        viewModel.configureSignalR(signalR)
        XCTAssertEqual(signalR.queueEventSubscriberCount, 1)

        signalR.simulateQueueEvent(QueueEvent(eventType: "PrintFarmer.Queue.JobQueued.v1"))
        await waitUntil { jobService.listAllJobsCallCount > baseline }
        XCTAssertEqual(jobService.listAllJobsCallCount, baseline + 1)

        signalR.simulateQueueEvent(.undecodable)
        await waitUntil { jobService.listAllJobsCallCount > baseline + 1 }
        XCTAssertEqual(jobService.listAllJobsCallCount, baseline + 2)

        viewModel.deactivate()
        XCTAssertEqual(signalR.queueEventSubscriberCount, 0)
    }

    func testReorderJobQueueUpdateWithNilPrinterRefreshesJobList() async throws {
        let jobService = MockJobService()
        let viewModel = JobListViewModel()
        viewModel.configure(jobService: jobService)
        viewModel.configure(jobAnalyticsService: MockJobAnalyticsService())
        await viewModel.loadJobs()
        let baseline = jobService.listAllJobsCallCount
        let signalR = MockSignalRService()
        viewModel.configureSignalR(signalR)

        signalR.simulateJobQueueUpdate(JobQueueUpdate(printerId: nil, jobs: []))
        await waitUntil { jobService.listAllJobsCallCount > baseline }
        XCTAssertEqual(jobService.listAllJobsCallCount, baseline + 1)
    }

    // MARK: - PrinterDetailViewModel camera reload

    func testCamerasUpdatedReloadsCameraMetadataOnlyForMatchingPrinter() async throws {
        let queue = ShiftTaskCallbackQueue()
        let service = MockPrinterService()
        let viewModel = PrinterDetailViewModel(
            printerId: TestData.testUUID,
            callbackEnqueuer: queue.enqueuer
        )
        viewModel.configure(printerService: service)
        var printer = try TestData.decodePrinter(from: TestJSON.printerMinimal)
        printer.isOnline = true
        service.printerToReturn = printer
        service.cameraUrlToReturn = PrinterCameraUrl(
            streamUrl: nil, snapshotUrl: nil, accessMode: .snapshotOnly,
            streamFormat: .unknown, snapshotStrategy: .directUrl
        )
        await viewModel.loadPrinter()
        await viewModel.waitForCanonicalLoadToBecomeIdle()
        let baseline = service.getCameraUrlCallCount
        XCTAssertEqual(baseline, 1)
        let signalR = MockSignalRService()
        viewModel.configureSignalR(signalR)
        XCTAssertEqual(signalR.camerasUpdatedSubscriberCount, 1)

        await deliverCameras(CamerasUpdatedEvent(printerId: UUID(), cameraIds: [UUID()]), via: signalR, queue: queue)
        await viewModel.waitForCanonicalLoadToBecomeIdle()
        XCTAssertEqual(service.getCameraUrlCallCount, baseline, "Another printer's cameras must not reload this detail")

        await deliverCameras(CamerasUpdatedEvent(printerId: TestData.testUUID, cameraIds: []), via: signalR, queue: queue)
        await viewModel.waitForCanonicalLoadToBecomeIdle()
        XCTAssertEqual(service.getCameraUrlCallCount, baseline + 1)
        XCTAssertEqual(service.getCameraUrlCalledWith, TestData.testUUID)

        viewModel.isViewActive = false
        signalR.simulateCamerasUpdated(CamerasUpdatedEvent(printerId: TestData.testUUID, cameraIds: []))
        while queue.count > 0 { await queue.runNext() }
        await viewModel.waitForCanonicalLoadToBecomeIdle()
        XCTAssertEqual(service.getCameraUrlCallCount, baseline + 1, "Inactive detail must not reload")
        viewModel.stopSnapshotPolling()
    }

    // MARK: - Helpers

    /// Mirrors SignalRService's dual-format (fractional / plain) ISO8601 decoder.
    private let signalRDecoder: JSONDecoder = {
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .custom { decoder in
            let container = try decoder.singleValueContainer()
            let text = try container.decode(String.self)
            if let date = APIClient.iso8601WithFractional.date(from: text) { return date }
            if let date = APIClient.iso8601Plain.date(from: text) { return date }
            throw DecodingError.dataCorruptedError(in: container, debugDescription: text)
        }
        return decoder
    }()

    private func waitUntil(
        timeout: Duration = .seconds(5),
        _ condition: @MainActor () -> Bool
    ) async {
        let deadline = ContinuousClock.now + timeout
        while !condition(), ContinuousClock.now < deadline {
            try? await Task.sleep(for: .milliseconds(5))
        }
    }

    private func deliverCameras(
        _ event: CamerasUpdatedEvent,
        via signalR: MockSignalRService,
        queue: ShiftTaskCallbackQueue
    ) async {
        let target = queue.count + 1
        signalR.simulateCamerasUpdated(event)
        await queue.waitForCount(target)
        while queue.count > 0 { await queue.runNext() }
    }

    #if DEBUG
    private func makeService() -> SignalRService {
        SignalRService(serverURL: URL(string: "http://signalr.test")!, tokenProvider: { nil })
    }

    private func invocation(target: String, argument: String) -> Data {
        var data = Data(#"{"type":1,"target":"\#(target)","arguments":[\#(argument)]}"#.utf8)
        data.append(SignalRFrameParser.recordSeparator)
        return data
    }
    #endif
}

private final class LockedValues<Value: Sendable>: @unchecked Sendable {
    private let lock = NSLock()
    private var storage: [Value] = []

    func append(_ value: Value) {
        lock.lock()
        storage.append(value)
        lock.unlock()
    }

    var values: [Value] {
        lock.lock()
        defer { lock.unlock() }
        return storage
    }
}
