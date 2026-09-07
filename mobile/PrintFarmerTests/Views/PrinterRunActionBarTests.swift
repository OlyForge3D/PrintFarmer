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

    /// Returns the SwiftUI-hosted content's intrinsic size at a proposed
    /// width. This is the size the bar itself wants — not a
    /// hosting-controller root view frame — so a collapsed or empty branch
    /// yields `CGSize.zero` and every hit-target assertion below measures
    /// actual content rather than the window I chose to host it in.
    /// Uses `UIHostingController.sizeThatFits(in:)` which was added in
    /// iOS 16 and is available on this app's iOS 17+ deployment target.
    private func contentSize<Content: View>(
        _ view: Content,
        proposedWidth: CGFloat = 390
    ) -> CGSize {
        let host = UIHostingController(rootView: view)
        return host.sizeThatFits(
            in: CGSize(width: proposedWidth, height: .infinity)
        )
    }

    func test_render_empty_producesNoBody() {
        // With no visible actions the bar renders EmptyView. Hosting must not
        // crash and the intrinsic content size must be zero-height — a
        // collapsed empty branch is the correct behaviour, and it must be
        // measured on the SwiftUI content, not a UIWindow root.
        let bar = PrinterRunActionBar(
            presentation: .empty,
            onSelect: { _ in XCTFail("empty bar must not fire callbacks") }
        )
        let size = contentSize(bar)
        XCTAssertEqual(
            size.height, 0,
            "Empty presentation must render as EmptyView (zero-height content)"
        )
    }

    func test_render_printingFixture_materializes_withNonZeroContentHeight() {
        let bar = PrinterRunActionBar(
            presentation: printingPresentation,
            onSelect: { _ in }
        )
        let size = contentSize(bar)
        XCTAssertGreaterThan(
            size.height, 0,
            "Printing fixture must produce non-zero intrinsic content height"
        )
    }

    func test_render_pausedFixture_materializes_withNonZeroContentHeight() {
        let bar = PrinterRunActionBar(
            presentation: pausedPresentation,
            onSelect: { _ in }
        )
        let size = contentSize(bar)
        XCTAssertGreaterThan(size.height, 0)
    }

    func test_render_offlineFixture_materializes_withDisabledActions() {
        let bar = PrinterRunActionBar(
            presentation: offlinePresentation,
            onSelect: { _ in
                XCTFail("all-disabled bar must not fire callbacks")
            }
        )
        let size = contentSize(bar)
        XCTAssertGreaterThan(size.height, 0)
    }

    func test_render_emergencyStopOnly_materializes() {
        let bar = PrinterRunActionBar(
            presentation: PrinterRunActionPresentation(descriptors: [
                .init(kind: .emergencyStop),
            ]),
            onSelect: { _ in }
        )
        let size = contentSize(bar)
        XCTAssertGreaterThan(size.height, 0)
    }

    // MARK: - Hit target proof (issue contract: 44x44 pt minimum, per Apple HIG)

    /// Contract from issue #2520: "a minimum 44x44pt hit target for every
    /// action". The bar enforces this via `fullWidthActionButton()` which
    /// applies `.frame(minHeight: 44)` for standard buttons and
    /// `.frame(minHeight: 50)` for the prominent Emergency Stop. This test
    /// proves those floors survive by measuring the bar's intrinsic
    /// content size — a regression that dropped `.fullWidthActionButton()`
    /// or shrunk a row would drop the total below the sum of the floors.
    ///
    /// The printing fixture renders Pause/Cancel/Stop in a horizontal row
    /// (>= 44pt) plus 10pt spacing plus Emergency Stop as its own row
    /// (>= 50pt), so the intrinsic height must be at least 44 + 10 + 50
    /// = 104pt. We assert the sum of the two per-row floors as the tightest
    /// legitimate lower bound.
    func test_render_printingFixture_meetsMinimumHitTargetHeights_atStandardDynamicType() {
        let bar = PrinterRunActionBar(
            presentation: printingPresentation,
            onSelect: { _ in }
        )
        let size = contentSize(bar)
        // 44pt (primary row floor) + 50pt (Emergency Stop prominent floor).
        // No spacing budget is asserted — that would over-constrain against
        // future layout tweaks.
        let primaryFloor: CGFloat = 44
        let emergencyFloor: CGFloat = 50
        XCTAssertGreaterThanOrEqual(
            size.height, primaryFloor + emergencyFloor,
            "Bar height must meet HIG floors: at least 44pt primary row + 50pt Emergency Stop row. Actual: \(size.height)pt"
        )
    }

    /// At the largest accessibility Dynamic Type size the bar switches to
    /// a stacked layout where each of the three primary rows keeps its
    /// own 44pt floor plus the Emergency Stop 50pt floor. Hicks-flag: a
    /// hidden regression that let the accessibility branch collapse or
    /// wrap into a single row would not be caught by the standard-size
    /// test alone — this comparison catches it because the stacked height
    /// must exceed the horizontal row height by AT LEAST 2 extra primary
    /// floors (3 rows instead of 1). Comparing stacked-vs-horizontal is
    /// dimension-neutral and safe against beta-OS layout drift.
    func test_render_atAccessibilityDynamicType_growsToPreservePerRowHitTargets() {
        let standardBar = PrinterRunActionBar(
            presentation: printingPresentation,
            onSelect: { _ in }
        )
        .environment(\.dynamicTypeSize, .large)

        let accessibilityBar = PrinterRunActionBar(
            presentation: printingPresentation,
            onSelect: { _ in }
        )
        .environment(\.dynamicTypeSize, .accessibility5)

        let standardSize = contentSize(standardBar)
        let accessibilitySize = contentSize(accessibilityBar)

        // Stacked layout MUST grow versus horizontal — 3 primary rows
        // instead of 1. Beta-OS metric drift is neutralised because both
        // sides are measured on the same host under the same runtime.
        XCTAssertGreaterThan(
            accessibilitySize.height, standardSize.height,
            "Accessibility Dynamic Type must produce a taller stacked layout than the standard horizontal row. Standard: \(standardSize.height)pt, Accessibility: \(accessibilitySize.height)pt"
        )

        // And the taller stacked layout must still meet the aggregate
        // per-row HIG floor: 3 primary rows (44pt each) + Emergency Stop
        // (50pt) = 182pt of pure per-row hit-target budget.
        let primaryFloor: CGFloat = 44
        let emergencyFloor: CGFloat = 50
        let stackedFloor = 3 * primaryFloor + emergencyFloor
        XCTAssertGreaterThanOrEqual(
            accessibilitySize.height, stackedFloor,
            "Stacked accessibility layout must meet aggregate HIG floor for 3 primary rows + Emergency Stop. Expected >= \(stackedFloor)pt, actual \(accessibilitySize.height)pt"
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
