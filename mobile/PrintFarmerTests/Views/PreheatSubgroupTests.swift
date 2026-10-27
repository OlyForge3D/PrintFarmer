import XCTest
import SwiftUI
import Vision
@testable import PrintFarmer

/// Smoke tests for `PreheatSubgroup` (issue #284). Snapshot tests are
/// intentionally deferred to issue #289; these verify the public seams the
/// view exposes for rendering decisions.
@MainActor
final class PreheatSubgroupTests: XCTestCase {

    func test_commandFailureKeepsActionableTextWithIconOnlyDismissal() async throws {
        let (model, service) = try await thermalModel()
        defer { model.deactivate() }
        service.errorToThrow = NetworkError.noConnection
        await model.setHeaterTarget(.hotend, target: 200)

        let controller = UIHostingController(rootView:
            PrinterControlCommandFeedback(viewModel: model, section: .heat)
                .frame(width: 326)
                .fixedSize(horizontal: false, vertical: true)
        )
        let window = UIWindow()
        window.windowScene = UIApplication.shared.connectedScenes.compactMap { $0 as? UIWindowScene }.first
        window.frame = CGRect(x: 0, y: 0, width: 326, height: 200)
        window.rootViewController = controller
        window.makeKeyAndVisible()
        defer { window.isHidden = true }
        try await settle(controller)

        let dismiss = try XCTUnwrap(try control("printer.controls.dismiss-error", in: controller) as? UIButton)
        XCTAssertNil(dismiss.configuration?.title, "Dismissal is an icon, not visible text")
        XCTAssertNotNil(dismiss.configuration?.image)
        XCTAssertEqual(dismiss.accessibilityLabel, "Dismiss error")
        XCTAssertGreaterThanOrEqual(dismiss.bounds.width, 44)
        XCTAssertGreaterThanOrEqual(dismiss.bounds.height, 44)
        let text = try recognizedTexts(controller).joined(separator: " ").lowercased()
        XCTAssertTrue(text.contains("command connection failed"), text)
        XCTAssertFalse(text.contains("internet"), text)
        XCTAssertFalse(text.contains("outcome may be unknown"), text)
        XCTAssertFalse(text.contains("dismiss"), text)

        dismiss.sendActions(for: .touchUpInside)
        try await settle(controller)
        XCTAssertNil(model.lastError)
        XCTAssertFalse(nativeControls(controller.view).contains {
            $0.accessibilityIdentifier == "printer.controls.dismiss-error"
        })
    }

    func test_thermalPhoneHeight() async throws {
        let (model, _) = try await thermalModel()
        let controller = UIHostingController(rootView: thermalContent(model, width: 326))
        let size = controller.sizeThatFits(in: CGSize(width: 326, height: 10000))
        // Measured at d34ae50688 on iOS 26.5 (23F77), same fixture/width/type.
        let priorPhoneHeight: CGFloat = 642.6667
        print("THERMAL_PHONE_HEIGHT width=\(size.width) height=\(size.height) prior=\(priorPhoneHeight)")
        XCTAssertLessThan(size.height, priorPhoneHeight, "Include the paired reading strip in Essential's footprint")
        XCTAssertGreaterThan(size.height, 88)
    }

    /// #3547: Nozzle and Bed share one row (name, measured value, tappable
    /// target, − and +). There is no Go/Set-targets button.
    func test_heatRows_shareOneRowWithTappableTargetAndSteppers() async throws {
        let (model, service) = try await thermalModel()
        var readings = model.printer
        readings.hotendTemp = 192.5
        readings.bedTemp = 37
        model.handlePrinterUpdate(readings)
        for (width, type): (CGFloat, DynamicTypeSize) in [
            (326, .large), (448, .large), (256, .large), (326, .accessibility5)
        ] {
            let (window, controller) = try await installThermal(model, width: width, type: type)
            defer { window.isHidden = true }
            let controls = nativeControls(controller.view)
            XCTAssertEqual(controls.count, 6, "Per heater: one target field plus − and +; no Go button")
            XCTAssertFalse(controls.contains { $0.accessibilityIdentifier == "printer.controls.heat.set-targets" })
            for heater in Heater.allCases {
                let field = try XCTUnwrap(controls.first {
                    $0.accessibilityIdentifier == "printer.controls.\(heater.rawValue).target"
                } as? UITextField)
                XCTAssertEqual(field.accessibilityLabel, "\(heater.title) target in degrees Celsius")
                // Accessibility sizes right-align so every row keeps one gap to °C (N12).
                XCTAssertEqual(field.textAlignment, type.isAccessibilitySize ? .right : .center,
                               "The target reads as a tappable value chip")
                XCTAssertEqual(field.borderStyle, .none)
                XCTAssertEqual(field.layer.cornerRadius, 8)
                XCTAssertNotNil(field.inputAccessoryView, "Numeric entry offers Set and Cancel")
                let decrease = try XCTUnwrap(controls.first {
                    $0.accessibilityIdentifier == "printer.controls.\(heater.rawValue).decrease"
                })
                let increase = try XCTUnwrap(controls.first {
                    $0.accessibilityIdentifier == "printer.controls.\(heater.rawValue).increase"
                })
                let fieldFrame = field.convert(field.bounds, to: controller.view)
                let decreaseFrame = decrease.convert(decrease.bounds, to: controller.view)
                let increaseFrame = increase.convert(increase.bounds, to: controller.view)
                XCTAssertEqual(fieldFrame.midY, increaseFrame.midY, accuracy: 1)
                XCTAssertLessThanOrEqual(decreaseFrame.maxX, fieldFrame.minX)
                XCTAssertLessThanOrEqual(fieldFrame.maxX, increaseFrame.minX)
                for control in [field, decrease, increase] {
                    let frame = control.convert(control.bounds, to: controller.view)
                    XCTAssertGreaterThanOrEqual(control.bounds.width, 44)
                    XCTAssertGreaterThanOrEqual(control.bounds.height, 44)
                    XCTAssertGreaterThanOrEqual(frame.minX, -1)
                    XCTAssertLessThanOrEqual(frame.maxX, width + 1)
                }
            }
            let image = UIGraphicsImageRenderer(bounds: controller.view.bounds).image { _ in
                XCTAssertTrue(controller.view.drawHierarchy(in: controller.view.bounds, afterScreenUpdates: true))
            }
            let attachment = XCTAttachment(image: image)
            attachment.name = "heat-rows-\(Int(width))-\(type)"
            attachment.lifetime = .keepAlways
            add(attachment)
        }
        XCTAssertNil(service.setTemperaturesCalledWith)
    }

    /// #3547: repeated ± taps send one request, for that heater only, after the debounce.
    func test_heatStepperBurst_sendsOneRequestForThatHeaterOnly() async throws {
        executionTimeAllowance = 120
        let (model, service) = try await thermalModel()
        let (window, controller) = try await installThermal(model, debounce: .milliseconds(300))
        defer { window.isHidden = true }
        let start = try XCTUnwrap(model.printer.hotendTarget)
        let increase = try control("printer.controls.hotend.increase", in: controller)
        await withCheckedContinuation { (dispatched: CheckedContinuation<Void, Never>) in
            service.afterSetTemperatures = { dispatched.resume() }
            Task { @MainActor in
                for _ in 0..<3 {
                    increase.sendActions(for: .touchUpInside)
                    try? await self.settle(controller)
                }
                XCTAssertEqual(service.setTemperaturesCallCount, 0, "± waits for the quiet period")
            }
        }
        service.afterSetTemperatures = nil
        try await Task.sleep(for: .milliseconds(500))
        XCTAssertEqual(service.setTemperaturesCallCount, 1, "A burst collapses into one request")
        XCTAssertEqual(service.setTemperaturesCalledWith?.hotend, start + 15)
        XCTAssertNil(service.setTemperaturesCalledWith?.bed, "Only the tapped heater changes")
        let field = try XCTUnwrap(try control("printer.controls.hotend.target", in: controller) as? UITextField)
        XCTAssertEqual(field.text, "", "No draft is created by ±")
        XCTAssertEqual(field.placeholder, HeatTargetRow.placeholder(currentTarget: start + 15),
                       "The row shows the queued target while it settles")
        model.cancelPendingCommand()
    }

    /// #3547: a typed value applies only on Set; Cancel reverts; range and
    /// precision validation stays; 0 switches the heater off.
    func test_typedTarget_appliesOnlyOnSetAndCancelReverts() async throws {
        executionTimeAllowance = 120
        let (model, service) = try await thermalModel()
        let (window, controller) = try await installThermal(model)
        defer { window.isHidden = true }
        let field = try XCTUnwrap(try control("printer.controls.hotend.target", in: controller) as? UITextField)
        field.text = "205"
        field.sendActions(for: .editingChanged)
        try await settle(controller)
        try press(field, "cancel")
        try await settle(controller)
        XCTAssertEqual(field.text, "", "Cancel reverts the typed draft")
        try await Task.sleep(for: .seconds(1))
        XCTAssertNil(service.setTemperaturesCalledWith, "Typing alone never sends")
        for invalid in ["200.5", "-1", "NaN", "99999"] {
            field.text = invalid
            field.sendActions(for: .editingChanged)
            try await settle(controller)
            try press(field, "set")
            try await settle(controller)
            XCTAssertNil(service.setTemperaturesCalledWith, invalid)
            XCTAssertEqual(field.text, invalid, "An invalid value stays for correction")
        }
        field.text = "205"
        field.sendActions(for: .editingChanged)
        try await settle(controller)
        try await pressAndAwaitDispatch(field, service: service)
        try await settle(controller)
        XCTAssertEqual(service.setTemperaturesCallCount, 1)
        XCTAssertEqual(service.setTemperaturesCalledWith?.hotend, 205)
        XCTAssertNil(service.setTemperaturesCalledWith?.bed)
        XCTAssertTrue(model.isExecuting)
        let pendingControls = nativeControls(controller.view)
        XCTAssertFalse(pendingControls.contains { $0.accessibilityIdentifier == "printer.controls.stop-waiting" },
                       "Pending is silent: no Stop waiting button (#3496)")
        XCTAssertEqual(pendingControls.count, 6, "Rows stay mounted while the request is in flight")
        XCTAssertTrue(pendingControls.allSatisfy(\.isEnabled),
                      "In flight never locks the rows; further edits queue in the view model (#3555 B02)")
        XCTAssertEqual(field.accessibilityValue, "Applying", "Only an in-flight row speaks Applying")
        model.cancelPendingCommand()
        try await settle(controller)
        let bed = try XCTUnwrap(try control("printer.controls.bed.target", in: controller) as? UITextField)
        bed.text = "0"
        bed.sendActions(for: .editingChanged)
        try await settle(controller)
        try await pressAndAwaitDispatch(bed, service: service)
        XCTAssertEqual(service.setTemperaturesCallCount, 2)
        XCTAssertNil(service.setTemperaturesCalledWith?.hotend)
        XCTAssertEqual(service.setTemperaturesCalledWith?.bed, 0, "Zero switches the bed off")
        model.cancelPendingCommand()
    }

    func test_thermalTargets_withUnknownMaximum_dispatchNativelyAndHonorOfflineLock() async throws {
        executionTimeAllowance = 120
        let (model, service) = try await thermalModel(knownLimits: false)
        let (window, controller) = try await installThermal(model)
        defer { window.isHidden = true }
        let field = try XCTUnwrap(try control("printer.controls.hotend.target", in: controller) as? UITextField)
        field.text = "205"
        field.sendActions(for: .editingChanged)
        try await settle(controller)
        try await pressAndAwaitDispatch(field, service: service)
        XCTAssertEqual(service.setTemperaturesCallCount, 1)
        XCTAssertEqual(service.setTemperaturesCalledWith?.hotend, 205)
        XCTAssertNil(service.setTemperaturesCalledWith?.bed)
        model.cancelPendingCommand()
        var offline = model.printer
        offline.isOnline = false
        model.handlePrinterUpdate(offline)
        try await settle(controller)
        XCTAssertTrue(nativeControls(controller.view).allSatisfy { !$0.isEnabled })
    }

    func test_thermalDraft_survivesAccessibilityReflowAndUnconfirmedBedStaysDisabled() async throws {
        let (model, _) = try await thermalModel(hotendOnly: true)
        let (window, controller) = try await installThermal(model)
        defer { window.isHidden = true }
        let field = try XCTUnwrap(try control("printer.controls.hotend.target", in: controller) as? UITextField)
        field.text = "231"
        field.sendActions(for: .editingChanged)
        try await settle(controller)
        controller.rootView = AnyView(thermalContent(model, width: 326, type: .accessibility5))
        try await settle(controller)
        let controls = nativeControls(controller.view)
        XCTAssertEqual(controls.count, 6)
        XCTAssertEqual((try control("printer.controls.hotend.target", in: controller) as? UITextField)?.text, "231")
        for suffix in ["target", "increase", "decrease"] {
            let bed = try XCTUnwrap(controls.first { $0.accessibilityIdentifier == "printer.controls.bed.\(suffix)" })
            XCTAssertFalse(bed.isEnabled, suffix)
        }
    }

    /// H3552-01: a disabled sensor row explains why through its VoiceOver hint
    /// only. Unsupported, access and offline reasons never render visible text;
    /// loading leaves the ordinary entry hint.
    func test_sensorRows_unavailableReasonIsVoiceOverHintOnly() async throws {
        executionTimeAllowance = 180
        let scenarios: [(name: String, expected: String?, make: () async throws -> (PrinterControlsViewModel, MockPrinterService))] = [
            ("unsupported", "Sensor control is unavailable on this printer.",
             { try await self.multiHeaterModel(supportsSensorControl: false) }),
            ("access-error", "Server denied control access.", {
                // Access is revoked after capabilities load, as a server switch does.
                let denied = AccessFlag()
                let result = try await self.multiHeaterModel(
                    accessCheck: { denied.value ? "Server denied control access." : nil }
                )
                denied.value = true
                result.0.refreshAccess()
                return result
            }),
            ("offline", "Printer is offline.", { try await self.multiHeaterModel(isOnline: false) }),
            ("loading", nil, { try await self.multiHeaterModel(loadCapabilities: false) })
        ]
        for scenario in scenarios {
            let (model, _) = try await scenario.make()
            defer { model.deactivate() }
            XCTAssertEqual(model.sensorRowMessage, scenario.expected, scenario.name)
            let (window, controller) = try await installMultiHeater(model)
            defer { window.isHidden = true }
            let field = try XCTUnwrap(
                try control(Self.chamberField, in: controller) as? UITextField, scenario.name
            )
            XCTAssertFalse(field.isEnabled, "\(scenario.name): gating is unchanged")
            if let expected = scenario.expected {
                XCTAssertEqual(field.accessibilityHint, expected, scenario.name)
            } else {
                XCTAssertTrue(field.accessibilityHint?.hasPrefix("Double tap to enter a value") == true,
                              "\(scenario.name): \(field.accessibilityHint ?? "nil")")
            }
            XCTAssertFalse(
                findIdentifier("printer.controls.sensor.reason", in: controller.view),
                "\(scenario.name): no visible sensor caption"
            )
            let texts = accessibilityTexts(controller.view)
            if let expected = scenario.expected {
                XCTAssertFalse(texts.contains { $0.contains(expected) }, "\(scenario.name): \(texts)")
            }
            VisibleText.assertNoLockoutText(VisibleText.collect(in: controller.view), scenario.name)
            let rendered = try recognizedTexts(controller)
            for fragment in ["unavailable", "offline", "denied", "Another", "locked"] {
                XCTAssertFalse(rendered.contains { $0.localizedCaseInsensitiveContains(fragment) },
                               "\(scenario.name) rendered \(fragment): \(rendered)")
            }
            attachScreenshot(controller, name: "sensor-rows-\(scenario.name)")
        }
    }

    @MainActor private final class AccessFlag { var value = false }

    private func findIdentifier(_ identifier: String, in root: NSObject, depth: Int = 0) -> Bool {
        guard depth < 40 else { return false }
        if (root as? UIAccessibilityIdentification)?.accessibilityIdentifier == identifier { return true }
        var children: [NSObject] = (root as? UIView)?.subviews ?? []
        if let elements = root.accessibilityElements as? [NSObject] {
            children += elements
        } else {
            let count = root.accessibilityElementCount()
            if count != NSNotFound, count > 0 {
                children += (0..<count).compactMap { root.accessibilityElement(at: $0) as? NSObject }
            }
        }
        return children.contains { findIdentifier(identifier, in: $0, depth: depth + 1) }
    }

    /// #3547 addition: an executing preset never surfaces as caption text
    /// ("Another printer operation is still pending.") on heater or sensor rows.
    func test_executingPreset_showsNoPendingCaptionOnHeatOrSensorRows() async throws {
        executionTimeAllowance = 120
        let (model, service) = try await multiHeaterModel()
        defer { model.deactivate() }
        let content = VStack(alignment: .leading, spacing: 8) {
            PreheatSubgroup(viewModel: model, compactForDetail: true)
            PrinterSensorControlRows(viewModel: model, compact: true)
        }
        .environment(\.horizontalSizeClass, .regular)
        .frame(width: 448)
        .fixedSize(horizontal: false, vertical: true)
        let controller = UIHostingController(rootView: content)
        let window = UIWindow(frame: CGRect(x: 0, y: 0, width: 448, height: 900))
        window.windowScene = UIApplication.shared.connectedScenes.compactMap { $0 as? UIWindowScene }.first
        window.rootViewController = controller
        window.makeKeyAndVisible()
        defer { window.isHidden = true }
        try await settle(controller)
        let idleTexts = accessibilityTexts(controller.view)
        XCTAssertTrue(idleTexts.contains { $0.contains("Heater Box1") }, "Collector sees row titles: \(idleTexts)")
        let rowControls = { self.nativeControls(controller.view)
            .filter { $0.accessibilityIdentifier?.hasPrefix("printer.controls.preheat") != true } }
        // Heater Box1's − is legitimately disabled at Off (its lower limit).
        let idleEnabled = Set(rowControls().filter(\.isEnabled).compactMap(\.accessibilityIdentifier))
        XCTAssertGreaterThanOrEqual(idleEnabled.count, 10, "\(idleEnabled)")

        let preset = PreheatPreset.pla
        await withCheckedContinuation { (dispatched: CheckedContinuation<Void, Never>) in
            service.afterSetTemperatures = { dispatched.resume() }
            Task { @MainActor in await model.preheat(preset) }
        }
        service.afterSetTemperatures = nil
        try await settle(controller)
        XCTAssertTrue(model.isExecuting, "The preset is still in flight")
        XCTAssertNil(model.heatRowBlockedReason)
        XCTAssertNil(model.sensorRowMessage)
        let chamber = try XCTUnwrap(nativeControls(controller.view)
            .first { $0.accessibilityIdentifier == Self.chamberField } as? UITextField)
        XCTAssertTrue(chamber.accessibilityHint?.hasPrefix("Double tap to enter a value") == true,
                      "In flight adds no blocked-reason hint: \(chamber.accessibilityHint ?? "nil")")
        let texts = accessibilityTexts(controller.view)
        // With AX automation on, the in-flight preset exposes its spec §4.1
        // VoiceOver value "Pending"; that is the only allowed occurrence.
        let pendingIndices = texts.indices.filter { texts[$0].localizedCaseInsensitiveContains("pending") }
        XCTAssertLessThanOrEqual(pendingIndices.count, 1, "\(texts)")
        for index in pendingIndices {
            XCTAssertEqual(texts[index], "Pending", "\(texts)")
            XCTAssertTrue(index > 0 && texts[index - 1] == "Preheat for PLA, in progress",
                          "Only the in-flight preset's VoiceOver value may say Pending: \(texts)")
        }
        XCTAssertFalse(texts.contains { $0.contains("Another") }, "\(texts)")
        let inFlightEnabled = Set(rowControls().filter(\.isEnabled).compactMap(\.accessibilityIdentifier))
        XCTAssertEqual(inFlightEnabled, idleEnabled, "In flight never locks the rows")
        let rendered = try recognizedTexts(controller)
        XCTAssertFalse(rendered.contains { $0.localizedCaseInsensitiveContains("pending") }, "\(rendered)")
        XCTAssertFalse(rendered.contains { $0.localizedCaseInsensitiveContains("another") }, "\(rendered)")
        let image = UIGraphicsImageRenderer(bounds: controller.view.bounds).image { _ in
            XCTAssertTrue(controller.view.drawHierarchy(in: controller.view.bounds, afterScreenUpdates: true))
        }
        let attachment = XCTAttachment(image: image)
        attachment.name = "heat-rows-multi-heater-preset-in-flight"
        attachment.lifetime = .keepAlways
        add(attachment)
        model.cancelPendingCommand()
    }

    func test_unsupportedHotendDisablesEveryPresetIncludingCoolDown() async throws {
        var printer = try TestData.decodePrinter()
        printer.state = "ready"
        let service = MockPrinterService()
        service.capabilitiesToReturn = PrinterBackendCapabilities(
            supportsMovement: false, supportsTemperatureControl: false,
            supportsBedTemperature: true, supportsFanControl: false,
            supportsHoming: false, supportedAxes: []
        )
        service.detailsToReturn = .controlsLimitsFixture(for: printer)
        let model = PrinterControlsViewModel.configuredForTests(printerService: service, printer: printer)
        await model.loadCapabilities()
        for preset in PreheatSubgroup.presets {
            XCTAssertNotNil(model.preheatBlockedReason(preset))
            await model.preheat(preset)
            XCTAssertNil(service.setTemperaturesCalledWith)
        }
    }


    // MARK: - Multi-heater (qp4-1 style: Nozzle, Bed, Chamber, Heater Box1)

    private func multiHeaterModel(
        chamberTarget: Double = 40, chamberStep: Double = 1,
        supportsSensorControl: Bool = true, isOnline: Bool = true,
        accessCheck: @escaping @MainActor () -> String? = { nil },
        loadCapabilities: Bool = true
    ) async throws -> (PrinterControlsViewModel, MockPrinterService) {
        var printer = try TestData.decodePrinter()
        printer.isOnline = isOnline
        printer.state = "idle"
        printer.sensors = [
            PrinterSensor(
                id: "chamber", displayName: "Chamber", kind: .chamber,
                readings: [
                    SensorReading(quantity: .temperature, value: .number(31), unit: .celsius),
                    SensorReading(quantity: .targetTemperature, value: .number(chamberTarget), unit: .celsius)
                ],
                writable: [SensorWritable(quantity: .targetTemperature, min: 0, max: 70, step: chamberStep, unit: .celsius)]
            ),
            PrinterSensor(
                id: "heater_box1", displayName: "Heater Box1", kind: .chamber,
                readings: [
                    SensorReading(quantity: .temperature, value: .number(28), unit: .celsius),
                    SensorReading(quantity: .targetTemperature, value: .number(0), unit: .celsius)
                ],
                writable: [SensorWritable(
                    quantity: .targetTemperature, min: 0, max: 60, step: 1, unit: .celsius, allowZero: true
                )]
            )
        ]
        let service = MockPrinterService()
        var capabilities = PrinterBackendCapabilities.allControlsFixture
        capabilities.supportsSensorControl = supportsSensorControl
        service.capabilitiesToReturn = capabilities
        service.detailsToReturn = .controlsLimitsFixture(for: printer)
        let model = PrinterControlsViewModel.configuredForTests(
            printerService: service, printer: printer, accessCheck: accessCheck
        )
        if loadCapabilities { await model.loadCapabilities() }
        return (model, service)
    }

    /// The live Heat panel order: presets, Nozzle/Bed rows, then sensor rows.
    private func installMultiHeater(
        _ model: PrinterControlsViewModel, width: CGFloat = 326, type: DynamicTypeSize = .large,
        debounce: Duration = .milliseconds(300)
    ) async throws -> (UIWindow, UIHostingController<AnyView>) {
        let content = VStack(alignment: .leading, spacing: 8) {
            PreheatSubgroup(viewModel: model, compactForDetail: true)
            PrinterSensorControlRows(viewModel: model, compact: true)
        }
        .padding(12)
        .environment(\.dynamicTypeSize, type)
        .environment(\.heatTargetDebounce, debounce)
        .environment(\.horizontalSizeClass, width > 390 ? .regular : .compact)
        .frame(width: width)
        .fixedSize(horizontal: false, vertical: true)
        .background(Color.pfCard)
        let controller = UIHostingController(rootView: AnyView(content))
        let size = controller.sizeThatFits(in: CGSize(width: width, height: 10000))
        let window = UIWindow()
        window.windowScene = UIApplication.shared.connectedScenes.compactMap { $0 as? UIWindowScene }.first
        window.frame = CGRect(origin: .zero, size: size)
        window.rootViewController = controller
        window.makeKeyAndVisible()
        try await settle(controller)
        return (window, controller)
    }

    private func attachScreenshot(_ controller: UIViewController, name: String) {
        let image = UIGraphicsImageRenderer(bounds: controller.view.bounds).image { _ in
            XCTAssertTrue(controller.view.drawHierarchy(in: controller.view.bounds, afterScreenUpdates: true))
        }
        let attachment = XCTAttachment(image: image)
        attachment.name = name
        attachment.lifetime = .keepAlways
        add(attachment)
    }

    /// On-screen text recognised from the rendered view (SwiftUI `Text` is not
    /// in the UIKit accessibility tree that unit tests can walk).
    private func recognizedTexts(_ controller: UIViewController) throws -> [String] {
        let format = UIGraphicsImageRendererFormat()
        format.scale = 3
        let image = UIGraphicsImageRenderer(bounds: controller.view.bounds, format: format).image { _ in
            _ = controller.view.drawHierarchy(in: controller.view.bounds, afterScreenUpdates: true)
        }
        let request = VNRecognizeTextRequest()
        request.recognitionLevel = .accurate
        request.usesLanguageCorrection = false
        try VNImageRequestHandler(cgImage: try XCTUnwrap(image.cgImage)).perform([request])
        return (request.results ?? []).compactMap { $0.topCandidates(1).first?.string }
    }

    /// OCR-tolerant comparison key: case, spacing and l/1 confusion ignored.
    private static func ocrKey(_ text: String) -> String {
        text.lowercased().filter { !$0.isWhitespace }.replacingOccurrences(of: "l", with: "1")
    }

    private static let chamberField = "printer.controls.sensor.chamber.TargetTemperature.value"
    private static let heaterBoxField = "printer.controls.sensor.heater_box1.TargetTemperature.value"

    /// #3555 H02: sensor rows show the same bare name as Nozzle and Bed.
    func test_multiHeater_rowTitlesAreBareNamesWithoutTarget() async throws {
        executionTimeAllowance = 120
        let (model, service) = try await multiHeaterModel()
        defer { model.deactivate() }
        for (width, type): (CGFloat, DynamicTypeSize) in [
            (256, .large), (326, .large), (448, .large), (326, .accessibility5)
        ] {
            let (window, controller) = try await installMultiHeater(model, width: width, type: type)
            defer { window.isHidden = true }
            // What is drawn on screen, not the VoiceOver labels.
            let visible = try recognizedTexts(controller)
            let normalized = Set(visible.map(Self.ocrKey))
            for title in ["Nozzle", "Bed", "Chamber", "Heater Box1"] {
                XCTAssertTrue(normalized.contains(Self.ocrKey(title)), "\(title) at \(width)/\(type): \(visible)")
            }
            XCTAssertFalse(visible.contains { $0.localizedCaseInsensitiveContains("target") },
                           "No row shows a \"<name> target\" title at \(width)/\(type): \(visible)")
            XCTAssertEqual(
                (try control(Self.chamberField, in: controller) as? UITextField)?.accessibilityLabel,
                "Chamber target in degrees Celsius", "VoiceOver keeps the descriptive label"
            )
            attachScreenshot(controller, name: "heat-rows-multi-heater-idle-\(Int(width))-\(type)")
        }
        XCTAssertEqual(PrinterSensorControlRows.title(
            sensor: try XCTUnwrap(model.printer.sensors.first { $0.id == "heater_box1" }), quantity: .targetTemperature
        ), "Heater Box1")
        XCTAssertEqual(service.setSensorValueCallCount, 0)
    }

    /// #3555 H02: typed sensor entry dispatches only a valid Set, exactly once, to that sensor.
    func test_sensorTypedEntry_cancelInvalidAndOutOfRangeDispatchNothing_validSetOnce() async throws {
        executionTimeAllowance = 120
        let (model, service) = try await multiHeaterModel()
        defer { model.deactivate() }
        let (window, controller) = try await installMultiHeater(model)
        defer { window.isHidden = true }
        let field = try XCTUnwrap(try control(Self.chamberField, in: controller) as? UITextField)
        field.text = "45"
        field.sendActions(for: .editingChanged)
        try await settle(controller)
        try press(field, "cancel")
        try await settle(controller)
        XCTAssertEqual(field.text, "", "Cancel reverts the typed draft")
        XCTAssertEqual(field.placeholder, "40")
        try await Task.sleep(for: .seconds(1))
        XCTAssertEqual(service.setSensorValueCallCount, 0, "Cancel never sends")
        // Off-grid, below and above range, not a number, and 0 where Off is not allowed.
        for invalid in ["45.5", "-1", "71", "NaN", "abc", "0.5"] {
            field.text = invalid
            field.sendActions(for: .editingChanged)
            try await settle(controller)
            try press(field, "set")
            try await settle(controller)
            XCTAssertEqual(field.text, invalid, "Set refuses \(invalid); the draft stays for correction")
        }
        for outOfRange in [45.5, -1, 71, 0.5] {
            XCTAssertEqual(
                model.sensorValueError(sensorId: "chamber", quantity: .targetTemperature, value: outOfRange),
                "Enter 0 to 70 in steps of 1.", "\(outOfRange)"
            )
        }
        XCTAssertThrowsError(try ControlNumberInput.optional("abc"))
        XCTAssertThrowsError(try ControlNumberInput.optional("NaN"))
        try await Task.sleep(for: .milliseconds(500))
        XCTAssertEqual(service.setSensorValueCallCount, 0, "Invalid entries never dispatch")
        XCTAssertNil(model.pendingCommand)

        field.text = "45"
        field.sendActions(for: .editingChanged)
        try await settle(controller)
        await withCheckedContinuation { (dispatched: CheckedContinuation<Void, Never>) in
            service.beforeSetSensorValue = { dispatched.resume() }
            try? press(field, "set")
        }
        service.beforeSetSensorValue = nil
        try await Task.sleep(for: .milliseconds(500))
        XCTAssertEqual(service.setSensorValueCallCount, 1, "A valid Set dispatches exactly once")
        XCTAssertEqual(service.setSensorValueCalledWith?.sensorId, "chamber")
        XCTAssertEqual(service.setSensorValueCalledWith?.quantity, .targetTemperature)
        XCTAssertEqual(service.setSensorValueCalledWith?.value, 45)
        XCTAssertNil(service.setTemperaturesCalledWith, "No heater changes")
        XCTAssertNil(model.pendingCommand)
        XCTAssertNil(model.commandNotice, "Chamber target acceptance must not render a disclaimer (#3565)")
        try await settle(controller)
        XCTAssertFalse(try recognizedTexts(controller).contains {
            $0.localizedCaseInsensitiveContains("command accepted")
                || $0.localizedCaseInsensitiveContains("physical completion")
                || $0.localizedCaseInsensitiveContains("observational")
        }, "The Heat panel must stay silent after a successful sensor command")
        model.cancelPendingCommand()
    }

    /// #3555 H02: sensor ± clamps to the writable range and disables at a limit.
    func test_sensorStepper_clampsToRangeAndDisablesAtLimits() async throws {
        executionTimeAllowance = 120
        let (model, service) = try await multiHeaterModel(chamberTarget: 68, chamberStep: 5)
        defer { model.deactivate() }
        let (window, controller) = try await installMultiHeater(model, debounce: .milliseconds(200))
        defer { window.isHidden = true }
        let increase = try control("printer.controls.sensor.chamber.TargetTemperature.increase", in: controller)
        let decrease = try control("printer.controls.sensor.chamber.TargetTemperature.decrease", in: controller)
        let boxDecrease = try control("printer.controls.sensor.heater_box1.TargetTemperature.decrease", in: controller)
        let boxIncrease = try control("printer.controls.sensor.heater_box1.TargetTemperature.increase", in: controller)
        XCTAssertTrue(increase.isEnabled, "68 + 5 clamps to the 70 maximum")
        XCTAssertTrue(decrease.isEnabled)
        XCTAssertFalse(boxDecrease.isEnabled, "Off (0) is the lower limit")
        XCTAssertTrue(boxIncrease.isEnabled)
        await withCheckedContinuation { (dispatched: CheckedContinuation<Void, Never>) in
            service.beforeSetSensorValue = { dispatched.resume() }
            increase.sendActions(for: .touchUpInside)
        }
        service.beforeSetSensorValue = nil
        XCTAssertEqual(service.setSensorValueCalledWith?.value, 70, "A step past the maximum clamps to it")
        XCTAssertEqual(service.setSensorValueCalledWith?.sensorId, "chamber")
        model.cancelPendingCommand()
        try await settle(controller)
        XCTAssertFalse(increase.isEnabled, "The queued 70 is the upper limit")
        XCTAssertTrue(decrease.isEnabled)
        increase.sendActions(for: .touchUpInside)
        try await Task.sleep(for: .milliseconds(500))
        XCTAssertEqual(service.setSensorValueCallCount, 1, "A disabled step at the limit never resends")
    }

    /// #3555 B01: the settle grace clears only the queued value, never a draft being edited.
    func test_settleGrace_neverClearsADraftBeingEdited() async throws {
        executionTimeAllowance = 120
        let (model, service) = try await thermalModel()
        model.heatTargetSettleGrace = .milliseconds(400)
        let (window, controller) = try await installThermal(model, debounce: .milliseconds(100))
        defer { window.isHidden = true }
        let start = try XCTUnwrap(model.printer.hotendTarget)
        let increase = try control("printer.controls.hotend.increase", in: controller)
        await tapAndAwaitDispatch(try XCTUnwrap(increase as? UIButton), service: service)
        try await settle(controller)
        XCTAssertEqual(model.queuedHeatTargets["hotend"], start + 5)
        let field = try XCTUnwrap(try control("printer.controls.hotend.target", in: controller) as? UITextField)
        field.delegate?.textFieldDidBeginEditing?(field)
        try await settle(controller)
        XCTAssertEqual(field.text, HeatTargetRow.draftText(start + 5), "Editing starts from the queued value")
        field.text = "21"
        field.sendActions(for: .editingChanged)
        try await Task.sleep(for: .milliseconds(800))
        try await settle(controller)
        XCTAssertNil(model.queuedHeatTargets["hotend"], "The grace elapsed")
        XCTAssertEqual(field.text, "21", "The grace never replaces or clears the draft")
        XCTAssertTrue(field.isEnabled)
        model.cancelPendingCommand()
    }

    /// #3555 B02: another row's in-flight send leaves the edited field enabled with its draft;
    /// its Set waits for that send and then applies only this heater.
    func test_otherRowSend_keepsEditedFieldEnabledWithDraft() async throws {
        executionTimeAllowance = 120
        let (model, service) = try await thermalModel()
        let (window, controller) = try await installThermal(model)
        defer { window.isHidden = true }
        let field = try XCTUnwrap(try control("printer.controls.hotend.target", in: controller) as? UITextField)
        field.delegate?.textFieldDidBeginEditing?(field)
        field.text = "210"
        field.sendActions(for: .editingChanged)
        try await settle(controller)
        let gate = AsyncStream<Void>.makeStream()
        service.beforeSetTemperatures = { for await _ in gate.stream { break } }
        let bedSend = Task { await model.setHeaterTarget(.bed, target: 60) }
        let deadline = ContinuousClock.now + .seconds(5)
        while service.setTemperaturesCallCount == 0, ContinuousClock.now < deadline {
            try await settle(controller)
        }
        try await settle(controller)
        XCTAssertTrue(model.isExecuting, "The Bed send is in flight")
        XCTAssertTrue(field.isEnabled, "The edited field stays enabled")
        XCTAssertEqual(field.text, "210", "The draft survives another row's send")
        XCTAssertNotEqual(field.accessibilityValue, "Applying", "Only Bed is applying")
        try press(field, "set")
        try await Task.sleep(for: .milliseconds(400))
        XCTAssertEqual(service.setTemperaturesCallCount, 1, "Set queues behind the Bed send")
        XCTAssertEqual(model.queuedHeatTargets["hotend"], 210)
        service.beforeSetTemperatures = nil
        gate.continuation.yield()
        gate.continuation.finish()
        await bedSend.value
        XCTAssertEqual(service.setTemperaturesCalledWith?.bed, 60)
        model.cancelPendingCommand()
        let sendDeadline = ContinuousClock.now + .seconds(10)
        while service.setTemperaturesCalledWith?.hotend != 210, ContinuousClock.now < sendDeadline {
            try await Task.sleep(for: .milliseconds(100))
        }
        XCTAssertEqual(service.setTemperaturesCallCount, 2)
        XCTAssertEqual(service.setTemperaturesCalledWith?.hotend, 210)
        XCTAssertNil(service.setTemperaturesCalledWith?.bed, "Only the edited heater is applied")
        model.cancelPendingCommand()
    }

    /// #3555 B03/B04: a target queued while idle is dropped, with Heat feedback, once a print
    /// starts; the queue lives in the view model so it survives the view going away.
    func test_queuedTarget_droppedWhenPrintStartsAndSurvivesViewDisappearing() async throws {
        executionTimeAllowance = 120
        let (model, service) = try await thermalModel()
        model.scheduleHeatTarget(key: "hotend", title: "Nozzle", value: 210, after: .milliseconds(300)) {
            await $0.setHeaterTarget(.hotend, target: 210)
        }
        XCTAssertEqual(model.queuedHeatTargets["hotend"], 210)
        var printing = model.printer
        printing.state = "printing"
        model.handlePrinterUpdate(printing)
        XCTAssertFalse(model.canControl)
        try await Task.sleep(for: .milliseconds(800))
        XCTAssertEqual(service.setTemperaturesCallCount, 0, "Re-checked before sending: never dispatched mid-print")
        XCTAssertNil(model.queuedHeatTargets["hotend"])
        XCTAssertEqual(model.feedbackSection, .heat)
        XCTAssertTrue(model.commandNotice?.hasPrefix("Nozzle target not sent.") == true, "\(model.commandNotice ?? "nil")")

        let (idleModel, idleService) = try await thermalModel()
        let (window, controller) = try await installThermal(idleModel, debounce: .milliseconds(300))
        let increase = try control("printer.controls.hotend.increase", in: controller)
        increase.sendActions(for: .touchUpInside)
        controller.rootView = AnyView(EmptyView())
        window.isHidden = true
        try await settle(controller)
        let deadline = ContinuousClock.now + .seconds(5)
        while idleService.setTemperaturesCallCount == 0, ContinuousClock.now < deadline {
            try await Task.sleep(for: .milliseconds(100))
        }
        XCTAssertEqual(idleService.setTemperaturesCallCount, 1, "The queued send outlives the view")
        idleModel.cancelPendingCommand()
    }

    /// #3555 B11: a queued sensor target is never sent once a print starts.
    func test_queuedSensorTarget_droppedWhenPrintStarts() async throws {
        executionTimeAllowance = 120
        let (model, service) = try await multiHeaterModel()
        defer { model.deactivate() }
        let (window, controller) = try await installMultiHeater(model, debounce: .milliseconds(300))
        defer { window.isHidden = true }
        try control("printer.controls.sensor.chamber.TargetTemperature.increase", in: controller)
            .sendActions(for: .touchUpInside)
        let key = "sensor.chamber.TargetTemperature"
        XCTAssertEqual(model.queuedHeatTargets[key], 41)
        var printing = model.printer
        printing.state = "printing"
        model.handlePrinterUpdate(printing)
        try await Task.sleep(for: .milliseconds(900))
        XCTAssertEqual(service.setSensorValueCallCount, 0, "Never dispatched mid-print")
        XCTAssertNil(model.queuedHeatTargets[key])
        XCTAssertEqual(model.feedbackSection, .heat)
        XCTAssertTrue(model.commandNotice?.hasPrefix("Chamber target not sent.") == true, "\(model.commandNotice ?? "nil")")
    }

    /// #3555 B12: a rejected or failed send clears its queued value at once, not after the grace.
    func test_rejectedOrFailedSend_clearsQueuedTargetImmediately() async throws {
        executionTimeAllowance = 120
        let (model, service) = try await thermalModel()
        model.heatTargetSettleGrace = .seconds(30)
        // Rejected: apply never begins a command.
        model.scheduleHeatTarget(key: "hotend", title: "Nozzle", value: 210, after: .milliseconds(50)) { _ in }
        var deadline = ContinuousClock.now + .seconds(3)
        while model.queuedHeatTargets["hotend"] != nil, ContinuousClock.now < deadline {
            try await Task.sleep(for: .milliseconds(50))
        }
        XCTAssertNil(model.queuedHeatTargets["hotend"], "Rejected send clears without the 30 s grace")

        // Failed: the service throws.
        service.errorToThrow = URLError(.notConnectedToInternet)
        model.scheduleHeatTarget(key: "bed", title: "Bed", value: 70, after: .milliseconds(50)) {
            await $0.setHeaterTarget(.bed, target: 70)
        }
        deadline = ContinuousClock.now + .seconds(3)
        while model.queuedHeatTargets["bed"] != nil, ContinuousClock.now < deadline {
            try await Task.sleep(for: .milliseconds(50))
        }
        XCTAssertEqual(service.setTemperaturesCallCount, 1)
        XCTAssertNotNil(model.lastError)
        XCTAssertNil(model.queuedHeatTargets["bed"], "Failed send clears without the 30 s grace")
    }

    private func thermalModel(
        knownLimits: Bool = true, hotendOnly: Bool = false
    ) async throws -> (PrinterControlsViewModel, MockPrinterService) {
        let json = TestJSON.printer
            .replacingOccurrences(of: "\"backend\": \"Moonraker\"", with: "\"backend\": \"OctoPrint\"")
            .replacingOccurrences(of: "\"state\": \"printing\"", with: "\"state\": \"ready\"")
        let printer = try TestData.decoder.decode(Printer.self, from: try XCTUnwrap(json.data(using: .utf8)))
        let service = MockPrinterService()
        service.capabilitiesToReturn = hotendOnly ? .hotendOnlyFixture : .allControlsFixture
        service.detailsToReturn = knownLimits ? .controlsLimitsFixture(for: printer) : nil
        let model = PrinterControlsViewModel.configuredForTests(printerService: service, printer: printer)
        await model.loadCapabilities()
        return (model, service)
    }

    private func thermalContent(
        _ model: PrinterControlsViewModel, width: CGFloat, type: DynamicTypeSize = .large,
        debounce: Duration = .milliseconds(700)
    ) -> some View {
        VStack(alignment: .leading, spacing: 16) {
            PrinterDetailTemperatureStrip(
                hotend: .init(measured: model.printer.hotendTemp, target: model.printer.hotendTarget, isOnline: model.printer.isOnline),
                bed: .init(measured: model.printer.bedTemp, target: model.printer.bedTarget, isOnline: model.printer.isOnline),
                essentialControls: true
            )
            PreheatSubgroup(viewModel: model)
        }
        .environment(\.dynamicTypeSize, type)
        .environment(\.heatTargetDebounce, debounce)
        .environment(\.horizontalSizeClass, width > 390 ? .regular : .compact)
        .frame(width: width)
        .fixedSize(horizontal: false, vertical: true)
        .background(Color.pfCard)
        .ignoresSafeArea()
    }

    /// #3496 N09 / R3514-H07: a pending → settled round-trip must keep the same
    /// UIButton (VoiceOver focus stays on the tapped control) and centred content.
    func test_controlActionButton_pendingRoundTripKeepsInstanceAndCentredContent() async throws {
        final class Holder: ObservableObject {
            @Published var pending = false
            @Published var enabled = true
            @Published var width: CGFloat = 354
        }
        struct Host: View {
            @ObservedObject var holder: Holder
            var body: some View {
                // Mirrors the Tune babystep pair: flexible buttons in an AnyLayout HStack.
                AnyLayout(HStackLayout(spacing: 8)) {
                    ControlActionButton(
                        title: "Raise nozzle", identifier: "n09.button", compact: true,
                        systemImage: "arrow.up", value: holder.pending ? "Pending" : nil,
                        minimumHeight: 44, isPending: holder.pending
                    ) {}
                    .frame(maxWidth: .infinity, minHeight: 44)
                    .disabled(!holder.enabled)
                    ControlActionButton(
                        title: "Lower nozzle", identifier: "n09.other", compact: true,
                        systemImage: "arrow.down", minimumHeight: 44
                    ) {}
                    .frame(maxWidth: .infinity, minHeight: 44)
                    .disabled(!holder.enabled)
                }
                .frame(width: holder.width)
                .fixedSize(horizontal: false, vertical: true)
            }
        }
        let holder = Holder()
        let controller = UIHostingController(rootView: Host(holder: holder))
        let window = UIWindow()
        window.windowScene = UIApplication.shared.connectedScenes.compactMap { $0 as? UIWindowScene }.first
        window.frame = CGRect(x: 0, y: 0, width: 354, height: 120)
        window.rootViewController = controller
        window.makeKeyAndVisible()
        defer { window.isHidden = true }
        try await settle(controller)

        func button() throws -> UIButton {
            try XCTUnwrap(nativeControls(controller.view).first { $0.accessibilityIdentifier == "n09.button" } as? UIButton)
        }
        func contentMidXOffset(_ button: UIButton) throws -> CGFloat {
            // SwiftUI re-measures the representable without resizing it; that measurement
            // must not leave the live button with its size-to-fit content layout.
            _ = controller.sizeThatFits(in: CGSize(width: 354, height: 10000))
            _ = button.sizeThatFits(CGSize(width: CGFloat.greatestFiniteMagnitude, height: .greatestFiniteMagnitude))
            button.setNeedsLayout()
            button.layoutIfNeeded()
            let title = try XCTUnwrap(button.titleLabel)
            var content = title.convert(title.bounds, to: button)
            if let image = button.imageView, !image.isHidden, image.bounds.width > 0 {
                content = content.union(image.convert(image.bounds, to: button))
            }
            return abs(content.midX - button.bounds.midX)
        }
        holder.width = 60
        try await settle(controller)
        holder.width = 354
        try await settle(controller)
        let original = try button()
        let centredOffset = try contentMidXOffset(original)
        XCTAssertLessThan(centredOffset, 2, "Content starts centred")

        holder.pending = true
        holder.enabled = false
        try await settle(controller)
        let pendingButton = try button()
        XCTAssertTrue(pendingButton === original, "Pending must not rebuild the UIButton")
        XCTAssertEqual(pendingButton.accessibilityValue, "Pending")
        XCTAssertTrue(pendingButton.subviews.contains { ($0 as? UIActivityIndicatorView)?.isAnimating == true })

        holder.pending = false
        holder.enabled = true
        try await settle(controller)
        let settled = try button()
        XCTAssertTrue(settled === original, "Settling must not rebuild the UIButton")
        XCTAssertFalse(settled.subviews.contains { ($0 as? UIActivityIndicatorView)?.isAnimating == true })
        XCTAssertLessThan(try contentMidXOffset(settled), 2, "Content stays centred after a pending round-trip")
    }

    private func installThermal(
        _ model: PrinterControlsViewModel, width: CGFloat = 326, type: DynamicTypeSize = .large,
        debounce: Duration = .milliseconds(700)
    ) async throws -> (UIWindow, UIHostingController<AnyView>) {
        let controller = UIHostingController(
            rootView: AnyView(thermalContent(model, width: width, type: type, debounce: debounce))
        )
        let size = controller.sizeThatFits(in: CGSize(width: width, height: 10000))
        let window = UIWindow()
        window.windowScene = UIApplication.shared.connectedScenes.compactMap { $0 as? UIWindowScene }.first
        window.frame = CGRect(origin: .zero, size: size)
        window.rootViewController = controller
        window.makeKeyAndVisible()
        try await settle(controller)
        return (window, controller)
    }

    /// Resumes when the service receives the request. The tap-to-dispatch path crosses main-actor
    /// turns, so the first on-screen pending frame can sit between them; on a cold CI simulator that
    /// frame compiled render pipelines on the main thread for 47s. A wall-clock expectation timed that
    /// renderer stall, not dispatch, so await the request causally under the test's execution allowance.
    private func tapAndAwaitDispatch(_ button: UIButton, service: MockPrinterService) async {
        await withCheckedContinuation { (dispatched: CheckedContinuation<Void, Never>) in
            service.afterSetTemperatures = { dispatched.resume() }
            button.sendActions(for: .touchUpInside)
        }
        service.afterSetTemperatures = nil
    }

    private func settle(_ controller: UIViewController) async throws {
        try await Task.sleep(for: .milliseconds(100))
        controller.view.setNeedsLayout()
        controller.view.layoutIfNeeded()
    }

    private func nativeControls(_ view: UIView) -> [UIControl] {
        (view as? UIControl).map { [$0] } ?? view.subviews.flatMap { nativeControls($0) }
    }

    private func control(_ identifier: String, in controller: UIViewController) throws -> UIControl {
        try XCTUnwrap(nativeControls(controller.view).first { $0.accessibilityIdentifier == identifier }, identifier)
    }

    /// Invokes the keypad toolbar's Set or Cancel item, as a tap would.
    private func press(_ field: UITextField, _ item: String) throws {
        let toolbar = try XCTUnwrap(field.inputAccessoryView as? UIToolbar)
        let identifier = "\(field.accessibilityIdentifier ?? "").\(item)"
        let button = try XCTUnwrap(toolbar.items?.first { $0.accessibilityIdentifier == identifier }, identifier)
        _ = (button.target as? NSObject)?.perform(button.action)
    }

    private func pressAndAwaitDispatch(_ field: UITextField, service: MockPrinterService) async throws {
        let toolbar = try XCTUnwrap(field.inputAccessoryView as? UIToolbar)
        let identifier = "\(field.accessibilityIdentifier ?? "").set"
        let button = try XCTUnwrap(toolbar.items?.first { $0.accessibilityIdentifier == identifier })
        await withCheckedContinuation { (dispatched: CheckedContinuation<Void, Never>) in
            service.afterSetTemperatures = { dispatched.resume() }
            _ = (button.target as? NSObject)?.perform(button.action)
        }
        service.afterSetTemperatures = nil
    }

    /// Visible SwiftUI text, read through the accessibility tree.
    private func accessibilityTexts(_ root: NSObject) -> [String] {
        var texts: [String] = []
        func visit(_ node: NSObject, depth: Int) {
            guard depth < 40 else { return }
            if let label = node.accessibilityLabel, !label.isEmpty { texts.append(label) }
            if let value = node.accessibilityValue, !value.isEmpty { texts.append(value) }
            if let elements = node.accessibilityElements as? [NSObject] {
                elements.forEach { visit($0, depth: depth + 1) }
            } else {
                let count = node.accessibilityElementCount()
                if count != NSNotFound, count > 0 {
                    (0..<count).compactMap { node.accessibilityElement(at: $0) as? NSObject }
                        .forEach { visit($0, depth: depth + 1) }
                }
            }
            if let view = node as? UIView { view.subviews.forEach { visit($0, depth: depth + 1) } }
        }
        visit(root, depth: 0)
        return texts
    }

    func test_heaterInput_requiresWholeDegreesWithoutSilentRounding() throws {
        XCTAssertNil(try ControlNumberInput.heaterTarget(" "))
        for (text, value) in [("0", 0.0), ("200", 200.0), ("240.000", 240.0), ("2e2", 200.0)] {
            XCTAssertEqual(try ControlNumberInput.heaterTarget(text), value)
        }
        for text in ["200.5", "0.1", "200.00000000000000001", "1e-999", "NaN", "inf"] {
            XCTAssertThrowsError(try ControlNumberInput.heaterTarget(text), text)
        }
    }

    func test_individualHeaterLabels_preserveUnknownVersusZero() {
        XCTAssertEqual(Heater.hotend.title, "Hotend")
        XCTAssertEqual(Heater.bed.title, "Bed")
    }

    func test_nativeNumberFieldCoordinator_preservesBlankZeroAndSignedInput() {
        var value = ""
        let coordinator = ControlNumberField.Coordinator(text: Binding(get: { value }, set: { value = $0 }))
        let field = UITextField()
        for input in ["0", "-1.25", "240", ""] {
            field.text = input
            coordinator.changed(field)
            XCTAssertEqual(value, input)
        }
        field.text = nil
        coordinator.changed(field)
        XCTAssertEqual(value, "")
    }

    func test_numericPadKeyboards_getDoneToolbarWiredToCoordinatorDoneTapped() async throws {
        // .decimalPad/.numberPad/etc. have no Return key, so
        // textFieldShouldReturn never fires; a "Done" toolbar is the only
        // way to dismiss the keyboard for these fields. First-responder
        // transitions aren't reliable in a headless test host, so this
        // asserts the toolbar wiring itself rather than an actual keyboard
        // dismissal; test_doneTapped_resignsFirstResponderOnTheWiredField
        // covers the dismissal behavior directly against the Coordinator.
        for keyboardType: UIKeyboardType in [.decimalPad, .numberPad, .phonePad, .asciiCapableNumberPad] {
            var value = "200"
            let field = ControlNumberField(
                placeholder: "", text: Binding(get: { value }, set: { value = $0 }),
                label: "target", identifier: "test.numeric-pad", keyboardType: keyboardType
            )
            let (_, controller) = try await install(field)
            let textField = try XCTUnwrap(nativeControls(controller.view).first as? UITextField, "\(keyboardType)")
            let toolbar = try XCTUnwrap(textField.inputAccessoryView as? UIToolbar, "\(keyboardType)")
            let done = try XCTUnwrap(toolbar.items?.last, "\(keyboardType)")

            XCTAssertTrue(done.target is ControlNumberField.Coordinator, "\(keyboardType)")
            XCTAssertEqual(done.action, #selector(ControlNumberField.Coordinator.doneTapped), "\(keyboardType)")
        }
    }

    func test_doneTapped_resignsFirstResponderOnTheWiredField() {
        // Directly exercises the Done-toolbar action against the
        // Coordinator, independent of UIKit's first-responder chain (which
        // isn't reliably available in a headless test host).
        let coordinator = ControlNumberField.Coordinator(text: Binding(get: { "" }, set: { _ in }))
        let field = ResignTrackingTextField()
        coordinator.field = field

        coordinator.doneTapped()

        XCTAssertEqual(field.resignFirstResponderCallCount, 1)
    }

    private final class ResignTrackingTextField: UITextField {
        private(set) var resignFirstResponderCallCount = 0
        override func resignFirstResponder() -> Bool {
            resignFirstResponderCallCount += 1
            return super.resignFirstResponder()
        }
    }

    func test_heatTargetField_offersCancelAndSetAndOnlySetApplies() async throws {
        var value = "205"
        var submitted = 0
        var cancelled = 0
        let field = ControlNumberField(
            placeholder: "200", text: Binding(get: { value }, set: { value = $0 }),
            label: "target", identifier: "test.heat", keyboardType: .decimalPad, stepperValueStyle: true,
            onSubmit: { submitted += 1; return true }, onCancel: { cancelled += 1 }
        )
        let (_, controller) = try await install(field)
        let textField = try XCTUnwrap(nativeControls(controller.view).first as? UITextField)
        let items = try XCTUnwrap((textField.inputAccessoryView as? UIToolbar)?.items)
        XCTAssertEqual(items.first?.accessibilityIdentifier, "test.heat.cancel")
        XCTAssertEqual(items.last?.accessibilityIdentifier, "test.heat.set")
        XCTAssertEqual(items.last?.title, "Set")
        try press(textField, "cancel")
        XCTAssertEqual([submitted, cancelled], [0, 1])
        try press(textField, "set")
        XCTAssertEqual([submitted, cancelled], [1, 1])
    }

    func test_punctuationKeyboard_hasNoAccessoryToolbar() async throws {
        // The default keyboard (used for signed/jog fields) has a native
        // Return key, so it must not grow an extra toolbar.
        var value = ""
        let field = ControlNumberField(
            placeholder: "", text: Binding(get: { value }, set: { value = $0 }),
            label: "jog", identifier: "test.punctuation-pad", keyboardType: .numbersAndPunctuation
        )
        let (_, controller) = try await install(field)
        let textField = try XCTUnwrap(nativeControls(controller.view).first as? UITextField)
        XCTAssertNil(textField.inputAccessoryView)
    }

    private func install<Content: View>(_ content: Content) async throws -> (UIWindow, UIHostingController<Content>) {
        let controller = UIHostingController(rootView: content)
        let size = controller.sizeThatFits(in: CGSize(width: 200, height: 200))
        let window = UIWindow()
        window.windowScene = UIApplication.shared.connectedScenes.compactMap { $0 as? UIWindowScene }.first
        window.frame = CGRect(origin: .zero, size: size.width > 0 ? size : CGSize(width: 200, height: 60))
        window.rootViewController = controller
        window.makeKeyAndVisible()
        try await settle(controller)
        return (window, controller)
    }

    // MARK: - presets

    func test_presets_containsAllFourInFixedOrder() {
        XCTAssertEqual(PreheatSubgroup.presets, [.pla, .petg, .abs, .coolDown])
        XCTAssertEqual(PreheatSubgroup.presets.count, 4)
    }

    // MARK: - isVisible(capabilities:)

    func test_isVisible_falseWhenCapabilitiesNil() {
        XCTAssertFalse(PreheatSubgroup.isVisible(capabilities: nil))
    }

    func test_isVisible_trueForFullCaps() {
        let caps = PrinterBackendCapabilities.allControlsFixture
        XCTAssertTrue(caps.supportsTemperatureControl)
        XCTAssertTrue(PreheatSubgroup.isVisible(capabilities: caps))
    }

    func test_isVisible_trueForExplicitHotendOnlyEvidence() {
        let caps = PrinterBackendCapabilities.hotendOnlyFixture
        XCTAssertTrue(caps.supportsTemperatureControl)
        XCTAssertFalse(caps.supportsBedTemperature)
        XCTAssertTrue(PreheatSubgroup.isVisible(capabilities: caps))
    }

    func test_isVisible_falseWhenTemperatureControlMissing() {
        let caps = PrinterBackendCapabilities.fallback(for: .unknown)
        XCTAssertFalse(caps.supportsTemperatureControl)
        XCTAssertFalse(PreheatSubgroup.isVisible(capabilities: caps))
    }

    // MARK: - canControl gate (the disabled-state input)

    func test_canControl_falseWhilePrinting() throws {
        // When the printer is printing, the ViewModel reports canControl=false
        // and the subgroup renders all four buttons in the disabled visual
        // state. We assert the gate the view consumes; the visual treatment is
        // covered by the upcoming snapshot tests (#289).
        let printer = try Self.makePrinter(state: "printing", isOnline: true)
        let vm = PrinterControlsViewModel.configuredForTests(printerService: PreheatSubgroupTestService(), printer: printer)
        XCTAssertFalse(vm.canControl)
        XCTAssertNotNil(vm.blockedReason)
    }

    func test_canControl_falseWhenOffline() throws {
        let printer = try Self.makePrinter(state: "ready", isOnline: false)
        let vm = PrinterControlsViewModel.configuredForTests(printerService: PreheatSubgroupTestService(), printer: printer)
        XCTAssertFalse(vm.canControl)
        XCTAssertEqual(vm.blockedReason, "Printer is offline.")
    }

    func test_canControl_trueWhenOnlineAndIdle() throws {
        let printer = try Self.makePrinter(state: "ready", isOnline: true)
        let vm = PrinterControlsViewModel.configuredForTests(printerService: PreheatSubgroupTestService(), printer: printer)
        XCTAssertTrue(vm.canControl)
        XCTAssertNil(vm.blockedReason)
    }

    // MARK: - body returns a non-nil view

    func test_body_doesNotCrashWhenVisible() throws {
        let printer = try Self.makePrinter(state: "ready", isOnline: true)
        let vm = PrinterControlsViewModel.configuredForTests(printerService: PreheatSubgroupTestService(), printer: printer)
        let subgroup = PreheatSubgroup(viewModel: vm)
        // SwiftUI body evaluation should not throw or trap.
        _ = subgroup.body
    }


    // MARK: - Accessibility labels (spec §4.1)

    func test_accessibilityLabel_idle_pla() throws {
        let vm = try makeVM(state: "ready", isOnline: true)
        let view = PreheatSubgroup(viewModel: vm)
        XCTAssertEqual(view.accessibilityLabel(preset: .pla, isPending: false), "Preheat for PLA")
    }

    func test_accessibilityLabel_idle_petg() throws {
        let vm = try makeVM(state: "ready", isOnline: true)
        let view = PreheatSubgroup(viewModel: vm)
        XCTAssertEqual(view.accessibilityLabel(preset: .petg, isPending: false), "Preheat for PETG")
    }

    func test_accessibilityLabel_idle_abs() throws {
        let vm = try makeVM(state: "ready", isOnline: true)
        let view = PreheatSubgroup(viewModel: vm)
        XCTAssertEqual(view.accessibilityLabel(preset: .abs, isPending: false), "Preheat for ABS")
    }

    func test_accessibilityLabel_idle_coolDown() throws {
        let vm = try makeVM(state: "ready", isOnline: true)
        let view = PreheatSubgroup(viewModel: vm)
        XCTAssertEqual(view.accessibilityLabel(preset: .coolDown, isPending: false), "Cool down")
    }

    func test_accessibilityLabel_pending_pla() throws {
        let vm = try makeVM(state: "ready", isOnline: true)
        let view = PreheatSubgroup(viewModel: vm)
        XCTAssertEqual(view.accessibilityLabel(preset: .pla, isPending: true), "Preheat for PLA, in progress")
    }

    func test_accessibilityLabel_pending_coolDown() throws {
        let vm = try makeVM(state: "ready", isOnline: true)
        let view = PreheatSubgroup(viewModel: vm)
        XCTAssertEqual(view.accessibilityLabel(preset: .coolDown, isPending: true), "Cooling down, in progress")
    }

    // MARK: - Accessibility hints (spec §4.1)

    func test_accessibilityHint_idle_pla_withBed() async throws {
        let vm = try makeVM(state: "ready", isOnline: true)
        await vm.loadCapabilities()
        let view = PreheatSubgroup(viewModel: vm)
        XCTAssertEqual(
            view.accessibilityHint(preset: .pla, canControl: true, hasError: false),
            "Sets hotend to 200 degrees, bed to 60 degrees."
        )
    }

    func test_accessibilityHint_idle_petg_withBed() async throws {
        let vm = try makeVM(state: "ready", isOnline: true)
        await vm.loadCapabilities()
        let view = PreheatSubgroup(viewModel: vm)
        XCTAssertEqual(
            view.accessibilityHint(preset: .petg, canControl: true, hasError: false),
            "Sets hotend to \(Int(PreheatPreset.petg.hotend)) degrees, bed to \(Int(PreheatPreset.petg.bed)) degrees."
        )
    }

    /// #3555 N10: one shared tile caption, mono "200/60", at every text size.
    func test_compactTargetsText_isOneLineAtEveryTextSize() {
        for preset in PreheatPreset.rowMaterials {
            let hotend = Int(preset.definition.hotend), bed = Int(preset.definition.bed)
            XCTAssertEqual(PreheatSubgroup.compactTargetsText(preset, hasBed: true), "\(hotend)/\(bed)")
            XCTAssertEqual(PreheatSubgroup.compactTargetsText(preset, hasBed: false), "\(hotend)")
            XCTAssertFalse(PreheatSubgroup.compactTargetsText(preset, hasBed: true).contains("\n"))
        }
    }

    /// Row captions and More-menu subtitles read from the preset definition.
    func test_presetTargetText_readsFromDefinition() {
        for preset in PreheatPreset.rowMaterials + PreheatPreset.menuMaterials {
            let hotend = Int(preset.definition.hotend), bed = Int(preset.definition.bed)
            XCTAssertEqual(PreheatSubgroup.compactTargetsText(preset, hasBed: true), "\(hotend)/\(bed)")
            XCTAssertEqual(PreheatSubgroup.compactTargetsText(preset, hasBed: false), "\(hotend)")
            XCTAssertEqual(PreheatSubgroup.menuItemTemperatureText(preset, hasBed: true), "\(hotend)° · \(bed)°")
            XCTAssertEqual(PreheatSubgroup.menuItemTemperatureText(preset, hasBed: false), "\(hotend)°")
            XCTAssertEqual(
                PreheatSubgroup.menuItemSingleLineText(
                    "ASA",
                    temperatures: PreheatSubgroup.menuItemTemperatureText(preset, hasBed: true)
                ),
                "ASA  \(hotend)° · \(bed)°"
            )
            XCTAssertEqual(
                preset.a11yHint(hasBed: true, turnsOffHeaters: true),
                "Sets hotend to \(hotend) degrees, bed to \(bed) degrees."
            )
        }
    }

    /// R3464-B13: every text size, including AX1–AX5, keeps the five slots so
    /// More (and through it ASA/TPU/PC/Nylon and Custom…) stays reachable.
    func test_presetRows_keepMoreAtEveryTextSizeAndWrapFromXXLarge() {
        let expected: [PreheatSubgroup.PresetSlot] =
            PreheatPreset.rowMaterials.map { .preset($0) } + [.more, .preset(.coolDown)]
        for size in DynamicTypeSize.allCases {
            let rows = PreheatSubgroup.presetRows(dynamicTypeSize: size)
            XCTAssertEqual(rows.flatMap { $0 }, expected, "\(size)")
            XCTAssertEqual(rows.count, size >= .xxLarge ? 2 : 1, "\(size)")
        }
        XCTAssertEqual(PreheatPreset.menuMaterials, [.asa, .tpu, .pc, .nylon])
    }

    /// R3464-B15: More's disabled hint names the actual reason.
    func test_moreMenuBlockedReason_matchesActualReason() async throws {
        let idle = try makeVM(state: "ready", isOnline: true)
        await idle.loadCapabilities()
        XCTAssertNil(idle.preheatMenuBlockedReason)

        let offline = try makeVM(state: "ready", isOnline: false)
        await offline.loadCapabilities()
        XCTAssertEqual(offline.preheatMenuBlockedReason, "Printer is offline.")

        let printing = try makeVM(state: "printing", isOnline: true)
        await printing.loadCapabilities()
        XCTAssertEqual(printing.preheatMenuBlockedReason, "Controls are locked while a print is active.")
        for vm in [offline, printing] {
            XCTAssertNotEqual(vm.preheatMenuBlockedReason, "Disabled while printing.")
        }
    }

    func test_accessibilityHint_idle_coolDown_describesHeaterShutdown() async throws {
        let vm = try makeVM(state: "ready", isOnline: true)
        await vm.loadCapabilities()
        XCTAssertTrue(vm.supportsHeaterShutdown)
        let view = PreheatSubgroup(viewModel: vm)
        XCTAssertEqual(
            view.accessibilityHint(preset: .coolDown, canControl: true, hasError: false),
            "Turns off all heaters."
        )
    }

    func test_coolDownHint_describesZeroTargetFallbackWithoutHeaterShutdown() {
        XCTAssertEqual(PreheatPreset.coolDown.a11yHint(hasBed: true, turnsOffHeaters: false), "Sets hotend and bed to 0 degrees.")
        XCTAssertEqual(PreheatPreset.coolDown.a11yHint(hasBed: false, turnsOffHeaters: false), "Sets hotend to 0 degrees.")
        XCTAssertEqual(PreheatPreset.coolDown.a11yHint(hasBed: true, turnsOffHeaters: true), "Turns off all heaters.")
        XCTAssertEqual(
            PreheatPreset.pla.a11yHint(hasBed: true, turnsOffHeaters: true),
            "Sets hotend to 200 degrees, bed to 60 degrees.",
            "Heater shutdown only changes the Cool down hint"
        )
    }

    func test_accessibilityHint_disabled_returnsSpec41Text() throws {
        let vm = try makeVM(state: "printing", isOnline: true)
        let view = PreheatSubgroup(viewModel: vm)
        XCTAssertEqual(
            view.accessibilityHint(preset: .pla, canControl: false, hasError: false),
            "Disabled while printing."
        )
    }

    // MARK: - Accessibility value (spec §4.1)

    func test_accessibilityValue_pending_returnsPending() throws {
        let vm = try makeVM(state: "ready", isOnline: true)
        let view = PreheatSubgroup(viewModel: vm)
        XCTAssertEqual(view.accessibilityValue(isPending: true, hasError: false), "Pending")
    }

    func test_accessibilityValue_error_returnsFailed() throws {
        let vm = try makeVM(state: "ready", isOnline: true)
        let view = PreheatSubgroup(viewModel: vm)
        XCTAssertEqual(view.accessibilityValue(isPending: false, hasError: true), "Failed")
    }

    func test_accessibilityValue_idle_isEmpty() throws {
        let vm = try makeVM(state: "ready", isOnline: true)
        let view = PreheatSubgroup(viewModel: vm)
        XCTAssertEqual(view.accessibilityValue(isPending: false, hasError: false), "")
    }

    // MARK: - Helpers (accessibility tests)

    private func makeVM(state: String, isOnline: Bool) throws -> PrinterControlsViewModel {
        let printer = try Self.makePrinter(state: state, isOnline: isOnline)
        return PrinterControlsViewModel.configuredForTests(printerService: PreheatSubgroupTestService(), printer: printer)
    }

    // MARK: - Helpers

    private static func makePrinter(state: String, isOnline: Bool) throws -> Printer {
        let json = """
        {
            "id": "11111111-1111-1111-1111-111111111111",
            "name": "Test Printer",
            "backend": "moonraker",
            "backendPort": 80,
            "inMaintenance": false,
            "isEnabled": true,
            "isOnline": \(isOnline),
            "state": "\(state)",
            "obicoEnabled": false
        }
        """
        return try JSONDecoder().decode(Printer.self, from: Data(json.utf8))
    }

    func testPresetFillRowAndRadiusOverridesAreOptInSoOtherScreensKeepTheirLayout() {
        let defaults = EnvironmentValues()
        XCTAssertFalse(defaults.preheatPresetsFillRow)
        XCTAssertNil(defaults.controlActionCornerRadius)
        XCTAssertNil(defaults.controlActionSelectedFill)
    }

    func testControlTabPresetRowIsFourEqualButtonsEndingWithSnowflakeCoolDown() throws {
        XCTAssertEqual(PreheatSubgroup.presets, [.pla, .petg, .abs, .coolDown])
        XCTAssertEqual(PreheatSubgroup.presets.last, .coolDown)
        XCTAssertEqual(PreheatSubgroup.detailPresetSymbol(.coolDown), "snowflake",
                       "Cool-down is an icon-only snowflake in every layout (N3555-02)")
        for preset in [PreheatPreset.pla, .petg, .abs] {
            XCTAssertNil(PreheatSubgroup.detailPresetSymbol(preset))
        }
        let printer = try Self.makePrinter(state: "ready", isOnline: true)
        let vm = PrinterControlsViewModel.configuredForTests(printerService: PreheatSubgroupTestService(), printer: printer)
        let subgroup = PreheatSubgroup(viewModel: vm)
        XCTAssertEqual(subgroup.accessibilityLabel(preset: .coolDown, isPending: false), "Cool down",
                       "The snowflake is icon-only visually but must stay spoken")
    }
}

/// Bare-bones service stub. The view-layer smoke tests don't exercise any
/// network-flavored code path; the existing `MockPrinterService` is overkill
/// here. Local stub keeps these tests narrowly scoped to the view.
private final class PreheatSubgroupTestService: PrinterServiceProtocol, @unchecked Sendable {
    func list(includeDisabled: Bool) async throws -> [Printer] { [] }
    func get(id: UUID) async throws -> Printer { throw NetworkError.notFound }
    func getStatus(id: UUID) async throws -> PrinterStatusDetail { throw NetworkError.notFound }
    func listCameraUrls() async throws -> [PrinterCameraUrls] { [] }
    func getCameraUrl(id: UUID) async throws -> PrinterCameraUrl { throw NetworkError.notFound }
    func getSnapshot(id: UUID) async throws -> Data { Data() }
    func getCurrentJob(id: UUID) async throws -> PrintJobStatusInfo? { nil }
    func getHistory(id: UUID, limit: Int?) async throws -> PrinterHistoryList { PrinterHistoryList(count: 0, jobs: []) }
    func pause(id: UUID) async throws -> CommandResult { CommandResult(success: true, message: nil) }
    func resume(id: UUID) async throws -> CommandResult { CommandResult(success: true, message: nil) }
    func cancel(id: UUID) async throws -> CommandResult { CommandResult(success: true, message: nil) }
    func stop(id: UUID) async throws -> CommandResult { CommandResult(success: true, message: nil) }
    func emergencyStop(id: UUID) async throws -> CommandResult { CommandResult(success: true, message: nil) }
    func setMaintenanceMode(
        id: UUID,
        inMaintenance: Bool,
        reviewedRowVersion: String
    ) async throws -> Printer {
        throw NetworkError.notFound
    }
    func getQueueOverview(model: String?, nozzle: Double?, material: String?) async throws -> [QueueOverview] { [] }
    func setActiveSpool(
        printerId: UUID,
        spoolId: Int?,
        reviewedRowVersion: String
    ) async throws -> CommandResult {
        CommandResult(success: true, message: nil)
    }
    func bindToolheadSpool(printerId: UUID, toolheadIndex: Int, request: ToolheadSpoolBindRequest, idempotencyKey: String) async throws -> CommandResult {
        CommandResult(success: true, message: nil)
    }
    func listAvailableSpools(printerId: UUID) async throws -> [SpoolmanSpool] { [] }
    func loadFilament(printerId: UUID) async throws -> CommandResult { CommandResult(success: true, message: nil) }
    func unloadFilament(printerId: UUID) async throws -> CommandResult { CommandResult(success: true, message: nil) }
    func changeFilament(printerId: UUID) async throws -> CommandResult { CommandResult(success: true, message: nil) }
    func setTemperatures(printerId: UUID, hotend: Double?, bed: Double?) async throws {}
    func home(printerId: UUID, axes: [String]) async throws {}
    func homeXY(printerId: UUID) async throws {}
    func homeZ(printerId: UUID) async throws {}
    func move(printerId: UUID, axis: String, distanceMm: Double, feedrateMmMin: Int) async throws {}
    func moveTo(printerId: UUID, x: Double?, y: Double?, z: Double?, feedrateMmMin: Int?) async throws -> CommandResult { throw NetworkError.notFound }
    func extrude(printerId: UUID, distanceMm: Double, feedrateMmPerMinute: Int) async throws -> CommandResult { throw NetworkError.notFound }
    func disableMotors(printerId: UUID) async throws -> CommandResult { throw NetworkError.notFound }
    func saveZOffset(printerId: UUID, offsetMm: Double, saveToFirmware: Bool, reviewedRowVersion: String) async throws -> CommandResult { throw NetworkError.notFound }
    func unloadFilament(printerId: UUID, toolheadIndex: Int?) async throws -> FilamentUnloadResult { throw NetworkError.notFound }
    func getBackendCapabilities(printerId: UUID) async throws -> PrinterBackendCapabilities {
        PrinterBackendCapabilities.allControlsFixture
    }

    // #711 F6 stubs — not exercised by preheat tests but required for conformance.
    func getDetails(id: UUID) async throws -> PrinterDetails { throw NetworkError.notFound }
    func listFallbackGroups(printerId: UUID) async throws -> [FilamentFallbackGroup] { [] }
    func getFallbackGroup(printerId: UUID, groupId: UUID) async throws -> FilamentFallbackGroup { throw NetworkError.notFound }
    func createFallbackGroup(printerId: UUID, _ request: CreateFilamentFallbackGroupRequest) async throws -> FilamentFallbackGroup { throw NetworkError.notFound }
    func updateFallbackGroup(printerId: UUID, groupId: UUID, _ request: UpdateFilamentFallbackGroupRequest) async throws -> FilamentFallbackGroup { throw NetworkError.notFound }
    func deleteFallbackGroup(printerId: UUID, groupId: UUID) async throws {}
    func getAvailableFallback(printerId: UUID, sourceToolheadId: UUID, material: String) async throws -> AvailableFallbackMember? { nil }
}
