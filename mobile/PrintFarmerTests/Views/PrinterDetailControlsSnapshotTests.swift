import XCTest
import SwiftUI
import SnapshotTesting
@testable import PrintFarmer

@MainActor
final class PrinterCardSnapshotTests: XCTestCase {
    func testCardSnapshotsAdaptToPhoneTabletAppearanceAndLargestType() throws {
        let printer = try TestData.decodePrinter()
        for width: CGFloat in [320, 390, 340] {
            for scheme in [ColorScheme.light, .dark] {
                for size in [DynamicTypeSize.large, .accessibility5] {
                    let card = PrinterCardView(
                        printer: printer, attentionCount: 3,
                        failureReason: "Obico detected a possible spaghetti failure. Review the print."
                    )
                    .environment(\.colorScheme, scheme)
                    .environment(\.dynamicTypeSize, size)
                    .frame(width: width)
                    .fixedSize(horizontal: false, vertical: true)
                    let host = UIHostingController(rootView: card)
                    host.safeAreaRegions = []
                    let fitting = host.sizeThatFits(in: CGSize(width: width, height: 2000))
                    XCTAssertGreaterThan(fitting.height, 60)
                    XCTAssertLessThan(fitting.height, 1800)
                    XCTAssertEqual(fitting.width, width, accuracy: 1)
                    host.view.frame = CGRect(origin: .zero, size: fitting)
                    host.view.backgroundColor = .clear
                    let window = UIWindow(frame: host.view.frame)
                    window.rootViewController = host
                    window.isHidden = false
                    host.view.layoutIfNeeded()
                    let renderer = UIGraphicsImageRenderer(size: fitting)
                    let image = renderer.image { _ in
                        host.view.drawHierarchy(in: host.view.bounds, afterScreenUpdates: true)
                    }
                    let attachment = XCTAttachment(image: image)
                    attachment.name = "review-only-production-PrinterCardView-mock-\(Int(width))-\(scheme)-\(size)-\(UIDevice.current.userInterfaceIdiom)"
                    attachment.lifetime = .keepAlways
                    add(attachment)
                    window.isHidden = true
                }
            }
        }
    }

    func testCombinedVoiceOverIncludesJobProgressETAAndFailureWithoutRemovedBadges() throws {
        let printer = try TestData.decodePrinter()
        let presentation = PrinterCardPresentation(
            printer: printer, isPendingReady: false, attentionCount: 2,
            failureReason: "Spaghetti detected", printTimeLeftSeconds: 8100
        )
        XCTAssertEqual(presentation.stateLabel, "Failure suspected")
        XCTAssertTrue(presentation.accessibilityLabel.contains("benchy.gcode"))
        XCTAssertTrue(presentation.accessibilityLabel.contains("46% complete"))
        XCTAssertTrue(presentation.accessibilityLabel.contains("2h 15m left"))
        XCTAssertTrue(presentation.accessibilityLabel.contains("Done"))
        XCTAssertTrue(presentation.accessibilityLabel.contains("2 attention items"))
        XCTAssertTrue(presentation.accessibilityLabel.contains("Failure suspected: Spaghetti detected"))
        XCTAssertFalse(presentation.accessibilityLabel.contains("homed"))
        XCTAssertFalse(presentation.accessibilityLabel.contains("coverage"))
    }

    func testUnknownPausedOfflineAndPendingReadyStatesStayHonest() throws {
        var printer = try TestData.decodePrinter()
        func presentation(_ seconds: Double?) -> PrinterCardPresentation {
            PrinterCardPresentation(printer: printer, isPendingReady: false,
                                    attentionCount: nil, failureReason: nil, printTimeLeftSeconds: seconds)
        }
        XCTAssertEqual(presentation(nil).etaLabel, "ETA unavailable")
        XCTAssertEqual(presentation(.nan).etaLabel, "ETA unavailable")
        XCTAssertEqual(presentation(-1).etaLabel, "ETA unavailable")
        printer.state = "paused"
        XCTAssertEqual(presentation(60).etaLabel, "1m left · Paused")
        printer.isOnline = false
        XCTAssertEqual(presentation(60).stateLabel, "Offline")
        XCTAssertEqual(presentation(60).jobLabel, "Offline")
        XCTAssertFalse(presentation(60).isActiveJob)
        printer.isOnline = true
        printer.state = "idle"
        XCTAssertEqual(presentation(nil).jobLabel, "Ready to seed")
        let maintenanceJSON = TestJSON.printerMinimal
            .replacingOccurrences(of: "\"inMaintenance\": false", with: "\"inMaintenance\": true")
            .replacingOccurrences(of: "\"isOnline\": false", with: "\"isOnline\": true")
        let maintenance = PrinterCardPresentation(
            printer: try TestData.decodePrinter(from: maintenanceJSON), isPendingReady: false,
            attentionCount: nil, failureReason: nil, printTimeLeftSeconds: nil)
        XCTAssertEqual(maintenance.jobLabel, "In maintenance")
        printer.isOnline = false
        let offlineBed = PrinterCardPresentation(printer: printer, isPendingReady: true,
                                                attentionCount: 1, failureReason: nil, printTimeLeftSeconds: nil)
        XCTAssertEqual(offlineBed.stateLabel, "Offline")
        XCTAssertEqual(offlineBed.jobLabel, "Offline")
        XCTAssertFalse(offlineBed.accessibilityLabel.contains("Ready to harvest"))
        XCTAssertFalse(offlineBed.accessibilityLabel.contains("Bed clear"))
        printer.isOnline = true
        let bed = PrinterCardPresentation(printer: printer, isPendingReady: true,
                                         attentionCount: 1, failureReason: nil, printTimeLeftSeconds: nil)
        XCTAssertEqual(bed.stateLabel, "Bed clear")
        XCTAssertEqual(bed.jobLabel, "Ready to harvest")
    }

    func testOnlineErrorWithoutJobContextNeverReadsReadyToSeed() throws {
        var printer = try TestData.decodePrinter()
        printer.isOnline = true
        printer.state = "error"
        printer.jobName = nil
        printer.fileName = nil
        for reason in [nil, "Spaghetti detected"] as [String?] {
            let card = PrinterCardPresentation(printer: printer, isPendingReady: false, attentionCount: nil,
                                               failureReason: reason, printTimeLeftSeconds: nil)
            XCTAssertEqual(card.jobLabel, "Error")
            XCTAssertFalse(card.accessibilityLabel.contains("Ready to seed"))
        }
    }

    func testMaintenanceUnknownAndCompletedStatePrecedence() throws {
        func card(state: String, inMaintenance: Bool) throws -> PrinterCardPresentation {
            let json = TestJSON.printerMinimal
                .replacingOccurrences(of: "\"inMaintenance\": false", with: "\"inMaintenance\": \(inMaintenance)")
                .replacingOccurrences(of: "\"isOnline\": false", with: "\"isOnline\": true")
            var printer = try TestData.decodePrinter(from: json)
            printer.state = state
            printer.jobName = "part.gcode"
            return PrinterCardPresentation(printer: printer, isPendingReady: false, attentionCount: nil,
                                           failureReason: nil, printTimeLeftSeconds: nil)
        }
        let errorMaintenance = try card(state: "error", inMaintenance: true)
        XCTAssertEqual(errorMaintenance.stateLabel, "Maintenance")
        XCTAssertEqual(errorMaintenance.jobLabel, "In maintenance")
        let pausedMaintenance = try card(state: "paused", inMaintenance: true)
        XCTAssertEqual(pausedMaintenance.stateLabel, "Maintenance")
        XCTAssertEqual(pausedMaintenance.jobLabel, "part.gcode")
        let unknown = try card(state: "calibrating", inMaintenance: false)
        XCTAssertEqual(unknown.stateLabel, "Calibrating")
        XCTAssertEqual(unknown.jobLabel, "Ready to seed")
        let completed = try card(state: "completed", inMaintenance: false)
        XCTAssertEqual(completed.stateLabel, "Completed")
        XCTAssertEqual(completed.jobLabel, "Ready to seed")
    }

    func testMissingTemperaturesReadAsNotReportedInVoiceOver() throws {
        var printer = try TestData.decodePrinter()
        printer.hotendTemp = nil
        printer.bedTemp = nil
        let label = PrinterCardPresentation(printer: printer, isPendingReady: false, attentionCount: nil,
                                            failureReason: nil, printTimeLeftSeconds: nil).accessibilityLabel
        XCTAssertTrue(label.contains("Nozzle not reported"))
        XCTAssertTrue(label.contains("Bed not reported"))
        XCTAssertFalse(label.contains("Nozzle unavailable"))
        XCTAssertFalse(label.contains("Bed unavailable"))
    }

    func testPendingReadyCardDoesNotPresentCompletedJobAsActiveProgress() throws {
        var printer = try TestData.decodePrinter()
        printer.state = "printing"
        printer.progress = 0.23
        printer.jobName = "clip_holder_x4.gcode"

        let presentation = PrinterCardPresentation(
            printer: printer,
            isPendingReady: true,
            attentionCount: 1,
            failureReason: nil,
            printTimeLeftSeconds: 1_320
        )

        XCTAssertEqual(presentation.stateLabel, "Bed clear")
        XCTAssertFalse(presentation.canShowJobETA)
        XCTAssertFalse(presentation.showsJobProgress)
        XCTAssertFalse(presentation.accessibilityLabel.contains("% complete"))
        XCTAssertFalse(presentation.accessibilityLabel.contains("ETA unavailable"))
        XCTAssertEqual(presentation.jobLabel, "Ready to harvest")
    }

    func testFailureCardRetainsKnownJobProgressAndOnlyShowsServerReportedETA() throws {
        var printer = try TestData.decodePrinter()
        printer.state = "error"
        printer.progress = 0.22
        printer.jobName = "gear_set_v3.gcode"
        printer.fileName = "gear_set_v3.gcode"
        let failure = "The printer reported a failed job."

        let withETA = PrinterCardPresentation(
            printer: printer,
            isPendingReady: false,
            attentionCount: 1,
            failureReason: failure,
            printTimeLeftSeconds: 9_660
        )
        XCTAssertFalse(withETA.isActiveJob)
        XCTAssertTrue(withETA.showsJobProgress)
        XCTAssertTrue(withETA.canShowJobETA)
        XCTAssertEqual(withETA.stateLabel, "Failure suspected")
        XCTAssertEqual(withETA.jobLabel, "gear_set_v3.gcode")
        XCTAssertTrue(withETA.etaLabel.contains("2h 41m left"))
        XCTAssertTrue(withETA.etaLabel.contains("Done"))
        XCTAssertTrue(withETA.accessibilityLabel.contains("22% progress at failure"))

        let withoutETA = PrinterCardPresentation(
            printer: printer,
            isPendingReady: false,
            attentionCount: 1,
            failureReason: failure,
            printTimeLeftSeconds: nil
        )
        XCTAssertEqual(withoutETA.etaLabel, "ETA unavailable")
        XCTAssertTrue(withoutETA.accessibilityLabel.contains("ETA unavailable"))
    }

    func testLateThumbnailFromOldRevisionCannotReplaceNewCard() async throws {
        let service = MockPrinterService()
        let barrier = AsyncBarrier()
        defer { barrier.close() }
        let image = UIGraphicsImageRenderer(size: CGSize(width: 2, height: 2)).image { context in
            UIColor.red.setFill()
            context.fill(CGRect(x: 0, y: 0, width: 2, height: 2))
        }
        let data = try XCTUnwrap(image.pngData())
        service.thumbnailHandler = { _, _ in await barrier.arriveAndWait(); return data }
        let model = PrinterCardMediaModel()
        let old = PrinterCardRequestID(printerID: TestData.testUUID, path: "/old",
                                       jobName: "old", state: "printing", isFailureSuspected: false,
                                       serviceID: ObjectIdentifier(service))
        let task = Task { await model.load(request: old, service: service) }
        await barrier.waitUntilArrived()
        let new = PrinterCardRequestID(printerID: TestData.testUUID, path: nil,
                                       jobName: nil, state: "idle", isFailureSuspected: false,
                                       serviceID: ObjectIdentifier(service))
        await model.load(request: new, service: service)
        barrier.release()
        await task.value
        XCTAssertEqual(model.request, new)
        XCTAssertNil(model.image)
        XCTAssertNil(model.printTimeLeftSeconds)
        model.cancel()
        XCTAssertNil(model.request)
    }

    func testAuthenticatedThumbnailIsDecodedAndDownsampledForCurrentRevision() async throws {
        let service = MockPrinterService()
        let image = UIGraphicsImageRenderer(size: CGSize(width: 600, height: 600)).image { context in
            UIColor.green.setFill()
            context.fill(CGRect(x: 0, y: 0, width: 600, height: 600))
        }
        let data = try XCTUnwrap(image.pngData())
        service.thumbnailHandler = { _, path in
            XCTAssertEqual(path, "/current-version")
            return data
        }
        let model = PrinterCardMediaModel()
        let request = PrinterCardRequestID(
            printerID: TestData.testUUID, path: "/current-version",
            jobName: "benchy", state: "printing", isFailureSuspected: false,
            serviceID: ObjectIdentifier(service)
        )
        await model.load(request: request, service: service)
        XCTAssertEqual(model.request, request)
        XCTAssertEqual(model.image?.size.width, 180)
        XCTAssertEqual(model.image?.size.height, 180)
    }

    func testThumbnailMissingInvalidAndTransportFailureLeaveNeutralPlaceholder() async throws {
        for error in [NetworkError.notFound, NetworkError.staleServerResponse] {
            let service = MockPrinterService()
            service.thumbnailHandler = { _, _ in throw error }
            let model = PrinterCardMediaModel()
            await model.load(request: .init(printerID: TestData.testUUID, path: "/thumbnail",
                                            jobName: "benchy", state: "printing", isFailureSuspected: false,
                                            serviceID: ObjectIdentifier(service)), service: service)
            XCTAssertNil(model.image)
        }
        let service = MockPrinterService()
        service.thumbnailHandler = { _, _ in Data("not an image".utf8) }
        let model = PrinterCardMediaModel()
        await model.load(request: .init(printerID: TestData.testUUID, path: "/thumbnail",
                                        jobName: "benchy", state: "printing", isFailureSuspected: false,
                                        serviceID: ObjectIdentifier(service)), service: service)
        XCTAssertNil(model.image)
    }
}

/// Legacy setup compatibility assertions, production instrument-panel contracts,
/// review-only captures, and one stable shared destructive-button golden.
@MainActor
final class PrinterDetailControlsSnapshotTests: XCTestCase {

    // UIKit/SwiftUI rasterization differs between iPhone and iPad test hosts,
    // even with the same layout and an explicit display-scale override.
    private var snapshotName: String? {
        UIDevice.current.userInterfaceIdiom == .pad ? "iPad" : nil
    }

    // Synthetic capability evidence for layout/lifecycle tests, not backend profiles.
    private static let layoutCaps = PrinterBackendCapabilities.allControlsFixture

    private static var essentialLayoutCaps: PrinterBackendCapabilities {
        var caps = layoutCaps
        caps.supportsAbsoluteMovement = true
        caps.supportsDisableMotors = true
        caps.supportsExtrusion = true
        caps.supportsFilamentLoad = true
        caps.supportsFilamentUnload = true
        caps.supportsFilamentChange = true
        caps.supportsZOffset = true
        caps.supportsZOffsetFirmwareSave = true
        caps.verifiedSafety = VerifiedSafetyFixtures.discovery()
        return caps
    }

    private func wireCapabilities(backend: String, operationFields: String) throws -> PrinterBackendCapabilities {
        let data = Data("""
        {
            "printerId": "550e8400-e29b-41d4-a716-446655440000",
            "printerName": "Contract fixture",
            "backend": "\(backend)",
            \(operationFields)
        }
        """.utf8)
        return PrinterBackendCapabilities(wire: try JSONDecoder().decode(PrinterBackendCapabilitiesWireDto.self, from: data))
    }

    // MARK: - Printer fixture (force state to idle so the section renders)

    /// Decodes the canonical printer JSON and overrides live-status fields so
    /// `PrinterControlsAccess.isEntryVisible(for:)` returns `true`.
    private func makePrinter(
        backend: PrinterBackend,
        isOnline: Bool = true,
        state: String? = "idle"
    ) throws -> Printer {
        // Substitute the backend in the JSON fixture; the `state`/`isOnline`
        // overrides are mutated post-decode because the live-status fields
        // are `var` on `Printer`.
        let backendString: String = {
            switch backend {
            case .moonraker: return "Moonraker"
            case .prusaLink: return "PrusaLink"
            case .octoPrint: return "OctoPrint"
            case .flashForge: return "FlashForge"
            case .sdcp: return "Sdcp"
            case .unknown: return "Unknown"
            }
        }()
        let json = TestJSON.printer.replacingOccurrences(
            of: "\"backend\": \"Moonraker\"",
            with: "\"backend\": \"\(backendString)\""
        )
        var printer = try TestData.decodePrinter(from: json)
        printer.isOnline = isOnline
        printer.state = state
        return printer
    }

    private func makeService(caps: PrinterBackendCapabilities?) -> MockPrinterService {
        let svc = MockPrinterService()
        svc.capabilitiesToReturn = caps
        svc.detailsToReturn = try? .controlsLimitsFixture(for: TestData.decodePrinter())
        return svc
    }

    private func host(_ view: some View) -> UIViewController {
        let host = UIHostingController(rootView: view.frame(width: 390))
        host.overrideUserInterfaceStyle = .light
        host.view.backgroundColor = .systemBackground
        return host
    }

    // MARK: - Embedded content and standalone ownership

    private func install(_ view: some View) -> (UIWindow, UIHostingController<AnyView>) {
        let controller = UIHostingController(rootView: AnyView(view))
        let window = UIWindow(frame: CGRect(x: 0, y: 0, width: 390, height: 844))
        window.windowScene = UIApplication.shared.connectedScenes.compactMap { $0 as? UIWindowScene }.first
        window.frame = CGRect(x: 0, y: 0, width: 390, height: 844)
        window.rootViewController = controller
        window.makeKeyAndVisible()
        controller.view.layoutIfNeeded()
        return (window, controller)
    }

    private func settle(_ controller: UIViewController) async throws {
        // Allow SwiftUI's scheduled body/onChange work to reach the hosted UIKit tree.
        try await Task.sleep(for: .milliseconds(100))
        controller.view.setNeedsLayout()
        controller.view.layoutIfNeeded()
    }

    private struct SettleTimeout: Error, CustomStringConvertible {
        let description: String
    }

    /// Settles until `condition` holds or `timeout` elapses. Button actions dispatch
    /// through `Task { await viewModel... }` and then a nested command task, so slower
    /// simulators can need more than one fixed settle before the service observes the
    /// call or the lease releases. A timeout records a failure and throws so the test
    /// stops instead of asserting against the previous command's state. Callers still
    /// assert the exact outcome afterward.
    private func settle(
        _ controller: UIViewController,
        _ description: String = "the expected asynchronous UI outcome",
        timeout: Duration = .seconds(10),
        file: StaticString = #filePath,
        line: UInt = #line,
        until condition: () -> Bool
    ) async throws {
        let deadline = ContinuousClock.now + timeout
        repeat {
            try await settle(controller)
            if condition() { return }
        } while ContinuousClock.now < deadline
        let message = "Timed out after \(timeout) waiting for \(description)"
        XCTFail(message, file: file, line: line)
        throw SettleTimeout(description: message)
    }

    // MARK: - Native control contracts

    private func nativeControls(in view: UIView) -> [UIControl] {
        (view as? UIControl).map { [$0] } ?? view.subviews.flatMap { nativeControls(in: $0) }
    }

    private func expandAbsolute<Content: View>(_ controller: UIHostingController<Content>) async throws {
        try await settle(controller)
        if let window = controller.view.window {
            window.frame.size = controller.sizeThatFits(in: CGSize(width: window.bounds.width, height: 10000))
            controller.view.frame = window.bounds
        }
        try await settle(controller)
    }

    func test_essentialDirectionalButtons_sendCorrectAxesSignsAndExistingRates() async throws {
        let printer = try makePrinter(backend: .octoPrint)
        let service = makeService(caps: Self.layoutCaps)
        let model = PrinterControlsViewModel.configuredForTests(printerService: service, printer: printer)
        await model.loadCapabilities()
        let (window, controller) = install(PrinterMotionControls(viewModel: model))
        defer { window.isHidden = true }
        try await settle(controller)
        func button(_ id: String) throws -> UIControl {
            try XCTUnwrap(nativeControls(in: controller.view).first { $0.accessibilityIdentifier == id })
        }
        try button("printer.controls.jog.step.10").sendActions(for: .touchUpInside)
        try await settle(controller)
        for axis in ["X", "Y", "Z"] {
            for (direction, sign) in [("negative", -1.0), ("positive", 1.0)] {
                try button("printer.controls.jog.\(axis.lowercased()).\(direction)").sendActions(for: .touchUpInside)
                try await settle(controller) {
                    service.moveCalledWith?.axis == axis && service.moveCalledWith?.distanceMm == sign * 10
                }
                XCTAssertEqual(service.moveCalledWith?.axis, axis)
                XCTAssertEqual(service.moveCalledWith?.distanceMm, sign * 10)
                XCTAssertEqual(
                    service.moveCalledWith?.feedrateMmMin,
                    axis == "Z" ? PrinterControlsViewModel.zFeedrateMmMin : PrinterControlsViewModel.xyFeedrateMmMin
                )
                model.cancelPendingCommand()
                try await settle(controller) { !model.isExecuting }
            }
        }
    }

    func test_essentialSelectorRendersBothFullTitlesForEitherSelectionAtAccessibilitySizes() async throws {
        let printer = try makePrinter(backend: .moonraker)
        for width: CGFloat in [320, 390, 1068] {
            for textSize in [DynamicTypeSize.large, .accessibility3, .accessibility5] {
                for selection in PrinterDetailPanel.allCases {
                    let content = PrinterDetailPanelsHost(
                        selection: .constant(selection), controlsAvailable: true, printer: printer,
                        status: { Color.clear }, control: { Color.clear },
                        filament: { Color.clear }, queue: { Color.clear }
                    )
                    .environment(\.dynamicTypeSize, textSize)
                    .frame(width: width, height: 844)
                    let (window, controller) = install(content)
                    defer { window.isHidden = true }
                    window.frame.size = CGSize(width: width, height: 844)
                    controller.view.frame = window.bounds
                    try await settle(controller)
                    func titleLabels(in view: UIView) -> [UILabel] {
                        guard !view.isHidden, view.alpha > 0 else { return [] }
                        let own = (view as? UILabel).map { [$0] } ?? []
                        return own + view.subviews.flatMap { titleLabels(in: $0) }
                    }
                    let titles = Set(PrinterDetailPanel.allCases.map(\.title))
                    let labels = titleLabels(in: controller.view).filter { titles.contains($0.text ?? "") }
                    XCTAssertEqual(Set(labels.compactMap(\.text)), titles)
                    for label in labels {
                        let font = try XCTUnwrap(label.font)
                        let required = (try XCTUnwrap(label.text) as NSString).size(withAttributes: [.font: font])
                        XCTAssertGreaterThanOrEqual(label.bounds.width + 1, ceil(required.width), label.text ?? "")
                        XCTAssertGreaterThanOrEqual(label.bounds.height + 1, ceil(font.lineHeight), label.text ?? "")
                    }
                    for control in nativeControls(in: controller.view) {
                        XCTAssertGreaterThanOrEqual(control.bounds.height, 44)
                        XCTAssertGreaterThanOrEqual(control.bounds.width, 44)
                    }
                    let image = UIGraphicsImageRenderer(bounds: controller.view.bounds).image { _ in
                        XCTAssertTrue(controller.view.drawHierarchy(in: controller.view.bounds, afterScreenUpdates: true))
                    }
                    let attachment = XCTAttachment(image: image)
                    attachment.name = "review-only-production-PrinterDetailPanelsHost-empty-page-fixtures-\(Int(width))-\(textSize)-\(selection.rawValue)"
                    attachment.lifetime = .keepAlways
                    add(attachment)
                }
            }
        }
    }

    func test_essentialHomeGlyphs_keepAccessibleNamesAndDispatchDistinctOperations() async throws {
        for textSize in [DynamicTypeSize.large, .accessibility3] {
            let printer = try makePrinter(backend: .octoPrint)
            let service = makeService(caps: Self.layoutCaps)
            let model = PrinterControlsViewModel.configuredForTests(printerService: service, printer: printer)
            await model.loadCapabilities()
            let content = PrinterMotionControls(viewModel: model)
                .environment(\.dynamicTypeSize, textSize)
                .frame(width: 390).fixedSize(horizontal: false, vertical: true)
            let (window, controller) = install(content)
            defer { window.isHidden = true }
            window.frame.size = controller.sizeThatFits(in: CGSize(width: 390, height: 10000))
            controller.view.frame = window.bounds
            try await settle(controller)
            let homes = nativeControls(in: controller.view).compactMap { $0 as? UIButton }.filter {
                $0.accessibilityIdentifier?.hasPrefix("printer.controls.home.") == true
            }
            XCTAssertEqual(homes.count, 3)
            for (id, label) in [("all", "Home all axes"), ("xy", "Home XY"), ("z", "Home Z")] {
                let button = try XCTUnwrap(homes.first {
                    $0.accessibilityIdentifier == "printer.controls.home.\(id)"
                })
                // Glyph-only: no title, so the image stays centred (#3514 N09).
                XCTAssertNil(button.configuration?.title)
                XCTAssertNotNil(button.configuration?.image)
                XCTAssertEqual(button.accessibilityLabel, label)
                XCTAssertGreaterThanOrEqual(button.bounds.width, 44)
                XCTAssertGreaterThanOrEqual(button.bounds.height, 48)
                XCTAssertTrue(button.isEnabled)
                button.sendActions(for: .touchUpInside)
                try await settle(controller) {
                    switch id {
                    case "all": service.homeCalledWith != nil
                    case "xy": service.homeXYCalledWith != nil
                    default: service.homeZCalledWith != nil
                    }
                }
                switch id {
                case "all":
                    XCTAssertEqual(service.homeCalledWith?.axes, ["X", "Y", "Z"])
                    XCTAssertNil(service.homeXYCalledWith)
                    XCTAssertNil(service.homeZCalledWith)
                case "xy":
                    XCTAssertEqual(service.homeXYCalledWith, printer.id)
                    XCTAssertNil(service.homeZCalledWith)
                default:
                    XCTAssertEqual(service.homeZCalledWith, printer.id)
                }
                model.cancelPendingCommand()
                try await settle(controller) { !model.isExecuting }
            }
            XCTAssertNil(service.moveCalledWith, "Homing glyphs must never issue a jog")
        }
    }

    func test_essentialHomeGlyphs_preserveIndependentCapabilitiesWithoutRelativeMovement() async throws {
        let printer = try makePrinter(backend: .moonraker)
        let caps = PrinterBackendCapabilities(
            supportsMovement: false, supportsTemperatureControl: true,
            supportsBedTemperature: true, supportsFanControl: false,
            supportsHoming: true, supportedAxes: ["X", "Y", "Z"],
            supportsHomingXY: false, supportsHomingZ: true
        )
        let service = makeService(caps: caps)
        let model = PrinterControlsViewModel.configuredForTests(printerService: service, printer: printer)
        await model.loadCapabilities()
        let (window, controller) = install(PrinterMotionControls(viewModel: model))
        defer { window.isHidden = true }
        try await settle(controller)
        func button(_ id: String) throws -> UIControl {
            try XCTUnwrap(nativeControls(in: controller.view).first {
                $0.accessibilityIdentifier == "printer.controls.\(id)"
            })
        }
        XCTAssertTrue(try button("home.all").isEnabled)
        XCTAssertFalse(try button("home.xy").isEnabled)
        XCTAssertTrue(try button("home.z").isEnabled)
        for axis in ["x", "y", "z"] {
            for direction in ["negative", "positive"] {
                XCTAssertFalse(try button("jog.\(axis).\(direction)").isEnabled)
            }
        }
        XCTAssertNil(service.homeCalledWith)
        XCTAssertNil(service.homeXYCalledWith)
        XCTAssertNil(service.homeZCalledWith)
        XCTAssertNil(service.moveCalledWith)
    }

    func test_absoluteEditorAcceptsOptionalAxesWithoutVerifiedSafetyPreflight() async throws {
        let printer = try makePrinter(backend: .octoPrint)
        var caps = Self.layoutCaps
        caps.supportsAbsoluteMovement = true
        caps.verifiedSafety = VerifiedSafetyFixtures.discovery()
        let service = makeService(caps: caps)
        service.statusToReturn = VerifiedSafetyFixtures.status(id: printer.id)
        let model = PrinterControlsViewModel.configuredForTests(printerService: service, printer: printer)
        await model.loadCapabilities()
        let content = JogSubgroup.AbsolutePositionControls(viewModel: model)
            .frame(width: 390).fixedSize(horizontal: false, vertical: true)
        let (window, controller) = install(content)
        defer { window.isHidden = true }
        window.frame.size = controller.sizeThatFits(in: CGSize(width: 390, height: 10000))
        controller.view.frame = window.bounds
        try await settle(controller)
        let controls = nativeControls(in: controller.view)
        let fields = try ["x", "y", "z"].map { axis in
            try XCTUnwrap(controls.first {
                $0.accessibilityIdentifier == "printer.controls.absolute.\(axis)"
            } as? UITextField)
        }

        let move = try XCTUnwrap(controls.first { $0.accessibilityIdentifier == "printer.controls.absolute.move" })
        XCTAssertFalse(move.isEnabled)
        // With optional coordinates allowed, move is enabled when 1, 2, or 3 coordinates are supplied.
        fields[0].text = "10"
        fields[0].sendActions(for: .editingChanged)
        try await settle(controller)
        XCTAssertTrue(move.isEnabled)

        // When all fields are cleared, move is disabled.
        for field in fields {
            field.text = ""
            field.sendActions(for: .editingChanged)
        }
        try await settle(controller)
        XCTAssertFalse(move.isEnabled)
        move.sendActions(for: .touchUpInside)
        try await settle(controller)
        XCTAssertNil(service.moveToCalledWith)
        XCTAssertNil(model.lastError, "Missing coordinates are blocked in the editor before the owner")

        for index in fields.indices {
            fields[index].text = ["0", "-2.5", "10"][index]
            fields[index].sendActions(for: .editingChanged)
        }
        try await settle(controller)
        XCTAssertTrue(move.isEnabled)
        service.capabilitiesToReturn?.verifiedSafety?.operations.absoluteMovement.support = .unknown
        await model.refreshSafetyEvidence()
        try await settle(controller)
        XCTAssertTrue(move.isEnabled, "Unknown safety evidence does not override native absolute-move support")
        service.capabilitiesToReturn = caps
        await model.refreshSafetyEvidence()
        try await settle(controller)
        XCTAssertTrue(move.isEnabled)
        move.sendActions(for: .touchUpInside)
        try await settle(controller, "the absolute move request to reach the service") {
            service.moveToCalledWith != nil
        }
        XCTAssertEqual(service.moveToCalledWith?.x, 0)
        XCTAssertEqual(service.moveToCalledWith?.y, -2.5)
        XCTAssertEqual(service.moveToCalledWith?.z, 10)
        XCTAssertEqual(service.moveToCalledWith?.feedrateMmMin, 600)
    }

    func test_guardedCalibration_supportedFlowHasAccessibleActionsAndRetainedStages() async throws {
        let printer = try makePrinter(backend: .octoPrint)
        var caps = Self.layoutCaps
        caps.supportsAbsoluteMovement = true
        caps.supportsZOffset = true
        caps.supportsZOffsetFirmwareSave = true
        caps.verifiedSafety = VerifiedSafetyFixtures.discovery()
        let service = makeService(caps: caps)
        service.statusToReturn = VerifiedSafetyFixtures.status(id: printer.id)
        service.detailsToReturn = PrinterDetails(
            id: printer.id, name: printer.name, backend: printer.backend,
            rowVersion: "reviewed-native-flow", zOffsetMm: 0.12
        )
        let model = PrinterControlsViewModel.configuredForTests(printerService: service, printer: printer)
        await model.loadCapabilities()
        let (window, controller) = install(PrinterZOffsetCalibrationControls(viewModel: model))
        defer { window.isHidden = true; model.cancelCalibration() }

        func action(_ suffix: String) throws -> UIControl {
            let button = try XCTUnwrap(nativeControls(in: controller.view).first {
                $0.accessibilityIdentifier == "printer.controls.calibration-\(suffix)"
            })
            XCTAssertTrue(button.isEnabled, suffix)
            XCTAssertGreaterThanOrEqual(button.bounds.height, 44)
            XCTAssertFalse(button.accessibilityLabel?.isEmpty ?? true)
            return button
        }
        func capture(
            _ step: ZOffsetCalibrationStep,
            file: StaticString = #filePath,
            line: UInt = #line,
            alsoUntil extra: () -> Bool = { true }
        ) async throws {
            try await settle(
                controller, "calibration step \(step.rawValue) with no command or review in flight",
                file: file, line: line
            ) {
                model.calibrationStep == step && !model.isExecuting && !model.isReviewingCalibration && extra()
            }
            XCTAssertEqual(model.calibrationStep, step)
            let image = UIGraphicsImageRenderer(bounds: controller.view.bounds).image { _ in
                XCTAssertTrue(controller.view.drawHierarchy(in: controller.view.bounds, afterScreenUpdates: true))
            }
            let attachment = XCTAttachment(image: image)
            attachment.name = "review-only-legacy-PrinterZOffsetCalibrationControls-mock-\(snapshotName ?? "iPhone")-\(step.rawValue)"
            attachment.lifetime = .keepAlways
            add(attachment)
        }

        try await settle(controller)
        try action("start").sendActions(for: .touchUpInside)
        // beginCalibrationHome() requires the stored baseline that startCalibration reads.
        try await capture(.introduction) { model.calibrationOffset != nil }
        model.beginCalibrationHome()
        try await capture(.home)
        try action("home").sendActions(for: .touchUpInside)
        try await settle(controller, "the calibration home request to reach the service") {
            service.homeCalledWith != nil
        }
        XCTAssertNotNil(service.homeCalledWith)
        service.statusToReturn = VerifiedSafetyFixtures.status(id: printer.id)
        await model.refreshSafetyEvidence()
        try await capture(.position)
        try action("position").sendActions(for: .touchUpInside)
        try await settle(controller, "the calibration position request to reach the service") {
            service.moveToCalledWith?.x == 40
        }
        XCTAssertEqual(service.moveToCalledWith?.x, 40)
        service.statusToReturn = VerifiedSafetyFixtures.status(id: printer.id, position: .init(x: 40, y: 30, z: 10))
        await model.refreshSafetyEvidence()
        try await capture(.adjust)
        try action("closer").sendActions(for: .touchUpInside)
        try await settle(controller, "the closer adjustment to reach the service at Z 9.95") {
            service.moveToCalledWith?.z == 9.95
        }
        XCTAssertEqual(service.moveToCalledWith?.z, 9.95)
        service.statusToReturn = VerifiedSafetyFixtures.status(id: printer.id, position: .init(x: 40, y: 30, z: 9.95))
        await model.refreshSafetyEvidence()
        try await settle(controller, "the telemetry-confirmed adjustment to finish before review") {
            model.calibrationStep == .adjust && !model.isExecuting && !model.isReviewingCalibration
        }
        try action("review").sendActions(for: .touchUpInside)
        try await capture(.save)
        try action("save").sendActions(for: .touchUpInside)
        try await capture(.done)
        XCTAssertEqual(service.saveZOffsetCalledWith?.reviewedRowVersion, "reviewed-native-flow")
        XCTAssertEqual(service.saveZOffsetCalledWith?.offsetMm, 0.07)
    }

    func test_guardedCalibration_cancelRemovesInlineFlowWithoutSendingCommands() async throws {
        let printer = try makePrinter(backend: .moonraker)
        let service = makeService(caps: Self.layoutCaps)
        let model = PrinterControlsViewModel.configuredForTests(printerService: service, printer: printer)
        await model.loadCapabilities()
        let (window, controller) = install(PrinterZOffsetCalibrationControls(viewModel: model))
        defer { window.isHidden = true }
        await model.startCalibration()
        try await settle(controller)
        let cancel = try XCTUnwrap(nativeControls(in: controller.view).first {
            $0.accessibilityIdentifier == "printer.controls.calibration-cancel"
        })
        XCTAssertTrue(cancel.isEnabled)
        XCTAssertGreaterThanOrEqual(cancel.bounds.height, 44)
        cancel.sendActions(for: .touchUpInside)
        try await settle(controller)
        XCTAssertNil(model.calibrationStep)
        XCTAssertNil(service.homeCalledWith)
        XCTAssertNil(service.moveToCalledWith)
        XCTAssertNil(service.saveZOffsetCalledWith)
    }

    // MARK: - Reachable Control tab (no pixel comparisons)

    func test_instrumentPanel_backendProfilesGateNativeHeaterEditors() async throws {
        let profiles: [(PrinterBackend, String, String, Bool)] = [
            (.moonraker, "Moonraker", """
        "supportsRelativeMovement": false, "supportsAbsoluteMovement": false,
        "supportsHoming": true, "supportsHomingXY": true, "supportsHomingZ": true,
        "supportsHotendTemperature": true, "supportsBedTemperature": true,
        "supportsExtrusion": true, "supportsDisableMotors": true, "supportsZOffset": true,
        "supportsTemperatureControlDuringPrint": true, "supportsHeaterShutdown": true,
        "supportedAxes": ["x", "y", "z"]
        """, true),
            (.flashForge, "FlashForge", """
        "supportsZOffset": true, "supportsRelativeMovement": false,
        "supportsHoming": false, "supportsHotendTemperature": false,
        "supportsBedTemperature": false, "supportedAxes": []
        """, false),
            (.sdcp, "Sdcp", """
        "supportsZOffset": true, "supportsRelativeMovement": false,
        "supportsHoming": false, "supportsHotendTemperature": false,
        "supportedAxes": []
        """, false)
        ]
        for (backend, name, fields, supportsHeat) in profiles {
            let printer = try makePrinter(backend: backend)
            let caps = try wireCapabilities(backend: name, operationFields: fields)
            let service = makeService(caps: caps)
            let model = PrinterControlsViewModel.configuredForTests(printerService: service, printer: printer)
            await model.loadCapabilities()
            let (window, controller) = install(PrinterInstrumentPanelControls(printer: printer, viewModel: model))
            defer { window.isHidden = true; model.deactivate() }
            try await settle(controller)
            XCTAssertNil(nativeControls(in: controller.view).first {
                $0.accessibilityIdentifier == "printer.controls.jog.x.positive"
            }, "\(name) does not advertise relative movement")
            try panelControl("printer.controls.segment.heat", in: controller).sendActions(for: .touchUpInside)
            try await settle(controller)
            for heater in ["hotend", "bed"] {
                let field = try panelControl("printer.controls.\(heater).target", in: controller)
                XCTAssertEqual(field.isEnabled, supportsHeat, "\(name) \(heater)")
                XCTAssertTrue(field.accessibilityLabel?.contains("degrees Celsius") == true)
            }
            attachPanelReview(controller, scenario: "\(name)-idle-heat")
            XCTAssertNil(service.moveCalledWith)
            XCTAssertNil(service.homeCalledWith)
            XCTAssertEqual(service.setTemperaturesCallCount, 0, "Opening Heat must not send targets")
        }
    }

    func test_instrumentPanel_loadingCapabilitiesCannotDispatchAndResolvesWithoutRemount() async throws {
        let printer = try makePrinter(backend: .octoPrint)
        let service = makeService(caps: Self.layoutCaps)
        let barrier = AsyncBarrier()
        service.beforeGetBackendCapabilities = { await barrier.arriveAndWait() }
        let model = PrinterControlsViewModel.configuredForTests(printerService: service, printer: printer)
        let load = Task { await model.loadCapabilities() }
        defer { barrier.close(); load.cancel(); model.deactivate() }
        await barrier.waitUntilArrived()
        let (window, controller) = install(PrinterInstrumentPanelControls(printer: printer, viewModel: model))
        defer { window.isHidden = true }
        try await settle(controller)
        XCTAssertNil(model.capabilities)
        XCTAssertTrue(model.isLoadingCapabilities)
        XCTAssertNil(nativeControls(in: controller.view).first {
            $0.accessibilityIdentifier == "printer.controls.jog.x.positive"
        }, "Unproven motion support must not expose jog actions")
        try panelControl("printer.controls.segment.heat", in: controller).sendActions(for: .touchUpInside)
        try await settle(controller)
        XCTAssertFalse(try panelControl("printer.controls.hotend.target", in: controller).isEnabled)
        XCTAssertFalse(try panelControl("printer.controls.bed.target", in: controller).isEnabled)
        XCTAssertEqual(service.setTemperaturesCallCount, 0)
        XCTAssertNil(service.moveCalledWith)

        barrier.release()
        await load.value
        try await settle(controller, "loaded heater editors") {
            nativeControls(in: controller.view).first {
                $0.accessibilityIdentifier == "printer.controls.hotend.target"
            }?.isEnabled == true
        }
        XCTAssertTrue(try panelControl("printer.controls.bed.target", in: controller).isEnabled)
        XCTAssertEqual(service.setTemperaturesCallCount, 0, "Capability resolution cannot replay a command")
    }

    /// #3540/#3545: active jobs gate Tune, and Tune is Speed, Flow and babystep
    /// only. Heater targets never render during a job, even when the backend
    /// advertises temperature control during a print.
    func test_instrumentPanel_activeJobsGateTuneAndOmitHeatersRegardlessOfCapability() async throws {
        for (state, canTune) in [("starting", false), ("printing", true), ("paused", true)] {
            for supportsHeat in [false, true] {
                let printer = try makePrinter(backend: .moonraker, state: state)
                var caps = Self.layoutCaps
                caps.supportsTemperatureControlDuringPrint = supportsHeat
                caps.supportsSpeedFactorControl = true
                caps.supportsFlowFactorControl = true
                let service = makeService(caps: caps)
                let model = PrinterControlsViewModel.configuredForTests(printerService: service, printer: printer)
                await model.loadCapabilities()
                let (window, controller) = install(PrinterInstrumentPanelControls(printer: printer, viewModel: model))
                defer { window.isHidden = true; model.deactivate() }
                try await settle(controller)
                for segment in ["move", "heat"] {
                    XCTAssertFalse(try panelControl("printer.controls.segment.\(segment)", in: controller).isEnabled, state)
                }
                let tune = try XCTUnwrap(try panelControl("printer.controls.segment.tune", in: controller) as? UIButton)
                XCTAssertEqual(tune.isEnabled, canTune, state)
                XCTAssertEqual(tune.isSelected, canTune, state)
                if canTune {
                    for identifier in [
                        "printer.controls.runtime.speed-factor.increase", "printer.controls.runtime.speed-factor.decrease",
                        "printer.controls.runtime.flow-factor.increase", "printer.controls.runtime.flow-factor.decrease",
                        "printer.controls.runtime.z-offset.increase", "printer.controls.runtime.z-offset.decrease"
                    ] {
                        let control = try panelControl(identifier, in: controller)
                        XCTAssertFalse(control.accessibilityLabel?.isEmpty ?? true, "\(state) \(identifier)")
                    }
                }
                let heaterControls = ["hotend", "bed"].flatMap { heater in
                    ["target", "decrease", "increase"].map { "printer.controls.\(heater).\($0)" }
                } + ["printer.controls.heat.set-targets"]
                for identifier in heaterControls {
                    XCTAssertNil(
                        nativeControls(in: controller.view).first { $0.accessibilityIdentifier == identifier },
                        "\(state) heat=\(supportsHeat): \(identifier) must not render during an active job"
                    )
                }
                XCTAssertEqual(service.setTemperaturesCallCount, 0)
                XCTAssertNil(service.moveCalledWith)
                attachPanelReview(controller, scenario: "\(state)-tune-synthetic-all-controls-heat-\(supportsHeat)")
            }
        }
    }

    func test_instrumentPanel_nativeMoveTargetsFitAndDispatchAcrossAdaptiveLayouts() async throws {
        for (width, typeSize) in [(CGFloat(320), DynamicTypeSize.large), (390, .accessibility3), (1024, .large)] {
            let printer = try makePrinter(backend: .moonraker)
            let service = makeService(caps: Self.layoutCaps)
            let model = PrinterControlsViewModel.configuredForTests(printerService: service, printer: printer)
            await model.loadCapabilities()
            let content = PrinterInstrumentPanelControls(printer: printer, viewModel: model)
                .environment(\.dynamicTypeSize, typeSize)
                .frame(width: width)
            let (window, controller) = install(content)
            defer { window.isHidden = true; model.deactivate() }
            window.frame.size = controller.sizeThatFits(in: CGSize(width: width, height: 10000))
            controller.view.frame = window.bounds
            try await settle(controller)
            let identifiers = [
                "printer.controls.segment.move", "printer.controls.segment.heat", "printer.controls.segment.tune",
                "printer.controls.jog.x.positive", "printer.controls.jog.y.negative", "printer.controls.jog.z.positive",
                "printer.controls.home.all"
            ]
            for identifier in identifiers {
                let control = try panelControl(identifier, in: controller)
                let frame = control.convert(control.bounds, to: controller.view)
                XCTAssertEqual(control.isEnabled, identifier != "printer.controls.segment.tune", identifier)
                // Native hit bounds avoid coordinate-conversion roundoff at fractional origins.
                XCTAssertGreaterThanOrEqual(control.bounds.height, 44, identifier)
                XCTAssertGreaterThanOrEqual(control.bounds.width, 44, identifier)
                XCTAssertGreaterThanOrEqual(frame.minX, -1, identifier)
                XCTAssertLessThanOrEqual(frame.maxX, width + 1, identifier)
                XCTAssertFalse(control.accessibilityLabel?.isEmpty ?? true, identifier)
            }
            let jog = try panelControl("printer.controls.jog.z.positive", in: controller)
            XCTAssertEqual(jog.accessibilityLabel, "Move Z positive")
            try panelControl("printer.controls.jog.step.10", in: controller).sendActions(for: .touchUpInside)
            try await settle(controller)
            XCTAssertNil(service.moveCalledWith, "Selecting a distance must not move the printer")
            try panelControl("printer.controls.jog.z.positive", in: controller).sendActions(for: .touchUpInside)
            try await settle(controller, "explicit 10 mm Z jog") { service.moveCalledWith != nil }
            XCTAssertEqual(service.moveCalledWith?.printerId, printer.id)
            XCTAssertEqual(service.moveCalledWith?.axis, "Z")
            XCTAssertEqual(service.moveCalledWith?.distanceMm, 10)
            XCTAssertEqual(service.moveCalledWith?.feedrateMmMin, PrinterControlsViewModel.zFeedrateMmMin)
            attachPanelReview(controller, scenario: "idle-move-\(Int(width))-\(typeSize)")
        }
    }

    /// #3548: gated controls are hidden or disabled with no explanatory text.
    /// Walks the rendered accessibility labels and values (what sighted users
    /// see) while printing, paused, with a command pending, and offline.
    func test_instrumentPanel_rendersNoLockoutTextInAnyBlockedState() async throws {
        let barrier = AsyncBarrier()
        addTeardownBlock { barrier.close() }
        for scenario in ["printing", "paused", "pending", "offline"] {
            let printer = try makePrinter(
                backend: .moonraker,
                isOnline: scenario != "offline",
                state: scenario == "paused" ? "paused" : "printing"
            )
            var caps = Self.layoutCaps
            caps.supportsSpeedFactorControl = true
            caps.supportsFlowFactorControl = true
            let service = makeService(caps: caps)
            let model = PrinterControlsViewModel.configuredForTests(printerService: service, printer: printer)
            await model.loadCapabilities()
            let (window, controller) = install(PrinterInstrumentPanelControls(printer: printer, viewModel: model))
            defer { window.isHidden = true; model.deactivate() }
            var command: Task<Void, Never>?
            if scenario == "pending" {
                service.beforeSetRuntimeFactor = { await barrier.arriveAndWait() }
                command = Task { await model.setRuntimeFactor(.speed, percent: 110) }
                await barrier.waitUntilArrived()
                XCTAssertTrue(model.isExecuting, "pending fixture must hold a command in flight")
            }
            try await settle(controller)
            let text = VisibleText.collect(in: controller.view)
            if scenario != "offline" {
                XCTAssertFalse(text.isEmpty, "\(scenario): walker must see the rendered panel")
            }
            VisibleText.assertNoLockoutText(text, scenario)
            attachPanelReview(controller, scenario: "\(scenario)-no-lockout-text")
            if let command {
                barrier.release()
                await command.value
            }
        }
    }

    /// The production Control-page host shows no offline banner (#3548).
    func test_offlineControlPage_rendersNoLockoutBanner() async throws {
        let (window, controller) = install(PrinterDetailControlsUnavailable())
        defer { window.isHidden = true }
        try await settle(controller)
        let text = VisibleText.collect(in: controller.view)
        VisibleText.assertNoLockoutText(text, "offline-control-page")
        XCTAssertFalse(text.contains { $0.localizedCaseInsensitiveContains("unavailable") }, "\(text)")
        XCTAssertFalse(text.contains { $0.localizedCaseInsensitiveContains("reconnect") }, "\(text)")
    }

    private func panelControl(
        _ identifier: String, in controller: UIViewController,
        file: StaticString = #filePath, line: UInt = #line
    ) throws -> UIControl {
        try XCTUnwrap(
            nativeControls(in: controller.view).first { $0.accessibilityIdentifier == identifier },
            identifier, file: file, line: line
        )
    }

    private func attachPanelReview(_ controller: UIViewController, scenario: String) {
        let image = UIGraphicsImageRenderer(bounds: controller.view.bounds).image { _ in
            controller.view.drawHierarchy(in: controller.view.bounds, afterScreenUpdates: true)
        }
        let attachment = XCTAttachment(image: image)
        let size = controller.view.bounds.size
        attachment.name = "review-only-production-PrinterInstrumentPanelControls-mock-\(scenario)-\(Int(size.width))x\(Int(size.height))-\(UIDevice.current.userInterfaceIdiom)"
        attachment.lifetime = .keepAlways
        add(attachment)
    }

    func test_snapshot_printerDetailBorderedDestructiveControls_darkMode() {
        let controls = HStack(spacing: 12) {
            PrinterDetailBorderedDestructiveButton(kind: .eject, action: {})
            PrinterDetailBorderedDestructiveButton(kind: .cancel, action: {})
        }
        .padding()
        .frame(maxWidth: .infinity)

        let rootView = VStack {
            controls
            Spacer()
        }
        .background(Color.pfBackground)
        .preferredColorScheme(.dark)

        let hostingController = host(rootView)
        hostingController.overrideUserInterfaceStyle = .dark
        assertSnapshot(of: hostingController, as: .image(on: .iPhone13), named: snapshotName)
    }
}

/// Rendered text collected from the accessibility tree. Labels and values are
/// what appears on screen; hints are VoiceOver-only and intentionally skipped.
@MainActor
enum VisibleText {
    static let forbiddenFragments = [
        "Another printer operation", "Command pending", "Command in progress", "locked",
        "while printing", "while a print", "Printer is offline", "Controls are", "Unavailable while",
        "unavailable while", "Unavailable.", "permission is required", "Requires Queue.Start", "does not report support", "Confirming printer status",
        "Controls unavailable", "Reconnect to use", "must be online and idle"
    ]

    /// SwiftUI only publishes its accessibility tree once automation is enabled.
    private static let automationEnabled: Bool = {
        guard let handle = dlopen("/usr/lib/libAccessibility.dylib", RTLD_NOW),
              let symbol = dlsym(handle, "_AXSSetAutomationEnabled") else { return false }
        typealias SetEnabled = @convention(c) (Int32) -> Void
        unsafeBitCast(symbol, to: SetEnabled.self)(1)
        return true
    }()

    static func collect(in view: UIView) -> [String] {
        XCTAssertTrue(automationEnabled, "Accessibility automation must be enabled to read SwiftUI text")
        view.setNeedsLayout()
        view.layoutIfNeeded()
        var seen = Set<ObjectIdentifier>()
        var output: [String] = []
        func visit(_ object: NSObject) {
            guard seen.insert(ObjectIdentifier(object)).inserted else { return }
            if let view = object as? UIView, view.isHidden || view.alpha == 0 { return }
            for text in [object.accessibilityLabel, object.accessibilityValue] {
                if let text, !text.isEmpty { output.append(text) }
            }
            if let elements = object.accessibilityElements as? [NSObject] {
                elements.forEach(visit)
            } else {
                let count = object.accessibilityElementCount()
                if count != NSNotFound, count > 0 {
                    (0..<count).compactMap { object.accessibilityElement(at: $0) as? NSObject }.forEach(visit)
                }
            }
            (object as? UIView)?.subviews.forEach(visit)
        }
        visit(view)
        return output
    }

    static func assertNoLockoutText(
        _ text: [String], _ scenario: String,
        file: StaticString = #filePath, line: UInt = #line
    ) {
        for string in text {
            for fragment in forbiddenFragments where string.contains(fragment) {
                XCTFail("\(scenario): rendered lockout text \"\(string)\"", file: file, line: line)
            }
        }
    }
}
