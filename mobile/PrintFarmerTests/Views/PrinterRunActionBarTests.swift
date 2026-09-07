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
    /// `UIHostingController.sizeThatFits(in:)` is available on this app's
    /// iOS 17+ deployment target.
    private func contentSize<Content: View>(
        _ view: Content,
        proposedWidth: CGFloat = 390
    ) -> CGSize {
        let host = UIHostingController(rootView: view)
        return host.sizeThatFits(
            in: CGSize(width: proposedWidth, height: .greatestFiniteMagnitude)
        )
    }

    func test_render_empty_producesNoBody() {
        // With no visible actions the bar renders EmptyView. Hosting must not
        // crash and the intrinsic content size must be zero-height — a
        // collapsed empty branch is the correct behaviour, and it must be
        // measured on the SwiftUI content, not a UIWindow root. `accuracy`
        // guards against sub-point layout-engine noise on a truly-empty body.
        let bar = PrinterRunActionBar(
            presentation: .empty,
            onSelect: { _ in XCTFail("empty bar must not fire callbacks") }
        )
        let size = contentSize(bar)
        XCTAssertEqual(
            size.height, 0, accuracy: 0.5,
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
    /// action". The bar enforces this via `fullWidthActionButton()`
    /// (verified in `mobile/PrintFarmer/Views/Components/ActionButtonStyle.swift`:
    /// `.frame(minHeight: 44)` for standard and `.frame(minHeight: 50)` for
    /// prominent), and via `.frame(maxWidth: .infinity)` for width.
    ///
    /// Hicks-flag: an aggregate-height assertion on the composed bar cannot
    /// prove PER-BUTTON floors — one taller sibling could mask an undersized
    /// neighbour. Instead, measure each kind IN ISOLATION (single-descriptor
    /// bar → intrinsic content size is that kind's own row) so the assertion
    /// is genuinely per-control.

    /// Per-kind isolated HIT-TARGET height floor for the four PRIMARY kinds
    /// (Pause / Resume / Cancel / Stop). Each renders through
    /// `fullWidthActionButton()` which enforces `minHeight: 44`. A regression
    /// that dropped `.fullWidthActionButton()` on any kind would collapse
    /// this kind's isolated bar below 44pt.
    func test_render_perKind_meetsPrimaryHitTargetFloor_inIsolation() {
        let proposedWidth: CGFloat = 390
        let primaryKinds: [PrinterRunActionKind] = [.pause, .resume, .cancel, .stop]
        for kind in primaryKinds {
            let bar = PrinterRunActionBar(
                presentation: PrinterRunActionPresentation(descriptors: [
                    .init(kind: kind)
                ]),
                onSelect: { _ in }
            )
            let size = contentSize(bar, proposedWidth: proposedWidth)
            XCTAssertGreaterThanOrEqual(
                size.height, 44,
                "\(kind): isolated primary button must render at >= 44pt tall. Actual: \(size.height)pt"
            )
            // Width: `fullWidthActionButton()` applies `.frame(maxWidth: .infinity)`.
            // At a proposedWidth of 390, we expect the bar to fill the width;
            // we assert the HIG floor of >= 44pt as the tightest legitimate
            // per-control lower bound.
            XCTAssertGreaterThanOrEqual(
                size.width, 44,
                "\(kind): isolated primary button must render at >= 44pt wide. Actual: \(size.width)pt"
            )
        }
    }

    /// Per-kind isolated HIT-TARGET height floor for Emergency Stop, which
    /// is prominent and enforces the higher `minHeight: 50` floor. Same
    /// per-control isolation: no siblings, no spacing budget.
    func test_render_emergencyStop_meetsProminentHitTargetFloor_inIsolation() {
        let proposedWidth: CGFloat = 390
        let bar = PrinterRunActionBar(
            presentation: PrinterRunActionPresentation(descriptors: [
                .init(kind: .emergencyStop)
            ]),
            onSelect: { _ in }
        )
        let size = contentSize(bar, proposedWidth: proposedWidth)
        XCTAssertGreaterThanOrEqual(
            size.height, 50,
            "Emergency Stop isolated button must render at >= 50pt tall (prominent floor). Actual: \(size.height)pt"
        )
        XCTAssertGreaterThanOrEqual(
            size.width, 44,
            "Emergency Stop isolated button must render at >= 44pt wide. Actual: \(size.width)pt"
        )
    }

    /// Per-kind isolated floor at accessibility Dynamic Type. Hicks-flag:
    /// larger accessibility fonts can grow height without stacking; at
    /// isolation there is nothing to stack, but this test guards against a
    /// regression that DROPS the `minHeight` floor at large Dynamic Type
    /// (e.g., a hypothetical `.frame(height: 44)` — hard cap — replaced
    /// with `.frame(idealHeight: 44)` that lets the label shrink below 44
    /// when the label doesn't need it). Each isolated button must still be
    /// >= its HIG floor when the label text is drawn at `.accessibility5`.
    func test_render_perKind_stillMeetsHitTargetFloor_atAccessibilityDynamicType() {
        let proposedWidth: CGFloat = 390
        let primaryKinds: [PrinterRunActionKind] = [.pause, .resume, .cancel, .stop]
        for kind in primaryKinds {
            let bar = PrinterRunActionBar(
                presentation: PrinterRunActionPresentation(descriptors: [
                    .init(kind: kind)
                ]),
                onSelect: { _ in }
            )
            .environment(\.dynamicTypeSize, .accessibility5)
            let size = contentSize(bar, proposedWidth: proposedWidth)
            XCTAssertGreaterThanOrEqual(
                size.height, 44,
                "\(kind): isolated primary button must remain >= 44pt tall at .accessibility5. Actual: \(size.height)pt"
            )
        }
        let emergencyBar = PrinterRunActionBar(
            presentation: PrinterRunActionPresentation(descriptors: [
                .init(kind: .emergencyStop)
            ]),
            onSelect: { _ in }
        )
        .environment(\.dynamicTypeSize, .accessibility5)
        let emergencySize = contentSize(emergencyBar, proposedWidth: proposedWidth)
        XCTAssertGreaterThanOrEqual(
            emergencySize.height, 50,
            "Emergency Stop isolated button must remain >= 50pt tall at .accessibility5. Actual: \(emergencySize.height)pt"
        )
    }

    /// Stacking proof at accessibility Dynamic Type. Comparative to
    /// address Hicks-flag "Larger accessibility fonts can also increase
    /// height without stacking": a fixture with THREE primaries + Emergency
    /// must grow by at least two additional per-primary floors versus a
    /// fixture with ONE primary + Emergency, measured at the same
    /// `.accessibility5` Dynamic Type. Same font growth on both sides, so
    /// the delta is pure stacking — a not-stacked layout would produce
    /// approximately the same height for both.
    ///
    /// Uses a very wide proposal (1200pt) to explicitly rule out label
    /// wrapping as an alternative explanation for the height delta. At
    /// 1200pt each of 3 horizontal primaries gets >= 380pt of width — the
    /// widest primary label ("Cancel") at `.accessibility5` needs far less
    /// than that to fit on a single line. So a still-horizontal (regressed)
    /// layout would yield delta ~= 0 (row height is dominated by the
    /// tallest button, and no button needs to wrap), while a correctly
    /// stacked layout yields delta >= 2 * 44 from the two additional 44pt
    /// rows. Layout branch selection is driven by `isAccessibilitySize`
    /// from the environment, not by container width, so the wider
    /// proposal does not itself change which branch runs.
    func test_render_atAccessibilityDynamicType_stacksPrimaries_notSingleRow() {
        let onePrimary = PrinterRunActionPresentation(descriptors: [
            .init(kind: .pause),
            .init(kind: .emergencyStop),
        ])
        let threePrimary = PrinterRunActionPresentation(descriptors: [
            .init(kind: .pause),
            .init(kind: .cancel),
            .init(kind: .stop),
            .init(kind: .emergencyStop),
        ])

        let oneBar = PrinterRunActionBar(presentation: onePrimary) { _ in }
            .environment(\.dynamicTypeSize, .accessibility5)
        let threeBar = PrinterRunActionBar(presentation: threePrimary) { _ in }
            .environment(\.dynamicTypeSize, .accessibility5)

        let wideProposal: CGFloat = 1200
        let oneSize = contentSize(oneBar, proposedWidth: wideProposal)
        let threeSize = contentSize(threeBar, proposedWidth: wideProposal)

        // Stacked: 3 primary rows each >= 44pt vs 1 primary row of the same
        // font size. Growth from 1 → 3 primaries must be at least 2 * 44
        // (the two extra rows). At the wide proposal above, a not-stacked
        // (still horizontal) layout would grow by ~0pt because the row
        // height is dominated by the tallest button, all three primaries
        // share the same font, AND at 1200pt no primary label needs to
        // wrap — so the wrapping confound Hicks raised is eliminated.
        let extraPrimaryFloor: CGFloat = 2 * 44
        XCTAssertGreaterThanOrEqual(
            threeSize.height - oneSize.height, extraPrimaryFloor,
            "At .accessibility5 with wide (\(wideProposal)pt) proposal, going from 1 → 3 primaries must add at least 2 * 44pt (two stacked rows). One-primary height: \(oneSize.height)pt, three-primary height: \(threeSize.height)pt, delta: \(threeSize.height - oneSize.height)pt"
        )
    }

    /// Comparative growth check: the accessibility branch (stacked) must
    /// produce a taller bar than the horizontal branch, proving the layout
    /// actually flipped on `isAccessibilitySize`. Safe against beta-OS
    /// drift because both measurements are taken on the same host under
    /// the same runtime.
    func test_render_atAccessibilityDynamicType_producesTallerBarThanHorizontal() {
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

        XCTAssertGreaterThan(
            accessibilitySize.height, standardSize.height,
            "Accessibility Dynamic Type must produce a taller bar than the standard horizontal row. Standard: \(standardSize.height)pt, Accessibility: \(accessibilitySize.height)pt"
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
