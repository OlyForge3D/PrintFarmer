import XCTest
import UserNotifications
@testable import PrintFarmer

/// Issue #1321 — lock-screen/Notification Center action buttons for
/// job-attention notifications (Pause/Resume/Cancel/Snooze/Open Swap).
///
/// Covers:
///  * Category/action registration (`registerNotificationCategories`).
///  * `handleJobAttentionAction` wiring each action identifier to the
///    correct service call, using `printerId`/`itemId` extracted from the
///    notification's `userInfo`.
///  * Defensive no-ops when a required service or identifier is missing.
///  * Open Swap forwarding through the same tap-routing notification the
///    existing deep-link flow already relies on.
@MainActor
final class JobAttentionNotificationActionsTests: XCTestCase {
    private var printerService: MockPrinterService!
    private var attentionService: MockAttentionService!
    private let validOriginServerId = "00000000-0000-0000-0000-000000000010"
    private var testDefaults: UserDefaults!
    private var testRegistry: ServerRegistry!
    private var testSuiteName: String!

    override func setUp() {
        super.setUp()
        printerService = MockPrinterService()
        attentionService = MockAttentionService()
        testSuiteName = "JobAttentionNotificationActionsTests-\(UUID().uuidString)"
        testDefaults = UserDefaults(suiteName: testSuiteName)
        testRegistry = ServerRegistry(userDefaults: testDefaults, migrateLegacyServerURL: false)
        let server = try! testRegistry.add(
            displayName: "Primary",
            baseURL: URL(string: "https://primary.example")!
        )
        try! testRegistry.associateOriginServerId(
            UUID(uuidString: validOriginServerId)!,
            with: server.id
        )
        PushNotificationManager.shared.configure(
            notificationService: MockNotificationService(),
            serverRegistry: testRegistry,
            serverID: server.id
        )
        PushNotificationManager.shared.configureActionHandling(
            printerService: printerService,
            attentionService: attentionService
        )
    }

    override func tearDown() {
        PushNotificationManager.shared.configure(
            notificationService: MockNotificationService()
        )
        testDefaults.removePersistentDomain(forName: testSuiteName)
        testSuiteName = nil
        testDefaults = nil
        testRegistry = nil
        printerService = nil
        attentionService = nil
        super.tearDown()
    }

    // MARK: - Category / action registration

    func testRegisterNotificationCategoriesRegistersJobAttentionCategoryWithAllFiveActions() async {
        PushNotificationManager.registerNotificationCategories()

        let categories = await UNUserNotificationCenter.current().notificationCategories()
        let jobAttention = categories.first { $0.identifier == PushNotificationManager.jobAttentionCategory }
        let category = try? XCTUnwrap(jobAttention)
        guard let category else {
            return XCTFail("Expected a registered JOB_ATTENTION category")
        }

        let actionIdentifiers = Set(category.actions.map(\.identifier))
        XCTAssertEqual(
            actionIdentifiers,
            Set([
                JobAttentionAction.pauseJob.rawValue,
                JobAttentionAction.resumeJob.rawValue,
                JobAttentionAction.cancelJob.rawValue,
                JobAttentionAction.snooze.rawValue,
                JobAttentionAction.openSwap.rawValue
            ]),
            "All five job-attention actions must be registered on the category."
        )
    }

    func testCancelActionIsDestructiveAndRequiresAuthentication() async {
        PushNotificationManager.registerNotificationCategories()

        let categories = await UNUserNotificationCenter.current().notificationCategories()
        let category = try? XCTUnwrap(
            categories.first { $0.identifier == PushNotificationManager.jobAttentionCategory }
        )
        let cancelAction = category?.actions.first { $0.identifier == JobAttentionAction.cancelJob.rawValue }
        let action = try? XCTUnwrap(cancelAction)
        guard let action else {
            return XCTFail("Expected a registered CANCEL_JOB action")
        }

        XCTAssertTrue(action.options.contains(.destructive))
        XCTAssertTrue(action.options.contains(.authenticationRequired))
    }

    func testOpenSwapActionIsForeground() async {
        PushNotificationManager.registerNotificationCategories()

        let categories = await UNUserNotificationCenter.current().notificationCategories()
        let category = try? XCTUnwrap(
            categories.first { $0.identifier == PushNotificationManager.jobAttentionCategory }
        )
        let openSwapAction = category?.actions.first { $0.identifier == JobAttentionAction.openSwap.rawValue }
        let action = try? XCTUnwrap(openSwapAction)
        guard let action else {
            return XCTFail("Expected a registered OPEN_SWAP action")
        }

        XCTAssertTrue(action.options.contains(.foreground))
    }

    // MARK: - Pause / Resume / Cancel wiring

    func testPauseJobActionCallsPrinterServicePauseWithPrinterIdFromUserInfo() async {
        let printerId = UUID()
        await PushNotificationManager.shared.handleJobAttentionAction(
            .pauseJob,
            userInfo: ["printerId": printerId.uuidString, "originServerId": validOriginServerId]
        )

        XCTAssertEqual(printerService.pauseCalledWith, printerId)
    }

    func testResumeJobActionCallsPrinterServiceResumeWithPrinterIdFromUserInfo() async {
        let printerId = UUID()
        await PushNotificationManager.shared.handleJobAttentionAction(
            .resumeJob,
            userInfo: ["printerId": printerId.uuidString, "originServerId": validOriginServerId]
        )

        XCTAssertEqual(printerService.resumeCalledWith, printerId)
    }

    func testCancelJobActionCallsPrinterServiceCancelWithPrinterIdFromUserInfo() async {
        let printerId = UUID()
        await PushNotificationManager.shared.handleJobAttentionAction(
            .cancelJob,
            userInfo: ["printerId": printerId.uuidString, "originServerId": validOriginServerId]
        )

        XCTAssertEqual(printerService.cancelCalledWith, printerId)
    }

    func testPauseJobActionIsANoOpWithoutPrinterId() async {
        await PushNotificationManager.shared.handleJobAttentionAction(.pauseJob, userInfo: [:])

        XCTAssertNil(printerService.pauseCalledWith)
    }

    func testPauseJobActionIsANoOpWithMalformedPrinterId() async {
        await PushNotificationManager.shared.handleJobAttentionAction(
            .pauseJob,
            userInfo: ["printerId": "not-a-uuid"]
        )

        XCTAssertNil(printerService.pauseCalledWith)
    }

    func testMutatingActionRejectsNotificationFromDifferentServer() async throws {
        let suiteName = "JobAttentionNotificationActionsTests-\(UUID().uuidString)"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suiteName))
        defer { defaults.removePersistentDomain(forName: suiteName) }
        let registry = ServerRegistry(
            userDefaults: defaults,
            migrateLegacyServerURL: false
        )
        let server = try registry.add(
            displayName: "Primary",
            baseURL: URL(string: "https://primary.example")!
        )
        let expectedOrigin = UUID(uuidString: "00000000-0000-0000-0000-000000000010")!
        try registry.associateOriginServerId(expectedOrigin, with: server.id)
        PushNotificationManager.shared.configure(
            notificationService: MockNotificationService(),
            serverRegistry: registry,
            serverID: server.id
        )

        await PushNotificationManager.shared.handleJobAttentionAction(
            .pauseJob,
            userInfo: [
                "printerId": UUID().uuidString,
                "originServerId": "00000000-0000-0000-0000-000000000011"
            ]
        )

        XCTAssertNil(printerService.pauseCalledWith)
    }

    func testPauseJobActionSwallowsServiceErrorsWithoutCrashing() async {
        printerService.errorToThrow = NetworkError.notFound
        let printerId = UUID()

        await PushNotificationManager.shared.handleJobAttentionAction(
            .pauseJob,
            userInfo: ["printerId": printerId.uuidString, "originServerId": validOriginServerId]
        )

        XCTAssertEqual(printerService.pauseCalledWith, printerId,
                       "The call must still be attempted even though it will fail.")
    }

    // MARK: - Snooze wiring

    func testSnoozeActionCallsAttentionServiceSnoozeWithItemIdAndFutureDeadline() async {
        let before = Date()
        await PushNotificationManager.shared.handleJobAttentionAction(
            .snooze,
            userInfo: ["itemId": "failure:abc-123", "originServerId": validOriginServerId]
        )
        let after = Date()

        let call = try? XCTUnwrap(attentionService.snoozeCalledWith)
        guard let call else {
            return XCTFail("Expected snooze to be called")
        }
        XCTAssertEqual(call.itemId, "failure:abc-123")
        XCTAssertGreaterThan(call.snoozedUntilUtc, before.addingTimeInterval(59 * 60),
                             "Snooze deadline should be roughly one hour out.")
        XCTAssertLessThan(call.snoozedUntilUtc, after.addingTimeInterval(61 * 60))
    }

    func testSnoozeActionIsANoOpWithoutItemId() async {
        await PushNotificationManager.shared.handleJobAttentionAction(.snooze, userInfo: [:])

        XCTAssertNil(attentionService.snoozeCalledWith)
    }

    func testSnoozeActionIsANoOpWithEmptyItemId() async {
        await PushNotificationManager.shared.handleJobAttentionAction(
            .snooze,
            userInfo: ["itemId": ""]
        )

        XCTAssertNil(attentionService.snoozeCalledWith)
    }

    // MARK: - Open Swap forwards to existing deep-link routing

    func testOpenSwapActionPostsPushNotificationTappedWithSameUserInfo() async {
        let printerId = UUID()
        let userInfo: [AnyHashable: Any] = [
            "deepLink": "printfarmer://printer/\(printerId.uuidString)",
            "originServerId": validOriginServerId
        ]

        var receivedLink: String?
        let observer = NotificationCenter.default.addObserver(
            forName: .pushNotificationTapped,
            object: nil,
            queue: nil
        ) { notification in
            receivedLink = notification.userInfo?["deepLink"] as? String
        }
        defer { NotificationCenter.default.removeObserver(observer) }

        await PushNotificationManager.shared.handleJobAttentionAction(.openSwap, userInfo: userInfo)

        XCTAssertEqual(receivedLink, userInfo["deepLink"] as? String,
                       "Open Swap must forward through the existing tap-routing notification.")
    }

    // MARK: - Missing services degrade gracefully

    func testActionErrorsFromAnUnwiredServiceAreSwallowedWithoutCrashing() async {
        // Simulate an action delivered before real services are attached
        // (e.g. very early cold start): the printer service always fails.
        PushNotificationManager.shared.configureActionHandling(
            printerService: NoOpPrinterService(),
            attentionService: MockAttentionService()
        )

        // Must not crash / throw — errors are logged, not surfaced.
        await PushNotificationManager.shared.handleJobAttentionAction(
            .pauseJob,
            userInfo: [
                "printerId": UUID().uuidString,
                "originServerId": validOriginServerId
            ]
        )
    }
}

/// Minimal always-failing `PrinterServiceProtocol` stand-in for the
/// "unwired" scenario above. Only the members exercised by job-attention
/// action handling need real behavior; everything else is unreachable here.
private final class NoOpPrinterService: PrinterServiceProtocol, @unchecked Sendable {
    func list(includeDisabled: Bool) async throws -> [Printer] { [] }
    func get(id: UUID) async throws -> Printer { throw NetworkError.notFound }
    func getDetails(id: UUID) async throws -> PrinterDetails { throw NetworkError.notFound }
    func getStatus(id: UUID) async throws -> PrinterStatusDetail { throw NetworkError.notFound }
    func listCameraUrls() async throws -> [PrinterCameraUrls] { [] }
    func getCameraUrl(id: UUID) async throws -> PrinterCameraUrl { throw NetworkError.notFound }
    func getSnapshot(id: UUID) async throws -> Data { Data() }
    func getCurrentJob(id: UUID) async throws -> PrintJobStatusInfo? { nil }
    func getHistory(id: UUID, limit: Int?) async throws -> PrinterHistoryList { PrinterHistoryList(count: 0, jobs: []) }
    func pause(id: UUID) async throws -> CommandResult { throw NetworkError.notFound }
    func resume(id: UUID) async throws -> CommandResult { throw NetworkError.notFound }
    func cancel(id: UUID) async throws -> CommandResult { throw NetworkError.notFound }
    func stop(id: UUID) async throws -> CommandResult { throw NetworkError.notFound }
    func emergencyStop(id: UUID) async throws -> CommandResult { throw NetworkError.notFound }
    func setMaintenanceMode(id: UUID, inMaintenance: Bool, reviewedRowVersion: String) async throws -> Printer {
        throw NetworkError.notFound
    }
    func getQueueOverview(model: String?, nozzle: Double?, material: String?) async throws -> [QueueOverview] { [] }
    func setActiveSpool(printerId: UUID, spoolId: Int?, reviewedRowVersion: String) async throws -> CommandResult {
        throw NetworkError.notFound
    }
    func bindToolheadSpool(
        printerId: UUID,
        toolheadIndex: Int,
        request: ToolheadSpoolBindRequest,
        idempotencyKey: String
    ) async throws -> CommandResult { throw NetworkError.notFound }
    func listAvailableSpools(printerId: UUID) async throws -> [SpoolmanSpool] { [] }
    func loadFilament(printerId: UUID) async throws -> CommandResult { throw NetworkError.notFound }
    func unloadFilament(printerId: UUID) async throws -> CommandResult { throw NetworkError.notFound }
    func changeFilament(printerId: UUID) async throws -> CommandResult { throw NetworkError.notFound }
    func getBackendCapabilities(printerId: UUID) async throws -> PrinterBackendCapabilities {
        throw NetworkError.notFound
    }
    func setTemperatures(printerId: UUID, hotend: Double?, bed: Double?) async throws {}
    func home(printerId: UUID, axes: [String]) async throws {}
    func homeXY(printerId: UUID) async throws {}
    func homeZ(printerId: UUID) async throws {}
    func move(printerId: UUID, axis: String, distanceMm: Double, feedrateMmMin: Int) async throws {}
    func listFallbackGroups(printerId: UUID) async throws -> [FilamentFallbackGroup] { [] }
    func getFallbackGroup(printerId: UUID, groupId: UUID) async throws -> FilamentFallbackGroup {
        throw NetworkError.notFound
    }
    func createFallbackGroup(
        printerId: UUID,
        _ request: CreateFilamentFallbackGroupRequest
    ) async throws -> FilamentFallbackGroup { throw NetworkError.notFound }
    func updateFallbackGroup(
        printerId: UUID,
        groupId: UUID,
        _ request: UpdateFilamentFallbackGroupRequest
    ) async throws -> FilamentFallbackGroup { throw NetworkError.notFound }
    func deleteFallbackGroup(printerId: UUID, groupId: UUID) async throws {}
    func getAvailableFallback(
        printerId: UUID,
        sourceToolheadId: UUID,
        material: String
    ) async throws -> AvailableFallbackMember? { nil }
}
