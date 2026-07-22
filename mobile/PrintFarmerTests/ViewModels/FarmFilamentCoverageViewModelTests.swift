import XCTest
@testable import PrintFarmer

// MARK: - Farm Filament Coverage ViewModel Tests (F4-M / issue #778)
//
// Proves the frozen generation- AND owner-epoch-authoritative
// contract using deterministic controlled service responses.
//
// Deterministic-test discipline (cycle-3 reviewer blocker C):
//   * No sleeps. No `Task.yield()` as a pass gate. No polling.
//   * Positive dispatches use `waitForCommittedGeneration(atLeast:)`.
//   * Absence dispatches use `waitForCallbackTick(atLeast:)`, which
//     is advanced INSIDE the callback body — so once the barrier
//     resumes, every in-flight callback has either dispatched (and
//     its load committed) or was filtered.
//   * Post-teardown absence uses the direct
//     `filamentCoverageSubscriberCount == 0` proof: the hub is
//     structurally guaranteed not to deliver, so no callback body
//     runs and immediate assertion is authoritative.

@MainActor
final class FarmFilamentCoverageViewModelTests: XCTestCase {

    // MARK: - Idempotent SignalR configuration + subscription count

    func testConfigureSignalRIsIdempotent() {
        let signalR = MockSignalRService()
        let vm = FarmFilamentCoverageViewModel()
        let service = ControlledFilamentCoverageService()
        vm.configure(coverageService: service)

        vm.configureSignalR(signalR)
        XCTAssertEqual(signalR.filamentCoverageSubscriberCount, 1)
        XCTAssertEqual(signalR.connectionStateSubscriberCount, 1)

        vm.configureSignalR(signalR)
        XCTAssertEqual(signalR.filamentCoverageSubscriberCount, 1,
                       "Reconfiguration must cancel prior tokens before re-registering.")
        XCTAssertEqual(signalR.connectionStateSubscriberCount, 1)

        vm.tearDownSignalR()
        XCTAssertEqual(signalR.filamentCoverageSubscriberCount, 0)
        XCTAssertEqual(signalR.connectionStateSubscriberCount, 0)
    }

    // MARK: - Success + tombstone precedence

    func testStaleSuccessDoesNotOverwriteNewerFeatureDisabled() async throws {
        let service = ControlledFilamentCoverageService()
        let vm = FarmFilamentCoverageViewModel()
        vm.configure(coverageService: service)

        async let firstLoad: Void = vm.load()
        async let secondLoad: Void = vm.load()

        await service.awaitPending(count: 2)

        await service.completeFeatureDisabled(index: 1)
        await service.completeSuccess(index: 0, fleet: Self.oneCoverPrinterFleet())

        _ = await firstLoad
        _ = await secondLoad

        XCTAssertTrue(vm.isFeatureDisabled,
                      "Older success must NOT re-enable coverage after a newer disabled tombstone.")
        XCTAssertTrue(vm.coverageByPrinter.isEmpty)
    }

    func testStaleErrorDoesNotOverwriteNewerSuccess() async throws {
        let service = ControlledFilamentCoverageService()
        let vm = FarmFilamentCoverageViewModel()
        vm.configure(coverageService: service)

        async let l1: Void = vm.load()
        async let l2: Void = vm.load()
        await service.awaitPending(count: 2)

        await service.completeSuccess(index: 1, fleet: Self.oneCoverPrinterFleet())
        await service.completeError(index: 0, error: NetworkError.serverError(503))

        _ = await l1
        _ = await l2

        XCTAssertFalse(vm.isFeatureDisabled)
        XCTAssertEqual(vm.coverageByPrinter.count, 1,
                       "Newer success must remain visible even when a stale error resolves later.")
    }

    // MARK: - Equal-timestamp newer-generation wins

    func testEqualEvaluatedAtNewerGenerationWins() async throws {
        let service = ControlledFilamentCoverageService()
        let vm = FarmFilamentCoverageViewModel()
        vm.configure(coverageService: service)

        let sharedTimestamp = Date(timeIntervalSinceReferenceDate: 1_000_000)

        async let l1: Void = vm.load()
        async let l2: Void = vm.load()
        await service.awaitPending(count: 2)

        let fleetA = Self.fleet(evaluatedAt: sharedTimestamp, printerName: "A")
        let fleetB = Self.fleet(evaluatedAt: sharedTimestamp, printerName: "B")
        await service.completeSuccess(index: 1, fleet: fleetB)
        await service.completeSuccess(index: 0, fleet: fleetA)

        _ = await l1
        _ = await l2

        XCTAssertEqual(vm.coverageByPrinter.count, 1)
        let stored = vm.coverageByPrinter.values.first!
        XCTAssertEqual(stored.printerName, "B",
                       "Equal evaluatedAtUtc must resolve to the newer generation, not the newer wall-clock arrival.")
    }

    // MARK: - Invalidation → single refetch

    func testFilamentCoverageChangedTriggersRefetch() async throws {
        let service = ControlledFilamentCoverageService()
        let signalR = MockSignalRService()
        let vm = FarmFilamentCoverageViewModel()
        vm.configure(coverageService: service)
        vm.configureSignalR(signalR)

        async let initialLoad: Void = vm.load()
        await service.awaitPending(count: 1)
        await service.completeSuccess(index: 0, fleet: Self.oneCoverPrinterFleet())
        _ = await initialLoad
        XCTAssertEqual(vm.dispatchedRequestCount, 1)

        signalR.simulateFilamentCoverageChanged(FilamentCoverageChangedEvent(
            printerId: nil, reason: "test", occurredAt: Date()
        ))

        await service.awaitPending(count: 1)
        await service.completeSuccess(index: 0, fleet: Self.oneCoverPrinterFleet())
        await vm.waitForCommittedGeneration(atLeast: 2)
        XCTAssertEqual(vm.dispatchedRequestCount, 2,
                       "Single event => single refetch (baseline + one invalidation refetch).")
    }

    // MARK: - Reconnect classification (reviewer blocker B + C)

    /// Cold-start `.connected` MUST NOT double-load. Absence proven
    /// deterministically via `waitForCallbackTick` — no yield gate.
    func testColdStartConnectedDoesNotRefetch() async throws {
        let service = ControlledFilamentCoverageService()
        let signalR = MockSignalRService()
        let vm = FarmFilamentCoverageViewModel()
        vm.configure(coverageService: service)
        // Initial state is `.disconnected` (the mock's default at
        // subscription time), so `hasSeenAnyConnected` starts false.
        vm.configureSignalR(signalR)

        async let initial: Void = vm.load()
        await service.awaitPending(count: 1)
        await service.completeSuccess(index: 0, fleet: Self.oneCoverPrinterFleet())
        _ = await initial
        XCTAssertEqual(vm.dispatchedRequestCount, 1)
        XCTAssertFalse(vm.hasSeenAnyConnectedForTesting,
                       "Bootstrap: hub started disconnected, so classifier is unseeded.")

        let tickBefore = vm.callbackTickForTesting

        // Deliver the cold `.connected` transition. The callback body
        // filters (flips `hasSeenAnyConnected` to true, does NOT
        // dispatch a load) and advances the tick on exit.
        signalR.simulateConnectionStateChange(.connected)
        await vm.waitForCallbackTick(atLeast: tickBefore + 1)

        XCTAssertEqual(vm.dispatchedRequestCount, 1,
                       "Cold-start `.connected` must not double-load (the view owns the initial fetch).")
        XCTAssertTrue(vm.hasSeenAnyConnectedForTesting,
                      "The cold transition must arm the classifier so the next `.connected` is treated as recovery.")
    }

    func testReconnectTransitionTriggersExactlyOneRecoveryRefetch() async throws {
        let service = ControlledFilamentCoverageService()
        let signalR = MockSignalRService()
        let vm = FarmFilamentCoverageViewModel()
        vm.configure(coverageService: service)
        vm.configureSignalR(signalR)

        // Baseline load.
        async let initial: Void = vm.load()
        await service.awaitPending(count: 1)
        await service.completeSuccess(index: 0, fleet: Self.oneCoverPrinterFleet())
        _ = await initial

        // Cold `.connected` — no refetch. Barrier-gated absence.
        let tick0 = vm.callbackTickForTesting
        signalR.simulateConnectionStateChange(.connected)
        await vm.waitForCallbackTick(atLeast: tick0 + 1)
        XCTAssertEqual(vm.dispatchedRequestCount, 1)

        // Recovery transition: `.reconnecting -> .connected` MUST
        // dispatch exactly one refetch.
        signalR.simulateConnectionStateChange(.reconnecting)
        signalR.simulateConnectionStateChange(.connected)
        await service.awaitPending(count: 1)
        await service.completeSuccess(index: 0, fleet: Self.oneCoverPrinterFleet())
        await vm.waitForCommittedGeneration(atLeast: 2)
        XCTAssertEqual(vm.dispatchedRequestCount, 2,
                       "Exactly one recovery refetch per reconnect transition.")
    }

    /// Reviewer blocker B: configure while the hub is ALREADY
    /// `.reconnecting`. The next `.connected` MUST dispatch a
    /// recovery refetch (not be mis-classified as cold-start).
    func testConfigureWhileReconnectingArmsRecoveryOnNextConnected() async throws {
        let service = ControlledFilamentCoverageService()
        let signalR = MockSignalRService()
        // Put the hub into `.reconnecting` BEFORE configureSignalR
        // so the subscription's initial state is `.reconnecting`.
        signalR.simulateConnectionStateChange(.reconnecting)

        let vm = FarmFilamentCoverageViewModel()
        vm.configure(coverageService: service)
        vm.configureSignalR(signalR)

        XCTAssertTrue(vm.hasSeenAnyConnectedForTesting,
                      "Configure-while-reconnecting must pre-seed the classifier so the pending .connected is treated as recovery.")

        // The pending `.connected` transition IS the recovery event.
        signalR.simulateConnectionStateChange(.connected)
        await service.awaitPending(count: 1)
        await service.completeSuccess(index: 0, fleet: Self.oneCoverPrinterFleet())
        await vm.waitForCommittedGeneration(atLeast: 1)
        XCTAssertEqual(vm.dispatchedRequestCount, 1,
                       "The .connected transition following configure-while-reconnecting MUST dispatch exactly one recovery refetch.")
    }

    /// Reviewer blocker B: a repeat configure/replacement AROUND a
    /// reconnect cycle must not stack refetches and must not miss
    /// them either. Sequence:
    ///   1. configure (initial state disconnected) → cold classifier
    ///   2. simulate `.connected` (cold) → no refetch, classifier arms
    ///   3. simulate `.reconnecting`
    ///   4. RE-configure the same service while `.reconnecting`
    ///      → new subscription's initial state is `.reconnecting`
    ///      → pre-seed classifier so next `.connected` is recovery
    ///   5. simulate `.connected` → exactly one recovery refetch
    func testRepeatConfigureAroundReconnectDispatchesExactlyOneRecovery() async throws {
        let service = ControlledFilamentCoverageService()
        let signalR = MockSignalRService()
        let vm = FarmFilamentCoverageViewModel()
        vm.configure(coverageService: service)

        // Step 1: initial configure (disconnected).
        vm.configureSignalR(signalR)
        XCTAssertFalse(vm.hasSeenAnyConnectedForTesting)

        // Step 2: cold `.connected` — no refetch.
        let tickA = vm.callbackTickForTesting
        signalR.simulateConnectionStateChange(.connected)
        await vm.waitForCallbackTick(atLeast: tickA + 1)
        XCTAssertEqual(vm.dispatchedRequestCount, 0)

        // Step 3: reconnecting.
        signalR.simulateConnectionStateChange(.reconnecting)

        // Step 4: re-configure while reconnecting.
        vm.configureSignalR(signalR)
        XCTAssertTrue(vm.hasSeenAnyConnectedForTesting,
                      "Re-configure while reconnecting must re-seed classifier as already-seen.")

        // Step 5: recovery `.connected` — exactly one refetch.
        signalR.simulateConnectionStateChange(.connected)
        await service.awaitPending(count: 1)
        await service.completeSuccess(index: 0, fleet: Self.oneCoverPrinterFleet())
        await vm.waitForCommittedGeneration(atLeast: 1)
        XCTAssertEqual(vm.dispatchedRequestCount, 1,
                       "Reconfigure-during-reconnecting must dispatch exactly one recovery refetch on the next .connected.")
    }

    // MARK: - Teardown safety (reviewer blocker A + C)

    /// Structural absence: after `tearDownSignalR`, the hub has zero
    /// subscribers so no callback body is ever invoked. Immediate
    /// assertion is authoritative — no barrier or yield needed.
    func testTeardownRemovesSubscribersAndBlocksInvalidationDelivery() async throws {
        let service = ControlledFilamentCoverageService()
        let signalR = MockSignalRService()
        let vm = FarmFilamentCoverageViewModel()
        vm.configure(coverageService: service)
        vm.configureSignalR(signalR)

        async let initial: Void = vm.load()
        await service.awaitPending(count: 1)
        await service.completeSuccess(index: 0, fleet: Self.oneCoverPrinterFleet())
        _ = await initial
        XCTAssertEqual(vm.dispatchedRequestCount, 1)

        vm.tearDownSignalR()
        XCTAssertEqual(signalR.filamentCoverageSubscriberCount, 0,
                       "Structural proof: no filament-coverage subscriber remains after teardown.")
        XCTAssertEqual(signalR.connectionStateSubscriberCount, 0,
                       "Structural proof: no connection-state subscriber remains after teardown.")

        signalR.simulateFilamentCoverageChanged(FilamentCoverageChangedEvent(
            printerId: nil, reason: "post-teardown", occurredAt: Date()
        ))
        // No handler is registered — the hub's synchronous deliver
        // path drains zero handlers and returns. Immediate assertion.
        XCTAssertEqual(vm.dispatchedRequestCount, 1,
                       "Post-teardown invalidation cannot trigger a refetch (no subscriber to fire).")
    }

    // MARK: - Owner-epoch authority (reviewer blocker A)
    //
    // Prove that replacing the coverage service or the SignalR
    // service INVALIDATES the old owner's in-flight work and
    // callbacks. Two controlled services let us race an old
    // completion against a replacement's fresh load.

    /// A queued OLD-service invalidation callback firing after the
    /// SignalR service has been replaced must not surface any GET on
    /// either the old or the replacement service. The old
    /// subscription is structurally CANCELLED by re-configure, so
    /// the callback body never runs — we prove that via subscriber
    /// counts (deterministic, no barrier needed).
    func testQueuedOldServiceInvalidationAfterReplaceCausesZeroGets() async throws {
        let oldCoverage = ControlledFilamentCoverageService()
        let oldSignalR = MockSignalRService()
        let vm = FarmFilamentCoverageViewModel()
        vm.configure(coverageService: oldCoverage)
        vm.configureSignalR(oldSignalR)
        XCTAssertEqual(oldSignalR.filamentCoverageSubscriberCount, 1,
                       "Precondition: old signalR has one subscriber.")

        // Replace the SignalR service. Reconfigure cancels the OLD
        // subscription and registers on the NEW hub.
        let newSignalR = MockSignalRService()
        vm.configureSignalR(newSignalR)
        XCTAssertEqual(oldSignalR.filamentCoverageSubscriberCount, 0,
                       "Replacement configure must cancel the old-service subscription.")
        XCTAssertEqual(newSignalR.filamentCoverageSubscriberCount, 1)

        // Fire an invalidation on the OLD (now-detached) signalR.
        // The old hub has zero subscribers, so no callback body
        // runs. Immediate assertion is authoritative.
        oldSignalR.simulateFilamentCoverageChanged(FilamentCoverageChangedEvent(
            printerId: nil, reason: "stale", occurredAt: Date()
        ))

        let oldPending = await oldCoverage.pendingCount
        XCTAssertEqual(oldPending, 0,
                       "Stale invalidation MUST NOT dispatch a GET against any service.")
        XCTAssertEqual(vm.dispatchedRequestCount, 0,
                       "No load was dispatched by the stale event.")
    }

    /// An OLD-service in-flight SUCCESS must NOT overwrite the
    /// replacement's state.
    func testInflightOldServiceSuccessCannotOverwriteReplacement() async throws {
        let oldCoverage = ControlledFilamentCoverageService()
        let vm = FarmFilamentCoverageViewModel()
        vm.configure(coverageService: oldCoverage)

        // Kick off a load against the old service; it will be
        // suspended at the actor's continuation.
        async let staleLoad: Void = vm.load()
        await oldCoverage.awaitPending(count: 1)

        // Replace the coverage service before the old load resolves.
        let newCoverage = ControlledFilamentCoverageService()
        vm.configure(coverageService: newCoverage)

        // Kick off a load against the new service and let it commit.
        async let freshLoad: Void = vm.load()
        await newCoverage.awaitPending(count: 1)
        await newCoverage.completeSuccess(index: 0, fleet:
            Self.fleet(evaluatedAt: Date(timeIntervalSinceReferenceDate: 100),
                       printerName: "Fresh"))
        _ = await freshLoad
        await vm.waitForCommittedGeneration(atLeast: 2)

        // Now resolve the old load with a DIFFERENT fleet. It should
        // be dropped by the owner-epoch gate.
        await oldCoverage.completeSuccess(index: 0, fleet:
            Self.fleet(evaluatedAt: Date(timeIntervalSinceReferenceDate: 200),
                       printerName: "Stale"))
        _ = await staleLoad

        XCTAssertEqual(vm.coverageByPrinter.values.first?.printerName, "Fresh",
                       "Stale in-flight success from the OLD service MUST NOT overwrite the replacement's snapshot.")
    }

    /// An OLD-service in-flight FEATURE-DISABLED completion must NOT
    /// tombstone the replacement.
    func testInflightOldServiceFeatureDisabledCannotTombstoneReplacement() async throws {
        let oldCoverage = ControlledFilamentCoverageService()
        let vm = FarmFilamentCoverageViewModel()
        vm.configure(coverageService: oldCoverage)

        async let staleLoad: Void = vm.load()
        await oldCoverage.awaitPending(count: 1)

        let newCoverage = ControlledFilamentCoverageService()
        vm.configure(coverageService: newCoverage)

        async let freshLoad: Void = vm.load()
        await newCoverage.awaitPending(count: 1)
        await newCoverage.completeSuccess(index: 0, fleet: Self.oneCoverPrinterFleet())
        _ = await freshLoad
        await vm.waitForCommittedGeneration(atLeast: 2)
        XCTAssertFalse(vm.isFeatureDisabled)

        // Stale load resolves with feature-disabled AFTER fresh
        // committed. Must be dropped.
        await oldCoverage.completeFeatureDisabled(index: 0)
        _ = await staleLoad

        XCTAssertFalse(vm.isFeatureDisabled,
                       "Stale featureDisabled from the OLD service MUST NOT tombstone the replacement.")
        XCTAssertEqual(vm.coverageByPrinter.count, 1)
    }

    /// An OLD-service in-flight ERROR must NOT overwrite the
    /// replacement's `lastLoadError`.
    func testInflightOldServiceErrorCannotOverwriteReplacement() async throws {
        let oldCoverage = ControlledFilamentCoverageService()
        let vm = FarmFilamentCoverageViewModel()
        vm.configure(coverageService: oldCoverage)

        async let staleLoad: Void = vm.load()
        await oldCoverage.awaitPending(count: 1)

        let newCoverage = ControlledFilamentCoverageService()
        vm.configure(coverageService: newCoverage)

        async let freshLoad: Void = vm.load()
        await newCoverage.awaitPending(count: 1)
        await newCoverage.completeSuccess(index: 0, fleet: Self.oneCoverPrinterFleet())
        _ = await freshLoad
        await vm.waitForCommittedGeneration(atLeast: 2)
        XCTAssertNil(vm.lastLoadError)

        await oldCoverage.completeError(index: 0, error: NetworkError.serverError(503))
        _ = await staleLoad

        XCTAssertNil(vm.lastLoadError,
                     "Stale server-error from the OLD service MUST NOT overwrite the replacement's clean state.")
    }

    func testTeardownBeforeDrainCausesNoCommit() async throws {
        let coverage = ControlledFilamentCoverageService()
        let vm = FarmFilamentCoverageViewModel()
        vm.configure(coverageService: coverage)

        async let staleLoad: Void = vm.load()
        await coverage.awaitPending(count: 1)

        // Bump the authority epoch (mimic teardown / reconfigure)
        // WITHOUT touching SignalR. Any epoch-bumping call
        // invalidates the in-flight load's captured epoch.
        vm.configure(coverageService: coverage)

        // Resolving the (now stale-epoch) in-flight load must NOT
        // commit. `staleLoad` returns silently after its post-await
        // epoch guard fails.
        await coverage.completeSuccess(index: 0, fleet: Self.oneCoverPrinterFleet())
        _ = await staleLoad

        XCTAssertTrue(vm.coverageByPrinter.isEmpty,
                      "Teardown-during-in-flight-load must prevent commit.")
        XCTAssertEqual(vm.lastCommittedGenerationForTesting, 0)
    }

    /// Teardown WITHOUT ever draining a callback triggers zero
    /// callback dispatches. Structural: no subscribers ⇒ no GETs.
    func testTeardownBeforeAnyCallbackCausesNoGets() async throws {
        let coverage = ControlledFilamentCoverageService()
        let signalR = MockSignalRService()
        let vm = FarmFilamentCoverageViewModel()
        vm.configure(coverageService: coverage)
        vm.configureSignalR(signalR)
        vm.tearDownSignalR()
        XCTAssertEqual(signalR.filamentCoverageSubscriberCount, 0)
        XCTAssertEqual(signalR.connectionStateSubscriberCount, 0)

        // Any event after teardown falls through to zero handlers.
        signalR.simulateFilamentCoverageChanged(FilamentCoverageChangedEvent(
            printerId: nil, reason: "unheard", occurredAt: Date()
        ))

        let pending = await coverage.pendingCount
        XCTAssertEqual(pending, 0,
                       "Post-teardown events must not dispatch any GET.")
        XCTAssertEqual(vm.dispatchedRequestCount, 0)
    }

    // MARK: - Fixtures

    private static func oneCoverPrinterFleet() -> FleetFilamentCoverage {
        fleet(evaluatedAt: Date(), printerName: "Alpha")
    }

    private static func fleet(evaluatedAt: Date, printerName: String) -> FleetFilamentCoverage {
        FleetFilamentCoverage(
            printers: [
                PrinterFilamentCoverage(
                    printerId: UUID(uuidString: "11111111-1111-1111-1111-111111111111")!,
                    printerName: printerName,
                    status: .covers,
                    toolheads: [
                        ToolheadFilamentCoverage(
                            toolheadIndex: 0,
                            toolheadName: "Ext 1",
                            status: .covers
                        )
                    ],
                    activeJobId: nil,
                    activeJobName: nil,
                    activeJobProgress: nil,
                    earliestPredictedRunoutAt: nil,
                    assignedQueuedJobCount: 0,
                    evaluatedAtUtc: evaluatedAt
                )
            ],
            evaluatedAtUtc: evaluatedAt
        )
    }
}
