import XCTest
import SwiftUI
import Vision
@testable import PrintFarmer

@MainActor
final class PrinterFilamentSectionTests: XCTestCase {
    func testFilamentCardKeepsWholeWordsAndControlsAtEveryAccessibilitySize() async throws {
        let sizes: [DynamicTypeSize] = [
            .accessibility1, .accessibility2, .accessibility3, .accessibility4, .accessibility5
        ]
        for width: CGFloat in [320, 700] {
            for size in sizes {
                let image = try await filamentCardImage(width: width, size: size, spoolName: "Prusament Galaxy Black")
                let lines = try recognizedText(in: image).map(\.text)
                let words = lines.joined(separator: " ").split(whereSeparator: \.isWhitespace).map(String.init)
                for word in ["Prusament", "Galaxy", "Black", "remaining", "job", "demand", "Swap", "Scan", "spool"] {
                    XCTAssertTrue(words.contains(word), "\(width)pt \(size): missing whole word \(word): \(lines)")
                }
                XCTAssertEqual(words.filter { $0 == "Unknown" }.count, 2, "\(width)pt \(size): \(lines)")
                if !lines.contains(where: { $0.contains("remaining") && $0.contains("demand") }) {
                    XCTAssertFalse(lines.contains { $0.contains("·") }, "Stacked quantities must omit the separator: \(lines)")
                }
            }
        }
    }

    func testSpoolNameScalesThroughEveryDynamicTypeSize() async throws {
        let sizes: [DynamicTypeSize] = [
            .xSmall, .small, .medium, .large, .xLarge, .xxLarge, .xxxLarge,
            .accessibility1, .accessibility2, .accessibility3, .accessibility4, .accessibility5
        ]
        for width: CGFloat in [320, 700] {
            var previousHeight: CGFloat = 0
            for size in sizes {
                let image = try await filamentCardImage(width: width, size: size, spoolName: "Prusament")
                let lines = try recognizedText(in: image)
                let name = try XCTUnwrap(lines.first { $0.text == "Prusament" }, "\(width)pt \(size): \(lines)")
                XCTAssertGreaterThan(
                    name.height, previousHeight,
                    "The rendered spool name must grow, including beyond xxxLarge: \(width)pt \(size)"
                )
                previousHeight = name.height
            }
        }
    }

    private func filamentCardImage(width: CGFloat, size: DynamicTypeSize, spoolName: String) async throws -> UIImage {
        let printer = try TestData.decodePrinter()
        let actions = [PrinterFilamentAction.Kind.change, .scanNFC].map {
            PrinterFilamentAction(kind: $0, target: .printer(printer.id), disabledReason: nil)
        }
        let model = PrinterFilamentPresentation(
            printer: printer, toolheads: [],
            spool: PrinterSpoolInfo(hasActiveSpool: true, activeSpoolId: 1, spoolName: spoolName, material: "PLA"),
            coverage: nil, coverageState: .disabled, isStale: false, supportedActions: [.change, .scanNFC]
        )
        let section = PrinterFilamentSection(
            presentation: model, actions: actions,
            onAction: { _ in XCTFail("Rendering must not send commands") }, showsAllActions: true
        )
        .environment(\.dynamicTypeSize, size)
        .environment(\.colorScheme, .light)
        .frame(width: width)
        .fixedSize(horizontal: false, vertical: true)
        let host = UIHostingController(rootView: section)
        host.safeAreaRegions = []
        let fitting = host.sizeThatFits(in: CGSize(width: width, height: 10_000))
        XCTAssertEqual(fitting.width, width, accuracy: 1)
        host.view.frame = CGRect(origin: .zero, size: fitting)
        let window = UIWindow(frame: host.view.frame)
        window.windowScene = UIApplication.shared.connectedScenes.compactMap { $0 as? UIWindowScene }.first
        window.frame = CGRect(origin: .zero, size: fitting)
        window.rootViewController = host
        window.makeKeyAndVisible()
        defer { window.isHidden = true }
        try await Task.sleep(for: .milliseconds(100))
        host.view.layoutIfNeeded()
        let image = UIGraphicsImageRenderer(bounds: host.view.bounds).image { _ in
            XCTAssertTrue(host.view.drawHierarchy(in: host.view.bounds, afterScreenUpdates: true))
        }
        let attachment = XCTAttachment(image: image)
        attachment.name = "filament-\(Int(width))-\(size)-\(UIDevice.current.userInterfaceIdiom)-\(spoolName)"
        attachment.lifetime = .keepAlways
        add(attachment)
        return image
    }

    private func recognizedText(in image: UIImage) throws -> [(text: String, height: CGFloat)] {
        let request = VNRecognizeTextRequest()
        request.recognitionLevel = .accurate
        request.usesLanguageCorrection = false
        try VNImageRequestHandler(cgImage: try XCTUnwrap(image.cgImage)).perform([request])
        return (request.results ?? []).compactMap { observation in
            observation.topCandidates(1).first.map {
                (text: $0.string, height: observation.boundingBox.height * image.size.height)
            }
        }
    }

    func testFilamentTabWaitsForSpoolLookupBeforeShowingUnknownQuantities() async throws {
        let printer = try TestData.decodePrinter()
        let spool = try TestData.decodeSpoolmanSpool()
        let model = PrinterFilamentPresentation(
            printer: printer, toolheads: [],
            spool: PrinterSpoolInfo(hasActiveSpool: true, activeSpoolId: spool.id, material: "PLA"),
            coverage: nil, coverageState: .loading, isStale: false, supportedActions: []
        )
        for (pending, details, message, expectsUnknown) in [
            (true, [Int: SpoolmanSpool](), Optional<String>.none, false),
            (true, [:], "Loading spool capacity…", false),
            (false, [spool.id: spool], nil, false),
            (false, [:], "Some assigned spools were not found in inventory.", true),
            (false, [:], "Spool capacity unavailable: Request timed out.", true)
        ] {
            let section = PrinterFilamentSection(
                presentation: model, actions: [], onAction: { _ in XCTFail("Rendering must not send commands") },
                showsAllActions: true, spoolDetailsByID: details,
                spoolLookupMessage: message, isSpoolLookupPending: pending
            )
            let host = UIHostingController(rootView: section.frame(width: 350))
            let window = UIWindow()
            window.windowScene = UIApplication.shared.connectedScenes.compactMap { $0 as? UIWindowScene }.first
            window.frame = CGRect(x: 0, y: 0, width: 390, height: 844)
            window.rootViewController = host
            window.makeKeyAndVisible()
            defer { window.isHidden = true }
            try await Task.sleep(for: .milliseconds(100))
            let accessibilityText = VisibleText.collect(in: host.view).joined(separator: " ")
            let image = UIGraphicsImageRenderer(bounds: host.view.bounds).image { _ in
                XCTAssertTrue(host.view.drawHierarchy(in: host.view.bounds, afterScreenUpdates: true))
            }
            let request = VNRecognizeTextRequest()
            request.recognitionLevel = .accurate
            request.usesLanguageCorrection = false
            try VNImageRequestHandler(cgImage: try XCTUnwrap(image.cgImage)).perform([request])
            let visibleText = (request.results ?? []).compactMap { $0.topCandidates(1).first?.string }
                .joined(separator: " ")
            let text = accessibilityText + " " + visibleText
            XCTAssertTrue(text.contains("PLA"), "The assigned filament must remain visible: \(text)")
            XCTAssertEqual(accessibilityText.contains("Spool amount Unknown"), expectsUnknown, text)
            XCTAssertEqual(visibleText.contains("Unknown"), expectsUnknown, visibleText)
            if let message {
                XCTAssertTrue(text.contains(message), "Real lookup failures must stay visible: \(text)")
            }
            if !details.isEmpty {
                XCTAssertTrue(text.contains(SpoolWeightMeter(
                    remainingGrams: spool.remainingWeightG, initialGrams: spool.initialWeightG
                ).label), text)
            }
        }
    }

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

    func testEmbeddedNativeAssignmentDelegatesToHostAndRemovesStaleOrBusyEntry() async throws {
        let printer = try TestData.decodePrinter()
        var received: [PrinterFilamentAction] = []
        for (stale, busy) in [(false, false), (true, false), (false, true)] {
            let action = PrinterFilamentAction(
                kind: .change, target: .printer(printer.id),
                disabledReason: busy ? "Another action is in progress" : nil
            )
            let section = PrinterFilamentSection(
                presentation: try presentation(printer: printer, stale: stale), actions: [action],
                onAction: { received.append($0) }, embedded: true
            )
            let host = UIHostingController(rootView: section.frame(width: 318))
            let window = UIWindow()
            window.windowScene = UIApplication.shared.connectedScenes.compactMap { $0 as? UIWindowScene }.first
            window.frame = CGRect(x: 0, y: 0, width: 390, height: 844)
            window.rootViewController = host
            window.makeKeyAndVisible()
            defer { window.isHidden = true }
            try await Task.sleep(for: .milliseconds(100))
            host.view.layoutIfNeeded()
            func buttons(_ view: UIView) -> [UIButton] {
                (view as? UIButton).map { [$0] } ?? view.subviews.flatMap { buttons($0) }
            }
            let entry = buttons(host.view).first { $0.accessibilityIdentifier == "printer.filament.action.\(action.id)" }
            if !stale && !busy {
                let button = try XCTUnwrap(entry)
                XCTAssertGreaterThanOrEqual(button.bounds.height, 44)
                button.sendActions(for: .touchUpInside)
                XCTAssertEqual(received, [action])
            } else {
                XCTAssertNil(entry, "Unavailable assignment stays in details, never as an enabled compact shortcut")
                XCTAssertEqual(section.detailActions, [action])
                section.select(action)
                XCTAssertEqual(received.count, 1)
            }
        }
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

    /// #3548: disabled filament actions render no reason caption while printing,
    /// paused, busy or offline. The reason is the VoiceOver hint only.
    func testDisabledActionsRenderNoReasonText() async throws {
        let reasons = [
            ("printing", "Filament load and unload are disabled while a print is active."),
            ("paused", "Filament load and unload are disabled while a print is active."),
            ("pending", "Command in progress."),
            ("offline", "Printer is offline.")
        ]
        for (scenario, reason) in reasons {
            var printer = try TestData.decodePrinter()
            printer.isOnline = scenario != "offline"
            printer.state = scenario == "paused" ? "paused" : (scenario == "printing" ? "printing" : "idle")
            let actions = [PrinterFilamentAction.Kind.change, .clearAssignment, .scanNFC].map {
                PrinterFilamentAction(kind: $0, target: .printer(printer.id), disabledReason: reason)
            }
            let section = PrinterFilamentSection(
                presentation: try presentation(printer: printer), actions: actions,
                onAction: { _ in XCTFail("Disabled actions must not dispatch") }
            )
            // `details` hosts the disabled actions where the reason caption used to render.
            let host = UIHostingController(rootView: section.details.frame(width: 318))
            let window = UIWindow()
            window.windowScene = UIApplication.shared.connectedScenes.compactMap { $0 as? UIWindowScene }.first
            window.frame = CGRect(x: 0, y: 0, width: 390, height: 844)
            window.rootViewController = host
            window.makeKeyAndVisible()
            defer { window.isHidden = true }
            try await Task.sleep(for: .milliseconds(100))
            host.view.layoutIfNeeded()
            let text = VisibleText.collect(in: host.view)
            XCTAssertFalse(text.isEmpty, "\(scenario): walker must see the rendered section")
            XCTAssertFalse(text.contains { $0.contains(reason) }, "\(scenario): \(text)")
            VisibleText.assertNoLockoutText(text, scenario)
        }
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
        let actionSets: [[PrinterFilamentAction.Kind]] = [
            [.set, .scanNFC],
            [.change, .clearAssignment, .scanNFC]
        ]
        for kinds in actionSets {
            let actions = kinds.map {
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
                    XCTAssertLessThanOrEqual(
                        full.width, width,
                        "Expanded width at proposed \(width)pt and dynamic type \(size)"
                    )
                }
            }
        }
    }
}
