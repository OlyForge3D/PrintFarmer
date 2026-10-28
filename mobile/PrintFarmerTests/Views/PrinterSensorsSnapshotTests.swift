import XCTest
import SwiftUI
@testable import PrintFarmer

/// Focused contracts for the sensor rows embedded in production Tune.
/// Captures are review evidence only; there are no pixel references.
@MainActor
final class PrinterSensorsSnapshotTests: XCTestCase {

    func test_controlSensorRows_haveAccessibleTargetsAndDispatchOnlyTheSelectedSensor() async throws {
        var printer = try TestData.decodePrinter()
        printer.isOnline = true
        printer.state = "idle"
        printer.sensors = [
            PrinterSensor(
                id: "part-fan", displayName: "Part fan", kind: .fan, fanRole: .partCooling,
                readings: [SensorReading(quantity: .fanSpeed, value: .number(42), unit: .percent)],
                writable: [
                    SensorWritable(quantity: .fanSpeed, min: 0, max: 100, step: 5, unit: .percent)
                ]
            ),
            PrinterSensor(
                id: "hotend-fan", displayName: "Hotend fan", kind: .fan, fanRole: .hotend,
                readings: [SensorReading(quantity: .fanRpm, value: .number(4200), unit: .rpm)]
            ),
            PrinterSensor(
                id: "chamber", displayName: "Chamber", kind: .chamber,
                readings: [SensorReading(quantity: .targetTemperature, value: .number(40), unit: .celsius)],
                writable: [
                    SensorWritable(quantity: .targetTemperature, min: 0, max: 80, step: 1, unit: .celsius)
                ]
            ),
            PrinterSensor(
                id: "unavailable", displayName: "Auxiliary fan", kind: .fan, fanRole: .auxiliary,
                readings: [SensorReading(quantity: .fanSpeed, value: .number(0), unit: .percent)],
                available: false,
                writable: [
                    SensorWritable(quantity: .fanSpeed, min: 0, max: 100, step: 5, unit: .percent)
                ]
            )
        ]

        let service = MockPrinterService()
        var capabilities = PrinterBackendCapabilities.fallback(for: .moonraker)
        capabilities.supportsSensorControl = true
        service.capabilitiesToReturn = capabilities
        let model = PrinterControlsViewModel.configuredForTests(printerService: service, printer: printer)
        await model.loadCapabilities()
        defer { model.deactivate() }
        XCTAssertEqual(model.controllableSensors.map(\.id), ["part-fan", "chamber", "unavailable"])

        let rows = VStack(alignment: .leading, spacing: 8) {
            PrinterSensorControlRows(viewModel: model, compact: true)
        }
        .environment(\.heatTargetDebounce, .milliseconds(300))
        .padding(12)
        .frame(width: 390)
        .fixedSize(horizontal: false, vertical: true)
        let controller = host(rows)
        let window = UIWindow(frame: CGRect(x: 0, y: 0, width: 390, height: 844))
        window.rootViewController = controller
        window.isHidden = false
        defer { window.isHidden = true }
        try await settle(controller)
        let controls = nativeControls(in: controller.view)
        let sensorControls = controls.filter { $0.accessibilityIdentifier?.hasPrefix("printer.controls.sensor.") == true }
        XCTAssertEqual(Set(sensorControls.compactMap(\.accessibilityIdentifier)), [
            "printer.controls.sensor.chamber.TargetTemperature.increase",
            "printer.controls.sensor.chamber.TargetTemperature.decrease",
            "printer.controls.sensor.chamber.TargetTemperature.value"
        ], "Read-only and fan entries must not expose Tune actions")
        for control in sensorControls {
            XCTAssertTrue(control.isEnabled)
            XCTAssertGreaterThanOrEqual(control.bounds.width, 44)
            XCTAssertGreaterThanOrEqual(control.bounds.height, 44)
            let frame = control.convert(control.bounds, to: controller.view)
            XCTAssertGreaterThanOrEqual(frame.minX, 0)
            XCTAssertLessThanOrEqual(frame.maxX, 390)
            if let field = control as? UITextField {
                XCTAssertEqual(field.accessibilityLabel, "Chamber target in degrees Celsius")
                XCTAssertEqual(field.placeholder, "40", "The tappable value shows the current target")
                continue
            }
            let direction = control.accessibilityIdentifier?.hasSuffix(".increase") == true ? "Increase" : "Decrease"
            XCTAssertEqual(control.accessibilityLabel, "\(direction) Chamber target")
        }
        let attachment = XCTAttachment(image: UIGraphicsImageRenderer(bounds: controller.view.bounds).image { _ in
            controller.view.drawHierarchy(in: controller.view.bounds, afterScreenUpdates: true)
        })
        attachment.name = "review-only-production-PrinterSensorControlRows-mock-chamber-target-40-\(UIDevice.current.userInterfaceIdiom)"
        attachment.lifetime = .keepAlways
        add(attachment)
        XCTAssertEqual(service.setSensorValueCallCount, 0)
        let increase = try XCTUnwrap(sensorControls.first {
            $0.accessibilityIdentifier == "printer.controls.sensor.chamber.TargetTemperature.increase"
        })
        // A ± burst collapses into one request for the final value (#3547).
        for _ in 0..<3 {
            increase.sendActions(for: .touchUpInside)
            try await settle(controller)
        }
        XCTAssertEqual(service.setSensorValueCallCount, 0, "± waits for the debounce before sending")
        let deadline = ContinuousClock.now + .seconds(5)
        while service.setSensorValueCallCount == 0, ContinuousClock.now < deadline {
            try await settle(controller)
        }
        try await Task.sleep(for: .milliseconds(500))
        XCTAssertEqual(service.setSensorValueCallCount, 1)
        XCTAssertEqual(service.setSensorValueCalledWith?.printerId, printer.id)
        XCTAssertEqual(service.setSensorValueCalledWith?.sensorId, "chamber")
        XCTAssertEqual(service.setSensorValueCalledWith?.quantity, .targetTemperature)
        XCTAssertEqual(service.setSensorValueCalledWith?.value, 43)
        XCTAssertEqual(service.setFanSpeedCallCount, 0)
    }

    func test_controlSensorRows_doNotOfferActionsWithoutCapabilityOrOnlineReadback() async throws {
        for (supportsSensor, online, hasReading) in [(false, true, true), (true, false, true), (true, true, false)] {
            var printer = try TestData.decodePrinter()
            printer.isOnline = online
            printer.state = "idle"
            printer.sensors = [PrinterSensor(
                id: "chamber", displayName: "Chamber", kind: .chamber,
                readings: hasReading ? [SensorReading(quantity: .targetTemperature, value: .number(40), unit: .celsius)] : [],
                writable: [SensorWritable(quantity: .targetTemperature, min: 0, max: 80, step: 1, unit: .celsius)]
            )]
            let service = MockPrinterService()
            var capabilities = PrinterBackendCapabilities.fallback(for: .moonraker)
            capabilities.supportsSensorControl = supportsSensor
            service.capabilitiesToReturn = capabilities
            let model = PrinterControlsViewModel.configuredForTests(printerService: service, printer: printer)
            await model.loadCapabilities()
            defer { model.deactivate() }
            let controller = host(PrinterSensorControlRows(viewModel: model, compact: true))
            let window = UIWindow(frame: CGRect(x: 0, y: 0, width: 390, height: 844))
            window.rootViewController = controller
            window.isHidden = false
            defer { window.isHidden = true }
            try await settle(controller)
            XCTAssertFalse(nativeControls(in: controller.view).contains {
                $0.accessibilityIdentifier?.hasPrefix("printer.controls.sensor.") == true && $0.isEnabled
            }, "Unsupported, offline or unknown relative targets cannot offer actions")
            XCTAssertEqual(service.setSensorValueCallCount, 0)
        }
    }

    func test_controlSensorRows_areDisabledWhilePrintingOrPaused() async throws {
        for state in ["printing", "Paused"] {
            var printer = try TestData.decodePrinter()
            printer.isOnline = true
            printer.state = state
            printer.sensors = [PrinterSensor(
                id: "chamber", displayName: "Chamber", kind: .chamber,
                readings: [SensorReading(quantity: .targetTemperature, value: .number(40), unit: .celsius)],
                writable: [SensorWritable(quantity: .targetTemperature, min: 0, max: 80, step: 1, unit: .celsius)]
            )]
            let service = MockPrinterService()
            var capabilities = PrinterBackendCapabilities.fallback(for: .moonraker)
            capabilities.supportsSensorControl = true
            service.capabilitiesToReturn = capabilities
            let model = PrinterControlsViewModel.configuredForTests(printerService: service, printer: printer)
            await model.loadCapabilities()
            defer { model.deactivate() }
            XCTAssertTrue(model.canControlSensors, "This regression covers the print-state gate, not access checks")
            XCTAssertTrue(model.isPrintingOrPaused)

            let controller = host(PrinterSensorControlRows(viewModel: model, compact: true))
            let window = UIWindow(frame: CGRect(x: 0, y: 0, width: 390, height: 844))
            window.rootViewController = controller
            window.isHidden = false
            defer { window.isHidden = true }
            try await settle(controller)

            let controls = nativeControls(in: controller.view).filter {
                $0.accessibilityIdentifier?.hasPrefix("printer.controls.sensor.chamber.TargetTemperature.") == true
            }
            XCTAssertEqual(Set(controls.compactMap(\.accessibilityIdentifier)), [
                "printer.controls.sensor.chamber.TargetTemperature.increase",
                "printer.controls.sensor.chamber.TargetTemperature.decrease",
                "printer.controls.sensor.chamber.TargetTemperature.value"
            ], state)
            XCTAssertTrue(controls.allSatisfy { !$0.isEnabled }, "\(state) must disable the entire sensor target row")
            XCTAssertEqual(service.setSensorValueCallCount, 0)
        }
    }

    private func nativeControls(in view: UIView) -> [UIControl] {
        (view as? UIControl).map { [$0] } ?? view.subviews.flatMap { nativeControls(in: $0) }
    }

    private func settle(_ controller: UIViewController) async throws {
        try await Task.sleep(for: .milliseconds(100))
        controller.view.layoutIfNeeded()
    }

    private func host(_ view: some View) -> UIViewController {
        let host = UIHostingController(rootView: view)
        host.overrideUserInterfaceStyle = .light
        host.view.backgroundColor = .systemBackground
        return host
    }
}
