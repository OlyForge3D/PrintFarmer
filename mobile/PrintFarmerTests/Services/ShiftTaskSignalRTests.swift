import Foundation
import XCTest
@testable import PrintFarmer

@MainActor
final class ShiftTaskSignalRTests: XCTestCase {
    #if DEBUG
    func testP3QueuedOldCallbackRechecksAuthorityInsideMainActorHop() async {
        let queue = ShiftTaskCallbackQueue()
        let viewModel = ShiftTasksViewModel(callbackEnqueuer: queue.enqueuer)
        let oldService = ScriptedShiftTaskService(
            defaultSnapshot: makeShiftTaskSnapshot(title: "Old")
        )
        let oldSignalR = MockSignalRService()
        let currentService = ScriptedShiftTaskService(
            defaultSnapshot: makeShiftTaskSnapshot(title: "Current")
        )
        let currentSignalR = MockSignalRService()

        viewModel.configure(
            taskService: oldService,
            signalRService: oldSignalR,
            shiftPlanEnabled: true
        )
        oldSignalR.simulateTaskInvalidation(target: "taskupdated")
        await queue.waitForCount(1)

        viewModel.configure(
            taskService: currentService,
            signalRService: currentSignalR,
            shiftPlanEnabled: true
        )
        await queue.runNext()

        let oldLoadCount = await oldService.loadCallCount
        let currentBeforeNewCallback = await currentService.loadCallCount
        XCTAssertEqual(oldLoadCount, 0)
        XCTAssertEqual(currentBeforeNewCallback, 0)
        XCTAssertNil(viewModel.snapshot)

        currentSignalR.simulateTaskInvalidation(target: "taskcreated")
        await queue.waitForCount(1)
        await queue.runNext()

        let currentAfterNewCallback = await currentService.loadCallCount
        XCTAssertEqual(currentAfterNewCallback, 1)
        XCTAssertEqual(
            viewModel.snapshot?.groups.first?.tasks.first?.title,
            "Current"
        )

        viewModel.deactivate()
        currentSignalR.simulateTaskInvalidation(target: "taskupdated")
        XCTAssertEqual(queue.count, 0)
    }
    #endif

    func testRepeatedSameServiceConfigurationDoesNotStackTaskListener() {
        let viewModel = ShiftTasksViewModel()
        let service = ScriptedShiftTaskService()
        let signalR = MockSignalRService()

        viewModel.configure(
            taskService: service,
            signalRService: signalR,
            shiftPlanEnabled: true
        )
        viewModel.configure(
            taskService: service,
            signalRService: signalR,
            shiftPlanEnabled: true
        )

        XCTAssertEqual(signalR.taskInvalidationSubscriberCount, 1)
        viewModel.deactivate()
        XCTAssertEqual(signalR.taskInvalidationSubscriberCount, 0)
    }

    #if DEBUG
    func testP4RawSignalRTransportMatrixAndFIFO() async throws {
        let service = SignalRService(
            serverURL: try XCTUnwrap(URL(string: "http://signalr.test")),
            tokenProvider: { nil }
        )
        let recorder = ShiftTaskEventRecorder()
        let subscription = service.onTaskInvalidated {
            recorder.record($0)
        }
        defer { subscription.cancel() }

        let created = invocationWithoutArguments(target: "taskcreated")
        let updatedScalar = invocation(target: "taskupdated", argument: "42")
        let pendingCount = invocation(
            target: "pendingtaskcount",
            argument: #"{"count":3}"#
        )
        let uppercase = invocation(target: "TaskUpdated", argument: #"{"id":"bad"}"#)
        let unknown = invocation(target: "futuretaskevent", argument: "true")
        let malformed = terminated(#"{"type":1,"target":"taskupdated""#)
        let unsupported = terminated(#"{"type":3}"#)

        let splitIndex = created.count / 2
        service.processIncomingDataForTesting(Data(created.prefix(splitIndex)))
        XCTAssertTrue(recorder.snapshot.isEmpty)
        service.processIncomingDataForTesting(Data(created.suffix(from: splitIndex)))

        var coalesced = Data()
        coalesced.append(updatedScalar)
        coalesced.append(pendingCount)
        coalesced.append(uppercase)
        coalesced.append(unknown)
        coalesced.append(malformed)
        coalesced.append(unsupported)
        service.processIncomingDataForTesting(coalesced)

        await recorder.waitForCount(3)
        XCTAssertEqual(
            recorder.snapshot.map(\.target),
            ["taskcreated", "taskupdated", "pendingtaskcount"]
        )
    }
    #endif

    func testP4FrameParserResetDropsSplitRecordOnCancellation() throws {
        var parser = SignalRFrameParser()
        let partial = Data(#"{"type":1,"target":"taskupdated""#.utf8)
        XCTAssertTrue(try parser.append(partial).isEmpty)

        parser.reset()
        let valid = terminated(
            #"{"type":1,"target":"taskcreated","arguments":[{"id":"fresh"}]}"#
        )
        let frames = try parser.append(valid)

        XCTAssertEqual(frames.count, 1)
        XCTAssertEqual(
            SignalRProtocolMessage.decode(frames[0]),
            .invocation(
                target: "taskcreated",
                firstArgument: Data(#"{"id":"fresh"}"#.utf8)
            )
        )
    }

    func testP4UnterminatedFrameIsBoundedAndParserRecovers() throws {
        var parser = SignalRFrameParser()
        let maximum = SignalRFrameParser.maximumFrameBytes

        XCTAssertTrue(
            try parser.append(Data(repeating: 0x41, count: maximum)).isEmpty
        )
        XCTAssertThrowsError(try parser.append(Data([0x42]))) { error in
            XCTAssertEqual(
                error as? SignalRFrameParserError,
                .frameTooLarge(maximumBytes: maximum)
            )
        }

        let valid = terminated(
            #"{"type":1,"target":"taskupdated","arguments":[1]}"#
        )
        XCTAssertEqual(try parser.append(valid).count, 1)
    }

    func testP4TypeCloseErrorMalformedAndUnknownMessagesParseSafely() {
        XCTAssertEqual(
            SignalRProtocolMessage.decode(Data(#"{"type":6}"#.utf8)),
            .ping
        )
        XCTAssertEqual(
            SignalRProtocolMessage.decode(
                Data(#"{"type":7,"error":"server closed"}"#.utf8)
            ),
            .close(error: "server closed")
        )
        XCTAssertEqual(
            SignalRProtocolMessage.decode(Data(#"{"type":99}"#.utf8)),
            .unsupported(type: 99)
        )
        XCTAssertEqual(
            SignalRProtocolMessage.decode(Data(#"{"type":"one"}"#.utf8)),
            .malformed
        )
        XCTAssertEqual(
            SignalRProtocolMessage.decode(Data("7".utf8)),
            .malformed
        )
        XCTAssertTrue(
            ShiftTaskInvalidation.supportedTargets.contains("taskupdated")
        )
        XCTAssertFalse(
            ShiftTaskInvalidation.supportedTargets.contains("TaskUpdated")
        )
    }

    func testP10RawFrameInjectionAndCallbackBarrierAreDebugOnly() {
        #if DEBUG
        let queue = ShiftTaskCallbackQueue()
        _ = ShiftTasksViewModel(callbackEnqueuer: queue.enqueuer)
        XCTAssertTrue(true)
        #else
        XCTFail("The unit proof target must compile with DEBUG test seams")
        #endif
    }

    private func invocation(target: String, argument: String) -> Data {
        terminated(
            #"{"type":1,"target":"\#(target)","arguments":[\#(argument)]}"#
        )
    }

    private func invocationWithoutArguments(target: String) -> Data {
        terminated(
            #"{"type":1,"target":"\#(target)","arguments":[]}"#
        )
    }

    private func terminated(_ json: String) -> Data {
        var data = Data(json.utf8)
        data.append(SignalRFrameParser.recordSeparator)
        return data
    }
}
