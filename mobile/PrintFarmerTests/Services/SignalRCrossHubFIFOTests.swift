import XCTest
@testable import PrintFarmer

/// r5 blocker #3: SERVICE-WIDE FIFO across hubs.
///
/// The r4 design used one-serial-queue-per-hub, which permitted the
/// A1→B→A2 interleave that reviewers rejected: an outer A1 handler that
/// synchronously triggered a delivery on hub B could resume BEFORE A1's
/// remaining handlers observed the state batched under A1. The r5
/// coordinator serialises delivery across every hub belonging to the same
/// `SignalRHubCoordinator`, so an outer batch on hub A completes before
/// any nested delivery on hub B runs, and a nested A2 requested from B's
/// handler runs after B finishes — not interleaved.
///
/// This test file drives `MockSignalRService` because production
/// `SignalRService` and mock share the exact same `SignalRHubCoordinator`
/// injected into every hub. The mock exposes deterministic `simulate*Sync`
/// entry points; the coordinator behaviour is what's under test.
@MainActor
final class SignalRCrossHubFIFOTests: XCTestCase {

    /// A→B→A cross-hub scenario. Subscribers on hub A record their delivery
    /// order via a shared array; the A handler synchronously enqueues a
    /// deliver on hub B. FIFO contract requires: A's outer delivery finishes
    /// (i.e. every A subscriber sees the initial event) BEFORE the nested B
    /// delivery runs, and a subsequent A2 enqueued from B's handler runs
    /// AFTER B's delivery finishes.
    func testCrossHubNestedDeliveryIsFIFOAcrossHubs() {
        let mock = MockSignalRService()
        let recorder = OrderRecorder()

        // Sentinel test IDs so each recorded step is human-readable.
        let printerA1 = TestIDs.printerA1
        let printerA2 = TestIDs.printerA2

        var subs: [SignalRSubscription] = []

        // Hub A subscriber #1 — synchronously delivers B when it sees A1.
        subs.append(mock.onPrinterUpdated { update in
            if update.id == printerA1 {
                recorder.record("A1-sub1")
                // Deliver on hub B synchronously — this must NOT interleave
                // between A's remaining subscribers. FIFO across hubs.
                mock.simulateJobQueueUpdate(TestIDs.makeJobQueueUpdate())
                return
            }
            if update.id == printerA2 {
                recorder.record("A2-sub1")
            }
        })

        // Hub A subscriber #2 — must observe A1 BEFORE B runs, proving
        // the outer A1 batch is not interrupted by the nested B request.
        subs.append(mock.onPrinterUpdated { update in
            if update.id == printerA1 {
                recorder.record("A1-sub2")
                return
            }
            if update.id == printerA2 {
                recorder.record("A2-sub2")
            }
        })

        // Hub B subscriber — synchronously requests A2 delivery. FIFO
        // contract: A2 delivery runs AFTER B finishes, not nested inside.
        subs.append(mock.onJobQueueUpdated { _ in
            recorder.record("B")
            mock.simulatePrinterUpdate(TestIDs.makePrinterUpdate(id: printerA2))
        })

        // Fire A1 synchronously. All nested deliveries drain before this
        // returns (the coordinator processes its `pendingDeliveries` queue).
        mock.simulatePrinterUpdate(TestIDs.makePrinterUpdate(id: printerA1))

        // FIFO order:
        //  1. A1 delivered → sub1 records "A1-sub1", enqueues B
        //  2. A1 outer batch continues → sub2 records "A1-sub2"
        //  3. Outer A1 batch complete → drain: B runs, records "B", enqueues A2
        //  4. B batch complete → drain: A2 runs, sub1 records "A2-sub1", sub2 records "A2-sub2"
        let recorded = recorder.snapshot()
        XCTAssertEqual(recorded, ["A1-sub1", "A1-sub2", "B", "A2-sub1", "A2-sub2"],
            "service-wide FIFO must complete outer batches before nested deliveries; got \(recorded)")

        // Explicitly retain subs across the checks.
        for sub in subs { sub.cancel() }
    }

    /// A→B→A cyclic callback must not deadlock (r5 blocker #3 explicit
    /// requirement). Whether the delivery is inline or drained later, no
    /// synchronous hub call may deadlock the coordinator.
    func testCyclicCrossHubCallbacksDoNotDeadlock() {
        let mock = MockSignalRService()
        let recorder = OrderRecorder()

        var subs: [SignalRSubscription] = []
        subs.append(mock.onPrinterUpdated { update in
            recorder.record("A(\(update.id.uuidString.prefix(4)))")
            if update.id == TestIDs.printerA1 {
                mock.simulateJobQueueUpdate(TestIDs.makeJobQueueUpdate())
            }
        })
        subs.append(mock.onJobQueueUpdated { _ in
            recorder.record("B")
            // Trigger A again from within B — cycle back. Must not deadlock.
            mock.simulatePrinterUpdate(TestIDs.makePrinterUpdate(id: TestIDs.printerA2))
        })

        mock.simulatePrinterUpdate(TestIDs.makePrinterUpdate(id: TestIDs.printerA1))
        let recorded = recorder.snapshot()
        XCTAssertFalse(recorded.isEmpty, "cyclic cross-hub delivery must not deadlock")
        for sub in subs { sub.cancel() }
    }

    /// Multi-server: two separate `MockSignalRService` instances own two
    /// separate coordinators. Events on service #1 must not touch service
    /// #2's handlers, proving the coordinator is per-service (not static).
    func testTwoServiceInstancesDoNotShareCoordinators() {
        let mockA = MockSignalRService()
        let mockB = MockSignalRService()
        let boxA = OrderRecorder()
        let boxB = OrderRecorder()

        let subA = mockA.onPrinterUpdated { update in boxA.record("A:\(update.id.uuidString.prefix(4))") }
        let subB = mockB.onPrinterUpdated { update in boxB.record("B:\(update.id.uuidString.prefix(4))") }

        mockA.simulatePrinterUpdate(TestIDs.makePrinterUpdate(id: TestIDs.printerA1))
        XCTAssertFalse(boxA.snapshot().isEmpty, "service A subscriber must observe A's event")
        XCTAssertTrue(boxB.snapshot().isEmpty, "service B must not observe A's event — coordinators are per-service")

        mockB.simulatePrinterUpdate(TestIDs.makePrinterUpdate(id: TestIDs.printerA2))
        XCTAssertFalse(boxB.snapshot().isEmpty, "service B subscriber must observe B's event")

        subA.cancel(); subB.cancel()
    }
}

// MARK: - Helpers

/// Thread-safe append-only recorder for delivery-order assertions.
private final class OrderRecorder: @unchecked Sendable {
    private let lock = NSLock()
    private var storage: [String] = []
    func record(_ item: String) {
        lock.lock(); defer { lock.unlock() }
        storage.append(item)
    }
    func snapshot() -> [String] {
        lock.lock(); defer { lock.unlock() }
        return storage
    }
}

private enum TestIDs {
    static let printerA1 = UUID(uuidString: "AAAAAAAA-1111-1111-1111-111111111111")!
    static let printerA2 = UUID(uuidString: "AAAAAAAA-2222-2222-2222-222222222222")!

    static func makePrinterUpdate(id: UUID) -> PrinterStatusUpdate {
        PrinterStatusUpdate(
            id: id, isOnline: true, state: "printing", progress: nil,
            jobName: nil, fileName: nil, thumbnailUrl: nil, cameraStreamUrl: nil,
            x: nil, y: nil, z: nil, hotendTemp: nil, bedTemp: nil,
            hotendTarget: nil, bedTarget: nil, homedAxes: nil,
            spoolInfo: nil, mmuStatus: nil
        )
    }

    static func makeJobQueueUpdate() -> JobQueueUpdate {
        JobQueueUpdate(printerId: printerA1, jobs: [])
    }
}
