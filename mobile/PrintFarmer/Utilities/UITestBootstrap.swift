import Foundation
import notify
import KeychainSwift
#if DEBUG
import UIKit
#endif
#if canImport(UserNotifications)
import UserNotifications
#endif

/// Production-safe test-only bootstrap for `PrintFarmerUITests`.
///
/// Activated exclusively by the `--uitesting` launch argument (see
/// `PrintFarmerUITests.PrintFarmerUITestCase`). When enabled, the app is
/// wired with:
///
/// * an in-memory `ServerRegistry` seeded with a single active
///   test server (so `RootView` skips `AddFirstServerView`), and
/// * the demo/mock `ServiceContainer` (so no real network is required), and
/// * an `AuthViewModel` whose auth state depends on the selected `Mode`.
///
/// Two deterministic modes are supported (issue #706 F1 review defect D):
///
/// * `.authenticated` (default) marks the session authenticated with
///   `DemoData.demoUser` so `ContentView`/the operator shell renders
///   immediately — used by the operator-shell UI tests.
/// * `.unauthenticated` (adds `--uitesting-unauthenticated`) leaves the
///   session signed out so `RootView` renders `LoginView` — used by
///   `LoginFlowUITests`.
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
    private static let launchEnvironmentKey = "PFARM_UI_TESTING"

    /// Additional launch argument that selects the *unauthenticated*
    /// login-flow mode. When present alongside `launchArgument`, the
    /// bootstrap seeds the same ephemeral registry + demo services but
    /// leaves the session signed out so `RootView` renders `LoginView`
    /// (issue #706 F1 review defect D). Absent it, the bootstrap seeds an
    /// authenticated operator shell as before.
    static let unauthenticatedLaunchArgument = "--uitesting-unauthenticated"

    /// Additional launch argument that disables every operator feature whose
    /// visibility is covered by issue #2117.
    static let operatorFeaturesDisabledLaunchArgument =
        "--uitesting-operator-features-disabled"
    static let attentionActionsLaunchArgument = "--uitesting-attention-actions"
    static let attentionHarvestScanLaunchArgument =
        "--uitesting-attention-harvest-scan"
    static let preserveStateLaunchArgument = "--uitesting-preserve-state"
    static let advancedControlsPromptSeenLaunchArgument =
        "--uitesting-advanced-controls-prompt-seen"
    static let onboardingSeenLaunchArgument = "--uitesting-onboarding-seen"
    static let networkPermissionCompletedLaunchArgument =
        "--uitesting-network-permission-complete"
    static let navigationChromeLaunchArgument = "--uitesting-navigation-chrome"
    #if DEBUG
    static let shiftTaskMutationErrorLaunchArgument =
        "--uitesting-shift-task-mutation-error"
    static let shiftTaskInitialLoadFailureLaunchArgument =
        "--uitesting-shift-task-initial-load-failure"
    #endif

    /// Seeds a deterministic fleet coverage snapshot spanning all four
    /// #778 states (covers, runout+ETA, runout without ETA, unknown)
    /// plus a multi-toolhead printer whose two toolheads share a
    /// display name, so XCUI can prove:
    ///
    ///   * badge presence + a11y labels for every state,
    ///   * `.unknown` NEVER surfaces a coverage claim on the Farm card,
    ///   * per-toolhead rows on the detail screen remain distinct
    ///     even when their `toolheadName` collides,
    ///   * navigation to the correct printer by stable UUID.
    ///
    /// The snapshot is served by `StubFilamentCoverageService`, which
    /// bypasses the demo service's `featureDisabled` short-circuit.
    /// Production code never touches this argument.
    static let filamentCoverageScenarioLaunchArgument =
        "--uitesting-filament-coverage-scenario"

    /// Seeds the cold-offline read-only farm shell scenario (#817 F10-C1b):
    /// a stub `FarmSnapshotStoring` whose `hydrateActive()` returns a
    /// present snapshot of the demo fleet with a fixed past `lastUpdatedAt`,
    /// combined with an offline `printerService` whose `list(...)` throws.
    /// On launch `DashboardViewModel` hydrates the cached fleet, the
    /// canonical load then fails, and the cache is preserved — so XCUI can
    /// prove the read-only stale shell, cached card projection, and the
    /// visible last-confirmed timestamp. Production code never touches this
    /// argument.
    static let coldOfflineShellLaunchArgument =
        "--uitesting-cold-offline-shell"
    #if DEBUG
    static let queueReorderLaunchArgument = "--uitesting-queue-reorder"
    static let issue3259VisualAcceptanceLaunchArgument =
        "--uitesting-issue3259-visual-acceptance"
    nonisolated static let appStoreScreenshotsLaunchArgument =
        "--uitesting-app-store-screenshots"

    nonisolated static func appStoreScreenshotDate(in arguments: [String]) -> Date? {
        guard arguments.contains("--uitesting"),
              arguments.contains("--uitesting-issue3259-visual-acceptance"),
              arguments.contains(appStoreScreenshotsLaunchArgument) else { return nil }
        return Date(timeIntervalSince1970: 1_791_193_260)
    }

    nonisolated static var appStoreScreenshotDate: Date? {
        appStoreScreenshotDate(in: CommandLine.arguments)
    }
    static let issue3259ControlIdleLaunchArgument =
        "--uitesting-issue3259-control-idle"
    static let issue3259SafeFilamentActionsLaunchArgument =
        "--uitesting-issue3259-safe-filament-actions"
    #endif

    #if DEBUG
    /// Seeds the #788 task-action routing scenario: a dedicated shift-task
    /// feed whose harvest / filament-runout / maintenance rows each target a
    /// REAL demo entity (existing completed job, existing printer) so the
    /// shipped destination flow can load its context. All three rows share a
    /// duplicated display name, so XCUI proves routing keys off stable IDs and
    /// never titles. Production code never touches this argument.
    static let taskActionRoutingLaunchArgument =
        "--uitesting-task-action-routing"
    #endif

    /// Deterministic launch modes selectable from the UI-test harness.
    enum Mode: Equatable {
        /// Pre-authenticated demo operator shell (default).
        case authenticated
        /// Signed-out state so login-flow tests see `LoginView`.
        case unauthenticated
        /// Pre-authenticated shell with Attention, filament coverage, shift
        /// Tasks, and printed-parts inventory disabled.
        case authenticatedOperatorFeaturesDisabled
        /// F2-U2 feed with failure media, stable-ID destinations, and
        /// server-backed failure + maintenance actions.
        case authenticatedAttentionActions
        /// A single completed-job Attention item whose item-scoped scan action
        /// opens the relocated harvest scanner.
        case authenticatedAttentionHarvestScan
        /// Authenticated compact shell with a persisted pre-shift-plan baseline
        /// that deterministically produces the inline Oversight upgrade offer.
        #if DEBUG
        case authenticatedShiftTaskMutationError
        case authenticatedShiftTaskInitialLoadFailure
        /// #788: dedicated task-action routing feed (harvest/swap/maintenance
        /// rows targeting real demo entities) for handoff XCUI.
        case authenticatedTaskActionRouting
        #endif
        /// Authenticated operator shell with a deterministic fleet
        /// coverage snapshot injected (F4-M #778 UI tests).
        case authenticatedFilamentCoverageScenario
        /// Authenticated shell seeded with a cached farm snapshot + offline
        /// printer service so the cold-offline read-only stale shell renders
        /// (#817).
        case authenticatedColdOfflineShell
        #if DEBUG
        case authenticatedQueueReorder
        /// Mockup-aligned synthetic state for paired screenshot review.
        case authenticatedIssue3259VisualAcceptance
        #endif
    }

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

    /// True when the current process was launched by the UI-test harness.
    /// The environment is fixed at process creation, before Xcode injects
    /// its UI-test runtime and before deferred SwiftUI tasks execute.
    static var isEnabled: Bool {
        ProcessInfo.processInfo.environment[launchEnvironmentKey] == "1"
            || isEnabled(in: CommandLine.arguments)
    }

    /// Pure test-friendly overload used by unit tests to verify the
    /// launch-mode decision without touching `CommandLine`.
    static func isEnabled(in arguments: [String]) -> Bool {
        arguments.contains(launchArgument)
    }

    /// The launch mode encoded in the current process arguments.
    static var mode: Mode {
        mode(in: CommandLine.arguments)
    }

    /// Pure overload: resolves the launch mode from an explicit argument
    /// list so unit tests can exercise it without `CommandLine`.
    static func mode(in arguments: [String]) -> Mode {
        if arguments.contains(coldOfflineShellLaunchArgument) {
            return .authenticatedColdOfflineShell
        }
        #if DEBUG
        if arguments.contains(issue3259VisualAcceptanceLaunchArgument) {
            return .authenticatedIssue3259VisualAcceptance
        }
        if arguments.contains(queueReorderLaunchArgument) {
            return .authenticatedQueueReorder
        }
        #endif
        if arguments.contains(attentionHarvestScanLaunchArgument) {
            return .authenticatedAttentionHarvestScan
        }
        if arguments.contains(attentionActionsLaunchArgument) {
            return .authenticatedAttentionActions
        }
        if arguments.contains(filamentCoverageScenarioLaunchArgument) {
            return .authenticatedFilamentCoverageScenario
        }
        if arguments.contains(operatorFeaturesDisabledLaunchArgument) {
            return .authenticatedOperatorFeaturesDisabled
        }
        #if DEBUG
        if arguments.contains(shiftTaskMutationErrorLaunchArgument) {
            return .authenticatedShiftTaskMutationError
        }
        if arguments.contains(shiftTaskInitialLoadFailureLaunchArgument) {
            return .authenticatedShiftTaskInitialLoadFailure
        }
        if arguments.contains(taskActionRoutingLaunchArgument) {
            return .authenticatedTaskActionRouting
        }
        #endif
        return arguments.contains(unauthenticatedLaunchArgument) ? .unauthenticated : .authenticated
    }

    /// Builds the deterministic UI-test environment for `mode`.
    ///
    /// Callers should invoke this only when `isEnabled` is true. The
    /// method wipes any pre-existing state under the test suite,
    /// registers a single active server, and wires demo services. In
    /// `.authenticated` mode the returned `AuthViewModel` is marked
    /// authenticated with `DemoData.demoUser`; in `.unauthenticated` mode
    /// it is left signed out so `RootView` renders `LoginView`.
    ///
    /// - Parameters:
    ///   - mode: which deterministic launch mode to seed.
    ///   - defaults: dependency-injection seam for tests. When `nil`, the
    ///     shared test suite is used (wiped on every launch). Unit tests
    ///     supply an ephemeral `UserDefaults(suiteName:)` to keep runs
    ///     hermetic.
    ///   - arguments: launch arguments that select deterministic UI-test
    ///     preferences without changing production defaults.
    @discardableResult
    static func makeBundle(
        mode: Mode = .authenticated,
        defaults: UserDefaults? = nil,
        arguments: [String] = CommandLine.arguments
    ) -> Environment {
        if defaults == nil {
            clearSystemNotificationState()
            seedStandardRouteDefaults(arguments: arguments)
        }
        let resolvedDefaults = defaults ?? makeUserDefaults(arguments: arguments)

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
        seedNavigationChromeServerIfRequested(
            arguments: arguments,
            registry: registry
        )
        // Demo services are already sufficient: they satisfy every
        // protocol the operator shell needs without hitting the network.
        // In the unauthenticated mode they also keep `LoginView`'s
        // sign-in path off the network (DemoAuthService).
        //
        // #817: the cold-offline shell needs a `FarmSnapshotStoring` whose
        // `hydrateActive()` returns a present cached snapshot. Because the
        // store is `let` on `ServiceContainer`, it must be injected through
        // the demo factory rather than reassigned afterwards.
        let injectedSnapshotStore: (any FarmSnapshotStoring)?
        if mode == .authenticatedColdOfflineShell {
            injectedSnapshotStore = Self.coldOfflineSnapshotStore(registry: registry)
        } else {
            injectedSnapshotStore = nil
        }
        #if DEBUG
        let testUser: UserDTO
        switch mode {
        case .authenticatedQueueReorder:
            testUser = Self.queueWritableDemoUser()
        case .authenticatedIssue3259VisualAcceptance:
            testUser = Self.visualAcceptanceDemoUser()
        default:
            testUser = DemoData.demoUser
        }
        #else
        let testUser = DemoData.demoUser
        #endif
        let services: ServiceContainer
        #if DEBUG
        if mode == .authenticatedIssue3259VisualAcceptance {
            services = Self.issue3259VisualAcceptanceServices(
                registry: registry,
                defaults: resolvedDefaults
            )
        } else {
            services = ServiceContainer.demo(
                serverRegistry: registry,
                farmSnapshotStore: injectedSnapshotStore
            )
        }
        #else
        services = ServiceContainer.demo(
            serverRegistry: registry,
            farmSnapshotStore: injectedSnapshotStore
        )
        #endif
        #if DEBUG
        if mode == .authenticatedQueueReorder {
            services.jobService = QueueReorderUITestJobService()
        }
        #endif
        // #1353: `ResolvedSystemCapabilities.defaults.printedPartsInventoryEnabled`
        // is `false` in production so a freshly-provisioned server without any
        // configured SKUs/mappings does not surface the harvest flow (see
        // PR #1002). The demo bootstrap, by contrast, IS a fully-configured
        // operator playground: `DemoPartsInventoryService` ships with seeded
        // parts and bins, so the harvest flow is a real, reachable surface
        // here. Explicitly enable the flag for the demo-shell modes that render
        // the harvest UI (JobDetailView's "Harvest to Inventory" button, the
        // Inventory scanner, and the ShiftTasks/Attention harvest destinations) so the
        // production default flip does not silently strand those surfaces
        // in the UI-test bundle.
        //
        // Scope is intentionally narrow: only modes where a UI test asserts
        // the harvest flow. `.authenticatedOperatorFeaturesDisabled` stays at
        // production defaults and then explicitly disables the affected
        // operator features below.
        let harvestEnabledDemoModes: Set<UITestBootstrap.Mode> = {
            var set: Set<UITestBootstrap.Mode> = [
                .authenticated,
                .authenticatedAttentionHarvestScan,
            ]
            #if DEBUG
            set.insert(.authenticatedTaskActionRouting)
            #endif
            return set
        }()
        if harvestEnabledDemoModes.contains(mode) {
            var enabled = ResolvedSystemCapabilities.defaults
            enabled.printedPartsInventoryEnabled = true
            services.capabilitiesService = StubSystemCapabilitiesService(resolved: enabled)
        }

        if mode == .authenticatedOperatorFeaturesDisabled {
            var disabled = ResolvedSystemCapabilities.defaults
            disabled.attentionEnabled = false
            disabled.filamentCoverageEnabled = false
            disabled.shiftPlanEnabled = false
            disabled.printedPartsInventoryEnabled = false
            services.capabilitiesService = StubSystemCapabilitiesService(resolved: disabled)
        }
        if mode == .authenticatedAttentionActions {
            services.attentionService = DemoAttentionService(
                feed: attentionActionsScenarioFeed(),
                gatedFailureAction: .resume,
                gateReleaseAction: .acknowledge,
                feedFailureAfterSuccessfulAction: .resume
            )
            services.printerService = DemoPrinterService(
                additionalPrinters: [duplicateNamePrinter()],
                snapshots: [
                    duplicateNamePrinterID: attentionActionsSnapshotData(),
                ]
            )
        }
        if mode == .authenticatedAttentionHarvestScan {
            services.attentionService = DemoAttentionService(
                feed: attentionHarvestScanScenarioFeed()
            )
        }
        if mode == .authenticatedFilamentCoverageScenario {
            services.filamentCoverageService = StubFilamentCoverageService(
                fleet: Self.filamentCoverageScenarioFleet()
            )
            // Add a printer with a display name DUPLICATED from an
            // existing demo printer but with a distinct UUID + a
            // distinct coverage status. This lets XCUI prove that
            // per-card assertions are scoped by stable UUID, never
            // by display name (reviewer blocker D). See
            // `filamentCoverageScenarioFleet` for the paired seed
            // that gives the duplicate its own runout badge.
            services.printerService = DemoPrinterService(
                additionalPrinters: [Self.duplicateNamePrinter()]
            )
        }
        #if DEBUG
        if mode == .authenticatedIssue3259VisualAcceptance {
            services.spoolService = DemoSpoolService(
                spoolOverrides: [Self.issue3259VisualAcceptanceSpool()]
            )
            services.signalRService = DemoSignalRService(simulatesProgress: false)
            services.filamentCoverageService = StubFilamentCoverageService(
                fleet: Self.issue3259VisualAcceptanceCoverage()
            )
            var capabilities = ResolvedSystemCapabilities.defaults
            capabilities.printedPartsInventoryEnabled = true
            services.capabilitiesService = StubSystemCapabilitiesService(resolved: capabilities)
        }
        if mode == .authenticatedShiftTaskMutationError {
            services.shiftTaskService = DemoShiftTaskService(
                scenario: .mutationFailureThenSuccess
            )
        }
        if mode == .authenticatedShiftTaskInitialLoadFailure {
            services.shiftTaskService = DemoShiftTaskService(
                scenario: .initialLoadFailureThenSuccess
            )
        }
        if mode == .authenticatedTaskActionRouting {
            services.shiftTaskService = DemoShiftTaskService(
                scenario: .taskActionRouting
            )
        }
        #endif
        // #817: make the canonical fleet load fail offline, so the pre-seeded
        // cached snapshot is preserved as the read-only stale shell instead of
        // being replaced by live data. Dashboard remains reachable from its
        // stable Oversight destination.
        if mode == .authenticatedColdOfflineShell {
            services.capabilitiesService = StubSystemCapabilitiesService(
                resolved: .defaults
            )
            services.printerService = DemoPrinterService(offlineError: NetworkError.noConnection)
        }

        let auth = AuthViewModel(services: services)
        switch mode {
        case .authenticated, .authenticatedOperatorFeaturesDisabled,
             .authenticatedAttentionActions,
             .authenticatedAttentionHarvestScan:
            auth.markAuthenticatedForUITesting(user: testUser)
        case .unauthenticated:
            break
        #if DEBUG
        case .authenticatedShiftTaskMutationError:
            auth.markAuthenticatedForUITesting(user: DemoData.demoUser)
        case .authenticatedShiftTaskInitialLoadFailure:
            auth.markAuthenticatedForUITesting(user: DemoData.demoUser)
        case .authenticatedTaskActionRouting:
            auth.markAuthenticatedForUITesting(user: DemoData.demoUser)
        #endif
        case .authenticatedFilamentCoverageScenario:
            auth.markAuthenticatedForUITesting(user: DemoData.demoUser)
        case .authenticatedColdOfflineShell:
            auth.markAuthenticatedForUITesting(user: DemoData.demoUser)
        #if DEBUG
        case .authenticatedQueueReorder:
            auth.markAuthenticatedForUITesting(user: testUser)
        case .authenticatedIssue3259VisualAcceptance:
            auth.markAuthenticatedForUITesting(user: testUser)
        #endif
        }

        return Environment(
            serverRegistry: registry,
            services: services,
            authViewModel: auth
        )
    }

    private static func queueWritableDemoUser() -> UserDTO {
        UserDTO(
            id: DemoData.demoUser.id,
            username: DemoData.demoUser.username,
            email: DemoData.demoUser.email,
            firstName: DemoData.demoUser.firstName,
            lastName: DemoData.demoUser.lastName,
            isActive: DemoData.demoUser.isActive,
            emailConfirmed: DemoData.demoUser.emailConfirmed,
            lastLogin: DemoData.demoUser.lastLogin,
            createdAt: DemoData.demoUser.createdAt,
            roles: DemoData.demoUser.roles,
            permissions: DemoData.demoUser.permissions + ["queue:write"]
        )
    }

    #if DEBUG
    private static func visualAcceptanceDemoUser() -> UserDTO {
        let user = queueWritableDemoUser()
        return UserDTO(
            id: user.id,
            username: user.username,
            email: user.email,
            firstName: user.firstName,
            lastName: user.lastName,
            isActive: user.isActive,
            emailConfirmed: user.emailConfirmed,
            lastLogin: user.lastLogin,
            createdAt: user.createdAt,
            roles: user.roles,
            permissions: user.permissions + ["queue:start"]
        )
    }

    private static let issue3259VisualAcceptanceToolheadID =
        UUID(uuidString: "32590000-0000-0000-0000-000000000001")!

    private static let issue3259Ender3S1ID =
        UUID(uuidString: "32590000-0000-0000-0000-000000000002")!
    private static let issue3259PrusaMiniID =
        UUID(uuidString: "32590000-0000-0000-0000-000000000003")!
    private static let issue3259SovolSV06ID =
        UUID(uuidString: "32590000-0000-0000-0000-000000000004")!
    private static let issue3259PrusaXLID =
        UUID(uuidString: "32590000-0000-0000-0000-000000000005")!
    private static let issue3259BambuA1ID =
        UUID(uuidString: "32590000-0000-0000-0000-000000000006")!
    private static let issue3259CrealityK1ID =
        UUID(uuidString: "32590000-0000-0000-0000-000000000007")!

    private static func issue3259VisualAcceptanceThumbnailPath(for printerID: UUID) -> String {
        "/api/printers/\(printerID.uuidString.lowercased())/current-job/thumbnail?v=3259000000000001"
    }

    private static func issue3259VisualAcceptanceThumbnailAssetName(for jobName: String?) -> String? {
        guard let jobName = jobName?.lowercased() else { return nil }
        if jobName.contains("benchy") {
            return "Issue3259VisualAcceptanceBenchy"
        }
        if jobName.contains("gear") {
            return "Issue3259VisualAcceptanceGear"
        }
        if jobName.contains("vase") || jobName.contains("tool_tray") {
            return "Issue3259VisualAcceptanceVase"
        }
        if jobName.contains("clip") || jobName.contains("cable_guide") {
            return "Issue3259VisualAcceptanceClip"
        }
        if jobName.contains("bracket") || jobName.contains("organizer") || jobName.contains("mount") {
            return "Issue3259VisualAcceptanceBracket"
        }
        return nil
    }

    private static func issue3259VisualAcceptanceThumbnailData(named assetName: String) -> Data {
        guard let data = UIImage(named: assetName)?.pngData() else {
            preconditionFailure("The visual-acceptance thumbnail asset \(assetName) must be available in the app bundle.")
        }
        return data
    }

    private static func issue3259QueueThumbnailPath(_ jobID: String) -> String {
        "/api/gcode-files/thumbnail/\(jobID)"
    }

    private struct Issue3259VisualAcceptanceFixture {
        let printers: [Printer]
        let status: PrinterStatusDetail
        let details: PrinterDetails
        let attentionFeed: AttentionFeed
        let currentJobThumbnailDataByID: [String: Data]
        let queueThumbnailDataByJobID: [String: Data]
    }

    private static func issue3259VisualAcceptanceFixture(
        controlIdle: Bool = false
    ) -> Issue3259VisualAcceptanceFixture {
        func demoPrinter(_ id: UUID) -> Printer {
            guard let printer = DemoData.printers.first(where: { $0.id == id }) else {
                preconditionFailure("The visual-acceptance fixture requires demo printer \(id).")
            }
            return printer
        }

        var printer = demoPrinter(DemoData.prusaMK4_1_ID)
        printer.progress = 0.64
        printer.currentLayer = 142
        printer.totalLayers = 221
        printer.fanSpeedPercent = 60
        printer.liveZOffsetMm = 0.025
        printer.jobName = "benchy_0.2mm_PLA.gcode"
        printer.fileName = "benchy_0.2mm_PLA.gcode"
        printer.currentJobThumbnailUrl = issue3259VisualAcceptanceThumbnailPath(for: printer.id)
        printer.spoolInfo = PrinterSpoolInfo(
            hasActiveSpool: true,
            activeSpoolId: 1,
            spoolName: "Prusament PLA · Coral",
            material: "PLA",
            colorHex: "#EF6B4A",
            filamentName: "Prusament PLA",
            vendor: "Prusa Research",
            remainingWeightG: appStoreScreenshotDate == nil ? 84 : 612,
            spoolInUse: true
        )
        if controlIdle {
            printer.state = "ready"
            printer.progress = nil
            printer.currentLayer = nil
            printer.totalLayers = nil
            printer.jobName = nil
            printer.fileName = nil
            printer.currentJobThumbnailUrl = nil
            printer.hotendTemp = 24
            printer.bedTemp = 23
            printer.hotendTarget = 215
            printer.bedTarget = 60
            printer.x = 0
            printer.y = 0
            printer.z = 5
            printer.homedAxes = ""
        }
        if ProcessInfo.processInfo.arguments.contains(issue3259SafeFilamentActionsLaunchArgument) {
            printer.state = "ready"
            printer.progress = nil
            printer.currentLayer = nil
            printer.totalLayers = nil
            printer.jobName = nil
            printer.fileName = nil
            printer.currentJobThumbnailUrl = nil
            printer.hotendTemp = PhysicalFilamentCommandEligibility.minimumLoadUnloadTemperatureC
            printer.hotendTarget = PhysicalFilamentCommandEligibility.minimumLoadUnloadTemperatureC
            printer.bedTemp = 23
            printer.bedTarget = 0
        }

        var bambuX1C = demoPrinter(DemoData.bambuX1C_ID)
        bambuX1C.state = "completed"
        bambuX1C.progress = nil
        bambuX1C.currentLayer = nil
        bambuX1C.totalLayers = nil
        bambuX1C.jobName = "clip_holder_x4.gcode"
        bambuX1C.fileName = "clip_holder_x4.gcode"
        bambuX1C.hotendTemp = 32
        bambuX1C.bedTemp = 41
        bambuX1C.hotendTarget = 0
        bambuX1C.bedTarget = 0

        var voron = demoPrinter(DemoData.voron24_ID)
        voron.progress = 0.22
        voron.jobName = "gear_set_v3.gcode"
        voron.fileName = "gear_set_v3.gcode"
        voron.currentJobThumbnailUrl = issue3259VisualAcceptanceThumbnailPath(for: voron.id)
        voron.hotendTemp = 250
        voron.bedTemp = 100

        var idleMk4 = demoPrinter(DemoData.prusaMK4_2_ID)
        idleMk4.state = "ready"
        idleMk4.progress = nil
        idleMk4.currentLayer = nil
        idleMk4.totalLayers = nil
        idleMk4.jobName = nil
        idleMk4.fileName = nil
        idleMk4.currentJobThumbnailUrl = nil
        idleMk4.hotendTemp = 24
        idleMk4.bedTemp = 23
        idleMk4.hotendTarget = 215
        idleMk4.bedTarget = 60
        idleMk4.fanSpeedPercent = 35
        idleMk4.liveZOffsetMm = 0.025
        idleMk4.x = 0
        idleMk4.y = 0
        idleMk4.z = 5
        idleMk4.homedAxes = "X Y Z"
        idleMk4.spoolInfo = PrinterSpoolInfo(
            hasActiveSpool: true,
            activeSpoolId: 1,
            spoolName: "Prusament PLA · Coral",
            material: "PLA",
            colorHex: "#EF6B4A",
            filamentName: "Prusament PLA",
            vendor: "Prusa Research",
            remainingWeightG: 350,
            spoolInUse: true
        )

        let basePrinters = [
            printer,
            voron,
            bambuX1C,
            issue3259MockupPrinter(
                id: issue3259Ender3S1ID,
                name: "Ender 3 S1",
                modelName: "Ender 3 S1",
                state: "paused",
                progress: 0.81,
                currentLayer: 162,
                totalLayers: 200,
                jobName: "spiral_vase.gcode",
                hotendTemp: 170,
                bedTemp: 60,
                spoolName: "PLA Purple",
                colorHex: "#A78BFA"
            ),
            issue3259MockupPrinter(
                id: issue3259PrusaMiniID,
                name: "Prusa Mini",
                modelName: "Mini",
                state: "printing",
                progress: 0.47,
                currentLayer: 89,
                totalLayers: 190,
                jobName: "clip_holder_x4.gcode",
                hotendTemp: 210,
                bedTemp: 60,
                spoolName: "PLA Teal",
                colorHex: "#34D399"
            ),
            idleMk4,
        ]
        var printers = basePrinters + [
            issue3259MockupPrinter(
                id: DemoData.bambuP1S_ID,
                name: "Bambu P1S",
                modelName: "P1S",
                state: "printing",
                progress: 0.09,
                currentLayer: 22,
                totalLayers: 242,
                jobName: "shelf_bracket_x6.gcode",
                hotendTemp: 245,
                bedTemp: 80,
                spoolName: "PLA White",
                colorHex: "#F3F4F6"
            ),
            issue3259MockupPrinter(
                id: issue3259SovolSV06ID,
                name: "Sovol SV06",
                modelName: "SV06",
                state: nil,
                isOnline: false
            ),
            issue3259MockupPrinter(
                id: DemoData.ender3V3_ID,
                name: "Ender 3 V3",
                modelName: "Ender 3 V3",
                state: "idle",
                hotendTemp: 22,
                bedTemp: 22
            ),
            issue3259MockupPrinter(
                id: issue3259PrusaXLID,
                name: "Prusa XL",
                modelName: "XL",
                state: "printing",
                progress: 0.38,
                currentLayer: 76,
                totalLayers: 200,
                jobName: "tool_organizer.gcode",
                hotendTemp: 215,
                bedTemp: 60,
                spoolName: "PLA Orange",
                colorHex: "#F97316"
            ),
            issue3259MockupPrinter(
                id: issue3259BambuA1ID,
                name: "Bambu A1",
                modelName: "A1",
                state: "printing",
                progress: 0.56,
                currentLayer: 112,
                totalLayers: 200,
                jobName: "cable_clip_set.gcode",
                hotendTemp: 220,
                bedTemp: 55,
                spoolName: "PETG Blue",
                colorHex: "#3B82F6"
            ),
            issue3259MockupPrinter(
                id: issue3259CrealityK1ID,
                name: "Creality K1",
                modelName: "K1",
                state: "idle",
                hotendTemp: 24,
                bedTemp: 23
            ),
        ]

        if appStoreScreenshotDate != nil {
            printers.removeAll {
                !$0.isOnline || ["paused", "completed", "error"].contains($0.state?.lowercased() ?? "")
            }
            printers[0] = printer
        }

        let status = PrinterStatusDetail(
            id: printer.id,
            isOnline: printer.isOnline,
            state: printer.state,
            progress: printer.progress.map { $0 * 100 },
            currentLayer: printer.currentLayer,
            totalLayers: printer.totalLayers,
            fanSpeedPercent: 60,
            liveZOffsetMm: 0.025,
            jobName: printer.jobName,
            thumbnailUrl: printer.thumbnailUrl,
            cameraStreamUrl: printer.cameraStreamUrl,
            cameraSnapshotUrl: printer.cameraSnapshotUrl,
            x: printer.x,
            y: printer.y,
            z: printer.z,
            hotendTemp: printer.hotendTemp,
            bedTemp: printer.bedTemp,
            hotendTarget: printer.hotendTarget,
            bedTarget: printer.bedTarget,
            homedAxes: printer.homedAxes,
            spoolInfo: printer.spoolInfo,
            mmuStatus: nil,
            printTimeLeftSeconds: controlIdle ? nil : 2_280,
            currentJobThumbnailUrl: printer.currentJobThumbnailUrl
        )
        let details = PrinterDetails(
            id: printer.id,
            name: printer.name,
            backend: printer.backend,
            manufacturerName: printer.manufacturerName,
            modelName: printer.modelName,
            toolheads: [
                Toolhead(
                    id: issue3259VisualAcceptanceToolheadID,
                    name: "Extruder 1",
                    index: 0,
                    isPrimary: true,
                    nozzleDiameter: 0.4,
                    supportedMaterials: ["PLA", "PETG"],
                    currentSpoolId: 1,
                    currentMaterial: "PLA",
                    currentFilamentColor: "#EF6B4A"
                )
            ],
            capabilities: PrinterHardwareCapabilities(
                maxBuildVolumeX: 220,
                maxBuildVolumeY: 220,
                maxBuildVolumeZ: 250,
                maxHotendTemp: 300,
                maxBedTemp: 120,
                hasHeatedBed: true
            )
        )
        let failedPrinter = printers.first { $0.state?.lowercased() == "error" }
        let attentionFeed = AttentionFeed(
            items: failedPrinter.map { failed in
                [
                    AttentionItem(
                        id: "failure:issue3259-visual-acceptance",
                        kind: .failure,
                        severity: .critical,
                        printerId: failed.id,
                        printerName: failed.name,
                        title: "Print failed",
                        detail: "The printer reported a failed job.",
                        occurredAt: Date(timeIntervalSince1970: 1_790_000_000),
                        actions: []
                    )
                ]
            } ?? [],
            nextCursor: nil,
            healthyPrinterCount: 0
        )
        let currentJobThumbnailDataByID: [String: Data] = Dictionary(
            uniqueKeysWithValues: printers.compactMap { printer -> (String, Data)? in
                guard printer.currentJobThumbnailUrl != nil,
                      let assetName = issue3259VisualAcceptanceThumbnailAssetName(
                        for: printer.jobName ?? printer.fileName
                      ) else {
                    return nil
                }
                return (
                    printer.id.uuidString.lowercased(),
                    issue3259VisualAcceptanceThumbnailData(named: assetName)
                )
            }
        )
        let queueThumbnailAssetNames = [
            "32590000-0000-0000-0000-000000000002": "Issue3259VisualAcceptanceClip",
            "32590000-0000-0000-0000-000000000003": "Issue3259VisualAcceptanceBracket",
            "32590000-0000-0000-0000-000000000004": "Issue3259VisualAcceptanceVase",
            "32590000-0000-0000-0000-000000000020": "Issue3259VisualAcceptanceClip",
            "32590000-0000-0000-0000-000000000021": "Issue3259VisualAcceptanceBracket",
            "32590000-0000-0000-0000-000000000022": "Issue3259VisualAcceptanceVase",
            "32590000-0000-0000-0000-000000000101": "Issue3259VisualAcceptanceClip",
            "32590000-0000-0000-0000-000000000102": "Issue3259VisualAcceptanceBracket",
            "32590000-0000-0000-0000-000000000103": "Issue3259VisualAcceptanceVase",
            "32590000-0000-0000-0000-000000000104": "Issue3259VisualAcceptanceClip",
            "32590000-0000-0000-0000-000000000105": "Issue3259VisualAcceptanceVase",
            "32590000-0000-0000-0000-000000000106": "Issue3259VisualAcceptanceGear",
            "32590000-0000-0000-0000-000000000110": "Issue3259VisualAcceptanceGear",
            "32590000-0000-0000-0000-000000000111": "Issue3259VisualAcceptanceClip",
            DemoData.job1ID.uuidString.lowercased(): "Issue3259VisualAcceptanceBenchy",
        ]
        let queueThumbnailDataByJobID = queueThumbnailAssetNames.mapValues {
            issue3259VisualAcceptanceThumbnailData(named: $0)
        }
        var statusWithSafety = status
        statusWithSafety.safetyTelemetry = issue3259VisualAcceptanceSafetyTelemetry(
            printer: printer,
            observedAt: Date()
        )
        return Issue3259VisualAcceptanceFixture(
            printers: printers,
            status: statusWithSafety,
            details: details,
            attentionFeed: attentionFeed,
            currentJobThumbnailDataByID: currentJobThumbnailDataByID,
            queueThumbnailDataByJobID: queueThumbnailDataByJobID
        )
    }

    private static func issue3259MockupPrinter(
        id: UUID,
        name: String,
        modelName: String,
        state: String?,
        isOnline: Bool = true,
        progress: Double? = nil,
        currentLayer: Int? = nil,
        totalLayers: Int? = nil,
        jobName: String? = nil,
        hotendTemp: Double? = nil,
        bedTemp: Double? = nil,
        spoolName: String? = nil,
        colorHex: String? = nil
    ) -> Printer {
        var payload: [String: Any] = [
            "id": id.uuidString,
            "name": name,
            "modelName": modelName,
            "isOnline": isOnline,
            "isEnabled": true,
        ]
        if let state { payload["state"] = state }
        if let progress { payload["progress"] = progress * 100 }
        if let currentLayer { payload["currentLayer"] = currentLayer }
        if let totalLayers { payload["totalLayers"] = totalLayers }
        if let jobName {
            payload["jobName"] = jobName
            payload["fileName"] = jobName
            if ["printing", "paused"].contains(state?.lowercased() ?? ""),
               issue3259VisualAcceptanceThumbnailAssetName(for: jobName) != nil {
                payload["currentJobThumbnailUrl"] = issue3259VisualAcceptanceThumbnailPath(for: id)
            }
        }
        if let hotendTemp { payload["hotendTemp"] = hotendTemp }
        if let bedTemp { payload["bedTemp"] = bedTemp }
        if let spoolName, let colorHex {
            payload["spoolInfo"] = [
                "hasActiveSpool": true,
                "activeSpoolId": 3259,
                "spoolName": spoolName,
                "material": "PLA",
                "colorHex": colorHex,
                "filamentName": spoolName,
                "vendor": "Mockup fixture",
                "remainingWeightG": 350,
                "spoolInUse": true,
            ]
        }
        do {
            let data = try JSONSerialization.data(withJSONObject: payload)
            return try JSONDecoder().decode(Printer.self, from: data)
        } catch {
            preconditionFailure("The visual-acceptance printer fixture must decode: \(error)")
        }
    }

    private static func issue3259VisualAcceptanceServices(
        registry: ServerRegistry,
        defaults: UserDefaults
    ) -> ServiceContainer {
        guard let server = registry.activeServer else {
            preconditionFailure("The visual-acceptance API fixture requires an active registered server.")
        }
        let controlIdle = ProcessInfo.processInfo.arguments.contains(issue3259ControlIdleLaunchArgument)
        let fixture = issue3259VisualAcceptanceFixture(controlIdle: controlIdle)
        Issue3259VisualAcceptanceURLProtocol.install(fixture)

        let keychain = KeychainSwift(keyPrefix: "PrintFarmerUITestIssue3259_")
        let credentials = ServerCredentialsStore(keychain: keychain)
        credentials.save(
            ServerCredentials(accessToken: Issue3259VisualAcceptanceURLProtocol.accessToken),
            serverId: server.id
        )
        registry.setAdvancedPrinterControlsEnabled(true)

        let services = ServiceContainer(
            serverRegistry: registry,
            credentialsStore: credentials,
            userDefaultsBox: AuthServiceUserDefaultsBox(defaults),
            synchronizeOfflineQueueOnStartup: false,
            apiClientFactory: { baseURL, generation, accessToken, authSessionToken, serverID in
                let identity = accessToken.flatMap { token in
                    serverID.map {
                        AuthenticatedIdentity(
                            accessToken: token,
                            serverID: $0,
                            authSessionToken: authSessionToken
                        )
                    }
                }
                let configuration = URLSessionConfiguration.ephemeral
                configuration.protocolClasses = [Issue3259VisualAcceptanceURLProtocol.self]
                return APIClient(
                    baseURL: baseURL,
                    session: URLSession(configuration: configuration),
                    serverGeneration: generation,
                    authenticated: identity
                )
            },
            signalRServiceFactory: { _, _ in
                DemoSignalRService(simulatesProgress: false)
            }
        )
        services.autoPrintService = DemoAutoDispatchService()
        return services
    }

    private static func issue3259VisualAcceptanceSafetyTelemetry(
        printer: Printer,
        observedAt: Date
    ) -> PrinterSafetyTelemetryDto {
        func scalar(_ value: Double?) -> SafetyScalarTelemetryFactDto {
            SafetyScalarTelemetryFactDto(
                value: value,
                observedAtUtc: observedAt,
                staleAfterSeconds: 120,
                source: "issue3259-visual-acceptance-api-fixture"
            )
        }

        return PrinterSafetyTelemetryDto(
            measuredHotendTemperatureC: scalar(printer.hotendTemp),
            targetHotendTemperatureC: scalar(printer.hotendTarget),
            homedAxes: SafetyAxesTelemetryFactDto(
                value: ["X", "Y", "Z"],
                observedAtUtc: observedAt,
                staleAfterSeconds: 120,
                source: "issue3259-visual-acceptance-api-fixture"
            ),
            coordinateOriginOffsetMm: SafetyVectorTelemetryFactDto(
                value: SafetyVector3Dto(x: 0, y: 0, z: 0),
                observedAtUtc: observedAt,
                staleAfterSeconds: 120,
                source: "issue3259-visual-acceptance-api-fixture"
            )
        )
    }

    private final class Issue3259VisualAcceptanceURLProtocol: URLProtocol, @unchecked Sendable {
        private struct FixtureState: Sendable {
            let printersData: Data
            let printersByID: [String: Data]
            let currentJobThumbnailUrlsByID: [String: String]
            let currentJobThumbnailDataByID: [String: Data]
            let queueThumbnailDataByJobID: [String: Data]
            var statusData: Data
            let additionalStatusDataByID: [String: Data]
            let detailsDataByID: [String: Data]
            let attentionData: Data
            let capabilitiesDataByID: [String: Data]
            let assignedQueueDataByID: [String: Data]
            var globalQueueData: Data
            let globalQueueAfterRerunData: Data
            let globalQueueAfterSecondRerunData: Data
            let recentFailureHistoryData: Data
            let failedJobsDataByID: [String: Data]
            let failedJobRowVersionsByID: [String: String]
            let rerunResponsesByID: [String: Data]
            let farmShapeData: Data
            let queueStatsData: Data
            let userData: Data
        }

        static let accessToken = "issue3259-ui-test-only"
        private static let printerID = DemoData.prusaMK4_1_ID.uuidString.lowercased()
        private static let lock = NSLock()
        nonisolated(unsafe) private static var fixtureState: FixtureState?

        @MainActor
        static func install(_ fixture: Issue3259VisualAcceptanceFixture) {
            do {
                let encoder = JSONEncoder()
                encoder.dateEncodingStrategy = .iso8601
                let encodedPrinters = try encoder.encode(fixture.printers)
                guard var printerObjects = try JSONSerialization.jsonObject(with: encodedPrinters) as? [[String: Any]] else {
                    preconditionFailure("The visual-acceptance printer fixture must encode as an array of objects.")
                }
                for index in printerObjects.indices {
                    if let progress = printerObjects[index]["progress"] as? Double {
                        printerObjects[index]["progress"] = progress * 100
                    }
                }
                let printersData = try JSONSerialization.data(withJSONObject: printerObjects)
                var printersByID: [String: Data] = [:]
                for printer in printerObjects {
                    guard let id = (printer["id"] as? String)?.lowercased() else { continue }
                    printersByID[id] = try JSONSerialization.data(withJSONObject: printer)
                }
                var currentJobThumbnailUrlsByID: [String: String] = [:]
                for printer in fixture.printers {
                    guard let path = printer.currentJobThumbnailUrl else { continue }
                    currentJobThumbnailUrlsByID[printer.id.uuidString.lowercased()] = path
                }
                let safetyData = try encoder.encode(issue3259VisualAcceptanceVerifiedSafety(
                    configurationRevision: fixture.printers.first(where: { $0.id == DemoData.prusaMK4_1_ID })?.configurationRevision ?? 0,
                    observedAt: Date()
                ))
                guard let safety = try JSONSerialization.jsonObject(with: safetyData) as? [String: Any] else {
                    preconditionFailure("The verified-safety fixture must encode as an object.")
                }
                let capabilitiesData = try JSONSerialization.data(withJSONObject: [
                    "printerId": printerID,
                    "printerName": fixture.printers.first(where: { $0.id == DemoData.prusaMK4_1_ID })?.name ?? "Prusa MK4",
                    "backend": "Moonraker",
                    "supportsMovement": true,
                    "supportsTemperatureControl": true,
                    "supportsFanControl": true,
                    "supportsFanSpeedReadback": true,
                    "supportsRelativeMovement": true,
                    "supportsAbsoluteMovement": true,
                    "supportsZOffset": true,
                    "supportsZOffsetAdjustment": true,
                    "supportsZOffsetReadback": true,
                    "supportsZOffsetFirmwareSave": true,
                    "supportsHoming": true,
                    "supportsHomingXY": true,
                    "supportsHomingZ": true,
                    "supportsHotendTemperature": true,
                    "supportsBedTemperature": true,
                    "supportsFilamentLoad": true,
                    "supportsFilamentUnload": true,
                    "supportsFilamentChange": false,
                    "supportedAxes": ["X", "Y", "Z"],
                    "verifiedSafety": safety
                ])
                let userData = try encoder.encode(visualAcceptanceDemoUser())
                let farmShapeData = try JSONSerialization.data(withJSONObject: [
                    "accountCount": 1,
                    "locationCount": 1,
                    "printerCount": fixture.printers.count,
                ])
                let queueStatsData = try encoder.encode(QueueStats(
                    totalQueued: 4,
                    totalPrinting: appStoreScreenshotDate == nil ? 3 : 1,
                    totalPaused: 0,
                    averageWaitTimeMinutes: 18,
                    byModel: []
                ))
                let detailsData = try encoder.encode(fixture.details)
                guard var idleDetails = try JSONSerialization.jsonObject(with: detailsData) as? [String: Any] else {
                    preconditionFailure("The idle-printer details fixture must encode as an object.")
                }
                idleDetails["id"] = DemoData.prusaMK4_2_ID.uuidString
                idleDetails["name"] = "Prusa MK4 #2"
                if var toolheads = idleDetails["toolheads"] as? [[String: Any]] {
                    for index in toolheads.indices {
                        toolheads[index]["id"] = "32590000-0000-0000-0000-000000000023"
                    }
                    idleDetails["toolheads"] = toolheads
                }
                let detailsDataByID = [
                    printerID: detailsData,
                    DemoData.prusaMK4_2_ID.uuidString.lowercased():
                        try JSONSerialization.data(withJSONObject: idleDetails),
                ]
                guard var idleCapabilities = try JSONSerialization.jsonObject(with: capabilitiesData) as? [String: Any] else {
                    preconditionFailure("The idle-printer capability fixture must encode as an object.")
                }
                idleCapabilities["printerId"] = DemoData.prusaMK4_2_ID.uuidString.lowercased()
                idleCapabilities["printerName"] = "Prusa MK4 #2"
                idleCapabilities["verifiedSafety"] = try JSONSerialization.jsonObject(with: encoder.encode(
                    issue3259VisualAcceptanceVerifiedSafety(
                        configurationRevision: fixture.printers.first(where: { $0.id == DemoData.prusaMK4_2_ID })?.configurationRevision ?? 0,
                        observedAt: Date()
                    )
                ))
                let capabilitiesDataByID = [
                    printerID: capabilitiesData,
                    DemoData.prusaMK4_2_ID.uuidString.lowercased():
                        try JSONSerialization.data(withJSONObject: idleCapabilities),
                ]
                let remainingSecondsByPrinterID: [UUID: Double] = [
                    DemoData.voron24_ID: 9_660,
                    issue3259Ender3S1ID: 7_200,
                    issue3259PrusaMiniID: 1_320,
                    DemoData.bambuP1S_ID: 19_800,
                    issue3259PrusaXLID: 12_600,
                    issue3259BambuA1ID: 5_040,
                ]
                let additionalStatusDataByID: [String: Data] = try Dictionary(
                    uniqueKeysWithValues: fixture.printers
                        .filter { $0.id != DemoData.prusaMK4_1_ID }
                        .map { printer in
                            var status = PrinterStatusDetail(
                                id: printer.id,
                                isOnline: printer.isOnline,
                                state: printer.state,
                                progress: printer.progress.map { $0 * 100 },
                                currentLayer: printer.currentLayer,
                                totalLayers: printer.totalLayers,
                                fanSpeedPercent: printer.fanSpeedPercent,
                                liveZOffsetMm: printer.liveZOffsetMm,
                                jobName: printer.jobName,
                                thumbnailUrl: printer.thumbnailUrl,
                                cameraStreamUrl: printer.cameraStreamUrl,
                                cameraSnapshotUrl: printer.cameraSnapshotUrl,
                                x: printer.x,
                                y: printer.y,
                                z: printer.z,
                                hotendTemp: printer.hotendTemp,
                                bedTemp: printer.bedTemp,
                                hotendTarget: printer.hotendTarget,
                                bedTarget: printer.bedTarget,
                                homedAxes: printer.homedAxes,
                                spoolInfo: printer.spoolInfo,
                                mmuStatus: nil,
                                printTimeLeftSeconds: remainingSecondsByPrinterID[printer.id]
                            )
                            if printer.id == DemoData.prusaMK4_2_ID {
                                status.safetyTelemetry = issue3259VisualAcceptanceSafetyTelemetry(
                                    printer: printer,
                                    observedAt: Date()
                                )
                            }
                            return (printer.id.uuidString.lowercased(), try encoder.encode(status))
                        }
                )
                let printerID = DemoData.prusaMK4_1_ID.uuidString.lowercased()
                let assignedQueueData = try encoder.encode([
                    QueuedJobInfo(
                        id: "32590000-0000-0000-0000-000000000002",
                        rowVersion: "issue3259-prusa1-assigned-v1",
                        name: "Coral spool bracket.gcode",
                        fileName: "Coral spool bracket.gcode",
                        assignedPrinterId: printerID,
                        printerName: "Prusa MK4 #1",
                        printerModel: "Prusa MK4",
                        status: "Queued",
                        priority: .normal,
                        queuePosition: 0,
                        estimatedPrintTimeSeconds: 5_400,
                        actualStartTimeUtc: nil,
                        actualEndTimeUtc: nil,
                        actualPrintTimeSeconds: nil,
                        failureReason: nil,
                        createdAtUtc: Date(timeIntervalSince1970: 1_790_000_000),
                        updatedAtUtc: nil,
                        thumbnailUrl: issue3259QueueThumbnailPath("32590000-0000-0000-0000-000000000002"),
                        filamentName: "PLA",
                        filamentColor: "#EF6B4A",
                        copies: 1,
                        completedCopies: 0,
                        remainingCopies: 1
                    ),
                    QueuedJobInfo(
                        id: "32590000-0000-0000-0000-000000000003",
                        rowVersion: "issue3259-prusa1-queue-head-v1",
                        name: "Toolhead cable guide.gcode",
                        fileName: "Toolhead cable guide.gcode",
                        assignedPrinterId: printerID,
                        printerName: "Prusa MK4 #1",
                        printerModel: "Prusa MK4",
                        status: "Queued",
                        priority: .high,
                        queuePosition: 1,
                        estimatedPrintTimeSeconds: 8_100,
                        actualStartTimeUtc: nil,
                        actualEndTimeUtc: nil,
                        actualPrintTimeSeconds: nil,
                        failureReason: nil,
                        createdAtUtc: Date(timeIntervalSince1970: 1_790_000_100),
                        updatedAtUtc: nil,
                        thumbnailUrl: issue3259QueueThumbnailPath("32590000-0000-0000-0000-000000000003"),
                        filamentName: "PLA",
                        filamentColor: "#EF6B4A",
                        copies: 1,
                        completedCopies: 0,
                        remainingCopies: 1
                    ),
                    QueuedJobInfo(
                        id: "32590000-0000-0000-0000-000000000004",
                        rowVersion: "issue3259-prusa1-queue-following-v1",
                        name: "Controller mount.gcode",
                        fileName: "Controller mount.gcode",
                        assignedPrinterId: printerID,
                        printerName: "Prusa MK4 #1",
                        printerModel: "Prusa MK4",
                        status: "Queued",
                        priority: .normal,
                        queuePosition: 2,
                        estimatedPrintTimeSeconds: 4_800,
                        actualStartTimeUtc: nil,
                        actualEndTimeUtc: nil,
                        actualPrintTimeSeconds: nil,
                        failureReason: nil,
                        createdAtUtc: Date(timeIntervalSince1970: 1_790_000_200),
                        updatedAtUtc: nil,
                        thumbnailUrl: issue3259QueueThumbnailPath("32590000-0000-0000-0000-000000000004"),
                        filamentName: "PLA",
                        filamentColor: "#EF6B4A",
                        copies: 1,
                        completedCopies: 0,
                        remainingCopies: 1
                    ),
                ])
                let idleAssignedQueueData = try encoder.encode([
                    QueuedJobInfo(
                        id: "32590000-0000-0000-0000-000000000020",
                        rowVersion: "issue3259-prusa2-assigned-v1",
                        name: "clip_holder",
                        fileName: "clip_holder.gcode",
                        assignedPrinterId: DemoData.prusaMK4_2_ID.uuidString.lowercased(),
                        printerName: "Prusa MK4 #2",
                        printerModel: "Prusa MK4",
                        status: "Queued",
                        priority: .normal,
                        queuePosition: 0,
                        estimatedPrintTimeSeconds: 5_400,
                        actualStartTimeUtc: nil,
                        actualEndTimeUtc: nil,
                        actualPrintTimeSeconds: nil,
                        failureReason: nil,
                        createdAtUtc: Date(timeIntervalSince1970: 1_790_000_000),
                        updatedAtUtc: nil,
                        thumbnailUrl: issue3259QueueThumbnailPath("32590000-0000-0000-0000-000000000020"),
                        filamentName: "PLA",
                        filamentColor: "#EF6B4A",
                        copies: 4,
                        completedCopies: 0,
                        remainingCopies: 4
                    ),
                    QueuedJobInfo(
                        id: "32590000-0000-0000-0000-000000000021",
                        rowVersion: "issue3259-prusa2-queue-head-v1",
                        name: "shelf_bracket",
                        fileName: "shelf_bracket.gcode",
                        assignedPrinterId: DemoData.prusaMK4_2_ID.uuidString.lowercased(),
                        printerName: "Prusa MK4 #2",
                        printerModel: "Prusa MK4",
                        status: "Queued",
                        priority: .high,
                        queuePosition: 1,
                        estimatedPrintTimeSeconds: 8_100,
                        actualStartTimeUtc: nil,
                        actualEndTimeUtc: nil,
                        actualPrintTimeSeconds: nil,
                        failureReason: nil,
                        createdAtUtc: Date(timeIntervalSince1970: 1_790_000_100),
                        updatedAtUtc: nil,
                        thumbnailUrl: issue3259QueueThumbnailPath("32590000-0000-0000-0000-000000000021"),
                        filamentName: "PETG",
                        filamentColor: "#3B82F6",
                        copies: 6,
                        completedCopies: 0,
                        remainingCopies: 6
                    ),
                    QueuedJobInfo(
                        id: "32590000-0000-0000-0000-000000000022",
                        rowVersion: "issue3259-prusa2-queue-following-v1",
                        name: "spiral_vase",
                        fileName: "spiral_vase.gcode",
                        assignedPrinterId: DemoData.prusaMK4_2_ID.uuidString.lowercased(),
                        printerName: "Prusa MK4 #2",
                        printerModel: "Prusa MK4",
                        status: "Queued",
                        priority: .normal,
                        queuePosition: 2,
                        estimatedPrintTimeSeconds: 4_800,
                        actualStartTimeUtc: nil,
                        actualEndTimeUtc: nil,
                        actualPrintTimeSeconds: nil,
                        failureReason: nil,
                        createdAtUtc: Date(timeIntervalSince1970: 1_790_000_200),
                        updatedAtUtc: nil,
                        thumbnailUrl: issue3259QueueThumbnailPath("32590000-0000-0000-0000-000000000022"),
                        filamentName: "PLA",
                        filamentColor: "#EF6B4A",
                        copies: 1,
                        completedCopies: 0,
                        remainingCopies: 1
                    ),
                ])
                let recentFailureHistoryData = try encoder.encode(
                    QueueHistoryPage(
                        entries: appStoreScreenshotDate == nil ? [
                            QueueHistoryEntry(
                                id: DemoData.job9ID.uuidString,
                                jobName: "cable_chain",
                                printerName: "Voron 2.4",
                                status: "Failed",
                                completedAt: Calendar.current.date(
                                    bySettingHour: 8,
                                    minute: 2,
                                    second: 0,
                                    of: Date()
                                ),
                                durationSeconds: 3_600,
                                completionPercentage: 12,
                                failureReason: "Thermal runaway detected"
                            )
                        ] : [],
                        totalCount: appStoreScreenshotDate == nil ? 1 : 0,
                        currentPage: 1,
                        pageSize: 5,
                        stats: nil
                    )
                )
                let globalQueueData = try encoder.encode(
                    issue3259VisualAcceptanceQueue(includeCurrentPrint: true)
                )
                let globalQueueAfterRerunData = try encoder.encode(
                    issue3259VisualAcceptanceQueue(includeCurrentPrint: true, includeRerunJob: true)
                )
                let globalQueueAfterSecondRerunData = try encoder.encode(
                    issue3259VisualAcceptanceQueue(includeCurrentPrint: true, includeSecondRerunJob: true)
                )
                let failedJobData = Data("""
                {
                    "id": "\(DemoData.job9ID)",
                    "rowVersion": "issue3259-failed-job-v1",
                    "status": "Failed",
                    "priority": "Normal",
                    "queuePosition": 0,
                    "gcodeFileName": "cable_chain.gcode",
                    "assignedPrinterId": "\(DemoData.voron24_ID)",
                    "assignedPrinterName": "Voron 2.4",
                    "createdAt": "2026-10-04T10:00:00Z",
                    "actualStartTime": "2026-10-04T10:10:00Z",
                    "actualEndTime": "2026-10-04T11:10:00Z",
                    "estimatedPrintTime": "01:00:00",
                    "estimatedFilamentUsage": 48.0,
                    "failureReason": "Thermal runaway detected",
                    "copies": 1,
                    "completedCopies": 0,
                    "remainingCopies": 1
                }
                """.utf8)
                let secondFailedJobData = Data("""
                {
                    "id": "\(DemoData.job10ID)",
                    "rowVersion": "issue3259-second-failed-job-v1",
                    "status": "Failed",
                    "priority": "Normal",
                    "queuePosition": 0,
                    "gcodeFileName": "lamp_shade_textured.gcode",
                    "assignedPrinterId": "\(DemoData.voron24_ID)",
                    "assignedPrinterName": "Voron 2.4",
                    "createdAt": "2026-10-03T10:00:00Z",
                    "actualStartTime": "2026-10-03T10:10:00Z",
                    "actualEndTime": "2026-10-03T11:10:00Z",
                    "estimatedPrintTime": "01:00:00",
                    "estimatedFilamentUsage": 65.0,
                    "failureReason": "Heater disconnected",
                    "copies": 1,
                    "completedCopies": 0,
                    "remainingCopies": 1
                }
                """.utf8)
                let rerunResponseData = Data("""
                {
                    "id": "32590000-0000-0000-0000-000000000104",
                    "rowVersion": "issue3259-rerun-v1",
                    "name": "cable_chain.gcode",
                    "status": "Queued",
                    "priority": "Normal",
                    "queuePosition": 3,
                    "createdAtUtc": "2026-10-05T16:00:00Z",
                    "copies": 1,
                    "completedCopies": 0,
                    "remainingCopies": 1
                }
                """.utf8)
                let secondRerunResponseData = Data("""
                {
                    "id": "32590000-0000-0000-0000-000000000105",
                    "rowVersion": "issue3259-second-rerun-v1",
                    "name": "lamp_shade_textured.gcode",
                    "status": "Queued",
                    "priority": "Normal",
                    "queuePosition": 3,
                    "createdAtUtc": "2026-10-05T16:00:00Z",
                    "copies": 1,
                    "completedCopies": 0,
                    "remainingCopies": 1
                }
                """.utf8)
                lock.lock()
                fixtureState = FixtureState(
                    printersData: printersData,
                    printersByID: printersByID,
                    currentJobThumbnailUrlsByID: currentJobThumbnailUrlsByID,
                    currentJobThumbnailDataByID: fixture.currentJobThumbnailDataByID,
                    queueThumbnailDataByJobID: fixture.queueThumbnailDataByJobID,
                    statusData: try encoder.encode(fixture.status),
                    additionalStatusDataByID: additionalStatusDataByID,
                    detailsDataByID: detailsDataByID,
                    attentionData: try encoder.encode(fixture.attentionFeed),
                    capabilitiesDataByID: capabilitiesDataByID,
                    assignedQueueDataByID: [
                        printerID: assignedQueueData,
                        DemoData.prusaMK4_2_ID.uuidString.lowercased(): idleAssignedQueueData,
                    ],
                    globalQueueData: globalQueueData,
                    globalQueueAfterRerunData: globalQueueAfterRerunData,
                    globalQueueAfterSecondRerunData: globalQueueAfterSecondRerunData,
                    recentFailureHistoryData: recentFailureHistoryData,
                    failedJobsDataByID: [
                        DemoData.job9ID.uuidString: failedJobData,
                        DemoData.job10ID.uuidString: secondFailedJobData,
                    ],
                    failedJobRowVersionsByID: [
                        DemoData.job9ID.uuidString: "issue3259-failed-job-v1",
                        DemoData.job10ID.uuidString: "issue3259-second-failed-job-v1",
                    ],
                    rerunResponsesByID: [
                        DemoData.job9ID.uuidString: rerunResponseData,
                        DemoData.job10ID.uuidString: secondRerunResponseData,
                    ],
                    farmShapeData: farmShapeData,
                    queueStatsData: queueStatsData,
                    userData: userData
                )
                lock.unlock()
            } catch {
                preconditionFailure("The authenticated visual-acceptance API fixture could not be encoded: \(error)")
            }
        }

        override class func canInit(with request: URLRequest) -> Bool {
            request.url?.host == "uitest.printfarmer.local"
        }

        override class func canonicalRequest(for request: URLRequest) -> URLRequest {
            request
        }

        override func startLoading() {
            guard let url = request.url else {
                client?.urlProtocol(self, didFailWithError: URLError(.badURL))
                return
            }
            let (statusCode, contentType, body) = Self.response(for: request)
            guard let response = HTTPURLResponse(
                url: url,
                statusCode: statusCode,
                httpVersion: "HTTP/1.1",
                headerFields: ["Content-Type": contentType]
            ) else {
                client?.urlProtocol(self, didFailWithError: URLError(.cannotParseResponse))
                return
            }
            client?.urlProtocol(self, didReceive: response, cacheStoragePolicy: .notAllowed)
            client?.urlProtocol(self, didLoad: body)
            client?.urlProtocolDidFinishLoading(self)
        }

        override func stopLoading() {}

        private static func response(for request: URLRequest) -> (Int, String, Data) {
            lock.lock()
            defer { lock.unlock() }
            guard var fixture = fixtureState else {
                return (503, "text/plain", Data("The visual-acceptance fixture was not installed.".utf8))
            }
            guard request.value(forHTTPHeaderField: "Authorization") == "Bearer \(accessToken)" else {
                return (401, "application/json", Data(#"{"error":"unauthorized"}"#.utf8))
            }

            let path = request.url?.path ?? ""
            let method = request.httpMethod?.uppercased() ?? "GET"
            let result: (Int, String, Data)
            switch (method, path) {
            case ("GET", "/api/auth/me"):
                result = (200, "application/json", fixture.userData)
            case ("GET", "/api/attention"):
                result = (200, "application/json", fixture.attentionData)
            case ("GET", "/api/printers"):
                result = (200, "application/json", fixture.printersData)
            case ("GET", "/api/printers/camera-urls"):
                result = (200, "application/json", Data("[]".utf8))
            case ("GET", let path)
                where path.hasPrefix("/api/printers/") && path.split(separator: "/").count == 3:
                let components = path.split(separator: "/")
                if let printer = fixture.printersByID[String(components[2]).lowercased()] {
                    result = (200, "application/json", printer)
                } else {
                    result = (404, "application/json", Data(#"{"error":"not-found"}"#.utf8))
                }
            case ("GET", let path)
                where path.hasPrefix("/api/printers/") && path.hasSuffix("/details"):
                let components = path.split(separator: "/")
                if components.count == 4,
                   let details = fixture.detailsDataByID[String(components[2]).lowercased()] {
                    result = (200, "application/json", details)
                } else {
                    result = (404, "application/json", Data(#"{"error":"not-found"}"#.utf8))
                }
            case ("GET", "/api/printers/\(printerID)/status"):
                result = (200, "application/json", fixture.statusData)
            case ("GET", let path)
                where path.hasPrefix("/api/printers/") && path.hasSuffix("/status"):
                let components = path.split(separator: "/")
                if components.count == 4,
                   let status = fixture.additionalStatusDataByID[String(components[2]).lowercased()] {
                    result = (200, "application/json", status)
                } else {
                    result = (404, "application/json", Data(#"{"error":"not-found"}"#.utf8))
                }
            case ("GET", let path)
                where path.hasPrefix("/api/printers/") && path.hasSuffix("/backend-capabilities"):
                let components = path.split(separator: "/")
                if components.count == 4,
                   let capabilities = fixture.capabilitiesDataByID[String(components[2]).lowercased()] {
                    result = (200, "application/json", capabilities)
                } else {
                    result = (404, "application/json", Data(#"{"error":"not-found"}"#.utf8))
                }
            case ("GET", let path)
                where path.hasPrefix("/api/job-queue-analytics/printer/"):
                let components = path.split(separator: "/")
                if components.count == 4,
                   let queue = fixture.assignedQueueDataByID[String(components[3]).lowercased()] {
                    result = (200, "application/json", queue)
                } else {
                    result = (404, "application/json", Data(#"{"error":"not-found"}"#.utf8))
                }
            case ("GET", "/api/job-queue-analytics"):
                result = (200, "application/json", fixture.globalQueueData)
            case ("GET", "/api/job-queue-analytics/stats"):
                result = (200, "application/json", fixture.queueStatsData)
            case ("GET", "/api/job-queue-analytics/history"):
                result = (200, "application/json", fixture.recentFailureHistoryData)
            case ("GET", "/api/system/farm-shape"):
                result = (200, "application/json", fixture.farmShapeData)
            case ("GET", let path)
                where path.hasPrefix("/api/job-queue/") && !path.hasSuffix("/rerun"):
                let jobID = String(path.dropFirst("/api/job-queue/".count))
                result = fixture.failedJobsDataByID[jobID].map {
                    (200, "application/json", $0)
                } ?? (404, "application/json", Data(#"{"error":"not-found"}"#.utf8))
            case ("POST", let path) where path.hasSuffix("/rerun"):
                let jobID = String(
                    path
                        .dropFirst("/api/job-queue/".count)
                        .dropLast("/rerun".count)
                )
                guard let rowVersion = fixture.failedJobRowVersionsByID[jobID],
                      request.value(forHTTPHeaderField: "If-Match") == "\"\(rowVersion)\"",
                      let responseData = fixture.rerunResponsesByID[jobID] else {
                    result = (412, "application/json", Data(#"{"error":"precondition_failed"}"#.utf8))
                    break
                }
                fixture.globalQueueData = jobID == DemoData.job9ID.uuidString
                    ? fixture.globalQueueAfterRerunData
                    : fixture.globalQueueAfterSecondRerunData
                result = (200, "application/json", responseData)
            case ("POST", let path)
                where path.hasPrefix("/api/printers/")
                    && path.split(separator: "/").count == 4
                    && (path.hasSuffix("/filament-load") || path.hasSuffix("/filament-unload")):
                let printerKey = String(path.split(separator: "/")[2]).lowercased()
                if fixture.capabilitiesDataByID[printerKey] != nil {
                    result = (
                        200,
                        "application/json",
                        Data(#"{"success":true,"message":"Accepted by authenticated visual-acceptance API fixture."}"#.utf8)
                    )
                } else {
                    result = (404, "application/json", Data(#"{"error":"not-found"}"#.utf8))
                }
            case ("GET", let path)
                where path.hasPrefix("/api/printers/")
                    && path.hasSuffix("/current-job/thumbnail"):
                let components = path.split(separator: "/")
                let requestedTarget = request.url.map {
                    $0.path + ($0.query.map { "?\($0)" } ?? "")
                }
                let printerKey = components.count == 5 ? String(components[2]).lowercased() : nil
                let expectedTarget = printerKey.flatMap {
                    fixture.currentJobThumbnailUrlsByID[$0]
                }
                if expectedTarget == requestedTarget,
                   let printerKey,
                   let thumbnail = fixture.currentJobThumbnailDataByID[printerKey] {
                    result = (200, "image/png", thumbnail)
                } else {
                    result = (404, "application/json", Data(#"{"error":"not-found"}"#.utf8))
                }
            case ("GET", let path)
                where path.hasPrefix("/api/gcode-files/thumbnail/")
                    && UUID(uuidString: String(path.dropFirst("/api/gcode-files/thumbnail/".count))) != nil:
                let jobID = String(path.dropFirst("/api/gcode-files/thumbnail/".count)).lowercased()
                result = fixture.queueThumbnailDataByJobID[jobID].map { (200, "image/png", $0) }
                    ?? (404, "application/json", Data(#"{"error":"not-found"}"#.utf8))
            case ("POST", "/api/printers/\(printerID)/temps"):
                result = updateTemperatures(&fixture, request: request)
            case ("POST", "/api/printers/\(printerID)/fan"):
                result = updateStatus(
                    &fixture,
                    field: "fanSpeedPercent",
                    request: request,
                    requestField: "speedPercent",
                    addsToCurrentValue: false
                )
            case ("POST", "/api/printers/\(printerID)/z-offset/adjust"):
                result = updateStatus(
                    &fixture,
                    field: "liveZOffsetMm",
                    request: request,
                    requestField: "offsetMm",
                    addsToCurrentValue: true
                )
            default:
                result = (404, "application/json", Data(#"{"error":"not-found"}"#.utf8))
            }
            fixtureState = fixture
            return result
        }

        private static func updateTemperatures(
            _ fixture: inout FixtureState,
            request: URLRequest
        ) -> (Int, String, Data) {
            do {
                guard let body = requestBodyData(request),
                      let requestObject = try JSONSerialization.jsonObject(with: body) as? [String: Any] else {
                    return (400, "application/problem+json", Data(#"{"detail":"Fixture received an invalid temperature command body."}"#.utf8))
                }
                let hotend = requestObject["hotend"] as? NSNumber
                let bed = requestObject["bed"] as? NSNumber
                guard hotend != nil || bed != nil else {
                    return (400, "application/problem+json", Data(#"{"detail":"Temperature command must include hotend or bed."}"#.utf8))
                }
                guard hotend.map({ $0.doubleValue.isFinite && $0.doubleValue.rounded() == $0.doubleValue && (0...300).contains($0.doubleValue) }) ?? true,
                      bed.map({ $0.doubleValue.isFinite && $0.doubleValue.rounded() == $0.doubleValue && (0...120).contains($0.doubleValue) }) ?? true else {
                    return (400, "application/problem+json", Data(#"{"detail":"Fixture heater targets must be whole degrees within the reported hardware limits."}"#.utf8))
                }
                guard var statusObject = try JSONSerialization.jsonObject(with: fixture.statusData) as? [String: Any] else {
                    return (500, "application/problem+json", Data(#"{"detail":"Fixture status payload could not be updated."}"#.utf8))
                }
                if let hotend { statusObject["hotendTarget"] = hotend }
                if let bed { statusObject["bedTarget"] = bed }
                fixture.statusData = try JSONSerialization.data(withJSONObject: statusObject)
                let command = CommandResult(
                    success: true,
                    message: "Accepted by authenticated visual-acceptance API fixture."
                )
                return (200, "application/json", try JSONEncoder().encode(command))
            } catch {
                return (500, "application/json", Data(#"{"error":"fixture-temperature-command-failed"}"#.utf8))
            }
        }

        private static func updateStatus(
            _ fixture: inout FixtureState,
            field: String,
            request: URLRequest,
            requestField: String,
            addsToCurrentValue: Bool
        ) -> (Int, String, Data) {
            do {
                guard let body = requestBodyData(request) else {
                    return (400, "application/problem+json", Data(#"{"detail":"Fixture received a command without an HTTP body."}"#.utf8))
                }
                guard let requestObject = try JSONSerialization.jsonObject(with: body) as? [String: Any],
                      let number = requestObject[requestField] as? NSNumber else {
                    return (400, "application/problem+json", Data(#"{"detail":"Fixture command body is missing the expected numeric field."}"#.utf8))
                }
                let value = number.doubleValue
                let commandMessage: String
                let validValue: Bool
                switch (field, requestField) {
                case ("fanSpeedPercent", "speedPercent"):
                    commandMessage = "speedPercent must be between 0 and 100."
                    validValue = value.isFinite && value.rounded() == value && (0...100).contains(value)
                case ("liveZOffsetMm", "offsetMm"):
                    commandMessage = "offsetMm must be non-zero and between -0.2 and 0.2 mm."
                    validValue = value.isFinite && value != 0 && (-0.2...0.2).contains(value)
                default:
                    return (400, "application/problem+json", Data(#"{"detail":"Fixture command field is unsupported."}"#.utf8))
                }
                let encoder = JSONEncoder()
                guard validValue else {
                    return (400, "application/json", try encoder.encode(
                        CommandResult(success: false, message: commandMessage)
                    ))
                }
                guard var statusObject = try JSONSerialization.jsonObject(with: fixture.statusData) as? [String: Any] else {
                    return (500, "application/problem+json", Data(#"{"detail":"Fixture status payload could not be updated."}"#.utf8))
                }
                guard let currentNumber = statusObject[field] as? NSNumber,
                      currentNumber.doubleValue.isFinite,
                      field != "fanSpeedPercent" || (0...100).contains(currentNumber.doubleValue) else {
                    let message = field == "fanSpeedPercent"
                        ? "The printer does not report a valid part-fan speed; no command was sent."
                        : "The printer does not report a valid live Z offset; no command was sent."
                    return (502, "application/json", try encoder.encode(
                        CommandResult(success: false, message: message)
                    ))
                }
                let currentValue = currentNumber.doubleValue
                statusObject[field] = addsToCurrentValue ? currentValue + value : value
                fixture.statusData = try JSONSerialization.data(withJSONObject: statusObject)
                let command = CommandResult(success: true, message: "Accepted by authenticated visual-acceptance API fixture.")
                return (200, "application/json", try encoder.encode(command))
            } catch {
                return (500, "application/json", Data(#"{"error":"fixture-command-failed"}"#.utf8))
            }
        }

        private static func requestBodyData(_ request: URLRequest) -> Data? {
            if let body = request.httpBody { return body }
            guard let stream = request.httpBodyStream else { return nil }
            stream.open()
            defer { stream.close() }

            var data = Data()
            var buffer = [UInt8](repeating: 0, count: 1_024)
            while true {
                let count = stream.read(&buffer, maxLength: buffer.count)
                guard count >= 0 else { return nil }
                guard count > 0 else { break }
                data.append(contentsOf: buffer.prefix(count))
            }
            return data
        }
    }

    private static func issue3259VisualAcceptanceVerifiedSafety(
        configurationRevision: Int64,
        observedAt: Date
    ) -> PrinterVerifiedSafetyDto {
        let supported = VerifiedSafetyOperationCapabilityDto(
            support: .supported,
            source: "issue3259-visual-acceptance-api-fixture",
            observedAtUtc: observedAt
        )
        let unsupported = VerifiedSafetyOperationCapabilityDto(
            support: .unsupported,
            source: "issue3259-visual-acceptance-api-fixture",
            observedAtUtc: observedAt
        )
        let verifiedScalar: (Double) -> VerifiedSafetyScalarFactDto = { value in
            VerifiedSafetyScalarFactDto(
                state: .verified,
                value: value,
                source: "issue3259-visual-acceptance-api-fixture",
                observedAtUtc: observedAt
            )
        }
        let verifiedVector: (SafetyVector3Dto) -> VerifiedSafetyVectorFactDto = { value in
            VerifiedSafetyVectorFactDto(
                state: .verified,
                value: value,
                source: "issue3259-visual-acceptance-api-fixture",
                observedAtUtc: observedAt
            )
        }
        let envelope = SafetyTravelEnvelopeDto(
            minimum: SafetyVector3Dto(x: 0, y: 0, z: 0),
            maximum: SafetyVector3Dto(x: 220, y: 220, z: 250)
        )
        let verifiedEnvelope = VerifiedSafetyEnvelopeFactDto(
            state: .verified,
            value: envelope,
            source: "issue3259-visual-acceptance-api-fixture",
            observedAtUtc: observedAt
        )

        return PrinterVerifiedSafetyDto(
            contractVersion: 1,
            discovery: VerifiedSafetyDiscoveryDto(
                state: .verified,
                observedAtUtc: observedAt,
                sourceRevision: String(configurationRevision)
            ),
            operations: VerifiedSafetyOperationsDto(
                absoluteMovement: supported,
                firmwareZOffsetSave: supported,
                filamentLoad: supported,
                filamentUnload: supported,
                filamentChange: unsupported
            ),
            extrusion: VerifiedSafetyExtrusionDto(
                minimumSafeMeasuredHotendTemperatureC: verifiedScalar(170)
            ),
            positioning: VerifiedSafetyPositioningDto(
                coordinateOriginMm: verifiedVector(SafetyVector3Dto(x: 0, y: 0, z: 0)),
                travelEnvelopeMm: verifiedEnvelope,
                minimumClearanceZMm: verifiedScalar(5)
            )
        )
    }

    private static func issue3259VisualAcceptanceSpool() -> SpoolmanSpool {
        SpoolmanSpool(
            id: 1,
            filamentId: 1,
            name: "Prusament PLA · Coral",
            material: "PLA",
            colorHex: "#EF6B4A",
            inUse: true,
            filamentName: "Prusament PLA",
            vendor: "Prusa Research",
            registeredAt: "2026-10-01",
            firstUsedAt: "2026-10-02",
            lastUsedAt: "2026-10-04",
            remainingWeightG: appStoreScreenshotDate == nil ? 84 : 612,
            initialWeightG: 1000,
            usedWeightG: appStoreScreenshotDate == nil ? 916 : 388,
            spoolWeightG: 200,
            remainingLengthMm: nil,
            usedLengthMm: nil,
            location: "Workshop",
            lotNumber: nil,
            archived: false,
            price: nil,
            comment: nil,
            hasNfcTag: true,
            usedPercent: appStoreScreenshotDate == nil ? 91.6 : 38.8,
            remainingPercent: appStoreScreenshotDate == nil ? 8.4 : 61.2
        )
    }

    private static func issue3259VisualAcceptanceQueue(
        includeCurrentPrint: Bool = false,
        includeRerunJob: Bool = false,
        includeSecondRerunJob: Bool = false
    ) -> [QueuedPrintJobResponse] {
        let mk4Printer = QueuePrinterMeta(
            id: DemoData.prusaMK4_1_ID.uuidString.lowercased(),
            name: "Prusa MK4 #1", modelName: "Prusa MK4",
            status: "Printing", isOnline: true
        )
        let idleMk4Printer = QueuePrinterMeta(
            id: DemoData.prusaMK4_2_ID.uuidString.lowercased(),
            name: "Prusa MK4 #2", modelName: "Prusa MK4",
            status: "Idle", isOnline: true
        )
        let voronPrinter = QueuePrinterMeta(
            id: DemoData.voron24_ID.uuidString.lowercased(),
            name: "Voron 2.4", modelName: "Voron 2.4",
            status: "Printing", isOnline: true
        )
        let bambuPrinter = QueuePrinterMeta(
            id: issue3259BambuA1ID.uuidString.lowercased(),
            name: "Bambu A1", modelName: "A1",
            status: "Printing", isOnline: true
        )
        let enderPrinter = QueuePrinterMeta(
            id: issue3259Ender3S1ID.uuidString.lowercased(),
            name: "Ender 3 S1", modelName: "Ender 3 S1",
            status: "Idle", isOnline: true
        )
        let createdAt = Date(timeIntervalSince1970: 1_791_134_000)

        func job(
            id: String,
            name: String,
            status: String,
            priority: PrintJobPriority,
            position: Int,
            requiredGrams: Int,
            durationSeconds: Int,
            assignedPrinter: QueuePrinterMeta?,
            compatibilityHint: QueuePrinterMeta? = nil,
            copies: Int = 1,
            materialType: String = "PLA",
            actualStartTime: Date? = nil
        ) -> QueuedPrintJobResponse {
            let row = QueuedJobInfo(
                id: id,
                rowVersion: "visual-acceptance-only-\(id)",
                name: name,
                fileName: name,
                assignedPrinterId: assignedPrinter?.id,
                printerName: assignedPrinter?.name ?? compatibilityHint?.name,
                printerModel: assignedPrinter?.modelName ?? compatibilityHint?.modelName,
                status: status,
                priority: priority,
                queuePosition: position,
                estimatedPrintTimeSeconds: durationSeconds,
                actualStartTimeUtc: actualStartTime,
                actualEndTimeUtc: nil,
                actualPrintTimeSeconds: nil,
                failureReason: nil,
                createdAtUtc: createdAt,
                updatedAtUtc: nil,
                thumbnailUrl: issue3259QueueThumbnailPath(id),
                filamentName: materialType == "PLA" ? "Prusament PLA" : materialType,
                filamentColor: materialType == "PLA" ? "#EF6B4A" : "#3B82F6",
                copies: copies,
                completedCopies: 0,
                remainingCopies: copies
            )
            let file = QueueGcodeFileMeta(
                id: id,
                name: name,
                fileName: name,
                fileSizeBytes: 1_200_000,
                materialType: materialType,
                nozzleDiameter: 0.4,
                estimatedPrintTimeSeconds: durationSeconds,
                estimatedFilamentUsageGrams: requiredGrams,
                thumbnailUrl: issue3259QueueThumbnailPath(id)
            )
            let estimatedStart = actualStartTime
                ?? createdAt.addingTimeInterval(TimeInterval(position * 7_200))
            return QueuedPrintJobResponse(
                job: row,
                gcodeFile: file,
                assignedPrinter: assignedPrinter,
                estimatedStartTime: estimatedStart,
                estimatedCompletionTime: estimatedStart.addingTimeInterval(TimeInterval(durationSeconds))
            )
        }

        var jobs: [QueuedPrintJobResponse] = []
        if includeCurrentPrint {
            jobs.append(job(
                id: DemoData.job1ID.uuidString.lowercased(),
                name: "benchy_0.2mm_PLA",
                status: "Printing",
                priority: .normal,
                position: 0,
                requiredGrams: 140,
                durationSeconds: 8_280,
                assignedPrinter: mk4Printer,
                actualStartTime: (appStoreScreenshotDate ?? Date()).addingTimeInterval(-6_000)
            ))
            jobs.append(job(
                id: "32590000-0000-0000-0000-000000000110",
                name: "gear_set_v3",
                status: "Printing",
                priority: .normal,
                position: 0,
                requiredGrams: 65,
                durationSeconds: 8_280,
                assignedPrinter: voronPrinter,
                actualStartTime: Date().addingTimeInterval(-1_800)
            ))
            jobs.append(job(
                id: "32590000-0000-0000-0000-000000000111",
                name: "cable_clip_set",
                status: "Printing",
                priority: .normal,
                position: 0,
                requiredGrams: 32,
                durationSeconds: 8_100,
                assignedPrinter: bambuPrinter,
                actualStartTime: Date().addingTimeInterval(-2_000)
            ))
        }
        jobs += [
            job(
                id: "32590000-0000-0000-0000-000000000101",
                name: "clip_holder",
                status: "Queued",
                priority: .normal,
                position: 0,
                requiredGrams: 42,
                durationSeconds: 5_400,
                assignedPrinter: nil,
                compatibilityHint: idleMk4Printer,
                copies: 4
            ),
            job(
                id: "32590000-0000-0000-0000-000000000102",
                name: "shelf_bracket",
                status: "Queued",
                priority: .high,
                position: 1,
                requiredGrams: 85,
                durationSeconds: 8_100,
                assignedPrinter: idleMk4Printer,
                copies: 6,
                materialType: "PETG"
            ),
            job(
                id: "32590000-0000-0000-0000-000000000103",
                name: "spiral_vase",
                status: "Queued",
                priority: .normal,
                position: 2,
                requiredGrams: 48,
                durationSeconds: 3_600,
                assignedPrinter: enderPrinter
            ),
            job(
                id: "32590000-0000-0000-0000-000000000106",
                name: "gear_set_v4",
                status: "Queued",
                priority: .normal,
                position: 3,
                requiredGrams: 65,
                durationSeconds: 4_800,
                assignedPrinter: voronPrinter
            ),
        ]
        if appStoreScreenshotDate != nil {
            jobs.removeAll { $0.job.status == "Printing" && $0.job.id != DemoData.job1ID.uuidString.lowercased() }
        }
        if includeRerunJob {
            jobs.append(job(
                id: "32590000-0000-0000-0000-000000000104",
                name: "cable_chain",
                status: "Queued",
                priority: .normal,
                position: 3,
                requiredGrams: 48,
                durationSeconds: 3_600,
                assignedPrinter: voronPrinter
            ))
        }
        if includeSecondRerunJob {
            jobs.append(job(
                id: "32590000-0000-0000-0000-000000000105",
                name: "lamp_shade_textured.gcode",
                status: "Queued",
                priority: .normal,
                position: 3,
                requiredGrams: 65,
                durationSeconds: 3_600,
                assignedPrinter: voronPrinter
            ))
        }
        return jobs
    }

    private static func issue3259VisualAcceptanceCoverage() -> FleetFilamentCoverage {
        let evaluatedAt = Date(timeIntervalSince1970: 1_791_137_600)
        let coverage = PrinterFilamentCoverage(
            printerId: DemoData.prusaMK4_1_ID,
            printerName: "Prusa MK4 #1",
            status: .runout,
            toolheads: [
                ToolheadFilamentCoverage(
                    toolheadIndex: 0,
                    toolheadId: issue3259VisualAcceptanceToolheadID,
                    toolheadName: "Extruder 1",
                    spoolId: 1,
                    material: "PLA",
                    filamentColor: "#EF6B4A",
                    remainingGrams: 84,
                    currentJobRequiredGrams: 140,
                    currentJobRemainingGrams: 90,
                    totalDemandGrams: 140,
                    status: .runout,
                    statusReason: "This job needs about 140 g."
                )
            ],
            activeJobId: DemoData.job1ID,
            activeJobName: "benchy_0.2mm_PLA.gcode",
            activeJobProgress: 0.64,
            earliestPredictedRunoutAt: nil,
            assignedQueuedJobCount: 0,
            evaluatedAtUtc: evaluatedAt
        )
        return FleetFilamentCoverage(printers: [coverage], evaluatedAtUtc: evaluatedAt)
    }
    #endif

    private static func seedNavigationChromeServerIfRequested(
        arguments: [String],
        registry: ServerRegistry
    ) {
        guard arguments.contains(navigationChromeLaunchArgument),
              registry.servers.count == 1 else {
            return
        }

        do {
            _ = try registry.add(
                displayName: "UI Test Backup",
                baseURL: URL(string: "http://uitest-backup.printfarmer.local")!,
                makeActiveIfNeeded: false
            )
        } catch {
            assertionFailure("UITestBootstrap failed to seed backup registry: \(error)")
        }
    }

    /// Returns a UserDefaults domain isolated from `.standard`, with any
    /// prior state removed so each launch starts from a clean slate.
    static func makeUserDefaults(arguments: [String] = CommandLine.arguments) -> UserDefaults {
        let defaults = UserDefaults(suiteName: userDefaultsSuiteName) ?? .standard
        if !arguments.contains(preserveStateLaunchArgument) {
            defaults.removePersistentDomain(forName: userDefaultsSuiteName)
        }
        return defaults
    }

    private static func clearSystemNotificationState() {
        #if canImport(UserNotifications)
        let center = UNUserNotificationCenter.current()
        center.removeAllDeliveredNotifications()
        center.removeAllPendingNotificationRequests()
        #endif
    }

    private static func seedStandardRouteDefaults(arguments: [String]) {
        let defaults = UserDefaults.standard
        guard !arguments.contains(preserveStateLaunchArgument) else {
            return
        }
        defaults.removeObject(forKey: AdvancedPrinterControlsPromptState.hasSeenPromptKey)
        defaults.removeObject(forKey: "hasSeenOnboarding")
        defaults.removeObject(forKey: "hasCompletedNetworkPermission")

        if arguments.contains(advancedControlsPromptSeenLaunchArgument) {
            defaults.set(true, forKey: AdvancedPrinterControlsPromptState.hasSeenPromptKey)
        }
        if arguments.contains(onboardingSeenLaunchArgument) {
            defaults.set(true, forKey: "hasSeenOnboarding")
        }
        if arguments.contains(networkPermissionCompletedLaunchArgument) {
            defaults.set(true, forKey: "hasCompletedNetworkPermission")
        }
    }

    // MARK: - F2-U2 #780 UI-test scenario

    static func attentionActionsScenarioFeed() -> AttentionFeed {
        let occurredAt = ISO8601DateFormatter()
            .date(from: "2026-07-22T15:00:00Z")!
        let deadlineAt = ISO8601DateFormatter()
            .date(from: "2026-07-23T15:00:00Z")!

        let failure = AttentionItem(
            id: "failure:78000000-0000-0000-0000-000000000001",
            kind: .failure,
            severity: .critical,
            printerId: duplicateNamePrinterID,
            printerName: "Prusa MK4 #1",
            title: "Print failure — auto-paused",
            detail: "Camera review is required before resuming or cancelling this print.",
            occurredAt: occurredAt,
            actions: [
                AttentionAction(kind: .resume, label: "Resume", requiresConfirmation: true),
                AttentionAction(kind: .cancel, label: "Cancel", requiresConfirmation: true),
                AttentionAction(kind: .snooze, label: "Snooze", requiresConfirmation: false),
            ],
            deadlineAt: deadlineAt,
            jobId: DemoData.job1ID
        )

        let maintenance = AttentionItem(
            id: "maintenance:78000000-0000-0000-0000-000000000002",
            kind: .maintenance,
            severity: .warning,
            printerId: DemoData.prusaMK4_2_ID,
            printerName: "Prusa MK4 #2",
            title: "Lubrication inspection due",
            detail: "Inspect the linear rails and acknowledge the maintenance alert.",
            occurredAt: occurredAt.addingTimeInterval(-300),
            actions: [
                AttentionAction(
                    kind: .acknowledge,
                    label: "Acknowledge",
                    requiresConfirmation: false
                ),
            ]
        )

        let unavailableMedia = AttentionItem(
            id: "failure:78000000-0000-0000-0000-000000000003",
            kind: .failure,
            severity: .info,
            printerId: DemoData.bambuX1C_ID,
            printerName: "Bambu X1C",
            title: "Camera snapshot unavailable",
            detail: "The card remains usable when camera data cannot be decoded.",
            occurredAt: occurredAt.addingTimeInterval(-600),
            actions: []
        )

        return AttentionFeed(
            items: [failure, maintenance, unavailableMedia],
            nextCursor: nil,
            healthyPrinterCount: 4
        )
    }

    static func attentionHarvestScanScenarioFeed() -> AttentionFeed {
        let occurredAt = ISO8601DateFormatter()
            .date(from: "2026-07-22T14:45:00Z")!
        let harvest = AttentionItem(
            id: "harvest:78000000-0000-0000-0000-000000000004",
            kind: .harvest,
            severity: .warning,
            printerId: DemoData.prusaMK4_2_ID,
            printerName: "Prusa MK4 #2",
            title: "Completed plate ready to harvest",
            detail: "Scan the destination bin before clearing the plate.",
            occurredAt: occurredAt.addingTimeInterval(-900),
            actions: [
                AttentionAction(
                    kind: .harvest,
                    label: "Harvest",
                    requiresConfirmation: false
                ),
            ],
            jobId: DemoData.job7ID
        )

        return AttentionFeed(
            items: [harvest],
            nextCursor: nil,
            healthyPrinterCount: 4
        )
    }

    static func attentionActionsSnapshotData() -> Data {
        Data(
            base64Encoded:
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII="
        )!
    }

    // MARK: - F4-M #778 UI-test scenario

    /// Builds the deterministic fleet coverage snapshot used by the
    /// `authenticatedFilamentCoverageScenario` mode. Every state
    /// required by the frozen contract is covered by exactly one
    /// demo printer so a single Farm view exercises them in one pass:
    ///
    ///   * Prusa MK4 #1 → `.covers`
    ///   * Prusa MK4 #2 → `.runout` with predicted ETA
    ///   * Bambu X1C    → `.runout` without predicted ETA
    ///   * Bambu P1S    → `.unknown` (no badge, per contract)
    ///   * Voron 2.4    → `.runout` with predicted ETA, and three
    ///     toolheads TWO of which share the display name
    ///     `"Extruder"` — proving stable-id rows survive duplicate
    ///     names.
    ///
    /// Ender 3 V3 (id 6) is intentionally omitted from the coverage
    /// fleet so the "printer without a snapshot" path also gets
    /// exercised implicitly (badge absent for that card).
    static func filamentCoverageScenarioFleet() -> FleetFilamentCoverage {
        // Use fixed timestamps so the ETA badge text is deterministic
        // across runs and destinations. The formatter renders the
        // local short time, so tests key on the presence of "at " in
        // the a11y label rather than a specific hour.
        let evaluatedAt = ISO8601DateFormatter().date(from: "2026-07-21T18:00:00Z")!
        let runoutETA  = ISO8601DateFormatter().date(from: "2026-07-21T21:30:00Z")!
        let voronETA   = ISO8601DateFormatter().date(from: "2026-07-21T22:15:00Z")!

        let covers = PrinterFilamentCoverage(
            printerId: DemoData.prusaMK4_1_ID,
            printerName: "Prusa MK4 #1",
            status: .covers,
            toolheads: [
                ToolheadFilamentCoverage(
                    toolheadIndex: 0,
                    toolheadName: "Extruder 1",
                    material: "PLA",
                    remainingGrams: 620,
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

        let runoutWithETA = PrinterFilamentCoverage(
            printerId: DemoData.prusaMK4_2_ID,
            printerName: "Prusa MK4 #2",
            status: .runout,
            toolheads: [
                ToolheadFilamentCoverage(
                    toolheadIndex: 0,
                    toolheadName: "Extruder 1",
                    material: "PETG",
                    remainingGrams: 42,
                    status: .runout,
                    predictedRunoutAt: runoutETA
                )
            ],
            activeJobId: nil,
            activeJobName: nil,
            activeJobProgress: nil,
            earliestPredictedRunoutAt: runoutETA,
            assignedQueuedJobCount: 1,
            evaluatedAtUtc: evaluatedAt
        )

        let runoutWithoutETA = PrinterFilamentCoverage(
            printerId: DemoData.bambuX1C_ID,
            printerName: "Bambu X1C",
            status: .runout,
            toolheads: [
                ToolheadFilamentCoverage(
                    toolheadIndex: 0,
                    toolheadName: "Hotend",
                    material: "PLA",
                    remainingGrams: 15,
                    status: .runout
                )
            ],
            activeJobId: nil,
            activeJobName: nil,
            activeJobProgress: nil,
            earliestPredictedRunoutAt: nil,
            assignedQueuedJobCount: 2,
            evaluatedAtUtc: evaluatedAt
        )

        let unknown = PrinterFilamentCoverage(
            printerId: DemoData.bambuP1S_ID,
            printerName: "Bambu P1S",
            status: .unknown,
            toolheads: [
                ToolheadFilamentCoverage(
                    toolheadIndex: 0,
                    toolheadName: "Hotend",
                    status: .unknown,
                    statusReason: "spool-remaining-unknown"
                )
            ],
            activeJobId: nil,
            activeJobName: nil,
            activeJobProgress: nil,
            earliestPredictedRunoutAt: nil,
            assignedQueuedJobCount: 0,
            evaluatedAtUtc: evaluatedAt
        )

        // Voron 2.4: 3 toolheads. Toolheads 0 and 2 deliberately share
        // the display name "Extruder" to prove that per-row identity
        // uses the backend id (or stable index), never the display
        // name. Toolhead 1 carries a distinct `toolheadId` so the
        // detail row's a11y id is derived from that backend UUID.
        let sharedName = "Extruder"
        let voron = PrinterFilamentCoverage(
            printerId: DemoData.voron24_ID,
            printerName: "Voron 2.4",
            status: .runout,
            toolheads: [
                ToolheadFilamentCoverage(
                    toolheadIndex: 0,
                    toolheadName: sharedName,
                    material: "PLA",
                    remainingGrams: 500,
                    status: .covers
                ),
                ToolheadFilamentCoverage(
                    toolheadIndex: 1,
                    toolheadId: UUID(uuidString: "20000000-1111-2222-3333-444444444444")!,
                    toolheadName: "Support",
                    material: "PVA",
                    remainingGrams: 30,
                    status: .runout,
                    predictedRunoutAt: voronETA
                ),
                ToolheadFilamentCoverage(
                    toolheadIndex: 2,
                    toolheadName: sharedName,
                    material: "PETG",
                    remainingGrams: 12,
                    status: .runout
                )
            ],
            activeJobId: nil,
            activeJobName: nil,
            activeJobProgress: nil,
            earliestPredictedRunoutAt: voronETA,
            assignedQueuedJobCount: 0,
            evaluatedAtUtc: evaluatedAt
        )

        return FleetFilamentCoverage(
            printers: [covers, runoutWithETA, runoutWithoutETA, unknown, voron, duplicateNameCoverage()],
            evaluatedAtUtc: evaluatedAt
        )
    }

    // MARK: - Duplicate-name printer (reviewer blocker D)

    /// Stable UUID for the scenario-only "duplicate display name"
    /// printer. Shares the display name of `Prusa MK4 #1` but is a
    /// completely distinct printer with its own coverage state, so
    /// XCUI can prove that:
    ///
    ///   * two Farm cards with identical display names remain
    ///     independently addressable by their stable UUID
    ///     (`farm-card-<uuid>`);
    ///   * tapping the duplicate lands on the correct printer's
    ///     detail (not the demo original);
    ///   * badge / absence assertions scoped beneath one card cannot
    ///     be satisfied by the other.
    static let duplicateNamePrinterID = UUID(uuidString: "10000000-0001-0000-0000-0000000000AA")!

    private static func duplicateNamePrinter() -> Printer {
        DemoData.decodePrinter(from: """
        {
            "id": "\(duplicateNamePrinterID.uuidString)",
            "name": "Prusa MK4 #1",
            "notes": "F4-M UI-test duplicate of Prusa MK4 #1 with a different UUID and status",
            "manufacturerName": "Prusa Research",
            "modelName": "MK4",
            "motionType": "Cartesian",
            "backend": "Moonraker",
            "backendPort": 7125,
            "frontendPort": 80,
            "inMaintenance": false,
            "isEnabled": true,
            "isOnline": true,
            "state": "idle",
            "obicoEnabled": false
        }
        """)
    }

    private static func duplicateNameCoverage() -> PrinterFilamentCoverage {
        // Distinct STATUS from Prusa MK4 #1 (which is `.covers`) —
        // the duplicate is `.runout` without ETA, so any assertion
        // that scopes badge lookup by stable id proves the right
        // card was hit.
        let evaluatedAt = ISO8601DateFormatter().date(from: "2026-07-21T18:00:00Z")!
        return PrinterFilamentCoverage(
            printerId: duplicateNamePrinterID,
            printerName: "Prusa MK4 #1",
            status: .runout,
            toolheads: [
                ToolheadFilamentCoverage(
                    toolheadIndex: 0,
                    toolheadName: "Extruder 1",
                    material: "PLA",
                    remainingGrams: 8,
                    status: .runout
                )
            ],
            activeJobId: nil,
            activeJobName: nil,
            activeJobProgress: nil,
            earliestPredictedRunoutAt: nil,
            assignedQueuedJobCount: 0,
            evaluatedAtUtc: evaluatedAt
        )
    }

    // MARK: - Cold-offline shell (#817)

    /// Fixed last-confirmed instant for the cold-offline snapshot, so the
    /// "last updated" banner text is deterministic across launches.
    /// 2024-01-01T00:00:00Z.
    static let coldOfflineConfirmedMillis: Int64 = 1_704_067_200_000

    /// Builds the stub snapshot store seeded with a present cached fleet for
    /// the active namespace. `hydrateActive()` returns it verbatim so the
    /// `DashboardView` renders the cached read-only cards immediately.
    static func coldOfflineSnapshotStore(registry: ServerRegistry) -> any FarmSnapshotStoring {
        let serverID = registry.activeServer?.id ?? UUID()
        let namespace = FarmSnapshotNamespace(serverID: serverID, userID: DemoData.demoUserID)
        let envelope = FarmSnapshotEnvelope(
            namespace: namespace,
            printers: DemoData.printers,
            pendingReadyPrinterIDs: [],
            lastUpdatedAtMillis: coldOfflineConfirmedMillis
        )
        let session = FarmSnapshotSession(
            serverID: serverID,
            userID: DemoData.demoUserID,
            generation: 0,
            token: 1
        )
        return UITestColdOfflineSnapshotStore(
            hydration: .snapshot(envelope),
            session: session
        )
    }

    /// UI-test-only `FarmSnapshotStoring` that always hydrates a preset
    /// snapshot for the active namespace. It performs no persistence — it
    /// exists solely so XCUI can drive the #817 cold-offline shell without a
    /// real disk-backed store. Commits are accepted but discarded because the
    /// offline canonical load never succeeds in this scenario.
    private final class UITestColdOfflineSnapshotStore: FarmSnapshotStoring, @unchecked Sendable {
        private let hydration: FarmSnapshotHydration
        private let session: FarmSnapshotSession?

        init(hydration: FarmSnapshotHydration, session: FarmSnapshotSession?) {
            self.hydration = hydration
            self.session = session
        }

        func prepareStartup() async -> Bool { true }
        func activate(session: FarmSnapshotSession) async -> Bool { true }
        func deactivate(session: FarmSnapshotSession) async -> Bool { true }
        func currentSession() async -> FarmSnapshotSession? { session }
        func hydrateActive() async -> FarmSnapshotHydration { hydration }
        func commit(_ envelope: FarmSnapshotEnvelope, capturedSession: FarmSnapshotSession) async -> FarmSnapshotCommitResult { .committed }
        func purge(serverID: UUID) async -> FarmSnapshotPurgeResult { .purged }
    }
}

/// UI-test-only main-run-loop liveness signal and watchdog (#3013).
///
/// Publishes the process uptime, in milliseconds, as Darwin notification
/// state from a main-run-loop timer. `PrintFarmerUITests` reads that state
/// with `notify_get_state`, which needs no accessibility query, so a stalled
/// shell snapshot can be attributed to the app's main thread or to XCTest.
///
/// A background watchdog aborts the app once the main run loop has not
/// turned for `stallLimit` seconds. XCTest otherwise waits 60 seconds for a
/// blocked snapshot; the abort fails the test sooner and its crash report
/// retains the blocked main-thread backtrace. Started only when
/// `UITestBootstrap.isEnabled`; the notification name is duplicated in the
/// UI-test target, which cannot import the app.
@MainActor
enum UITestMainThreadHeartbeat {
    static let notificationName = "com.olyforge3d.printfarmer.uitesting.main-heartbeat"
    static let interval: TimeInterval = 0.5
    static let stallLimit: TimeInterval = 20

    private static var token: Int32 = NOTIFY_TOKEN_INVALID
    private static var timer: Timer?
    private static let beats = UITestMainThreadBeatCounter()

    static func start() {
        guard timer == nil,
              notify_register_check(notificationName, &token) == NOTIFY_STATUS_OK else { return }
        // Fires on the first main-run-loop turn, then every `interval`. Nothing
        // is published before a callback, so a published beat always means the
        // run loop turned and the watchdog is armed.
        let timer = Timer(fire: Date(), interval: interval, repeats: true) { _ in
            MainActor.assumeIsolated { beat() }
        }
        // Common modes keep beating while UIKit tracks touches or scrolling.
        RunLoop.main.add(timer, forMode: .common)
        self.timer = timer
    }

    /// Uptime in whole milliseconds in the low 42 bits and the publishing
    /// process ID above them, so the UI-test runner can tell which app
    /// instance was running while XCTest launched or terminated one.
    nonisolated static func state(forUptime uptime: TimeInterval, pid: Int32) -> UInt64 {
        let millis = UInt64(max(0, uptime) * 1000) & uptimeMask
        return (UInt64(UInt32(bitPattern: pid)) << uptimeBits) | millis
    }

    nonisolated static let uptimeBits: UInt64 = 42
    nonisolated static let uptimeMask: UInt64 = (1 << uptimeBits) - 1

    private static func beat() {
        // Arm before the first publish so the runner never admits a launch
        // that the watchdog is not yet guarding. Arming only once the run loop
        // turns means slow initial rendering is never reported as a stall.
        if beats.increment() == 1 {
            UITestMainThreadWatchdog(beats: beats, limit: stallLimit).start()
        }
        publish()
    }

    private static func publish() {
        notify_set_state(
            token,
            state(
                forUptime: ProcessInfo.processInfo.systemUptime,
                pid: ProcessInfo.processInfo.processIdentifier
            )
        )
    }
}

final class UITestMainThreadBeatCounter: @unchecked Sendable {
    private let lock = NSLock()
    private var count: UInt64 = 0

    var value: UInt64 { lock.withLock { count } }

    @discardableResult
    func increment() -> UInt64 {
        lock.withLock {
            count += 1
            return count
        }
    }
}

/// Accumulates stall time only across contiguous watchdog ticks. A gap longer
/// than `maxTickGap` means the whole process was suspended or starved, which
/// is not evidence that the main thread alone stopped, so it restarts timing.
struct UITestMainThreadStallMeter {
    let limit: TimeInterval
    let maxTickGap: TimeInterval
    private var lastBeat: UInt64?
    private var lastTick: TimeInterval?
    private(set) var stalled: TimeInterval = 0

    init(limit: TimeInterval, maxTickGap: TimeInterval = 3) {
        self.limit = limit
        self.maxTickGap = maxTickGap
    }

    mutating func tick(beat: UInt64, at now: TimeInterval) -> Bool {
        defer {
            lastBeat = beat
            lastTick = now
        }
        guard let lastBeat, let lastTick, beat == lastBeat else {
            stalled = 0
            return false
        }
        let gap = now - lastTick
        guard gap >= 0, gap <= maxTickGap else {
            stalled = 0
            return false
        }
        stalled += gap
        return stalled >= limit
    }
}

private final class UITestMainThreadWatchdog: @unchecked Sendable {
    private let beats: UITestMainThreadBeatCounter
    private let queue = DispatchQueue(label: "com.olyforge3d.printfarmer.uitesting.watchdog")
    private var meter: UITestMainThreadStallMeter
    private var timer: DispatchSourceTimer?

    init(beats: UITestMainThreadBeatCounter, limit: TimeInterval) {
        self.beats = beats
        meter = UITestMainThreadStallMeter(limit: limit)
    }

    func start() {
        let timer = DispatchSource.makeTimerSource(queue: queue)
        timer.schedule(deadline: .now() + 1, repeating: 1, leeway: .milliseconds(100))
        // The source retains this watchdog for the life of the process.
        timer.setEventHandler { [self] in check() }
        self.timer = timer
        timer.resume()
    }

    private func check() {
        guard meter.tick(beat: beats.value, at: ProcessInfo.processInfo.systemUptime) else { return }
        fatalError(
            "UI-test main run loop stalled for \(Int(meter.stalled))s; "
                + "aborting to retain the main-thread backtrace (#3013)"
        )
    }
}

#if DEBUG
private final class QueueReorderUITestJobService: DemoJobService, @unchecked Sendable {
    private let lock = NSLock()
    private var queueJobs = QueueReorderUITestJobService.makeQueueJobs()

    override func listAllJobs() async throws -> QueuedPrintJobPage {
        lock.withLock {
            QueuedPrintJobPage(jobs: queueJobs, mayHaveMore: false)
        }
    }

    override func moveQueuedJob(
        id: UUID,
        reviewedRowVersion: String,
        neighbor: QueuePositionNeighbor
    ) async throws -> MoveQueuedJobResponse {
        try lock.withLock {
            guard let source = queueJobs.firstIndex(where: { $0.job.jobUUID == id }),
                  let neighborIndex = queueJobs.firstIndex(where: {
                      switch neighbor {
                      case .before(let neighborID, _), .after(let neighborID, _):
                          $0.job.jobUUID == neighborID
                      }
                  }) else {
                throw NetworkError.conflict(nil)
            }
            let sourceJob = queueJobs[source]
            let neighborJob = queueJobs[neighborIndex]
            guard sourceJob.job.jobStatus == .queued,
                  sourceJob.job.rowVersion == reviewedRowVersion,
                  neighborJob.job.jobStatus == .queued,
                  neighborJob.job.assignedPrinterId == sourceJob.job.assignedPrinterId,
                  neighborJob.job.priority == sourceJob.job.priority else {
                throw NetworkError.conflict(nil)
            }

            let neighborRevision: String
            switch neighbor {
            case .before(_, let rowVersion), .after(_, let rowVersion):
                neighborRevision = rowVersion
            }
            guard neighborJob.job.rowVersion == neighborRevision else {
                throw NetworkError.preconditionFailed(nil)
            }

            let moved = queueJobs.remove(at: source)
            guard let updatedNeighborIndex = queueJobs.firstIndex(where: {
                $0.job.jobUUID == neighborJob.job.jobUUID
            }) else {
                throw NetworkError.conflict(nil)
            }
            let insertionIndex: Int
            switch neighbor {
            case .before:
                insertionIndex = updatedNeighborIndex
            case .after:
                insertionIndex = updatedNeighborIndex + 1
            }
            queueJobs.insert(moved, at: insertionIndex)
            return MoveQueuedJobResponse(id: id.uuidString, rowVersion: reviewedRowVersion)
        }
    }

    private static func makeQueueJobs() -> [QueuedPrintJobResponse] {
        let printerID = DemoData.prusaMK4_1_ID
        let otherPrinterID = DemoData.bambuX1C_ID
        return [
            queueRow(
                id: "32340000-0000-0000-0000-000000000001",
                name: "Queue pinned printing.gcode",
                status: .printing,
                priority: .high,
                printerID: printerID,
                rowVersion: "AQIDAA==",
                position: 0
            ),
            queueRow(
                id: "32340000-0000-0000-0000-000000000002",
                name: "Queue pinned assigned.gcode",
                status: .assigned,
                priority: .high,
                printerID: printerID,
                rowVersion: "AQIDAg==",
                position: 0
            ),
            queueRow(
                id: "32340000-0000-0000-0000-000000000003",
                name: "Queue reorder alpha.gcode",
                status: .queued,
                priority: .high,
                printerID: printerID,
                rowVersion: "AQIDAw==",
                position: 1
            ),
            queueRow(
                id: "32340000-0000-0000-0000-000000000004",
                name: "Queue reorder beta.gcode",
                status: .queued,
                priority: .high,
                printerID: printerID,
                rowVersion: "AQIDBA==",
                position: 2
            ),
            queueRow(
                id: "32340000-0000-0000-0000-000000000007",
                name: "Queue reorder gamma.gcode",
                status: .queued,
                priority: .high,
                printerID: printerID,
                rowVersion: "AQIDBw==",
                position: 3
            ),
            queueRow(
                id: "32340000-0000-0000-0000-000000000005",
                name: "Queue priority boundary.gcode",
                status: .queued,
                priority: .normal,
                printerID: printerID,
                rowVersion: "AQIDBQ==",
                position: 1
            ),
            queueRow(
                id: "32340000-0000-0000-0000-000000000006",
                name: "Queue printer boundary.gcode",
                status: .queued,
                priority: .high,
                printerID: otherPrinterID,
                rowVersion: "AQIDBg==",
                position: 1
            )
        ]
    }

    private static func queueRow(
        id: String,
        name: String,
        status: PrintJobStatus,
        priority: PrintJobPriority,
        printerID: UUID,
        rowVersion: String,
        position: Int
    ) -> QueuedPrintJobResponse {
        QueuedPrintJobResponse(
            job: QueuedJobInfo(
                id: id,
                rowVersion: rowVersion,
                name: name,
                fileName: name,
                assignedPrinterId: printerID.uuidString,
                printerName: printerID == DemoData.prusaMK4_1_ID ? "Prusa MK4 #1" : "Bambu X1C",
                printerModel: nil,
                status: status.rawValue,
                priority: priority,
                queuePosition: position,
                estimatedPrintTimeSeconds: nil,
                actualStartTimeUtc: nil,
                actualEndTimeUtc: nil,
                actualPrintTimeSeconds: nil,
                failureReason: nil,
                createdAtUtc: Date(timeIntervalSince1970: 1_728_000_000 + TimeInterval(position)),
                updatedAtUtc: nil,
                thumbnailUrl: nil,
                filamentName: "PLA",
                filamentColor: "#336699",
                copies: 1,
                completedCopies: 0,
                remainingCopies: 1
            ),
            gcodeFile: nil,
            assignedPrinter: nil,
            estimatedStartTime: nil,
            estimatedCompletionTime: nil
        )
    }
}
#endif
