import XCTest
import UserNotifications
@testable import PrintFarmer

@MainActor
final class LocalNotificationTests: XCTestCase {
    func testForegroundNotificationsShowBannerSoundAndBadge() {
        let options = PushNotificationManager.foregroundPresentationOptions()

        XCTAssertTrue(options.contains(.banner))
        XCTAssertTrue(options.contains(.sound))
        XCTAssertTrue(options.contains(.badge))
    }

    func testPendingReadyPermissionRequestUsesLocalNotificationAuthorization() async {
        let requester = StubNotificationAuthorizationRequester(decision: true)
        let monitor = PendingReadyMonitor(notificationAuthorizationRequester: requester)

        await monitor.requestNotificationPermission()

        XCTAssertEqual(requester.requestCount, 1)
        XCTAssertEqual(requester.requestedOptions, [.alert, .badge, .sound])
    }

    func testPendingReadyPermissionDenialDoesNotRetry() async {
        let requester = StubNotificationAuthorizationRequester(decision: false)
        let monitor = PendingReadyMonitor(notificationAuthorizationRequester: requester)

        await monitor.requestNotificationPermission()

        XCTAssertEqual(requester.requestCount, 1)
    }
}

private final class StubNotificationAuthorizationRequester:
    NotificationAuthorizationRequesting,
    @unchecked Sendable
{
    private let lock = NSLock()
    private let decision: Bool
    private var storedRequestCount = 0
    private var storedRequestedOptions: UNAuthorizationOptions = []

    init(decision: Bool) {
        self.decision = decision
    }

    var requestCount: Int {
        lock.withLock { storedRequestCount }
    }

    var requestedOptions: UNAuthorizationOptions {
        lock.withLock { storedRequestedOptions }
    }

    func requestAuthorization(options: UNAuthorizationOptions) async throws -> Bool {
        lock.withLock {
            storedRequestCount += 1
            storedRequestedOptions = options
        }
        return decision
    }
}
