import XCTest
@testable import PrintFarmer

@MainActor
final class NotificationsViewModelTests: XCTestCase {

    private var mockNotificationService: MockNotificationService!
    private var viewModel: NotificationsViewModel!

    override func setUp() {
        super.setUp()
        mockNotificationService = MockNotificationService()
        viewModel = NotificationsViewModel()
        viewModel.configure(notificationService: mockNotificationService)
    }

    override func tearDown() {
        viewModel = nil
        mockNotificationService = nil
        super.tearDown()
    }

    // MARK: - Initial State

    func testInitialState() {
        XCTAssertTrue(viewModel.notifications.isEmpty)
        XCTAssertEqual(viewModel.unreadCount, 0)
        XCTAssertFalse(viewModel.isLoading)
        XCTAssertNil(viewModel.errorMessage)
    }

    // MARK: - Load Notifications

    func testLoadNotificationsSuccess() async throws {
        let notif = try TestData.decodeAppNotification(from: TestJSON.appNotificationUnread)
        mockNotificationService.notificationsToReturn = [notif]
        mockNotificationService.unreadCountToReturn = 1

        await viewModel.loadNotifications()

        XCTAssertEqual(viewModel.notifications.count, 1)
        XCTAssertEqual(viewModel.unreadCount, 1)
        XCTAssertFalse(viewModel.isLoading)
        XCTAssertNil(viewModel.errorMessage)
    }

    func testLoadNotificationsMultiple() async throws {
        let unread = try TestData.decodeAppNotification(from: TestJSON.appNotificationUnread)
        let read = try TestData.decodeAppNotification(from: TestJSON.appNotificationRead)
        let failed = try TestData.decodeAppNotification(from: TestJSON.appNotificationFailed)
        mockNotificationService.notificationsToReturn = [unread, read, failed]
        mockNotificationService.unreadCountToReturn = 2

        await viewModel.loadNotifications()

        XCTAssertEqual(viewModel.notifications.count, 3)
        XCTAssertEqual(viewModel.unreadCount, 2)
    }

    func testLoadNotificationsEmpty() async {
        mockNotificationService.notificationsToReturn = []
        mockNotificationService.unreadCountToReturn = 0

        await viewModel.loadNotifications()

        XCTAssertTrue(viewModel.notifications.isEmpty)
        XCTAssertEqual(viewModel.unreadCount, 0)
        XCTAssertNil(viewModel.errorMessage)
    }

    func testLoadNotificationsError() async {
        mockNotificationService.errorToThrow = NetworkError.noConnection

        await viewModel.loadNotifications()

        XCTAssertNotNil(viewModel.errorMessage)
        XCTAssertFalse(viewModel.isLoading)
    }

    func testLoadNotificationsClearsErrorOnSuccess() async throws {
        mockNotificationService.errorToThrow = NetworkError.noConnection
        await viewModel.loadNotifications()
        XCTAssertNotNil(viewModel.errorMessage)

        mockNotificationService.errorToThrow = nil
        mockNotificationService.notificationsToReturn = []
        mockNotificationService.unreadCountToReturn = 0
        await viewModel.loadNotifications()

        XCTAssertNil(viewModel.errorMessage)
    }

    func testLoadWithoutConfigureDoesNotCrash() async {
        let unconfigured = NotificationsViewModel()
        await unconfigured.loadNotifications()
        XCTAssertFalse(unconfigured.isLoading)
    }

    // MARK: - Mark Read

    func testMarkReadCallsService() async throws {
        let notif = try TestData.decodeAppNotification(from: TestJSON.appNotificationUnread)
        mockNotificationService.notificationsToReturn = [notif]
        mockNotificationService.unreadCountToReturn = 0

        await viewModel.markRead(id: "notif-001")

        XCTAssertEqual(mockNotificationService.markReadCalledWith, "notif-001")
    }

    func testMarkReadReloadsNotifications() async throws {
        let notif = try TestData.decodeAppNotification(from: TestJSON.appNotificationUnread)
        mockNotificationService.notificationsToReturn = [notif]
        mockNotificationService.unreadCountToReturn = 0

        await viewModel.markRead(id: "notif-001")

        XCTAssertNotNil(mockNotificationService.listCalledWithLimit)
    }

    func testMarkReadSetsErrorOnFailure() async {
        mockNotificationService.errorToThrow = NetworkError.serverError(500)

        await viewModel.markRead(id: "notif-001")

        XCTAssertNotNil(viewModel.errorMessage)
    }

    func testMarkReadWithoutConfigureDoesNotCrash() async {
        let unconfigured = NotificationsViewModel()
        await unconfigured.markRead(id: "test")
        XCTAssertNil(unconfigured.errorMessage)
    }

    // MARK: - Mark All Read

    func testMarkAllReadCallsServiceWithUnreadIds() async throws {
        let unread = try TestData.decodeAppNotification(from: TestJSON.appNotificationUnread)
        let failed = try TestData.decodeAppNotification(from: TestJSON.appNotificationFailed)
        let read = try TestData.decodeAppNotification(from: TestJSON.appNotificationRead)
        mockNotificationService.notificationsToReturn = [unread, failed, read]
        mockNotificationService.unreadCountToReturn = 2
        await viewModel.loadNotifications()

        mockNotificationService.unreadCountToReturn = 0
        await viewModel.markAllRead()

        let calledIds = mockNotificationService.markAllReadCalledWith
        XCTAssertNotNil(calledIds)
        XCTAssertEqual(calledIds?.count, 2)
        XCTAssertTrue(calledIds?.contains("notif-001") ?? false)
        XCTAssertTrue(calledIds?.contains("notif-003") ?? false)
    }

    func testMarkAllReadSkipsWhenAllRead() async throws {
        let read = try TestData.decodeAppNotification(from: TestJSON.appNotificationRead)
        mockNotificationService.notificationsToReturn = [read]
        mockNotificationService.unreadCountToReturn = 0
        await viewModel.loadNotifications()

        await viewModel.markAllRead()

        XCTAssertNil(mockNotificationService.markAllReadCalledWith)
    }

    func testMarkAllReadSkipsWhenEmpty() async {
        mockNotificationService.notificationsToReturn = []
        mockNotificationService.unreadCountToReturn = 0
        await viewModel.loadNotifications()

        await viewModel.markAllRead()

        XCTAssertNil(mockNotificationService.markAllReadCalledWith)
    }

    func testMarkAllReadSetsErrorOnFailure() async throws {
        let unread = try TestData.decodeAppNotification(from: TestJSON.appNotificationUnread)
        mockNotificationService.notificationsToReturn = [unread]
        mockNotificationService.unreadCountToReturn = 1
        await viewModel.loadNotifications()

        mockNotificationService.errorToThrow = NetworkError.serverError(500)
        await viewModel.markAllRead()

        XCTAssertNotNil(viewModel.errorMessage)
    }

    // MARK: - Delete Notification

    func testDeleteNotificationCallsService() async throws {
        let notif = try TestData.decodeAppNotification(from: TestJSON.appNotificationRead)
        mockNotificationService.notificationsToReturn = [notif]
        mockNotificationService.unreadCountToReturn = 0
        await viewModel.loadNotifications()

        await viewModel.deleteNotification(id: "notif-002")

        XCTAssertEqual(mockNotificationService.deleteCalledWith, "notif-002")
    }

    func testDeleteNotificationRemovesFromLocalList() async throws {
        let notif = try TestData.decodeAppNotification(from: TestJSON.appNotificationRead)
        mockNotificationService.notificationsToReturn = [notif]
        mockNotificationService.unreadCountToReturn = 0
        await viewModel.loadNotifications()
        XCTAssertEqual(viewModel.notifications.count, 1)

        await viewModel.deleteNotification(id: "notif-002")

        XCTAssertTrue(viewModel.notifications.isEmpty)
    }

    func testDeleteUnreadNotificationDecrementsCount() async throws {
        let unread = try TestData.decodeAppNotification(from: TestJSON.appNotificationUnread)
        mockNotificationService.notificationsToReturn = [unread]
        mockNotificationService.unreadCountToReturn = 1
        await viewModel.loadNotifications()
        XCTAssertEqual(viewModel.unreadCount, 1)

        await viewModel.deleteNotification(id: "notif-001")

        XCTAssertEqual(viewModel.unreadCount, 0)
    }

    func testDeleteReadNotificationDoesNotDecrementCount() async throws {
        let read = try TestData.decodeAppNotification(from: TestJSON.appNotificationRead)
        mockNotificationService.notificationsToReturn = [read]
        mockNotificationService.unreadCountToReturn = 1
        await viewModel.loadNotifications()

        await viewModel.deleteNotification(id: "notif-002")

        XCTAssertEqual(viewModel.unreadCount, 1)
    }

    func testDeleteUnreadCountDoesNotGoNegative() async throws {
        let unread = try TestData.decodeAppNotification(from: TestJSON.appNotificationUnread)
        mockNotificationService.notificationsToReturn = [unread]
        mockNotificationService.unreadCountToReturn = 0
        await viewModel.loadNotifications()

        await viewModel.deleteNotification(id: "notif-001")

        XCTAssertEqual(viewModel.unreadCount, 0)
    }

    func testDeleteNotificationSetsErrorOnFailure() async throws {
        let notif = try TestData.decodeAppNotification(from: TestJSON.appNotificationRead)
        mockNotificationService.notificationsToReturn = [notif]
        mockNotificationService.unreadCountToReturn = 0
        await viewModel.loadNotifications()

        mockNotificationService.errorToThrow = NetworkError.serverError(500)
        await viewModel.deleteNotification(id: "notif-002")

        XCTAssertNotNil(viewModel.errorMessage)
    }

    func testDeleteWithoutConfigureDoesNotCrash() async {
        let unconfigured = NotificationsViewModel()
        await unconfigured.deleteNotification(id: "test")
        XCTAssertNil(unconfigured.errorMessage)
    }
}
