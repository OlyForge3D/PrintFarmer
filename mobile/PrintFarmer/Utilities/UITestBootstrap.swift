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

    /// Additional launch argument that selects the *unauthenticated*
    /// login-flow mode. When present alongside `launchArgument`, the
    /// bootstrap seeds the same ephemeral registry + demo services but
    /// leaves the session signed out so `RootView` renders `LoginView`
    /// (issue #706 F1 review defect D). Absent it, the bootstrap seeds an
    /// authenticated operator shell as before.
    static let unauthenticatedLaunchArgument = "--uitesting-unauthenticated"

    /// Additional launch argument that forces the shared operator feature
    /// gate (#725) to resolve with `attentionEnabled == false` under the
    /// authenticated bootstrap. When present alongside `launchArgument`,
    /// the bootstrap wires the demo services but overrides
    /// `capabilitiesService` with a `StubSystemCapabilitiesService` seeded
    /// to a disabled snapshot, so `AttentionView` renders its
    /// feature-disabled fallback and exposes the legacy Notifications /
    /// Dashboard / Maintenance sheets. Required by issue #727 UI tests
    /// which need deterministic access to the fallback Notifications sheet.
    static let attentionDisabledLaunchArgument = "--uitesting-attention-disabled"
    static let attentionActionsLaunchArgument = "--uitesting-attention-actions"
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

    /// Deterministic launch modes selectable from the UI-test harness.
    enum Mode: Equatable {
        /// Pre-authenticated demo operator shell (default).
        case authenticated
        /// Signed-out state so login-flow tests see `LoginView`.
        case unauthenticated
        /// Pre-authenticated demo operator shell with the operator
        /// feature gate forced to disabled — `AttentionView` renders the
        /// fallback so the legacy Notifications sheet is reachable.
        case authenticatedAttentionDisabled
        /// F2-U2 feed with failure media, stable-ID destinations, and
        /// server-backed failure + maintenance actions.
        case authenticatedAttentionActions
        #if DEBUG
        case authenticatedShiftTaskMutationError
        case authenticatedShiftTaskInitialLoadFailure
        #endif
        /// Authenticated operator shell with a deterministic fleet
        /// coverage snapshot injected (F4-M #778 UI tests).
        case authenticatedFilamentCoverageScenario
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

    /// The launch mode encoded in the current process arguments.
    static var mode: Mode {
        mode(in: CommandLine.arguments)
    }

    /// Pure overload: resolves the launch mode from an explicit argument
    /// list so unit tests can exercise it without `CommandLine`.
    static func mode(in arguments: [String]) -> Mode {
        if arguments.contains(attentionActionsLaunchArgument) {
            return .authenticatedAttentionActions
        }
        if arguments.contains(filamentCoverageScenarioLaunchArgument) {
            return .authenticatedFilamentCoverageScenario
        }
        if arguments.contains(attentionDisabledLaunchArgument) {
            return .authenticatedAttentionDisabled
        }
        #if DEBUG
        if arguments.contains(shiftTaskMutationErrorLaunchArgument) {
            return .authenticatedShiftTaskMutationError
        }
        if arguments.contains(shiftTaskInitialLoadFailureLaunchArgument) {
            return .authenticatedShiftTaskInitialLoadFailure
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
    @discardableResult
    static func makeBundle(mode: Mode = .authenticated, defaults: UserDefaults? = nil) -> Environment {
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
        // In the unauthenticated mode they also keep `LoginView`'s
        // sign-in path off the network (DemoAuthService).
        let services = ServiceContainer.demo()

        // #727: `.authenticatedAttentionDisabled` swaps the demo
        // capabilities service for one whose resolved snapshot has
        // `attentionEnabled == false`. `AttentionView.task` then reads
        // that snapshot and renders `disabledFallback`, which is the
        // only surface that exposes the legacy Notifications sheet.
        if mode == .authenticatedAttentionDisabled {
            var disabled = ResolvedSystemCapabilities.defaults
            disabled.attentionEnabled = false
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
        #endif

        let auth = AuthViewModel(services: services)
        switch mode {
        case .authenticated, .authenticatedAttentionDisabled, .authenticatedAttentionActions:
            auth.markAuthenticatedForUITesting(user: DemoData.demoUser)
        case .unauthenticated:
            break
        #if DEBUG
        case .authenticatedShiftTaskMutationError:
            auth.markAuthenticatedForUITesting(user: DemoData.demoUser)
        case .authenticatedShiftTaskInitialLoadFailure:
            auth.markAuthenticatedForUITesting(user: DemoData.demoUser)
        #endif
        case .authenticatedFilamentCoverageScenario:
            auth.markAuthenticatedForUITesting(user: DemoData.demoUser)
        }

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
}
