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
        XCTAssertEqual(bed.stateLabel, "Bed clear?")
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

        XCTAssertEqual(presentation.stateLabel, "Bed clear?")
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
final class PrinterControlsSectionSnapshotTests: XCTestCase {

    // UIKit/SwiftUI rasterization differs between iPhone and iPad test hosts,
    // even with the same layout and an explicit display-scale override.
    private var snapshotName: String? {
        UIDevice.current.userInterfaceIdiom == .pad ? "iPad" : nil
    }

    // Synthetic capability evidence for layout/lifecycle tests, not backend profiles.
    private static let layoutCaps = PrinterBackendCapabilities.allControlsFixture

    func test_directControlsHaveNoTrackingOrRecoveryActions() async throws {
        let printer = try makePrinter(backend: .moonraker)
        let service = makeService(caps: Self.layoutCaps)
        let model = PrinterControlsViewModel.configuredForTests(printerService: service, printer: printer)
        await model.loadCapabilities()
        let (window, controller) = install(
            PrinterSetupControlsContent(printer: printer, viewModel: model)
                .frame(width: 390).fixedSize(horizontal: false, vertical: true)
        )
        defer { window.isHidden = true; model.deactivate() }
        try await settle(controller)
        let identifiers = nativeControls(in: controller.view).compactMap(\.accessibilityIdentifier)
        XCTAssertTrue(identifiers.contains("printer.controls.home.all"))
        for obsolete in ["details", "refresh", "recovery", "review-admission", "resubmit"] {
            XCTAssertFalse(identifiers.contains("printer.controls.motion.\(obsolete)"))
        }
    }

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
    /// `PrinterControlsSection.isHidden(for:)` returns `false`.
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

    func test_embeddedContent_mountAndRemount_doNotLoadOrDispatch() async throws {
        let printer = try makePrinter(backend: .octoPrint)
        let service = makeService(caps: Self.layoutCaps)
        let model = PrinterControlsViewModel.configuredForTests(printerService: service, printer: printer)
        let content = PrinterSetupControlsContent(printer: printer, viewModel: model)
        XCTAssertTrue(content.viewModel === model)
        let (window, controller) = install(content)
        defer { window.isHidden = true }
        try await settle(controller)
        controller.rootView = AnyView(EmptyView())
        try await settle(controller)
        controller.rootView = AnyView(PrinterSetupControlsContent(printer: printer, viewModel: model))
        try await settle(controller)

        XCTAssertNil(model.capabilities, "Embedded content must not start capability loading, even indirectly via Jog")
        XCTAssertNil(service.getBackendCapabilitiesCalledWith)
        XCTAssertNil(service.setTemperaturesCalledWith)
        XCTAssertNil(service.homeCalledWith)
        XCTAssertNil(service.moveCalledWith)
    }

    func test_recreatedContent_observesSharedRequestLockAndRelease() async throws {
        let printer = try makePrinter(backend: .octoPrint)
        let serverID = UUID()
        let originalService = makeService(caps: Self.layoutCaps)
        let original = PrinterControlsViewModel.configuredForTests(
            printerService: originalService, printer: printer, serverID: serverID
        )
        await original.loadCapabilities()
        let barrier = AsyncBarrier()
        addTeardownBlock { barrier.close() }
        originalService.beforeSetTemperatures = { await barrier.arriveAndWait() }
        let request = Task { await original.setHeaterTarget(.hotend, target: 200) }
        await barrier.waitUntilArrived()
        original.deactivate()

        let service = makeService(caps: Self.layoutCaps)
        let model = PrinterControlsViewModel.configuredForTests(
            printerService: service, printer: printer, serverID: serverID
        )
        await model.loadCapabilities()
        let (window, controller) = install(PrinterSetupControlsContent(printer: printer, viewModel: model))
        defer { window.isHidden = true }
        try await settle(controller)
        let controls = nativeControls(in: controller.view)
        let editor = try XCTUnwrap(controls.first { $0.accessibilityIdentifier == "printer.controls.hotend.target" })
        XCTAssertFalse(editor.isEnabled)
        XCTAssertTrue(model.blockedReason?.contains("Another controls view") == true)
        XCTAssertFalse(controls.contains { $0.accessibilityIdentifier == "printer.controls.stop-waiting" },
                       "A replacement must not offer a no-op Stop waiting for someone else's request")
        XCTAssertNil(service.setTemperaturesCalledWith)
        barrier.release()
        await request.value
        try await settle(controller)
        let releasedEditor = try XCTUnwrap(nativeControls(in: controller.view).first {
            $0.accessibilityIdentifier == "printer.controls.hotend.target"
        })
        XCTAssertTrue(releasedEditor.isEnabled, "Shared lease changes must invalidate the replacement's observed UI")
        XCTAssertNil(model.blockedReason)
        XCTAssertNil(model.commandNotice, "Old owner outcomes must not be presented as replacement outcomes")
    }

    func test_embeddedContent_remount_preservesPendingAndErrorOnExternalOwner() async throws {
        let printer = try makePrinter(backend: .octoPrint)
        let service = makeService(caps: Self.layoutCaps)
        let model = PrinterControlsViewModel.configuredForTests(printerService: service, printer: printer)
        await model.loadCapabilities()
        let gate = AsyncBarrier()
        defer { gate.close() }
        service.beforeMotion = { await gate.arriveAndWait() }
        let command = Task { await model.jog(axis: "X", distanceMm: 10) }
        await gate.waitUntilArrived()
        let pending = try XCTUnwrap(model.pendingCommand)
        service.getBackendCapabilitiesCalledWith = nil
        service.moveCalledWith = nil
        let (window, controller) = install(PrinterSetupControlsContent(printer: printer, viewModel: model))
        defer { window.isHidden = true }
        try await settle(controller)
        controller.rootView = AnyView(EmptyView())
        try await settle(controller)
        XCTAssertEqual(model.pendingCommand, pending)
        controller.rootView = AnyView(PrinterSetupControlsContent(printer: printer, viewModel: model))
        try await settle(controller)
        XCTAssertEqual(model.pendingCommand, pending, "Embedding again must retain the same in-flight command")
        controller.rootView = AnyView(EmptyView())
        try await settle(controller)

        // The external owner receives updates while its presentation is absent.
        var offline = printer
        offline.isOnline = false
        model.handlePrinterUpdate(offline)
        XCTAssertNotNil(model.pendingCommand, "Offline presentation cannot recall an in-flight command")
        gate.release()
        await command.value
        XCTAssertNil(model.pendingCommand)
        model.handlePrinterUpdate(printer)
        service.errorToThrow = NetworkError.timeout
        await model.preheat(.pla)
        let error = try XCTUnwrap(model.lastError)

        controller.rootView = AnyView(PrinterSetupControlsContent(printer: printer, viewModel: model))
        try await settle(controller)
        XCTAssertEqual(model.lastError, error)
        XCTAssertEqual(model.capabilities, Self.layoutCaps)
        XCTAssertNil(service.getBackendCapabilitiesCalledWith)
        XCTAssertNil(service.moveCalledWith, "Remounting must not replay a pending command")

        model.dismissError()
        service.errorToThrow = nil
        await model.preheat(.pla)
        XCTAssertNil(model.lastError)
        XCTAssertEqual(service.setTemperaturesCalledWith?.hotend, 200)
    }

    func test_standaloneOwner_forwardsOfflineSnapshotOutsideHiddenContent() async throws {
        let printer = try makePrinter(backend: .octoPrint)
        let service = makeService(caps: Self.layoutCaps)
        let model = PrinterControlsViewModel.configuredForTests(printerService: service, printer: printer)
        await model.loadCapabilities()
        let (window, controller) = install(PrinterControlsSection(printer: printer, viewModel: model))
        defer { window.isHidden = true }
        try await settle(controller)
        let gate = AsyncBarrier()
        defer { gate.close() }
        service.beforeMotion = { await gate.arriveAndWait() }
        let command = Task { await model.jog(axis: "X", distanceMm: 10) }
        await gate.waitUntilArrived()
        XCTAssertNotNil(model.pendingCommand)

        var offline = printer
        offline.isOnline = false
        controller.rootView = AnyView(PrinterControlsSection(printer: offline, viewModel: model))
        try await settle(controller)

        XCTAssertNotNil(model.pendingCommand, "A hidden control still owns its outstanding direct request")
        XCTAssertFalse(model.printer.isOnline)
        XCTAssertFalse(model.canControl)
        gate.release()
        await command.value
        XCTAssertNil(model.pendingCommand)
    }

    func test_standaloneOwner_redraw_retainsInstalledModelAndTargetCorrelation() async throws {
        var printer = try makePrinter(backend: .moonraker)
        printer.hotendTarget = 215
        let service = makeService(caps: Self.layoutCaps)
        let model = PrinterControlsViewModel.configuredForTests(printerService: service, printer: printer)
        await model.loadCapabilities()
        let (window, controller) = install(PrinterControlsSection(printer: printer, viewModel: model))
        defer { window.isHidden = true }
        try await settle(controller)
        await model.preheat(.pla)
        let pending = try XCTUnwrap(model.pendingCommand)

        let replacement = PrinterControlsViewModel.configuredForTests(printerService: service, printer: printer)
        var noisy = printer
        noisy.hotendTemp = 199
        controller.rootView = AnyView(PrinterControlsSection(printer: noisy, viewModel: replacement))
        try await settle(controller)
        XCTAssertEqual(model.pendingCommand, pending)
        XCTAssertEqual(model.printer.hotendTemp, 199, "Redraw must update the installed owner, not the new initializer argument")
        XCTAssertNil(replacement.capabilities)

        noisy.hotendTarget = 200
        noisy.bedTarget = 60
        controller.rootView = AnyView(PrinterControlsSection(printer: noisy, viewModel: replacement))
        try await settle(controller)
        XCTAssertNil(model.pendingCommand)
        XCTAssertEqual(model.printer.hotendTarget, 200)
    }

    func test_embeddedLoadedLimitedAxes_enablesOnlySupportedDirections() async throws {
        let printer = try makePrinter(backend: .moonraker)
        let caps = PrinterBackendCapabilities(
            supportsMovement: true,
            supportsTemperatureControl: true,
            supportsBedTemperature: true,
            supportsFanControl: true,
            supportsHoming: true,
            supportedAxes: ["Y", "Z"]
        )
        let service = makeService(caps: caps)
        let model = PrinterControlsViewModel.configuredForTests(printerService: service, printer: printer)
        await model.loadCapabilities()
        service.getBackendCapabilitiesCalledWith = nil
        let (window, controller) = install(PrinterSetupControlsContent(printer: printer, viewModel: model))
        defer { window.isHidden = true }
        try await settle(controller)

        window.frame.size.height = 3000
        controller.view.frame = window.bounds
        try await settle(controller)
        let controls = nativeControls(in: controller.view)
        let y = try XCTUnwrap(controls.compactMap { $0 as? UIButton }.first {
            $0.accessibilityIdentifier == "printer.controls.jog.y.positive"
        })
        XCTAssertTrue(y.isEnabled)
        XCTAssertEqual(y.accessibilityLabel, "Move Y positive")
        XCTAssertGreaterThanOrEqual(y.bounds.height, 44)
        XCTAssertFalse(try XCTUnwrap(controls.first {
            $0.accessibilityIdentifier == "printer.controls.jog.x.positive"
        }).isEnabled)
        XCTAssertNil(service.getBackendCapabilitiesCalledWith)
        XCTAssertNil(service.moveCalledWith)
    }

    func test_embeddedContent_largeType_reflowsAtPhoneAndTabletWidths() async throws {
        let printer = try makePrinter(backend: .moonraker, state: "paused")
        let service = makeService(caps: Self.layoutCaps)
        let model = PrinterControlsViewModel.configuredForTests(printerService: service, printer: printer)
        await model.loadCapabilities()
        for width: CGFloat in [390, 1024] {
            let content = PrinterSetupControlsContent(printer: printer, viewModel: model)
                .environment(\.horizontalSizeClass, width == 390 ? .compact : .regular)
            let standard = UIHostingController(rootView: content.environment(\.dynamicTypeSize, .large))
            let accessible = UIHostingController(rootView: content.environment(\.dynamicTypeSize, .accessibility3))
            let proposedSize = CGSize(width: width, height: 10_000)
            let normalSize = standard.sizeThatFits(in: proposedSize)
            let largeSize = accessible.sizeThatFits(in: proposedSize)
            XCTAssertGreaterThan(largeSize.height, normalSize.height)
            XCTAssertLessThanOrEqual(largeSize.width, width)
        }
        XCTAssertFalse(model.canControl)
        XCTAssertEqual(PreheatSubgroup(viewModel: model).accessibilityLabel(preset: .pla, isPending: false), "Preheat for PLA")
        XCTAssertEqual(JogSubgroup(viewModel: model).jogAccessibilityLabel(direction: 1), "Jog forward")
        XCTAssertNil(service.moveCalledWith)
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

    func test_essentialControls_phoneOrderTabletColumnsAndDisclosedAbsoluteMove() async throws {
        let printer = try makePrinter(backend: .moonraker)
        var caps = Self.layoutCaps
        caps.supportsAbsoluteMovement = true
        let service = makeService(caps: caps)
        let model = PrinterControlsViewModel.configuredForTests(printerService: service, printer: printer)
        await model.loadCapabilities()
        for width: CGFloat in [390, 1024] {
            let content = PrinterSetupControlsContent(printer: printer, viewModel: model, usesColumns: width >= 760)
                .environment(\.dynamicTypeSize, .large)
                .frame(width: width).fixedSize(horizontal: false, vertical: true)
            let (window, controller) = install(content)
            defer { window.isHidden = true }
            window.frame.size = controller.sizeThatFits(in: CGSize(width: width, height: 10000))
            controller.view.frame = window.bounds
            try await settle(controller)
            func control(_ id: String) throws -> UIControl {
                try XCTUnwrap(nativeControls(in: controller.view).first { $0.accessibilityIdentifier == id }, id)
            }
            func frame(_ id: String) throws -> CGRect {
                let value = try control(id)
                return value.convert(value.bounds, to: controller.view)
            }
            let heat = try frame("printer.controls.heat.set-targets")
            let motion = try frame("printer.controls.home.all")
            let material = try frame("printer.controls.filament-load")
            if width < 760 {
                XCTAssertLessThan(heat.maxY, motion.minY)
                XCTAssertLessThan(motion.maxY, material.minY)
            } else {
                XCTAssertLessThan(heat.maxX, motion.minX)
                XCTAssertLessThan(material.maxX, motion.minX)
                XCTAssertLessThan(heat.maxY, material.minY)
            }
            let hotend = try frame("printer.controls.hotend.target")
            let bed = try frame("printer.controls.bed.target")
            XCTAssertEqual(hotend.maxY, bed.maxY, accuracy: 1)
            XCTAssertLessThan(hotend.maxX, bed.minX)
            XCTAssertEqual(bed.maxY, heat.maxY, accuracy: 1)
            XCTAssertLessThan(bed.maxX, heat.minX)
            XCTAssertFalse(nativeControls(in: controller.view).contains {
                ["printer.controls.hotend.set", "printer.controls.bed.set",
                 "printer.controls.hotend.off", "printer.controls.bed.off"].contains($0.accessibilityIdentifier ?? "")
            })
            let left = try frame("printer.controls.jog.x.negative")
            let right = try frame("printer.controls.jog.x.positive")
            let up = try frame("printer.controls.jog.y.positive")
            let down = try frame("printer.controls.jog.y.negative")
            XCTAssertLessThan(left.maxX, right.minX)
            XCTAssertEqual(left.midY, right.midY, accuracy: 1)
            XCTAssertLessThan(up.maxY, down.minY)
            XCTAssertEqual(up.midX, down.midX, accuracy: 1)
            let homeAll = try frame("printer.controls.home.all")
            let homeXY = try frame("printer.controls.home.xy")
            let homeZ = try frame("printer.controls.home.z")
            let zUp = try frame("printer.controls.jog.z.positive")
            let zDown = try frame("printer.controls.jog.z.negative")
            XCTAssertEqual(homeAll.midY, up.midY, accuracy: 1)
            XCTAssertEqual(homeAll.midX, left.midX, accuracy: 1)
            XCTAssertEqual(homeXY.midX, up.midX, accuracy: 1)
            XCTAssertEqual(homeXY.midY, left.midY, accuracy: 1)
            XCTAssertEqual(homeZ.midX, zUp.midX, accuracy: 1)
            XCTAssertEqual(homeZ.midY, homeXY.midY, accuracy: 1)
            XCTAssertEqual(zUp.midY, up.midY, accuracy: 1)
            XCTAssertEqual(zDown.midY, down.midY, accuracy: 1)
            XCTAssertLessThan(homeXY.maxX, homeZ.minX)
            for value in nativeControls(in: controller.view) {
                XCTAssertGreaterThanOrEqual(value.bounds.width, 44, value.accessibilityIdentifier ?? "")
                XCTAssertGreaterThanOrEqual(value.bounds.height, 44, value.accessibilityIdentifier ?? "")
            }
            try await expandAbsolute(controller)
            XCTAssertNotNil(try control("printer.controls.absolute.x"))
        }
        XCTAssertNil(service.moveCalledWith)
        XCTAssertNil(service.moveToCalledWith)
        XCTAssertNil(service.setTemperaturesCalledWith)
    }

    func test_essentialPrototype_matchedScrollViewportsAndNativeControlMetrics() async throws {
        let printer = try makePrinter(backend: .moonraker)
        let service = makeService(caps: Self.essentialLayoutCaps)
        service.statusToReturn = VerifiedSafetyFixtures.status(id: printer.id)
        let model = PrinterControlsViewModel.configuredForTests(printerService: service, printer: printer)
        await model.loadCapabilities()
        let actions = PrinterDetailFilamentActionMapping.actions(
            printerID: printer.id, hasActiveSpool: true, isPerformingAction: false, nfcAvailable: true
        )
        let presentation = PrinterFilamentPresentation(
            printer: printer, toolheads: [], spool: printer.spoolInfo, coverage: nil,
            coverageState: .unavailable, isStale: false,
            supportedActions: PrinterDetailFilamentActionMapping.supportedActions(hasActiveSpool: true)
        )
        // Essential HTML at 1320px, with the owner's compact Go refinement.
        for size in [CGSize(width: 386, height: 612), CGSize(width: 1068, height: 650)] {
            let tablet = size.width > 760
            let content = ScrollView {
                PrinterSetupControlsContent(
                    printer: printer, viewModel: model, usesColumns: tablet,
                    materialPresentation: presentation, materialActions: actions
                )
                .padding(.horizontal, tablet ? 24 : 16)
                .padding(.top, 16).padding(.bottom, tablet ? 24 : 22)
            }
            .background(Color.pfBackgroundTertiary)
            .environment(\.dynamicTypeSize, .large)
            .environment(\.colorScheme, .light)
            .frame(width: size.width, height: size.height).ignoresSafeArea()
            let (window, controller) = install(content)
            defer { window.isHidden = true }
            window.frame.size = size
            controller.view.frame = window.bounds
            try await settle(controller)
            let native = nativeControls(in: controller.view)
            func frame(_ id: String) throws -> CGRect {
                let item = try XCTUnwrap(native.first { $0.accessibilityIdentifier == id }, id)
                return item.convert(item.bounds, to: controller.view)
            }
            let hotend = try frame("printer.controls.hotend.target")
            let set = try frame("printer.controls.heat.set-targets")
            XCTAssertEqual(hotend.minX, tablet ? 42 : 34, accuracy: 1)
            XCTAssertEqual(set.minX, tablet ? 475.703125 : 300, accuracy: 1)
            // Both revised prototype and native inputs have 45-point heights.
            XCTAssertEqual(hotend.minY, 254.0, accuracy: 2)
            XCTAssertEqual(set.minY, hotend.minY, accuracy: 1)
            XCTAssertEqual(set.height, hotend.height, accuracy: 1)
            XCTAssertEqual(set.width, 52, accuracy: 0.5)
            let up = try frame("printer.controls.jog.y.positive")
            let down = try frame("printer.controls.jog.y.negative")
            XCTAssertEqual(up.height, 48, accuracy: 1)
            XCTAssertEqual(up.minY, tablet ? 166.09375 : 579.6666666666666, accuracy: 4)
            XCTAssertEqual(down.minY - up.minY, 108, accuracy: 1)
            let motors = try frame("printer.controls.disable-motors")
            let calibration = try frame("printer.controls.calibration-start")
            XCTAssertEqual(motors.minY, calibration.minY, accuracy: 1)
            XCTAssertLessThan(motors.maxX, calibration.minX)
            let step = try XCTUnwrap(native.first {
                $0.accessibilityIdentifier == "printer.controls.jog.step.10"
            } as? UIButton)
            XCTAssertEqual(try XCTUnwrap(step.titleLabel?.font).pointSize, 13, accuracy: 0.1)
            for control in native {
                XCTAssertGreaterThanOrEqual(control.bounds.height, 44, control.accessibilityIdentifier ?? "")
                XCTAssertGreaterThanOrEqual(control.bounds.width, 44, control.accessibilityIdentifier ?? "")
                if let id = control.accessibilityIdentifier {
                    print("ESSENTIAL_METRIC width=\(size.width) id=\(id) frame=\(control.convert(control.bounds, to: controller.view))")
                }
            }
            func scrollView(_ view: UIView) -> UIScrollView? {
                (view as? UIScrollView) ?? view.subviews.lazy.compactMap { scrollView($0) }.first
            }
            let scroll = try XCTUnwrap(scrollView(controller.view))
            print("ESSENTIAL_CONTENT width=\(size.width) height=\(scroll.contentSize.height)")
            let offsets: [CGFloat] = tablet ? [0, 420] : [0, 470, 1060]
            for (page, offset) in offsets.enumerated() {
                scroll.setContentOffset(CGPoint(x: 0, y: min(offset, max(0, scroll.contentSize.height - size.height))), animated: false)
                try await settle(controller)
                let image = UIGraphicsImageRenderer(bounds: controller.view.bounds).image { _ in
                    XCTAssertTrue(controller.view.drawHierarchy(in: controller.view.bounds, afterScreenUpdates: true))
                }
                let attachment = XCTAttachment(image: image)
                attachment.name = "review-only-legacy-PrinterSetupControlsContent-mock-\(Int(size.width))x\(Int(size.height))-page\(page)"
                attachment.lifetime = .keepAlways
                add(attachment)
            }
        }
        XCTAssertNil(service.setTemperaturesCalledWith)
        XCTAssertNil(service.moveCalledWith)
        XCTAssertTrue(service.physicalFilamentCalls.isEmpty)
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

    func test_essentialCompletePageIncludesSharedIdentityAndAdaptiveSelector() async throws {
        let printer = try makePrinter(backend: .moonraker)
        let service = makeService(caps: Self.essentialLayoutCaps)
        service.statusToReturn = VerifiedSafetyFixtures.status(id: printer.id)
        let model = PrinterControlsViewModel.configuredForTests(printerService: service, printer: printer)
        await model.loadCapabilities()
        for size in [CGSize(width: 390, height: 844), CGSize(width: 1068, height: 850)] {
            let tablet = size.width >= 760
            let content = PrinterDetailPanelsHost(
                selection: .constant(.control), controlsAvailable: true, printer: printer,
                status: { Text("Status fixture") },
                control: {
                    ScrollView {
                        PrinterSetupControlsContent(printer: printer, viewModel: model, usesColumns: tablet)
                            .padding(.horizontal, tablet ? 24 : 16)
                            .padding(.vertical, 16)
                    }
                    .background(Color.pfBackgroundTertiary)
                },
                filament: { Color.clear },
                queue: { Color.clear }
            )
            .safeAreaInset(edge: .top, spacing: 0) {
                PrinterRunActionBar(
                    presentation: PrinterDetailRunActionMapping.presentation(
                        isOnline: true, isPrinting: false, isPaused: false, isPerformingAction: false
                    ).emergencyAction,
                    onSelect: { _ in XCTFail("Visual comparison must not dispatch commands") }
                )
                .frame(maxWidth: .infinity, alignment: .trailing)
                .padding(.horizontal).padding(.vertical, 4)
            }
            .environment(\.dynamicTypeSize, .large)
            .environment(\.colorScheme, .light)
            .frame(width: size.width, height: size.height).ignoresSafeArea()
            let (window, controller) = install(content)
            defer { window.isHidden = true }
            window.frame.size = size
            controller.view.frame = window.bounds
            try await settle(controller)
            let selector = try XCTUnwrap(nativeControls(in: controller.view).compactMap {
                $0 as? UISegmentedControl
            }.first)
            XCTAssertEqual(selector.selectedSegmentIndex, 1)
            XCTAssertEqual(selector.bounds.width, size.width - (tablet ? 48 : 32), accuracy: 1)
            XCTAssertGreaterThanOrEqual(selector.bounds.height, 44)
            XCTAssertNotNil(nativeControls(in: controller.view).first {
                $0.accessibilityIdentifier == "printer.controls.disable-motors"
            })
            let image = UIGraphicsImageRenderer(bounds: controller.view.bounds).image { _ in
                XCTAssertTrue(controller.view.drawHierarchy(in: controller.view.bounds, afterScreenUpdates: true))
            }
            let attachment = XCTAttachment(image: image)
            attachment.name = "review-only-legacy-PrinterSetupControlsContent-in-panels-host-mock-\(Int(size.width))-\(snapshotName ?? "iPhone")"
            attachment.lifetime = .keepAlways
            add(attachment)
        }
        XCTAssertNil(service.moveCalledWith)
        XCTAssertNil(service.setTemperaturesCalledWith)
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

    func test_obsoleteDetailsAndSafetyEntryPointsAreNotReachable() async throws {
        let printer = try makePrinter(backend: .moonraker)
        let service = makeService(caps: Self.layoutCaps)
        let model = PrinterControlsViewModel.configuredForTests(printerService: service, printer: printer)
        await model.loadCapabilities()
        let (window, controller) = install(PrinterSetupControlsContent(
            printer: printer,
            viewModel: model
        ))
        defer { window.isHidden = true }
        try await settle(controller)
        XCTAssertFalse(nativeControls(in: controller.view).contains {
            [
                "printer.controls.calibration.details",
                "printer.controls.refresh-safety"
            ].contains($0.accessibilityIdentifier)
        }, "Obsolete Details and Safety controls must not be reachable")
        XCTAssertNotNil(
            nativeControls(in: controller.view).first {
                $0.accessibilityIdentifier == "printer.controls.calibration-start"
            },
            "Removing obsolete surfaces must preserve calibration"
        )
        XCTAssertNotNil(
            nativeControls(in: controller.view).first {
                $0.accessibilityIdentifier == "printer.controls.extrude"
            },
            "Removing obsolete surfaces must preserve material controls"
        )

        // Verify active workflow behaviour when calibration is started
        XCTAssertNotNil(model.calibrationBlockedReason)
        await model.startCalibration()
        try await settle(controller)
        XCTAssertNotNil(model.calibrationStep)
        XCTAssertNotNil(model.calibrationBlockedReason)
        XCTAssertNil(service.homeCalledWith)
        XCTAssertNil(service.saveZOffsetCalledWith)
    }

    func test_unsupportedControlsStayDisabledInTheSamePositions() async throws {
        let printer = try makePrinter(backend: .moonraker)
        var unsupported = PrinterBackendCapabilities(
            supportsMovement: false, supportsTemperatureControl: false,
            supportsBedTemperature: false, supportsFanControl: false,
            supportsHoming: false, supportedAxes: []
        )
        // Cool down is hidden (not disabled) without heater shutdown; keep it so
        // this test compares only controls that disable in place.
        unsupported.supportsHeaterShutdown = Self.essentialLayoutCaps.supportsHeaterShutdown
        let identifiers = [
            "hotend.target", "bed.target", "heat.set-targets", "jog.step.10",
            "jog.x.positive", "jog.x.negative", "jog.y.positive", "jog.y.negative",
            "jog.z.positive", "jog.z.negative", "home.all", "home.xy", "home.z",
            "disable-motors", "calibration-start",
            "filament-load", "filament-unload", "filament-change",
            "extrusion-distance", "extrusion-speed", "extrude", "retract"
        ]
        for width: CGFloat in [390, 1024] {
            for size in [DynamicTypeSize.large, .accessibility3] {
                var supportedFrames: [String: CGRect] = [:]
                var supportedIdentifiers: Set<String> = []
                for (supported, caps) in [(true, Self.essentialLayoutCaps), (false, unsupported)] {
                    let service = makeService(caps: caps)
                    service.statusToReturn = VerifiedSafetyFixtures.status(id: printer.id)
                    let model = PrinterControlsViewModel.configuredForTests(printerService: service, printer: printer)
                    await model.loadCapabilities()
                    let content = PrinterSetupControlsContent(printer: printer, viewModel: model, usesColumns: width >= 760)
                        .environment(\.dynamicTypeSize, size)
                        .frame(width: width).fixedSize(horizontal: false, vertical: true)
                    let (window, controller) = install(content)
                    defer { window.isHidden = true }
                    window.frame.size = controller.sizeThatFits(in: CGSize(width: width, height: 10000))
                    controller.view.frame = window.bounds
                    try await settle(controller)
                    // Content can grow while settling; a stale window would center it and shift every row.
                    window.frame.size = controller.sizeThatFits(in: CGSize(width: width, height: 10000))
                    controller.view.frame = window.bounds
                    try await settle(controller)
                    let controls = nativeControls(in: controller.view)
                    let visibleIdentifiers = Set(controls.compactMap(\.accessibilityIdentifier))
                    let hotend = try XCTUnwrap(controls.first {
                        $0.accessibilityIdentifier == "printer.controls.hotend.target"
                    })
                    for suffix in ["bed.target", "heat.set-targets", "extrusion-distance", "extrusion-speed"] {
                        let control = try XCTUnwrap(controls.first {
                            $0.accessibilityIdentifier == "printer.controls.\(suffix)"
                        })
                        XCTAssertEqual(control.bounds.height, hotend.bounds.height, accuracy: 1, suffix)
                    }
                    if supported {
                        supportedIdentifiers = visibleIdentifiers
                    } else {
                        let expectedIdentifiers = supportedIdentifiers.subtracting([
                            "printer.controls.absolute.help",
                            "printer.controls.absolute.x",
                            "printer.controls.absolute.y",
                            "printer.controls.absolute.z",
                            "printer.controls.absolute.move"
                        ])
                        XCTAssertEqual(visibleIdentifiers, expectedIdentifiers)
                    }
                    for suffix in identifiers {
                        let id = "printer.controls.\(suffix)"
                        let control = try XCTUnwrap(controls.first { $0.accessibilityIdentifier == id }, id)
                        let frame = control.convert(control.bounds, to: controller.view)
                        if supported {
                            supportedFrames[id] = frame
                        } else {
                            XCTAssertFalse(control.isEnabled, id)
                            let expected = try XCTUnwrap(supportedFrames[id])
                            XCTAssertEqual(frame.minX, expected.minX, accuracy: 1, id)
                            XCTAssertEqual(frame.width, expected.width, accuracy: 1, id)
                            XCTAssertEqual(frame.height, expected.height, accuracy: 1, id)
                            if !["disable-motors", "calibration-start", "filament-load", "filament-unload", "filament-change", "extrusion-distance", "extrusion-speed", "extrude", "retract"].contains(suffix) {
                                XCTAssertEqual(frame.minY, expected.minY, accuracy: 1, id)
                            }
                        }
                    }
                    XCTAssertNil(service.setTemperaturesCalledWith)
                    XCTAssertNil(service.moveCalledWith)
                    XCTAssertNil(service.homeCalledWith)
                    XCTAssertNil(service.disableMotorsCalledWith)
                    XCTAssertNil(service.saveZOffsetCalledWith)
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

    func test_essentialMotionFeedback_pendingFailureAndConfirmationStayInCard() async throws {
        for textSize in [DynamicTypeSize.large, .accessibility3] {
            var printer = try makePrinter(backend: .octoPrint)
            printer.homedAxes = ""
            let service = makeService(caps: Self.layoutCaps)
            let model = PrinterControlsViewModel.configuredForTests(printerService: service, printer: printer)
            await model.loadCapabilities()
            let content = PrinterSetupControlsContent(printer: printer, viewModel: model, usesColumns: false)
                .environment(\.dynamicTypeSize, textSize)
                .frame(width: 390).fixedSize(horizontal: false, vertical: true)
            let (window, controller) = install(content)
            defer { window.isHidden = true }

            func layout() async throws {
                try await settle(controller)
                window.frame.size = controller.sizeThatFits(in: CGSize(width: 390, height: 10000))
                controller.view.frame = window.bounds
                try await settle(controller)
            }
            func button(_ id: String) throws -> UIButton {
                try XCTUnwrap(nativeControls(in: controller.view).first {
                    $0.accessibilityIdentifier == "printer.controls.\(id)"
                } as? UIButton)
            }
            func assertMotionLocal(_ feedback: UIView) throws {
                let heat = try button("heat.set-targets").convert(try button("heat.set-targets").bounds, to: controller.view)
                let home = try button("home.all").convert(try button("home.all").bounds, to: controller.view)
                let material = try button("filament-load").convert(try button("filament-load").bounds, to: controller.view)
                let frame = feedback.convert(feedback.bounds, to: controller.view)
                XCTAssertGreaterThan(frame.minY, heat.maxY)
                XCTAssertLessThan(frame.maxY, home.minY, "Feedback belongs beneath the motion heading, not below the page")
                XCTAssertLessThan(frame.maxY, material.minY)
            }
            func capture(_ name: String) async throws {
                // Retain a real phone viewport, not a several-thousand-point
                // accessibility layout that exceeds the render server limit.
                let (captureWindow, captureController) = install(
                    ScrollView { PrinterMotionControls(viewModel: model).padding(18) }
                        .environment(\.dynamicTypeSize, textSize)
                        .frame(width: 390, height: 844)
                )
                defer {
                    captureWindow.isHidden = true
                    window.makeKeyAndVisible()
                }
                try await settle(captureController)
                let image = UIGraphicsImageRenderer(bounds: captureController.view.bounds).image { _ in
                    XCTAssertTrue(captureController.view.drawHierarchy(in: captureController.view.bounds, afterScreenUpdates: true))
                }
                let attachment = XCTAttachment(image: image)
                attachment.name = "review-only-legacy-PrinterSetupControlsContent-command-feedback-mock-\(name)-\(textSize)-\(snapshotName ?? "iPhone")"
                attachment.lifetime = .keepAlways
                add(attachment)
            }

            try await layout()
            for id in ["home.all", "home.xy", "home.z", "jog.x.positive"] {
                let gate = AsyncBarrier()
                defer { gate.close() }
                service.beforeMotion = { await gate.arriveAndWait() }
                let origin = try button(id)
                origin.sendActions(for: .touchUpInside)
                await gate.waitUntilArrived()
                try await layout()
                XCTAssertNotNil(model.pendingCommand)
                XCTAssertTrue(origin.subviews.contains {
                    ($0 as? UIActivityIndicatorView)?.isAnimating == true && !$0.isHidden
                }, "Pending must be visible, not only an accessibility value")
                XCTAssertFalse(origin.isEnabled)
                XCTAssertFalse(nativeControls(in: controller.view).contains {
                    $0.accessibilityIdentifier == "printer.controls.stop-waiting"
                }, "Pending is silent: no Stop waiting button (#3496)")
                try await capture(id)
                model.cancelPendingCommand()
                try await layout()
                XCTAssertNotNil(model.pendingCommand, "Stopping observation cannot release an in-flight request")
                gate.release()
                service.beforeMotion = nil
                try await layout()
                XCTAssertFalse(origin.subviews.contains { ($0 as? UIActivityIndicatorView)?.isAnimating == true })
                XCTAssertTrue(model.commandNotice?.contains("Physical outcome is unknown") == true)
            }

            service.errorToThrow = NetworkError.timeout
            try button("home.xy").sendActions(for: .touchUpInside)
            try await layout()
            XCTAssertNotNil(model.lastError)
            XCTAssertFalse(try button("home.xy").subviews.contains { ($0 as? UIActivityIndicatorView)?.isAnimating == true })
            let dismiss = try button("dismiss-error")
            try assertMotionLocal(dismiss)
            try await capture("failed")
            dismiss.sendActions(for: .touchUpInside)
            try await layout()
            XCTAssertNil(model.lastError)

            service.errorToThrow = nil
            try button("home.xy").sendActions(for: .touchUpInside)
            try await layout()
            printer.homedAxes = "xy"
            model.handlePrinterUpdate(printer)
            try await layout()
            XCTAssertNil(model.pendingCommand)
            XCTAssertEqual(model.feedbackSection, .motion)
            XCTAssertNil(model.commandNotice, "Success is silent (#3496)")
            XCTAssertFalse(try button("home.xy").subviews.contains { ($0 as? UIActivityIndicatorView)?.isAnimating == true })
            try await capture("confirmed")
        }
    }

    func test_essentialFeedback_heatAndMaterialUseTheirOwnCards() async throws {
        var printer = try makePrinter(backend: .moonraker)
        printer.hotendTemp = 220
        let service = makeService(caps: Self.essentialLayoutCaps)
        let now = Date()
        service.statusToReturn = VerifiedSafetyFixtures.status(id: printer.id, at: now)
        let model = PrinterControlsViewModel.configuredForTests(printerService: service, printer: printer, clock: { now })
        await model.loadCapabilities()
        XCTAssertNil(model.extrusionBlockedReason)
        let barrier = AsyncBarrier()
        addTeardownBlock { barrier.close() }
        service.beforeExtrude = { await barrier.arriveAndWait() }
        let content = PrinterSetupControlsContent(printer: printer, viewModel: model, usesColumns: false)
            .frame(width: 390).fixedSize(horizontal: false, vertical: true)
        let (window, controller) = install(content)
        defer { window.isHidden = true }

        for section in [ControlCommand.Section.heat, .material] {
            let request: Task<Void, Never>?
            if section == .heat {
                await model.setHeaterTarget(.hotend, target: 230)
                request = nil
            } else {
                request = Task { await model.extrude(distanceMm: 10, speedMmPerSecond: 1) }
                await barrier.waitUntilArrived()
            }
            try await settle(controller)
            window.frame.size = controller.sizeThatFits(in: CGSize(width: 390, height: 10000))
            controller.view.frame = window.bounds
            try await settle(controller)
            let controls = nativeControls(in: controller.view)
            XCTAssertFalse(controls.contains { $0.accessibilityIdentifier == "printer.controls.stop-waiting" },
                           "Pending is silent: no Stop waiting button (#3496)")
            let originID = section == .heat ? "heat.set-targets" : "filament-load"
            let origin = try XCTUnwrap(controls.first { $0.accessibilityIdentifier == "printer.controls.\(originID)" })
            XCTAssertFalse(origin.isEnabled, "The originating control stays disabled while in flight")
            if section == .material {
                barrier.release()
                await request?.value
                XCTAssertNil(model.pendingCommand)
                XCTAssertEqual(model.feedbackSection, .material)
            }
            model.cancelPendingCommand()
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

    func test_individualControls_phoneFullContentEvidence() async throws {
        try await captureIndividualControls(width: 390, dynamicType: .large, name: "phone")
    }

    func test_unknownLimits_editorDispatchesNativeHeatingAndZeroWithoutReplayOnRetry() async throws {
        let printer = try makePrinter(backend: .octoPrint)
        var caps = Self.layoutCaps
        caps.supportsAbsoluteMovement = true
        caps.verifiedSafety = VerifiedSafetyFixtures.discovery()
        let service = makeService(caps: caps)
        service.statusToReturn = VerifiedSafetyFixtures.status(id: printer.id)
        service.detailsToReturn = nil
        let model = PrinterControlsViewModel.configuredForTests(printerService: service, printer: printer)
        await model.loadCapabilities()
        let content = PrinterSetupControlsContent(printer: printer, viewModel: model)
            .frame(width: 390).fixedSize(horizontal: false, vertical: true)
        let (window, controller) = install(content)
        defer { window.isHidden = true }
        window.frame.size = controller.sizeThatFits(in: CGSize(width: 390, height: 10000))
        controller.view.frame = window.bounds
        try await settle(controller)
        func control(_ id: String) throws -> UIControl {
            try XCTUnwrap(nativeControls(in: controller.view).first { $0.accessibilityIdentifier == id })
        }
        let heater = try XCTUnwrap(try control("printer.controls.hotend.target") as? UITextField)
        heater.text = "200"
        heater.sendActions(for: .editingChanged)
        try await settle(controller)
        let heatingDispatched = expectation(description: "Unknown limits do not block the explicit native target")
        service.afterSetTemperatures = { heatingDispatched.fulfill() }
        defer { service.afterSetTemperatures = nil }
        try control("printer.controls.heat.set-targets").sendActions(for: .touchUpInside)
        await fulfillment(of: [heatingDispatched], timeout: 5)
        service.afterSetTemperatures = nil
        try await settle(controller)
        XCTAssertEqual(service.setTemperaturesCallCount, 1)
        XCTAssertEqual(service.setTemperaturesCalledWith?.hotend, 200)
        XCTAssertNil(service.setTemperaturesCalledWith?.bed)
        XCTAssertNil(model.lastError)
        XCTAssertNil(model.preheatBlockedReason(.pla))
        XCTAssertNil(model.preheatBlockedReason(.coolDown))
        XCTAssertNotNil(model.hardwareLoadError)
        model.cancelPendingCommand()
        try await settle(controller)

        window.frame.size = controller.sizeThatFits(in: CGSize(width: 390, height: 10000))
        controller.view.frame = window.bounds
        try await settle(controller)
        let image = UIGraphicsImageRenderer(bounds: controller.view.bounds).image { _ in
            XCTAssertTrue(controller.view.drawHierarchy(in: controller.view.bounds, afterScreenUpdates: true))
        }
        let attachment = XCTAttachment(image: image)
        attachment.name = "review-only-legacy-PrinterSetupControlsContent-mock-unknown-heater-limits"
        attachment.lifetime = .keepAlways
        add(attachment)

        heater.text = "0"
        heater.sendActions(for: .editingChanged)
        try await settle(controller)
        let coolDownDispatched = expectation(description: "The UI's asynchronous zero-target command reaches the service")
        service.afterSetTemperatures = { coolDownDispatched.fulfill() }
        try control("printer.controls.heat.set-targets").sendActions(for: .touchUpInside)
        await fulfillment(of: [coolDownDispatched], timeout: 5)
        service.afterSetTemperatures = nil
        XCTAssertEqual(service.setTemperaturesCallCount, 2)
        XCTAssertEqual(service.setTemperaturesCalledWith?.hotend, 0)
        XCTAssertNil(service.setTemperaturesCalledWith?.bed)
        model.cancelPendingCommand()
        try await settle(controller)
        service.setTemperaturesCalledWith = nil
        service.detailsToReturn = .controlsLimitsFixture(for: printer, hotend: 260, bed: 110)
        try control("printer.controls.retry-limits").sendActions(for: .touchUpInside)
        try await settle(controller, "the asynchronous heater-limits retry to load") {
            !model.isLoadingHardware && model.maximum(for: .hotend) == 260
        }
        XCTAssertEqual(model.maximum(for: .hotend), 260)
        XCTAssertNil(model.hardwareLoadError)
        XCTAssertNil(service.setTemperaturesCalledWith, "A read retry never replays a target")
        XCTAssertEqual(service.setTemperaturesCallCount, 2)
        heater.text = "261"
        heater.sendActions(for: .editingChanged)
        try await settle(controller)
        try control("printer.controls.heat.set-targets").sendActions(for: .touchUpInside)
        try await settle(controller)
        XCTAssertNil(service.setTemperaturesCalledWith)
        XCTAssertEqual(service.setTemperaturesCallCount, 2, "A known maximum still validates explicit input")
        XCTAssertNil(model.lastError)

        try await expandAbsolute(controller)
        let x = try XCTUnwrap(try control("printer.controls.absolute.x") as? UITextField)
        let y = try XCTUnwrap(try control("printer.controls.absolute.y") as? UITextField)
        let z = try XCTUnwrap(try control("printer.controls.absolute.z") as? UITextField)
        x.text = "1"
        x.sendActions(for: .editingChanged)
        y.text = "2"
        y.sendActions(for: .editingChanged)
        z.text = "3"
        z.sendActions(for: .editingChanged)
        try await settle(controller)
        try control("printer.controls.absolute.move").sendActions(for: .touchUpInside)
        try await settle(controller, "the absolute move request to reach the service") {
            service.moveToCalledWith != nil
        }
        XCTAssertEqual(service.moveToCalledWith?.x, 1)
        XCTAssertEqual(service.moveToCalledWith?.y, 2)
        XCTAssertEqual(service.moveToCalledWith?.z, 3)
    }

    func test_editorPrecision_rejectsBeforeDispatchAndExposesNativeGuidance() async throws {
        let printer = try makePrinter(backend: .octoPrint)
        var caps = Self.layoutCaps
        caps.supportsAbsoluteMovement = true
        caps.verifiedSafety = VerifiedSafetyFixtures.discovery()
        let service = makeService(caps: caps)
        service.statusToReturn = VerifiedSafetyFixtures.status(id: printer.id)
        let model = PrinterControlsViewModel.configuredForTests(printerService: service, printer: printer)
        await model.loadCapabilities()
        let content = PrinterSetupControlsContent(printer: printer, viewModel: model)
            .frame(width: 390)
            .fixedSize(horizontal: false, vertical: true)
        let (window, controller) = install(content)
        defer { window.isHidden = true }
        window.frame.size = controller.sizeThatFits(in: CGSize(width: 390, height: 10000))
        controller.view.frame = window.bounds
        try await settle(controller)
        try await expandAbsolute(controller)
        let controls = nativeControls(in: controller.view)
        let heater = try XCTUnwrap(controls.first { $0.accessibilityIdentifier == "printer.controls.hotend.target" } as? UITextField)
        let setHeater = try XCTUnwrap(controls.first { $0.accessibilityIdentifier == "printer.controls.heat.set-targets" })
        let coordinate = try XCTUnwrap(controls.first { $0.accessibilityIdentifier == "printer.controls.absolute.x" } as? UITextField)
        let move = try XCTUnwrap(controls.first { $0.accessibilityIdentifier == "printer.controls.absolute.move" })
        XCTAssertEqual(
            heater.accessibilityHint,
            "Maximum \(try XCTUnwrap(model.maximum(for: .hotend))) degrees. " + ControlNumberInput.heaterPrecisionMessage
                + " Blank leaves this heater unchanged; zero switches it off."
        )
        XCTAssertEqual(coordinate.accessibilityHint, ControlNumberInput.coordinatePrecisionMessage)

        heater.text = "200.5"
        heater.sendActions(for: .editingChanged)
        try await settle(controller)
        setHeater.sendActions(for: .touchUpInside)
        try await settle(controller)
        coordinate.text = "1.2345"
        coordinate.sendActions(for: .editingChanged)
        try await settle(controller)
        move.sendActions(for: .touchUpInside)
        try await settle(controller)
        XCTAssertNil(service.setTemperaturesCalledWith)
        XCTAssertNil(service.moveToCalledWith)
        XCTAssertNil(model.pendingCommand)
        XCTAssertNil(model.lastError, "Editor validation must reject before constructing a routine command")
        XCTAssertEqual(heater.text, "200.5")
        XCTAssertEqual(coordinate.text, "1.2345", "Do not silently round the entered physical target")

        window.frame.size = controller.sizeThatFits(in: CGSize(width: 390, height: 10000))
        controller.view.frame = window.bounds
        try await settle(controller)
        let image = UIGraphicsImageRenderer(bounds: controller.view.bounds).image { _ in
            XCTAssertTrue(controller.view.drawHierarchy(in: controller.view.bounds, afterScreenUpdates: true))
        }
        let attachment = XCTAttachment(image: image)
        attachment.name = "review-only-legacy-PrinterSetupControlsContent-mock-precision-validation"
        attachment.lifetime = .keepAlways
        add(attachment)

        heater.text = "200"
        heater.sendActions(for: .editingChanged)
        try await settle(controller)
        let heaterDispatched = expectation(description: "Valid whole-degree target dispatched")
        service.afterSetTemperatures = { heaterDispatched.fulfill() }
        setHeater.sendActions(for: .touchUpInside)
        await fulfillment(of: [heaterDispatched], timeout: 1)
        try await settle(controller)
        XCTAssertEqual(service.setTemperaturesCalledWith?.hotend, 200)
        XCTAssertNil(service.setTemperaturesCalledWith?.bed)
        var reported = printer
        reported.hotendTarget = 200
        model.handlePrinterUpdate(reported)
        try await settle(controller)
        coordinate.text = "1.001"
        coordinate.sendActions(for: .editingChanged)
        for (id, text) in [("y", "0"), ("z", "10")] {
            let field = try XCTUnwrap(controls.first {
                $0.accessibilityIdentifier == "printer.controls.absolute.\(id)"
            } as? UITextField)
            field.text = text
            field.sendActions(for: .editingChanged)
        }
        try await settle(controller)
        XCTAssertTrue(move.isEnabled)
        let moveDispatched = expectation(description: "Valid precise absolute position dispatched")
        service.beforeAbsoluteMove = { moveDispatched.fulfill() }
        move.sendActions(for: .touchUpInside)
        await fulfillment(of: [moveDispatched], timeout: 1)
        try await settle(controller)
        XCTAssertEqual(service.moveToCalledWith?.x, 1.001)
        XCTAssertEqual(service.moveToCalledWith?.y, 0)
        XCTAssertEqual(service.moveToCalledWith?.z, 10)
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

    func test_pendingCommand_disablesNativeActionsSilently() async throws {
        let printer = try makePrinter(backend: .moonraker)
        let service = makeService(caps: Self.layoutCaps)
        let model = PrinterControlsViewModel.configuredForTests(printerService: service, printer: printer)
        await model.loadCapabilities()
        await model.setHeaterTarget(.hotend, target: 220)
        XCTAssertNotNil(model.pendingCommand)
        let (window, controller) = install(PrinterSetupControlsContent(printer: printer, viewModel: model))
        defer { window.isHidden = true }
        window.frame.size.height = 3000
        controller.view.frame = window.bounds
        try await settle(controller)
        let controls = nativeControls(in: controller.view)
        let apply = try XCTUnwrap(controls.first { $0.accessibilityIdentifier == "printer.controls.heat.set-targets" })
        let input = try XCTUnwrap(controls.first { $0.accessibilityIdentifier == "printer.controls.hotend.target" })
        XCTAssertFalse(controls.contains { $0.accessibilityIdentifier == "printer.controls.stop-waiting" },
                       "Pending is silent: no Stop waiting button (#3496)")
        XCTAssertFalse(apply.isEnabled)
        XCTAssertFalse(input.isEnabled)
        model.cancelPendingCommand()
        XCTAssertNil(model.pendingCommand)
        XCTAssertTrue(model.commandNotice?.contains("may still execute") == true)
    }

    func test_individualControls_regularWidthFullContentEvidence() async throws {
        try await captureIndividualControls(width: 1024, dynamicType: .large, name: "regular")
    }

    func test_blockedEditors_hideOfflineAndExplainReadinessAndPermissionGates() async throws {
        for reason in [
            "Printer is offline.",
            "Sign in and wait for this server to be ready.",
            "Printer controls require Queue.Start permission."
        ] {
            let printer = try makePrinter(backend: .octoPrint, isOnline: reason != "Printer is offline.")
            var caps = Self.layoutCaps
            caps.supportsAbsoluteMovement = true
            let service = makeService(caps: caps)
            let serverID = UUID()
            let composition = PrinterControlsComposition(
                identity: .init(serverID: serverID, generation: 0, revision: 0), printerService: service
            )
            let model = PrinterControlsViewModel(composition: composition, printer: printer)
            await model.loadCapabilities()
            model.configureAccess(serverID: serverID) { printer.isOnline ? reason : nil }
            XCTAssertEqual(model.blockedReason, reason)
            let content = PrinterSetupControlsContent(printer: printer, viewModel: model)
                .frame(width: 390)
                .fixedSize(horizontal: false, vertical: true)
            let (window, controller) = install(content)
            defer { window.isHidden = true }
            window.frame.size = controller.sizeThatFits(in: CGSize(width: 390, height: 10000))
            controller.view.frame = window.bounds
            try await settle(controller)
            if printer.isOnline { try await expandAbsolute(controller) }
            let controls = nativeControls(in: controller.view)
            if !printer.isOnline {
                XCTAssertTrue(controls.isEmpty, "Offline setup controls remain hidden by the host contract")
                continue
            } else {
                for label in ["Hotend target in degrees Celsius", "X destination in millimeters"] {
                    let control = try XCTUnwrap(controls.first { $0.accessibilityLabel == label })
                    XCTAssertFalse(control.isEnabled)
                }
            }
            let image = UIGraphicsImageRenderer(bounds: controller.view.bounds).image { _ in
                XCTAssertTrue(controller.view.drawHierarchy(in: controller.view.bounds, afterScreenUpdates: true))
            }
            let attachment = XCTAttachment(image: image)
            attachment.name = "review-only-legacy-PrinterSetupControlsContent-mock-blocked-\(reason)"
            attachment.lifetime = .keepAlways
            add(attachment)
        }
    }

    func test_individualControls_narrowSplitLargeTypeEvidence() async throws {
        try await captureIndividualControls(width: 500, dynamicType: .accessibility3, name: "narrow-accessibility")
    }

    private func captureIndividualControls(width: CGFloat, dynamicType: DynamicTypeSize, name: String) async throws {
        var printer = try makePrinter(backend: .moonraker)
        printer.hotendTemp = nil
        printer.bedTemp = 0
        printer.homedAxes = nil
        var caps = Self.layoutCaps
        caps.supportsAbsoluteMovement = true
        caps.supportsDisableMotors = true
        let service = makeService(caps: caps)
        service.detailsToReturn = PrinterDetails(
            id: printer.id, name: printer.name, backend: printer.backend,
            capabilities: PrinterHardwareCapabilities(
                maxBuildVolumeX: 256, maxBuildVolumeY: 256, maxBuildVolumeZ: 256,
                maxHotendTemp: 280, maxBedTemp: 110, hasHeatedBed: true
            )
        )
        let model = PrinterControlsViewModel.configuredForTests(printerService: service, printer: printer)
        await model.loadCapabilities()
        let content = PrinterSetupControlsContent(
            printer: printer, viewModel: model,
            usesColumns: PrinterDetailLayout.usesColumns(width: width, dynamicTypeSize: dynamicType)
        )
        .environment(\.horizontalSizeClass, width >= 760 ? .regular : .compact)
        .environment(\.dynamicTypeSize, dynamicType)
        .frame(width: width)
        .fixedSize(horizontal: false, vertical: true)
        let controller = UIHostingController(rootView: content)
        let size = controller.sizeThatFits(in: CGSize(width: width, height: 10000))
        XCTAssertGreaterThan(size.height, 500)
        XCTAssertLessThan(size.height, 10000, "The complete layout must fit without clipping")
        XCTAssertEqual(size.width, width, accuracy: 1)
        let window = UIWindow(frame: CGRect(origin: .zero, size: size))
        window.windowScene = UIApplication.shared.connectedScenes.compactMap { $0 as? UIWindowScene }.first
        window.frame = CGRect(origin: .zero, size: size)
        window.rootViewController = controller
        window.makeKeyAndVisible()
        defer { window.isHidden = true }
        controller.view.frame = window.bounds
        try await settle(controller)
        try await expandAbsolute(controller)
        let controls = nativeControls(in: controller.view)
        for label in [
            "Hotend target in degrees Celsius", "Bed target in degrees Celsius",
            "X destination in millimeters", "Y destination in millimeters",
            "Z destination in millimeters",
            "Go, set heater targets", "Move to position", "Disable motors"
        ] {
            let target = try XCTUnwrap(controls.first { $0.accessibilityLabel == label }, label)
            XCTAssertGreaterThanOrEqual(target.bounds.height, 44, label)
            XCTAssertGreaterThanOrEqual(target.bounds.width, 44, label)
            XCTAssertEqual(target.isEnabled, label != "Move to position", label)
            XCTAssertTrue(target.point(inside: CGPoint(x: 1, y: 1), with: nil), label)
        }
        let image = UIGraphicsImageRenderer(bounds: controller.view.bounds).image { _ in
            controller.view.drawHierarchy(in: controller.view.bounds, afterScreenUpdates: true)
        }
        let attachment = XCTAttachment(image: image)
        attachment.name = "review-only-legacy-PrinterSetupControlsContent-mock-\(snapshotName ?? "iPhone")-\(name)"
        attachment.lifetime = XCTAttachment.Lifetime.keepAlways
        add(attachment)
        XCTAssertTrue(model.supports(.hotend))
        XCTAssertTrue(model.supports(.bed))
        XCTAssertTrue(JogSubgroup.AbsolutePositionControls.isVisible(model.capabilities))
        XCTAssertEqual(model.hardware?.maxHotendTemp, 280)
        XCTAssertNil(model.printer.hotendTemp)
        XCTAssertEqual(model.printer.bedTemp, 0)
        XCTAssertNil(model.printer.homedAxes)
        XCTAssertNil(service.setTemperaturesCalledWith)
        XCTAssertNil(service.moveToCalledWith)
        XCTAssertNil(service.disableMotorsCalledWith)
    }

    func test_guardedMaterial_supportedPhoneEvidence() async throws {
        try await guardedMaterialEvidence(width: 390, dynamicType: .large, supported: true)
    }

    func test_guardedMaterial_supportedRegularWidthEvidence() async throws {
        try await guardedMaterialEvidence(width: 1024, dynamicType: .large, supported: true)
    }

    func test_guardedMaterial_narrowSplitAccessibilityEvidence() async throws {
        try await guardedMaterialEvidence(width: 320, dynamicType: .accessibility3, supported: true)
    }

    func test_guardedMaterial_unsupportedEvidence() async throws {
        try await guardedMaterialEvidence(width: 390, dynamicType: .large, supported: false)
    }

    private func guardedMaterialEvidence(width: CGFloat, dynamicType: DynamicTypeSize, supported: Bool) async throws {
        let printer = try makePrinter(backend: .moonraker)
        var caps = Self.layoutCaps
        caps.supportsExtrusion = true
        caps.supportsFilamentLoad = supported
        caps.supportsFilamentUnload = supported
        caps.supportsFilamentChange = supported
        caps.supportsZOffset = true
        caps.supportsZOffsetFirmwareSave = supported
        caps.supportsAbsoluteMovement = supported
        if supported { caps.verifiedSafety = VerifiedSafetyFixtures.discovery() }
        let service = makeService(caps: caps)
        service.statusToReturn = VerifiedSafetyFixtures.status(id: printer.id)
        let model = PrinterControlsViewModel.configuredForTests(printerService: service, printer: printer)
        await model.loadCapabilities()
        let content = PrinterSetupControlsContent(
            printer: printer, viewModel: model,
            usesColumns: PrinterDetailLayout.usesColumns(width: width, dynamicTypeSize: dynamicType),
            materialPresentation: PrinterFilamentPresentation(
                printer: printer, toolheads: [], spool: printer.spoolInfo,
                coverage: nil, coverageState: .unavailable, isStale: false, supportedActions: []
            )
        )
        .environment(\.dynamicTypeSize, dynamicType)
        .frame(width: width)
        .fixedSize(horizontal: false, vertical: true)
        let controller = UIHostingController(rootView: AnyView(content))
        let size = controller.sizeThatFits(in: CGSize(width: width, height: 20000))
        XCTAssertEqual(size.width, width, accuracy: 1)
        XCTAssertGreaterThan(size.height, 500)
        XCTAssertLessThan(size.height, 20000, "All material/calibration text must fit without clipping")
        let window = UIWindow(frame: CGRect(origin: .zero, size: size))
        window.windowScene = UIApplication.shared.connectedScenes.compactMap { $0 as? UIWindowScene }.first
        window.frame = CGRect(origin: .zero, size: size)
        window.rootViewController = controller
        window.makeKeyAndVisible()
        defer { window.isHidden = true }
        controller.view.frame = window.bounds
        try await settle(controller)
        let controls = nativeControls(in: controller.view)
        for suffix in ["extrude", "retract", "filament-load", "filament-unload", "filament-change", "calibration-start"] {
            let button = try XCTUnwrap(
                controls.first { $0.accessibilityIdentifier == "printer.controls.\(suffix)" }, suffix
            )
            XCTAssertGreaterThanOrEqual(button.bounds.height, 44, suffix)
            XCTAssertGreaterThanOrEqual(button.bounds.width, 44, suffix)
            XCTAssertFalse(button.accessibilityLabel?.isEmpty ?? true, suffix)
            let isNativeExtrusion = suffix == "extrude" || suffix == "retract"
            XCTAssertEqual(button.isEnabled, isNativeExtrusion || supported, suffix)
        }
        // Capture actual scroll viewports rather than asking the render server
        // for a many-thousand-point AX surface. Keep native scale and every tile.
        controller.rootView = AnyView(ScrollView { content })
        window.frame.size = CGSize(width: width, height: 844)
        controller.view.frame = window.bounds
        try await settle(controller)
        func findScrollView(_ view: UIView) -> UIScrollView? {
            if let scroll = view as? UIScrollView { return scroll }
            return view.subviews.lazy.compactMap { findScrollView($0) }.first
        }
        let scroll = try XCTUnwrap(findScrollView(controller.view))
        let lastOffset = max(0, scroll.contentSize.height - scroll.bounds.height)
        let pageHeight = max(1, scroll.bounds.height - 44)
        let pageCount = Int(ceil(lastOffset / pageHeight)) + 1
        for page in 0..<pageCount {
            scroll.setContentOffset(CGPoint(x: 0, y: min(CGFloat(page) * pageHeight, lastOffset)), animated: false)
            try await settle(controller)
            let image = UIGraphicsImageRenderer(bounds: controller.view.bounds).image { _ in
                XCTAssertTrue(controller.view.drawHierarchy(in: controller.view.bounds, afterScreenUpdates: true))
            }
            let attachment = XCTAttachment(image: image)
            attachment.name = "review-only-legacy-MaterialControls-mock-\(snapshotName ?? "iPhone")-\(Int(width))-\(dynamicType)-\(supported)-page\(page)"
            attachment.lifetime = .keepAlways
            add(attachment)
        }
        XCTAssertTrue(service.physicalFilamentCalls.isEmpty)
        XCTAssertNil(service.extrudeCalledWith)
        XCTAssertNil(service.saveZOffsetCalledWith)
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

    func test_instrumentPanel_activeJobsGateTuneAndHeatersByCapability() async throws {
        for (state, canTune) in [("starting", false), ("printing", true), ("paused", true)] {
            for supportsHeat in [false, true] {
                let printer = try makePrinter(backend: .moonraker, state: state)
                var caps = Self.layoutCaps
                caps.supportsTemperatureControlDuringPrint = supportsHeat
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
                for heater in ["hotend", "bed"] {
                    for action in ["target", "decrease", "increase"] {
                        let identifier = "printer.controls.\(heater).\(action)"
                        let control = nativeControls(in: controller.view).first {
                            $0.accessibilityIdentifier == identifier
                        }
                        if canTune && supportsHeat {
                            let control = try XCTUnwrap(control, "\(state) \(identifier)")
                            XCTAssertTrue(control.isEnabled, "\(state) \(identifier)")
                            XCTAssertFalse(control.accessibilityLabel?.isEmpty ?? true, identifier)
                        } else {
                            XCTAssertNil(control, "\(state) must not expose unsupported Tune heater actions")
                        }
                    }
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
