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
        XCTAssertGreaterThan(normalSize.height, CGFloat(actions.count * 44))
        XCTAssertGreaterThan(largeSize.height, normalSize.height)
        XCTAssertLessThanOrEqual(largeSize.width, proposal.width)
    }
}
