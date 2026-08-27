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

    func testCapabilityDisableClearsCoverageAndStopsFurtherProbes() async {
        let service = ControlledFilamentCoverageService()
        let signalR = MockSignalRService()
        let vm = FarmFilamentCoverageViewModel()
        vm.configure(coverageService: service)
        vm.configureSignalR(signalR)

        async let initial: Void = vm.load()
        await service.awaitPending(count: 1)
        await service.completeSuccess(index: 0, fleet: Self.oneCoverPrinterFleet())
        _ = await initial
        XCTAssertEqual(vm.coverageByPrinter.count, 1)

        vm.disableForCapabilityGate()
        await vm.load()

        XCTAssertTrue(vm.isFeatureDisabled)
        XCTAssertTrue(vm.coverageByPrinter.isEmpty)
        XCTAssertNil(vm.lastLoadError)
        XCTAssertFalse(vm.isShowingStaleCache)
        XCTAssertEqual(vm.dispatchedRequestCount, 1)
        XCTAssertEqual(signalR.filamentCoverageSubscriberCount, 0)
        XCTAssertEqual(signalR.connectionStateSubscriberCount, 0)
    }

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

    #if DEBUG
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
    #endif

    // MARK: - Reconnect classification (reviewer blocker B + C)

    /// Cold-start `.connected` MUST NOT double-load. Absence proven
    /// deterministically via `waitForCallbackTick` — no yield gate.
    #if DEBUG
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
    #endif

    #if DEBUG
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
    #endif

    /// Reviewer blocker B: configure while the hub is ALREADY
    /// `.reconnecting`. The next `.connected` MUST dispatch a
    /// recovery refetch (not be mis-classified as cold-start).
    #if DEBUG
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
    #endif

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
    #if DEBUG
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
    #endif

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
    #if DEBUG
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
    #endif

    /// An OLD-service in-flight FEATURE-DISABLED completion must NOT
    /// tombstone the replacement.
    #if DEBUG
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
    #endif

    /// An OLD-service in-flight ERROR must NOT overwrite the
    /// replacement's `lastLoadError`.
    #if DEBUG
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
    #endif

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

    // MARK: - Split-authority proofs (cycle-3 round-2 blocker H)

    /// A still-current SignalR invalidation callback that DRAINS
    /// AFTER coverage A→B replacement must dispatch exactly ONE
    /// load — against the CURRENT (B) coverage service — and commit
    /// under B's coverage authority. The SignalR epoch was NOT
    /// bumped by the coverage replace, so the callback stays valid.
    ///
    /// Freezing sequence (deterministic, no yield): the test is on
    /// MainActor and does not `await` between `simulate…` and
    /// `configure(coverageService: B)`. The Task the invalidation
    /// callback enqueues therefore cannot start running until the
    /// test yields at `await B.awaitPending`. That gives us the
    /// EXACT `emit → callback queued but not drained → replace →
    /// drain` sequence Hicks required.
    #if DEBUG
    func testStillCurrentSignalRInvalidationDrainsAgainstReplacementCoverage() async throws {
        let coverageA = ControlledFilamentCoverageService()
        let coverageB = ControlledFilamentCoverageService()
        let signalR = MockSignalRService()
        let vm = FarmFilamentCoverageViewModel()
        vm.configure(coverageService: coverageA)
        vm.configureSignalR(signalR)

        // Baseline load against A so we have a known request count.
        async let baseline: Void = vm.load()
        await coverageA.awaitPending(count: 1)
        await coverageA.completeSuccess(index: 0, fleet: Self.fleet(
            evaluatedAt: Date(timeIntervalSinceReferenceDate: 1),
            printerName: "A-baseline"))
        _ = await baseline
        XCTAssertEqual(vm.dispatchedRequestCount, 1)

        let sigEpochBefore = vm.signalRAuthorityEpochForTesting

        // 1. Emit invalidation → callback Task enqueued on MainActor.
        //    Test is on MainActor and does not yield → callback body
        //    cannot run yet.
        signalR.simulateFilamentCoverageChanged(FilamentCoverageChangedEvent(
            printerId: nil, reason: "still-current-sub", occurredAt: Date()
        ))

        // 2. Replace coverage BEFORE the callback drains. Bumps ONLY
        //    the coverage epoch. SignalR epoch is unchanged, so the
        //    queued callback remains valid.
        vm.configure(coverageService: coverageB)
        XCTAssertEqual(vm.signalRAuthorityEpochForTesting, sigEpochBefore,
                       "Coverage replacement must NOT bump the SignalR epoch.")

        // 3. Drain — the queued callback runs, sees its SignalR
        //    epoch still current, and dispatches a load. That load
        //    captures the CURRENT coverage epoch (B's) and hits
        //    coverageB.
        await coverageB.awaitPending(count: 1)
        let __pending = await coverageA.pendingCount
        XCTAssertEqual(__pending, 0,
                       "The drained callback MUST dispatch against B (the current owner), not A.")

        // 4. Complete B's load and let the commit land.
        await coverageB.completeSuccess(index: 0, fleet: Self.fleet(
            evaluatedAt: Date(timeIntervalSinceReferenceDate: 2),
            printerName: "B-from-invalidation"))
        await vm.waitForCommittedGeneration(atLeast: 2)

        XCTAssertEqual(vm.dispatchedRequestCount, 2,
                       "Exactly one refetch was dispatched by the drained callback.")
        XCTAssertEqual(vm.coverageByPrinter.values.first?.printerName, "B-from-invalidation",
                       "The commit must reflect the B-service snapshot, not A's.")
    }
    #endif

    /// Reconnect recovery symmetrically survives a coverage
    /// replacement: after A→B swap, a still-current SignalR
    /// `.reconnecting → .connected` transition must dispatch
    /// exactly one recovery load against B.
    #if DEBUG
    func testStillCurrentSignalRReconnectRecoveryDrainsAgainstReplacementCoverage() async throws {
        let coverageA = ControlledFilamentCoverageService()
        let coverageB = ControlledFilamentCoverageService()
        let signalR = MockSignalRService()
        let vm = FarmFilamentCoverageViewModel()
        vm.configure(coverageService: coverageA)
        vm.configureSignalR(signalR)

        // Cold `.connected` first (arm the classifier so the next
        // transition counts as recovery). Absence gated by
        // waitForCallbackTick.
        let tickBefore = vm.callbackTickForTesting
        signalR.simulateConnectionStateChange(.connected)
        await vm.waitForCallbackTick(atLeast: tickBefore + 1)
        XCTAssertEqual(vm.dispatchedRequestCount, 0)

        // 1. Emit `.reconnecting` then `.connected` — the second
        //    IS recovery. Two callback Tasks enqueued.
        signalR.simulateConnectionStateChange(.reconnecting)
        signalR.simulateConnectionStateChange(.connected)

        // 2. Replace coverage BEFORE the recovery Task drains. Only
        //    coverage epoch bumps; SignalR remains current.
        vm.configure(coverageService: coverageB)

        // 3. Drain — the recovery callback dispatches ONE load
        //    against B (current coverage owner). A sees zero
        //    pending; only B does.
        await coverageB.awaitPending(count: 1)
        let __pending = await coverageA.pendingCount
        XCTAssertEqual(__pending, 0,
                       "Reconnect recovery MUST dispatch against B, not A.")

        await coverageB.completeSuccess(index: 0, fleet: Self.fleet(
            evaluatedAt: Date(timeIntervalSinceReferenceDate: 10),
            printerName: "B-from-reconnect"))
        await vm.waitForCommittedGeneration(atLeast: 1)

        XCTAssertEqual(vm.dispatchedRequestCount, 1,
                       "Exactly one recovery refetch was dispatched.")
        XCTAssertEqual(vm.coverageByPrinter.values.first?.printerName, "B-from-reconnect")
    }
    #endif

    /// S1 emits an invalidation → callback Task queued but not
    /// drained → `configureSignalR(S2)` replaces the subscription
    /// (bumps SignalR epoch) → drain. The S1 callback's captured
    /// epoch is stale, so it no-ops. Zero GETs on either service.
    ///
    /// This is the genuine "queued-before-replace" proof Hicks
    /// required — NOT a substituted `subscriberCount == 0` test.
    #if DEBUG
    func testS1InvalidationQueuedBeforeS2ReplaceDrainsAsNoOp() async throws {
        let coverage = ControlledFilamentCoverageService()
        let s1 = MockSignalRService()
        let s2 = MockSignalRService()
        let vm = FarmFilamentCoverageViewModel()
        vm.configure(coverageService: coverage)
        vm.configureSignalR(s1)

        let s1TickBefore = vm.callbackTickForTesting

        // 1. Emit on S1 → callback Task enqueued on MainActor.
        //    Test is on MainActor and does not yield → callback body
        //    cannot run yet.
        s1.simulateFilamentCoverageChanged(FilamentCoverageChangedEvent(
            printerId: nil, reason: "queued-before-replace", occurredAt: Date()
        ))

        // 2. Replace SignalR BEFORE the queued Task drains. This
        //    bumps the SignalR epoch; the queued Task's captured
        //    epoch is now stale.
        vm.configureSignalR(s2)

        // 3. Drain — the queued Task runs on MainActor, its guard
        //    fails, tick advances, returns. Zero dispatch.
        await vm.waitForCallbackTick(atLeast: s1TickBefore + 1)

        let pending = await coverage.pendingCount
        XCTAssertEqual(pending, 0,
                       "S1 callback drained AFTER S2 replacement MUST NOT dispatch a GET.")
        XCTAssertEqual(vm.dispatchedRequestCount, 0)
    }
    #endif

    /// S1 emits an invalidation → callback Task queued but not
    /// drained → `tearDownSignalR()` → drain. The S1 callback's
    /// captured epoch is stale, so it no-ops. Zero GETs.
    #if DEBUG
    func testS1InvalidationQueuedBeforeTeardownDrainsAsNoOp() async throws {
        let coverage = ControlledFilamentCoverageService()
        let s1 = MockSignalRService()
        let vm = FarmFilamentCoverageViewModel()
        vm.configure(coverageService: coverage)
        vm.configureSignalR(s1)

        let s1TickBefore = vm.callbackTickForTesting

        // Emit → queued.
        s1.simulateFilamentCoverageChanged(FilamentCoverageChangedEvent(
            printerId: nil, reason: "queued-before-teardown", occurredAt: Date()
        ))

        // Teardown BEFORE the queued Task drains. Bumps SignalR
        // epoch; captured epoch is stale.
        vm.tearDownSignalR()

        // Drain — queued Task runs, guard fails, tick advances.
        await vm.waitForCallbackTick(atLeast: s1TickBefore + 1)

        let pending = await coverage.pendingCount
        XCTAssertEqual(pending, 0,
                       "S1 callback drained AFTER teardown MUST NOT dispatch a GET.")
        XCTAssertEqual(vm.dispatchedRequestCount, 0)
    }
    #endif

    /// S1 emits a `.reconnecting` transition (queued) → replace S2
    /// → the queued transition drains under a stale SignalR epoch
    /// and no-ops. NO recovery is armed under S1's classifier
    /// state. This proves the classifier doesn't get corrupted by
    /// a stale queued state transition.
    #if DEBUG
    func testS1ConnectionStateChangeQueuedBeforeS2ReplaceDrainsAsNoOp() async throws {
        let coverage = ControlledFilamentCoverageService()
        let s1 = MockSignalRService()
        let s2 = MockSignalRService()
        let vm = FarmFilamentCoverageViewModel()
        vm.configure(coverageService: coverage)
        vm.configureSignalR(s1)

        // Arm S1's classifier (cold `.connected` skipped).
        let tickA = vm.callbackTickForTesting
        s1.simulateConnectionStateChange(.connected)
        await vm.waitForCallbackTick(atLeast: tickA + 1)
        XCTAssertEqual(vm.dispatchedRequestCount, 0)

        // Emit a reconnect cycle on S1 — queued but not drained.
        let tickB = vm.callbackTickForTesting
        s1.simulateConnectionStateChange(.reconnecting)
        s1.simulateConnectionStateChange(.connected)

        // Replace SignalR BEFORE those queued transitions drain.
        vm.configureSignalR(s2)

        // Drain both queued S1 transitions — both no-op under stale
        // epoch. Tick advances twice.
        await vm.waitForCallbackTick(atLeast: tickB + 2)

        let pending = await coverage.pendingCount
        XCTAssertEqual(pending, 0,
                       "Stale S1 reconnect-recovery transition MUST NOT dispatch a load.")
        XCTAssertEqual(vm.dispatchedRequestCount, 0)
    }
    #endif

    // MARK: - Configuration-order permutations (blocker H last bullet)

    /// Configure coverage FIRST, then SignalR — the standard order
    /// used by the views. Baseline behavior.
    #if DEBUG
    func testCoverageThenSignalRPermutation() async throws {
        let coverage = ControlledFilamentCoverageService()
        let signalR = MockSignalRService()
        let vm = FarmFilamentCoverageViewModel()
        vm.configure(coverageService: coverage)
        vm.configureSignalR(signalR)
        XCTAssertEqual(vm.coverageAuthorityEpochForTesting, 1)
        XCTAssertEqual(vm.signalRAuthorityEpochForTesting, 1)
    }
    #endif

    /// Configure SignalR FIRST, then coverage. The load() called
    /// via a callback captures the current coverage epoch fresh, so
    /// once coverage is set the callback dispatches correctly.
    #if DEBUG
    func testSignalRThenCoveragePermutation_CallbackDispatchesAfterCoverageAttached() async throws {
        let coverage = ControlledFilamentCoverageService()
        let signalR = MockSignalRService()
        let vm = FarmFilamentCoverageViewModel()
        vm.configureSignalR(signalR)  // coverageService is still nil
        // Emit BEFORE coverage is attached — load() guard on
        // coverage epoch will fail (coverageService == nil).
        let tickBefore = vm.callbackTickForTesting
        signalR.simulateFilamentCoverageChanged(FilamentCoverageChangedEvent(
            printerId: nil, reason: "no-coverage-yet", occurredAt: Date()
        ))
        await vm.waitForCallbackTick(atLeast: tickBefore + 1)
        let pending0 = await coverage.pendingCount
        XCTAssertEqual(pending0, 0,
                       "With coverage not yet attached, callback MUST NOT dispatch.")
        XCTAssertEqual(vm.dispatchedRequestCount, 0)

        // Attach coverage. Now a fresh emit dispatches correctly.
        vm.configure(coverageService: coverage)
        let tickBefore2 = vm.callbackTickForTesting
        signalR.simulateFilamentCoverageChanged(FilamentCoverageChangedEvent(
            printerId: nil, reason: "post-attach", occurredAt: Date()
        ))
        await coverage.awaitPending(count: 1)
        await coverage.completeSuccess(index: 0, fleet: Self.oneCoverPrinterFleet())
        await vm.waitForCallbackTick(atLeast: tickBefore2 + 1)
        XCTAssertEqual(vm.dispatchedRequestCount, 1)
    }
    #endif

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
