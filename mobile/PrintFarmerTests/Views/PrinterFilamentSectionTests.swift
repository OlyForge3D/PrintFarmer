import XCTest
import SwiftUI
@testable import PrintFarmer

@MainActor
final class PrinterFilamentSectionTests: XCTestCase {
    private func presentation(
        printer: Printer,
        stale: Bool = false,
        supported: Set<PrinterFilamentAction.Kind> = Set(PrinterFilamentAction.Kind.allCases)
    ) throws -> PrinterFilamentPresentation {
        PrinterFilamentPresentation(
            printer: printer,
            toolheads: [Toolhead(id: UUID(), name: "A long physical toolhead name", index: 0, isPrimary: true)],
            spool: PrinterSpoolInfo(hasActiveSpool: true, material: "PLA", remainingWeightG: nil),
            coverage: nil, coverageState: .unavailable, isStale: stale,
            supportedActions: supported
        )
    }

    func testEveryEnabledKindEmitsExactPrinterIntentOnce() throws {
        let printer = try TestData.decodePrinter()
        let actions = PrinterFilamentAction.Kind.allCases.map {
            PrinterFilamentAction(kind: $0, target: .printer(printer.id), disabledReason: nil)
        }
        var received: [PrinterFilamentAction] = []
        let view = PrinterFilamentSection(presentation: try presentation(printer: printer), actions: actions) {
            received.append($0)
        }
        for action in actions { view.select(action) }
        XCTAssertEqual(received, actions)
        XCTAssertEqual(PrinterFilamentAction.Kind.clearAssignment.title, "Clear spool assignment")
    }

    func testDisabledUnsupportedMismatchedSlotAndUnlistedActionsEmitNothing() throws {
        let printer = try TestData.decodePrinter()
        let actions: [PrinterFilamentAction] = [
            .init(kind: .set, target: .printer(printer.id), disabledReason: "Offline"),
            .init(kind: .change, target: .printer(UUID()), disabledReason: nil),
            .init(kind: .clearAssignment, target: .slot(printerID: printer.id, toolheadID: UUID()), disabledReason: nil),
            .init(kind: .scanNFC, target: .printer(printer.id), disabledReason: nil)
        ]
        var received: [PrinterFilamentAction] = []
        let model = try presentation(printer: printer, supported: [.set, .change, .clearAssignment])
        let view = PrinterFilamentSection(presentation: model, actions: actions) { received.append($0) }
        actions.forEach { view.select($0) }
        view.select(.init(kind: .guidedSwap, target: .printer(printer.id), disabledReason: nil))
        XCTAssertTrue(received.isEmpty)
        XCTAssertTrue(actions.allSatisfy { model.disabledReason(for: $0) != nil })
    }

    func testStaleViewDoesNotEmitMutation() throws {
        let printer = try TestData.decodePrinter()
        let action = PrinterFilamentAction(kind: .set, target: .printer(printer.id), disabledReason: nil)
        var calls = 0
        let view = PrinterFilamentSection(presentation: try presentation(printer: printer, stale: true), actions: [action]) { _ in calls += 1 }
        view.select(action)
        XCTAssertEqual(calls, 0)
    }

    func testHostedLargeTextGrowsVerticallyWithoutClippingToFixedHeight() throws {
        let printer = try TestData.decodePrinter()
        let actions = PrinterFilamentAction.Kind.allCases.map {
            PrinterFilamentAction(kind: $0, target: .printer(printer.id), disabledReason: nil)
        }
        let view = PrinterFilamentSection(presentation: try presentation(printer: printer), actions: actions) { _ in
            XCTFail("Rendering must not dispatch actions")
        }
        let normal = UIHostingController(rootView: view.environment(\.dynamicTypeSize, .large))
        let large = UIHostingController(rootView: view.environment(\.dynamicTypeSize, .accessibility5))
        let proposal = CGSize(width: 320, height: 10_000)
        let normalSize = normal.sizeThatFits(in: proposal)
        let largeSize = large.sizeThatFits(in: proposal)
        XCTAssertGreaterThan(normalSize.height, 88)
        XCTAssertGreaterThan(largeSize.height, normalSize.height)
        XCTAssertLessThanOrEqual(largeSize.width, proposal.width)
    }

    func testDefaultKeepsOnlyCompactAssignmentActionAndDisclosesNFCAndClearing() throws {
        let printer = try TestData.decodePrinter()
        let actions: [PrinterFilamentAction] = [
            .init(kind: .change, target: .printer(printer.id), disabledReason: nil),
            .init(kind: .clearAssignment, target: .printer(printer.id), disabledReason: nil),
            .init(kind: .scanNFC, target: .printer(printer.id), disabledReason: "NFC unavailable")
        ]
        var received: [PrinterFilamentAction] = []
        let view = PrinterFilamentSection(presentation: try presentation(printer: printer), actions: actions) {
            received.append($0)
        }
        XCTAssertFalse(view.detailsExpanded)
        XCTAssertEqual(view.primaryAction, actions[0])
        XCTAssertEqual(view.detailActions, Array(actions.dropFirst()))
        view.select(actions[1])
        view.select(actions[2])
        XCTAssertEqual(received, [actions[1]])
    }

    func testStaleAssignmentActionsRemainDisclosedButNeverEnabled() throws {
        let printer = try TestData.decodePrinter()
        let action = PrinterFilamentAction(kind: .set, target: .printer(printer.id), disabledReason: nil)
        let view = PrinterFilamentSection(presentation: try presentation(printer: printer, stale: true), actions: [action]) { _ in
            XCTFail("Stale data must not dispatch")
        }
        XCTAssertNil(view.primaryAction)
        XCTAssertEqual(view.detailActions, [action])
        view.select(action)
    }

    func testExpandedDetailsAreProgressivelyDisclosedAtPhoneAndTabletWidths() throws {
        let printer = try TestData.decodePrinter()
        let model = try presentation(printer: printer)
        let actions = PrinterFilamentAction.Kind.allCases.map {
            PrinterFilamentAction(kind: $0, target: .printer(printer.id), disabledReason: nil)
        }
        let collapsed = PrinterFilamentSection(presentation: model, actions: actions) { _ in }
        let expanded = PrinterFilamentSection(
            presentation: model, actions: actions, onAction: { _ in }, detailsExpanded: true
        )
        for width: CGFloat in [320, 700] {
            for size in [DynamicTypeSize.large, .accessibility5] {
                let proposal = CGSize(width: width, height: 10_000)
                let small = UIHostingController(rootView: collapsed.environment(\.dynamicTypeSize, size))
                    .sizeThatFits(in: proposal)
                let full = UIHostingController(rootView: expanded.environment(\.dynamicTypeSize, size))
                    .sizeThatFits(in: proposal)
                XCTAssertGreaterThan(full.height, small.height)
                XCTAssertLessThanOrEqual(full.width, width)
            }
        }
    }
}
