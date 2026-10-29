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
                let lines = try recognizedText(in: image)
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

    func testSpoolNameScalesThroughEveryDynamicTypeSize() {
        let sizes: [DynamicTypeSize] = [
            .xSmall, .small, .medium, .large, .xLarge, .xxLarge, .xxxLarge,
            .accessibility1, .accessibility2, .accessibility3, .accessibility4, .accessibility5
        ]
        for width: CGFloat in [320, 700] {
            var previousHeight: CGFloat = 0
            for size in sizes {
                let name = PrinterFilamentSection.SpoolName(name: "Prusament")
                    .environment(\.dynamicTypeSize, size)
                let host = UIHostingController(rootView: name)
                host.safeAreaRegions = []
                let fitting = host.sizeThatFits(in: CGSize(width: width, height: 10_000))
                XCTAssertGreaterThan(
                    fitting.height, previousHeight,
                    "The rendered spool name must grow, including beyond xxxLarge: \(width)pt \(size)"
                )
                XCTAssertLessThanOrEqual(fitting.width, width)
                previousHeight = fitting.height
            }
        }
    }

    /// N3552-02 (#3561): a disabled Scan spool pill must look disabled through
    /// its chrome, not only a recolored label, and must still show no reason.
    func testDisabledScanSpoolPillUsesSharedDisabledChromeWithoutReasonText() async throws {
        let reason = "Printer is offline"
        for width: CGFloat in [320, 700] {
            let enabled = try await filamentCardImage(width: width, size: .large, spoolName: "Prusament")
            let disabled = try await filamentCardImage(
                width: width, size: .large, spoolName: "Prusament", scanDisabledReason: reason
            )
            let disabledLines = try recognizedText(in: disabled)
            XCTAssertTrue(disabledLines.contains { $0.contains("Scan spool") }, "\(width)pt: \(disabledLines)")
            let visible = disabledLines.joined(separator: " ")
            XCTAssertFalse(visible.contains(reason), "\(width)pt: disabled reason must stay out of view: \(disabledLines)")
            for fragment in VisibleText.forbiddenFragments {
                XCTAssertFalse(visible.localizedCaseInsensitiveContains(fragment), "\(width)pt: \(fragment) in \(disabledLines)")
            }

            let label = try XCTUnwrap(textBox(of: "Scan spool", in: enabled), "\(width)pt: Scan spool label not found")
            let changed = try changedPixels(enabled, disabled, excluding: label.insetBy(dx: -4 * enabled.scale, dy: -4 * enabled.scale))
            XCTAssertGreaterThanOrEqual(
                changed, 1_000,
                "\(width)pt: disabling Scan spool must restyle the pill fill/border, not only the label (\(changed) px)"
            )
        }
    }

    /// Pixel rect (top-left origin) of the first OCR observation containing `text`.
    private func textBox(of text: String, in image: UIImage) throws -> CGRect? {
        let cgImage = try XCTUnwrap(image.cgImage)
        let request = VNRecognizeTextRequest()
        request.recognitionLevel = .accurate
        request.usesLanguageCorrection = false
        try VNImageRequestHandler(cgImage: cgImage).perform([request])
        guard let match = (request.results ?? []).first(where: { $0.topCandidates(1).first?.string.contains(text) == true })
        else { return nil }
        let box = match.boundingBox
        let width = CGFloat(cgImage.width), height = CGFloat(cgImage.height)
        return CGRect(x: box.minX * width, y: (1 - box.maxY) * height, width: box.width * width, height: box.height * height)
    }

    /// Counts pixels outside `excluded` whose RGB differs by at least 2 levels.
    private func changedPixels(_ lhs: UIImage, _ rhs: UIImage, excluding excluded: CGRect) throws -> Int {
        let left = try rgba(lhs), right = try rgba(rhs)
        XCTAssertEqual(left.width, right.width)
        XCTAssertEqual(left.height, right.height)
        guard left.width == right.width, left.height == right.height else { return 0 }
        var count = 0
        for y in 0..<left.height {
            for x in 0..<left.width where !excluded.contains(CGPoint(x: x, y: y)) {
                let index = (y * left.width + x) * 4
                if (0..<3).contains(where: { abs(Int(left.bytes[index + $0]) - Int(right.bytes[index + $0])) >= 2 }) {
                    count += 1
                }
            }
        }
        return count
    }

    private func rgba(_ image: UIImage) throws -> (bytes: [UInt8], width: Int, height: Int) {
        let cgImage = try XCTUnwrap(image.cgImage)
        let width = cgImage.width, height = cgImage.height
        var bytes = [UInt8](repeating: 0, count: width * height * 4)
        let context = try XCTUnwrap(CGContext(
            data: &bytes, width: width, height: height, bitsPerComponent: 8, bytesPerRow: width * 4,
            space: CGColorSpaceCreateDeviceRGB(), bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
        ))
        context.draw(cgImage, in: CGRect(x: 0, y: 0, width: width, height: height))
        return (bytes, width, height)
    }

    private func filamentCardImage(
        width: CGFloat, size: DynamicTypeSize, spoolName: String, scanDisabledReason: String? = nil
    ) async throws -> UIImage {
        let printer = try TestData.decodePrinter()
        let actions = [PrinterFilamentAction.Kind.change, .scanNFC].map {
            PrinterFilamentAction(
                kind: $0, target: .printer(printer.id), disabledReason: $0 == .scanNFC ? scanDisabledReason : nil
            )
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
        attachment.name = "filament-\(Int(width))-\(size)-\(UIDevice.current.userInterfaceIdiom)-\(spoolName)\(scanDisabledReason == nil ? "" : "-scan-disabled")"
        attachment.lifetime = .keepAlways
        add(attachment)
        return image
    }

    private func recognizedText(in image: UIImage) throws -> [String] {
        let request = VNRecognizeTextRequest()
        request.recognitionLevel = .accurate
        request.usesLanguageCorrection = false
        try VNImageRequestHandler(cgImage: try XCTUnwrap(image.cgImage)).perform([request])
        return (request.results ?? []).compactMap { $0.topCandidates(1).first?.string }
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
