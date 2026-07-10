import Foundation

/// Production-safe test-only bootstrap for `PrintFarmerUITests`.
///
/// Activated exclusively by the `--uitesting` launch argument (see
/// `PrintFarmerUITests.PrintFarmerUITestCase`). When enabled, the app is
/// wired with:
///
/// * an in-memory `ServerRegistry` seeded with a single active
///   test server (so `RootView` skips `AddFirstServerView`), and
/// * the demo/mock `ServiceContainer` (so no real network is required), and
/// * an `AuthViewModel` marked authenticated with `DemoData.demoUser` so
///   `ContentView`/the operator shell renders immediately.
///
/// The bootstrap **never** persists state into `.standard` UserDefaults:
/// it uses a dedicated `UserDefaults(suiteName:)` domain that is wiped on
/// every launch. `DemoMode.shared` is deliberately *not* activated —
/// production auth code paths and demo-mode persistence remain untouched
/// on normal launches.
///
/// The launch-mode decision is driven purely by `CommandLine.arguments`
/// (or an explicit array in tests). It has no compile-time side effects
/// on non-UI-testing builds.
@MainActor
enum UITestBootstrap {

    /// Launch argument that flips the app into deterministic UI-test mode.
    static let launchArgument = "--uitesting"

    /// Dedicated `UserDefaults` suite. Isolated from `.standard` so a
    /// crashing UI test cannot leak fake auth/registry state into real
    /// user launches.
    static let userDefaultsSuiteName = "com.printfarmer.uitest"

    /// The environment produced by the bootstrap: a fully-authenticated,
    /// demo-backed set of dependencies ready to be handed to SwiftUI.
    /// Named to avoid ambiguity with `Foundation.Bundle`.
    struct Environment {
        let serverRegistry: ServerRegistry
        let services: ServiceContainer
        let authViewModel: AuthViewModel
    }

    /// True when the current process was launched with `--uitesting`.
    /// Safe to call from `PFarmApp.init()`.
    static var isEnabled: Bool {
        isEnabled(in: CommandLine.arguments)
    }

    /// Pure test-friendly overload used by unit tests to verify the
    /// launch-mode decision without touching `CommandLine`.
    static func isEnabled(in arguments: [String]) -> Bool {
        arguments.contains(launchArgument)
    }

    /// Builds the deterministic UI-test environment.
    ///
    /// Callers should invoke this only when `isEnabled` is true. The
    /// method wipes any pre-existing state under the test suite,
    /// registers a single active server, wires demo services, and marks
    /// the returned `AuthViewModel` as authenticated with `DemoData.demoUser`.
    ///
    /// - Parameter defaults: dependency-injection seam for tests. When
    ///   `nil`, the shared test suite is used (wiped on every launch).
    ///   Unit tests supply an ephemeral `UserDefaults(suiteName:)` to
    ///   keep runs hermetic.
    @discardableResult
    static func makeBundle(defaults: UserDefaults? = nil) -> Environment {
        let resolvedDefaults = defaults ?? makeUserDefaults()

        let registry = ServerRegistry(
            userDefaults: resolvedDefaults,
            migrateLegacyServerURL: false
        )

        if registry.servers.isEmpty {
            let baseURL = URL(string: "http://uitest.printfarmer.local")!
            // `add(...)` is throwing but with the wiped suite + fixed URL
            // it cannot fail here; treat any failure as programmer error.
            do {
                _ = try registry.add(
                    displayName: "UI Test Server",
                    baseURL: baseURL,
                    makeActiveIfNeeded: true
                )
            } catch {
                assertionFailure("UITestBootstrap failed to seed registry: \(error)")
            }
        }

        // Demo services are already sufficient: they satisfy every
        // protocol the operator shell needs without hitting the network.
        let services = ServiceContainer.demo()

        let auth = AuthViewModel(services: services)
        auth.markAuthenticatedForUITesting(user: DemoData.demoUser)

        return Environment(
            serverRegistry: registry,
            services: services,
            authViewModel: auth
        )
    }

    /// Returns a UserDefaults domain isolated from `.standard`, with any
    /// prior state removed so each launch starts from a clean slate.
    static func makeUserDefaults() -> UserDefaults {
        let defaults = UserDefaults(suiteName: userDefaultsSuiteName) ?? .standard
        defaults.removePersistentDomain(forName: userDefaultsSuiteName)
        return defaults
    }
}
