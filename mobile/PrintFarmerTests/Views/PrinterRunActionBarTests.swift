import XCTest
import SwiftUI
@testable import PrintFarmer

/// View-level tests for `PrinterRunActionBar` (issue #2520).
///
/// These validate:
///   * The bar materializes without crashing for the printing / paused / idle /
///     limited-permission fixtures.
///   * The callback contract: each enabled activation fires exactly one
///     `onSelect(kind)`; hidden, disabled, pending, or absent descriptors
///     produce zero. This is checked via the presentation's callback gate
///     (which the bar delegates to), so we don't need XCUITest to prove it.
///   * Emergency Stop's independent gate isn't affected by an unrelated
///     pending action.
///   * The accessibility strings the bar actually applies (label, hint, id).
@MainActor
final class PrinterRunActionBarTests: XCTestCase {

    // MARK: - Fixtures

    private var printingPresentation: PrinterRunActionPresentation {
        PrinterRunActionPresentation(descriptors: [
            .init(kind: .pause),
            .init(kind: .cancel),
            .init(kind: .stop),
            .init(kind: .emergencyStop),
        ])
    }

    private var pausedPresentation: PrinterRunActionPresentation {
        PrinterRunActionPresentation(descriptors: [
            .init(kind: .resume),
            .init(kind: .cancel),
            .init(kind: .stop),
            .init(kind: .emergencyStop),
        ])
    }

    private var offlinePresentation: PrinterRunActionPresentation {
        PrinterRunActionPresentation(descriptors: [
            .init(kind: .pause, isEnabled: false, unavailableReason: "printer offline"),
            .init(kind: .cancel, isEnabled: false, unavailableReason: "printer offline"),
            .init(kind: .emergencyStop, isEnabled: false, unavailableReason: "printer offline"),
        ])
    }

    // MARK: - Smoke render

    /// Helper: host a bar in a real UIWindow so `layoutIfNeeded()` produces
    /// non-zero bounds we can assert against. Hicks-flag: bare
    /// `UIHostingController` without a window yields zero-sized frames that
    /// can silently regress into "render nothing" without a test noticing.
    private func hostInWindow<Content: View>(_ view: Content) -> UIHostingController<Content> {
        let host = UIHostingController(rootView: view)
        let window = UIWindow(frame: CGRect(x: 0, y: 0, width: 390, height: 844))
        window.rootViewController = host
        window.makeKeyAndVisible()
        host.view.setNeedsLayout()
        host.view.layoutIfNeeded()
        return host
    }

    func test_render_empty_producesNoBody() {
        // With no visible actions the bar renders EmptyView. Hosting must not
        // crash and the body must not add extra chrome for a phantom row.
        let bar = PrinterRunActionBar(
            presentation: .empty,
            onSelect: { _ in XCTFail("empty bar must not fire callbacks") }
        )
        let host = UIHostingController(rootView: bar)
        host.view.layoutIfNeeded()
        XCTAssertNotNil(host.view)
    }

    func test_render_printingFixture_materializes_withNonZeroHeight() {
        let bar = PrinterRunActionBar(
            presentation: printingPresentation,
            onSelect: { _ in }
        )
        let host = hostInWindow(bar)
        XCTAssertNotNil(host.view)
        XCTAssertGreaterThan(
            host.view.bounds.height, 0,
            "Bar with visible descriptors must produce a non-zero-height view"
        )
    }

    func test_render_pausedFixture_materializes_withNonZeroHeight() {
        let bar = PrinterRunActionBar(
            presentation: pausedPresentation,
            onSelect: { _ in }
        )
        let host = hostInWindow(bar)
        XCTAssertNotNil(host.view)
        XCTAssertGreaterThan(host.view.bounds.height, 0)
    }

    func test_render_offlineFixture_materializes_withDisabledActions() {
        let bar = PrinterRunActionBar(
            presentation: offlinePresentation,
            onSelect: { _ in
                XCTFail("all-disabled bar must not fire callbacks")
            }
        )
        let host = hostInWindow(bar)
        XCTAssertNotNil(host.view)
        XCTAssertGreaterThan(host.view.bounds.height, 0)
    }

    func test_render_emergencyStopOnly_materializes() {
        // Idle host that keeps Emergency Stop reachable.
        let bar = PrinterRunActionBar(
            presentation: PrinterRunActionPresentation(descriptors: [
                .init(kind: .emergencyStop),
            ]),
            onSelect: { _ in }
        )
        let host = hostInWindow(bar)
        XCTAssertNotNil(host.view)
        XCTAssertGreaterThan(host.view.bounds.height, 0)
    }

    /// Hicks-flag: the bar's layout branch swaps to a stacked configuration
    /// once `dynamicTypeSize.isAccessibilitySize` is true. Host the bar at
    /// the largest accessibility size and confirm it still materializes
    /// with non-zero bounds — a regression that made the stacked branch
    /// crash or collapse would fail this test without needing pixel
    /// snapshots (which drift under beta-OS SDKs).
    func test_render_atAccessibilityDynamicType_stillMaterializes() {
        let bar = PrinterRunActionBar(
            presentation: printingPresentation,
            onSelect: { _ in }
        )
        .environment(\.dynamicTypeSize, .accessibility5)
        let host = hostInWindow(bar)
        XCTAssertNotNil(host.view)
        XCTAssertGreaterThan(
            host.view.bounds.height, 0,
            "Bar must remain visible at the largest accessibility Dynamic Type size"
        )
    }

    // MARK: - Callback contract (via presentation gate — matches the bar's own guard)

    func test_callbackGate_firesForEnabledVisibleNonPendingKind() {
        XCTAssertTrue(printingPresentation.shouldFireCallback(for: .pause))
        XCTAssertTrue(printingPresentation.shouldFireCallback(for: .cancel))
        XCTAssertTrue(printingPresentation.shouldFireCallback(for: .stop))
        XCTAssertTrue(printingPresentation.shouldFireCallback(for: .emergencyStop))
    }

    func test_callbackGate_doesNotFireForAbsentKind() {
        // Printing fixture has no Resume descriptor.
        XCTAssertFalse(printingPresentation.shouldFireCallback(for: .resume))
    }

    func test_callbackGate_doesNotFireForHiddenKind() {
        let presentation = PrinterRunActionPresentation(descriptors: [
            .init(kind: .stop, isVisible: false),
        ])
        XCTAssertFalse(presentation.shouldFireCallback(for: .stop))
    }

    func test_callbackGate_doesNotFireForDisabledKind() {
        let presentation = PrinterRunActionPresentation(descriptors: [
            .init(kind: .stop, isEnabled: false,
                  unavailableReason: "no active job"),
        ])
        XCTAssertFalse(presentation.shouldFireCallback(for: .stop))
    }

    func test_callbackGate_doesNotFireForPendingKind() {
        let presentation = PrinterRunActionPresentation(descriptors: [
            .init(kind: .cancel, isPending: true),
        ])
        XCTAssertFalse(presentation.shouldFireCallback(for: .cancel))
    }

    func test_callbackGate_emergencyStopIsIndependent_ofAnUnrelatedPending() {
        let presentation = PrinterRunActionPresentation(descriptors: [
            .init(kind: .cancel, isPending: true),
            .init(kind: .emergencyStop),
        ])
        XCTAssertFalse(presentation.shouldFireCallback(for: .cancel))
        XCTAssertTrue(presentation.shouldFireCallback(for: .emergencyStop),
                      "Bar must never blanket-disable Emergency Stop due to other pending state")
    }

    // MARK: - Bar routes onSelect exactly once per enabled activation

    /// Drives the bar's own `fire(_:)` — exposed as `internal` for this
    /// exact reason — through the same `presentation.shouldFireCallback →
    /// onSelect` path production button activation takes. Hicks flagged
    /// the previous version of this test for manually appending kinds
    /// instead of exercising the seam; this version invokes `bar.fire(_:)`
    /// and asserts the recorded onSelect events.
    func test_bar_fire_routesEnabledKind_toOnSelect_exactlyOnce() {
        var recorded: [PrinterRunActionKind] = []
        let bar = PrinterRunActionBar(presentation: printingPresentation) { kind in
            recorded.append(kind)
        }

        bar.fire(.pause)
        XCTAssertEqual(recorded, [.pause],
                       "Enabled Pause activation must fire onSelect once")

        bar.fire(.emergencyStop)
        XCTAssertEqual(recorded, [.pause, .emergencyStop],
                       "Enabled Emergency Stop activation must fire onSelect once")
    }

    /// A pending descriptor is a real activation — SwiftUI still delivers
    /// the tap — but the bar's gate must swallow it. This test drives
    /// `fire(_:)` directly and confirms nothing lands in `recorded`.
    func test_bar_fire_swallowsPendingKind_noOnSelect() {
        var recorded: [PrinterRunActionKind] = []
        let presentation = PrinterRunActionPresentation(descriptors: [
            .init(kind: .pause),
            .init(kind: .cancel, isPending: true),
            .init(kind: .emergencyStop),
        ])
        let bar = PrinterRunActionBar(presentation: presentation) { kind in
            recorded.append(kind)
        }

        bar.fire(.cancel)
        XCTAssertTrue(recorded.isEmpty,
                      "Pending Cancel must not fire onSelect even when the bar receives the tap")

        // And an unrelated enabled activation still works.
        bar.fire(.pause)
        XCTAssertEqual(recorded, [.pause])
    }

    /// A disabled descriptor with an unavailable reason must also be
    /// swallowed by the gate.
    func test_bar_fire_swallowsDisabledKind_noOnSelect() {
        var recorded: [PrinterRunActionKind] = []
        let presentation = PrinterRunActionPresentation(descriptors: [
            .init(kind: .stop, isEnabled: false, unavailableReason: "no active job"),
            .init(kind: .emergencyStop),
        ])
        let bar = PrinterRunActionBar(presentation: presentation) { kind in
            recorded.append(kind)
        }

        bar.fire(.stop)
        XCTAssertTrue(recorded.isEmpty,
                      "Disabled Stop must not fire onSelect")

        // Emergency Stop remains independent and reachable.
        bar.fire(.emergencyStop)
        XCTAssertEqual(recorded, [.emergencyStop])
    }

    /// Absent (never-supplied) and hidden descriptors must both be
    /// swallowed by the gate — a redraw or gesture side-effect must not
    /// leak a spurious event for a kind the host chose not to expose.
    func test_bar_fire_swallowsAbsentAndHiddenKinds_noOnSelect() {
        var recorded: [PrinterRunActionKind] = []
        let presentation = PrinterRunActionPresentation(descriptors: [
            .init(kind: .pause),
            .init(kind: .stop, isVisible: false),
        ])
        let bar = PrinterRunActionBar(presentation: presentation) { kind in
            recorded.append(kind)
        }

        // .resume was never supplied.
        bar.fire(.resume)
        // .stop was supplied but hidden.
        bar.fire(.stop)
        XCTAssertTrue(recorded.isEmpty,
                      "Absent (.resume) and hidden (.stop) kinds must not fire onSelect")

        // Sanity: the visible enabled kind still routes.
        bar.fire(.pause)
        XCTAssertEqual(recorded, [.pause])
    }

    /// Emergency Stop must remain reachable even when an unrelated kind is
    /// pending. This locks in the safety invariant: the bar never
    /// blanket-disables Emergency Stop for cross-action pending state.
    func test_bar_fire_emergencyStopStillRoutes_whenUnrelatedKindPending() {
        var recorded: [PrinterRunActionKind] = []
        let presentation = PrinterRunActionPresentation(descriptors: [
            .init(kind: .cancel, isPending: true),
            .init(kind: .emergencyStop),
        ])
        let bar = PrinterRunActionBar(presentation: presentation) { kind in
            recorded.append(kind)
        }

        bar.fire(.cancel)          // pending, swallowed
        bar.fire(.emergencyStop)   // independent, must fire
        XCTAssertEqual(recorded, [.emergencyStop])
    }

    // MARK: - Accessibility labels the bar surfaces

    func test_bar_usesDistinctAccessibilityLabelForEmergencyStop() {
        // Same labels the bar applies via `.accessibilityLabel(...)`.
        XCTAssertEqual(
            PrinterRunActionLabels.accessibilityLabel(for: .emergencyStop),
            "Emergency stop printer"
        )
        XCTAssertNotEqual(
            PrinterRunActionLabels.accessibilityLabel(for: .stop),
            PrinterRunActionLabels.accessibilityLabel(for: .emergencyStop),
            "Emergency Stop must not share Stop's accessibility label"
        )
    }

    func test_bar_emergencyStopHint_mentionsPhysicalSafety() {
        // Enforced separately at the label layer; this test locks the string
        // in as part of the bar's public accessibility contract.
        let hint = PrinterRunActionLabels.accessibilityHint(for: .emergencyStop)
        XCTAssertTrue(hint.lowercased().contains("physical safety"))
    }

    func test_bar_preservesEmergencyStopTitle_notReducedToUnlabeledIcon() {
        XCTAssertEqual(
            PrinterRunActionLabels.title(for: .emergencyStop),
            "Emergency Stop"
        )
    }
}
