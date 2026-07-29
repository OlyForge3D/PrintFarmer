import XCTest
@testable import PrintFarmer

// MARK: - Allowlist, classifier, review-and-retry, and VM enqueue integration
//
// Proves the scope lock (only part adjustment + harvest can be encoded /
// enqueued — no live printer/filament command has any representation), the
// single shared failure classifier, the two-step review-and-retry-as-new
// (reads current state, mints a NEW key ONLY on explicit confirm), and the
// additive view-model enqueue-on-offline-failure integration.

@MainActor
final class OfflineWriteAllowlistTests: XCTestCase {

    // MARK: Static allowlist proof

    func testAllowlistIsExactlyFourKinds() {
        XCTAssertEqual(
            OfflineWriteKind.allCases,
            [.partAdjustment, .harvest, .taskComplete, .toolheadBind]
        )
        XCTAssertEqual(
            OfflineWriteKind.allCases.count, 4,
            "After Q2 the queue encodes EXACTLY four operation kinds — part adjustment, harvest, task complete, toolhead bind. Widening scope requires adding a case here; a live printer/filament command has no kind."
        )
    }

    func testAllowlistedOperationsRouteOnlyToPartWriteEndpoints() {
        let adjust = OfflineQueueFixtures.adjust(sku: "SKU-A", key: "k")
        let harvest = OfflineQueueFixtures.harvest(key: "k")
        XCTAssertEqual(adjust.route.method, "POST")
        XCTAssertTrue(adjust.route.path.hasPrefix("/api/parts-inventory/"))
        XCTAssertTrue(adjust.route.path.hasSuffix("/adjust"))
        XCTAssertEqual(harvest.route.method, "POST")
        XCTAssertTrue(harvest.route.path.hasPrefix("/api/job-queue/"))
        XCTAssertTrue(harvest.route.path.hasSuffix("/harvest"))
    }

    func testTaskCompleteAndToolheadBindRouteToTheirCanonicalEndpoints() {
        let complete = OfflineQueueFixtures.taskComplete(taskID: "T-1", key: "k")
        XCTAssertEqual(complete.route.method, "POST")
        XCTAssertEqual(complete.route.path, "/api/tasks/T-1/complete")

        let printerID = UUID()
        let bind = OfflineQueueFixtures.toolheadBind(printerID: printerID, toolheadIndex: 2, key: "k", spoolId: 9)
        XCTAssertEqual(bind.route.method, "PUT")
        XCTAssertEqual(bind.route.path, "/api/printers/\(printerID.uuidString)/toolheads/2/spool")
    }

    // MARK: Runtime encode/decode round-trip

    func testBothOperationsRoundTripThroughCodable() throws {
        let encoder = JSONEncoder()
        let decoder = JSONDecoder()
        for op in [OfflineQueueFixtures.adjust(sku: "SKU-A", key: "k1", delta: -3),
                   OfflineQueueFixtures.harvest(jobId: UUID(), key: "k2"),
                   OfflineQueueFixtures.taskComplete(taskID: "T-1", key: "k3"),
                   OfflineQueueFixtures.toolheadBind(toolheadIndex: 1, key: "k4", spoolId: 7, expectedPriorSpoolId: 3)] {
            let data = try encoder.encode(op)
            let decoded = try decoder.decode(OfflineWriteOperation.self, from: data)
            XCTAssertEqual(decoded, op)
        }
    }

    // MARK: Live-command discriminators cannot decode into a replayable op

    func testUnknownKindDiscriminatorsAreRejected() {
        // A representative set of live printer/filament commands that must
        // NEVER be encodable/enqueueable — each is rejected on decode.
        let liveCommands = ["pausePrint", "resumePrint", "cancelPrint",
                            "setTemperature", "moveAxis", "unloadFilament",
                            "loadFilament", "purge", "toolChange",
                            "mmuSelect", "validateSwap",
                            "taskSkip", "taskDismiss"]
        let decoder = JSONDecoder()
        for command in liveCommands {
            let json = Data("{\"kind\":\"\(command)\"}".utf8)
            XCTAssertThrowsError(try decoder.decode(OfflineWriteOperation.self, from: json)) { error in
                guard let unknown = error as? OfflineWriteOperation.UnknownKindError else {
                    return XCTFail("\(command) should be an UnknownKindError, got \(error)")
                }
                XCTAssertEqual(unknown.raw, command)
            }
        }
    }

    // MARK: Classifier mapping table

    private func reason(_ outcome: OfflineWriteReplayOutcome) -> OfflineWriteConflictReason? {
        if case .conflict(let conflict) = outcome { return conflict.reason }
        return nil
    }

    private func wrongBinConflict() -> PartsInventoryConflict {
        PartsInventoryConflict(
            code: PartsInventoryConflict.wrongBinCode, title: "Wrong bin", detail: "Scan B1",
            mismatches: [WrongBinMismatch(partSku: "SKU-A", expectedBinCode: "B1", scannedBinCode: "B2")],
            jobId: nil, projectFileId: nil, gcodeFileId: nil, guidance: nil
        )
    }

    private func mappingRequiredConflict() -> PartsInventoryConflict {
        PartsInventoryConflict(
            code: PartsInventoryConflict.partMappingRequiredCode, title: "Mapping required",
            detail: "No output mapping", mismatches: nil, jobId: UUID(),
            projectFileId: nil, gcodeFileId: nil, guidance: "Add a mapping"
        )
    }

    func testTransientAndServerFailuresAreRetryable() {
        let retryables: [NetworkError] = [
            .noConnection, .timeout, .serverUnreachable, .staleServerResponse,
            .invalidResponse, .serverError(500), .serverError(503),
            .transportError(URLError(.networkConnectionLost)),
            .clientError(408, nil), .clientError(425, nil), .clientError(429, nil)
        ]
        for error in retryables {
            XCTAssertEqual(OfflineWriteReplayClassifier.outcome(for: error), .retryable, "\(error) must be retryable")
            XCTAssertTrue(OfflineWriteReplayClassifier.isEnqueueableOfflineFailure(error), "\(error) is enqueueable")
        }
    }

    func testIdentityFailuresStopReplayWithoutMutation() {
        for error in [NetworkError.unauthorized, NetworkError.authFailed("expired")] {
            XCTAssertEqual(OfflineWriteReplayClassifier.outcome(for: error), .identityChanged)
            XCTAssertFalse(OfflineWriteReplayClassifier.isEnqueueableOfflineFailure(error))
        }
    }

    func testTerminalConflictsMapToExplicitReasons() {
        XCTAssertEqual(reason(OfflineWriteReplayClassifier.outcome(for: NetworkError.forbidden)), .authorization)
        XCTAssertEqual(reason(OfflineWriteReplayClassifier.outcome(for: .partsInventoryConflict(wrongBinConflict()))), .wrongBin)
        XCTAssertEqual(reason(OfflineWriteReplayClassifier.outcome(for: .partsInventoryConflict(mappingRequiredConflict()))), .mappingRequired)
        XCTAssertEqual(reason(OfflineWriteReplayClassifier.outcome(for: .clientError(400, nil))), .validation)
        XCTAssertEqual(reason(OfflineWriteReplayClassifier.outcome(for: .clientError(422, nil))), .validation)
        XCTAssertEqual(reason(OfflineWriteReplayClassifier.outcome(for: .conflict)), .businessConflict)
        XCTAssertEqual(reason(OfflineWriteReplayClassifier.outcome(for: .notFound)), .businessConflict)
        XCTAssertEqual(reason(OfflineWriteReplayClassifier.outcome(for: .methodNotAllowed)), .businessConflict)
        XCTAssertEqual(reason(OfflineWriteReplayClassifier.outcome(for: .clientError(409, nil))), .businessConflict)
        XCTAssertEqual(reason(OfflineWriteReplayClassifier.outcome(for: .preconditionFailed(nil))), .staleState)
        XCTAssertEqual(reason(OfflineWriteReplayClassifier.outcome(for: .preconditionRequired(nil))), .staleState)

        let idempotencyConflict = APIError(title: nil, status: 409, detail: "dup", errors: nil, message: nil, code: "idempotencyKeyConflict")
        XCTAssertEqual(reason(OfflineWriteReplayClassifier.outcome(for: .clientError(409, idempotencyConflict))), .sameKeyDifferentBody)

        // None of the terminal rejections may be silently re-enqueued.
        for error in [NetworkError.forbidden, .clientError(422, nil), .clientError(409, idempotencyConflict),
                     .partsInventoryConflict(wrongBinConflict()), .notFound,
                     .preconditionFailed(nil), .preconditionRequired(nil)] {
            XCTAssertFalse(OfflineWriteReplayClassifier.isEnqueueableOfflineFailure(error), "\(error) must NOT be enqueued")
        }
    }

    // MARK: Review-and-retry-as-new (status VM)

    func testReviewReadsCurrentStateAndMintsNewKeyOnlyOnConfirm() async {
        let serverID = UUID(); let userID = UUID()
        let store = InMemoryOfflineWriteQueueStore()
        let clock = MutableOfflineQueueClock(OfflineQueueFixtures.epoch)
        let queue = OfflineWriteQueue(
            store: store, transport: ScriptedReplayTransport(fallback: .success), clock: clock
        )
        await queue.bind(serverID: serverID, userID: userID)
        _ = await queue.enqueue(OfflineQueueFixtures.adjust(sku: "SKU-A", key: "original-key"))

        // Age the item strictly beyond the 7-day window, then a replay pass
        // parks it as expiredNeedsReview WITHOUT any network request.
        clock.advance(OfflineQueueFixtures.sevenDays + 1)
        await queue.replayPending()

        let service = MockPartsInventoryService()
        service.partsToReturn = [makePart(sku: "SKU-A", onHand: 42)]
        let vm = OfflineQueueStatusViewModel(queue: queue, partsInventoryService: service)
        await vm.refresh()
        XCTAssertEqual(vm.entries.count, 1)
        guard let entry = vm.entries.first else { return XCTFail("expected one entry") }
        XCTAssertEqual(entry.item.status, .expiredNeedsReview)

        // STEP 1 — beginReview reads current server state and stages it, but
        // mints NOTHING: the queue still holds the SAME item with the OLD key.
        await vm.beginReview(entry)
        XCTAssertEqual(service.listPartsCalls.count, 1, "review must read current server state exactly once")
        XCTAssertEqual(vm.review?.currentOnHandBySku["SKU-A"], 42)
        let beforeConfirm = await queue.items(forServer: serverID, user: userID)
        XCTAssertEqual(beforeConfirm.map { $0.idempotencyKey }, ["original-key"], "no new intent may be minted before explicit confirm")
        XCTAssertTrue(service.adjustPartCalls.isEmpty, "review must NOT silently replay")

        // STEP 2 — confirm mints a NEW key and replaces the reviewed item.
        await vm.confirmRetryAsNew()
        let afterConfirm = await queue.items(forServer: serverID, user: userID)
        XCTAssertEqual(afterConfirm.count, 1, "exactly one item remains — the renewed intent")
        let newKey = afterConfirm[0].idempotencyKey
        XCTAssertNotEqual(newKey, "original-key", "retry-as-new must mint a fresh idempotency key")
        XCTAssertTrue(afterConfirm[0].status.isPending, "the renewed intent is a fresh, replayable item")
        XCTAssertNil(vm.review)
    }

    // MARK: View-model additive enqueue on offline-class failure

    func testPartAdjustmentEnqueuesFrozenIntentOnOfflineFailure() async {
        let serverID = UUID(); let userID = UUID()
        let queue = OfflineWriteQueue(
            store: InMemoryOfflineWriteQueueStore(),
            transport: ScriptedReplayTransport(fallback: .retryable),
            clock: MutableOfflineQueueClock(OfflineQueueFixtures.epoch)
        )
        await queue.bind(serverID: serverID, userID: userID)

        let service = MockPartsInventoryService()
        service.adjustPartError = NetworkError.noConnection
        let vm = PartAdjustmentViewModel(part: makePart(sku: "SKU-A", onHand: 5))
        vm.delta = -4
        let result = await vm.submit(partsInventoryService: service, offlineQueue: queue)

        XCTAssertNil(result)
        XCTAssertNil(vm.errorMessage, "an offline-class failure is retained, not surfaced as an error")
        XCTAssertEqual(vm.successMessage, "Saved offline — will sync when reconnected.")

        let items = await queue.items(forServer: serverID, user: userID)
        XCTAssertEqual(items.count, 1)
        guard case .partAdjustment(let sku, let request) = items[0].operation else {
            return XCTFail("expected a part adjustment")
        }
        XCTAssertEqual(sku, "SKU-A")
        XCTAssertEqual(request.delta, -4, "the frozen body preserves the operator's intent")
        XCTAssertEqual(request.operationKey, items[0].idempotencyKey)
    }

    func testTerminalPartAdjustmentFailureIsNotEnqueued() async {
        let serverID = UUID(); let userID = UUID()
        let queue = OfflineWriteQueue(
            store: InMemoryOfflineWriteQueueStore(),
            transport: ScriptedReplayTransport(fallback: .retryable),
            clock: MutableOfflineQueueClock(OfflineQueueFixtures.epoch)
        )
        await queue.bind(serverID: serverID, userID: userID)

        let service = MockPartsInventoryService()
        service.adjustPartError = NetworkError.clientError(422, nil)
        let vm = PartAdjustmentViewModel(part: makePart(sku: "SKU-A", onHand: 5))
        _ = await vm.submit(partsInventoryService: service, offlineQueue: queue)

        XCTAssertNotNil(vm.errorMessage, "a validation rejection surfaces immediately")
        XCTAssertNil(vm.successMessage)
        let items = await queue.items(forServer: serverID, user: userID)
        XCTAssertTrue(items.isEmpty, "a terminal failure must never be queued")
    }

    func testHarvestEnqueuesFrozenIntentOnOfflineFailure() async {
        let serverID = UUID(); let userID = UUID()
        let queue = OfflineWriteQueue(
            store: InMemoryOfflineWriteQueueStore(),
            transport: ScriptedReplayTransport(fallback: .retryable),
            clock: MutableOfflineQueueClock(OfflineQueueFixtures.epoch)
        )
        await queue.bind(serverID: serverID, userID: userID)

        let service = MockPartsInventoryService()
        service.harvestError = NetworkError.timeout
        let job = makeJob()
        let vm = HarvestViewModel(job: job)
        await vm.submit(partsInventoryService: service, offlineQueue: queue)

        XCTAssertTrue(vm.offlineEnqueued)
        XCTAssertNil(vm.errorMessage)
        let items = await queue.items(forServer: serverID, user: userID)
        XCTAssertEqual(items.count, 1)
        guard case .harvest(let jobId, let request) = items[0].operation else {
            return XCTFail("expected a harvest")
        }
        XCTAssertEqual(jobId, job.id)
        XCTAssertEqual(request.operationKey, job.id.uuidString, "harvest reuses the per-job idempotency key")
    }

    func testHarvestWrongBinConflictIsNotEnqueued() async {
        let serverID = UUID(); let userID = UUID()
        let queue = OfflineWriteQueue(
            store: InMemoryOfflineWriteQueueStore(),
            transport: ScriptedReplayTransport(fallback: .retryable),
            clock: MutableOfflineQueueClock(OfflineQueueFixtures.epoch)
        )
        await queue.bind(serverID: serverID, userID: userID)

        let service = MockPartsInventoryService()
        service.harvestError = NetworkError.partsInventoryConflict(wrongBinConflict())
        let vm = HarvestViewModel(job: makeJob())
        await vm.submit(partsInventoryService: service, offlineQueue: queue)

        XCTAssertNotNil(vm.wrongBinConflict, "a wrong-bin conflict surfaces for operator disposition")
        XCTAssertFalse(vm.offlineEnqueued)
        let items = await queue.items(forServer: serverID, user: userID)
        XCTAssertTrue(items.isEmpty, "a typed harvest conflict must never be queued")
    }

    // MARK: Local fixtures

    private func makePart(sku: String, onHand: Int) -> PartInventoryResponse {
        PartInventoryResponse(
            id: UUID(), sku: sku, name: "Part", description: nil, modelFileRef: nil,
            defaultBinId: nil, defaultBinCode: nil, defaultBinName: nil,
            onHand: onHand, reorderPoint: 2, needsReorder: false, isActive: true,
            createdAt: .now, updatedAt: .now
        )
    }

    private func makeJob() -> PrintJob {
        PrintJob(
            id: UUID(), status: .completed, priority: 1, queuePosition: 0,
            gcodeFileId: nil, gcodeFileName: "part.gcode",
            assignedPrinterId: nil, assignedPrinterName: nil,
            createdAt: .now, updatedAt: .now, actualStartTime: nil, actualEndTime: nil,
            estimatedPrintTime: nil, actualPrintTime: nil,
            estimatedFilamentUsage: nil, actualFilamentUsage: nil,
            estimatedCost: nil, actualCost: nil, failureReason: nil,
            requiredNozzleDiameter: nil, requiredMaterialType: nil,
            spoolmanFilamentId: nil, filamentName: nil, filamentVendor: nil, filamentColor: nil,
            copies: 1, completedCopies: 1, remainingCopies: 0,
            projectFileId: nil, thumbnailUrl: nil
        )
    }
}
