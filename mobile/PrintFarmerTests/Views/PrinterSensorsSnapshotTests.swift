import XCTest
import SwiftUI
import SnapshotTesting
@testable import PrintFarmer

@MainActor
final class PrinterSensorsSnapshotTests: XCTestCase {

    private var snapshotName: String? {
        UIDevice.current.userInterfaceIdiom == .pad ? "iPad" : nil
    }

    func test_statusSensorReadout() {
        let readout = PrinterSensorsReadout(sensors: [
            PrinterSensor(
                id: "nozzle", displayName: "Nozzle", kind: .nozzle,
                readings: [
                    SensorReading(quantity: .temperature, value: .number(205), unit: .celsius),
                    SensorReading(quantity: .targetTemperature, value: .number(210), unit: .celsius)
                ]
            ),
            PrinterSensor(
                id: "bed", displayName: "Bed", kind: .bed,
                readings: [
                    SensorReading(quantity: .temperature, value: .number(58), unit: .celsius),
                    SensorReading(quantity: .targetTemperature, value: .number(60), unit: .celsius)
                ]
            ),
            PrinterSensor(
                id: "chamber", displayName: "Chamber", kind: .chamber,
                readings: [SensorReading(quantity: .temperature, value: .number(37.5), unit: .celsius)]
            ),
            PrinterSensor(
                id: "part-fan", displayName: "Part fan", kind: .fan, fanRole: .partCooling,
                readings: [
                    SensorReading(quantity: .fanSpeed, value: .number(42), unit: .percent),
                    SensorReading(quantity: .fanRpm, value: .number(3840), unit: .rpm)
                ]
            ),
            PrinterSensor(id: "mcu", displayName: "MCU", kind: .mcu, available: false)
        ])
        .padding(12)
        .frame(width: 390)
        .fixedSize(horizontal: false, vertical: true)

        assertSnapshot(of: host(readout), as: .image(on: .iPhone13), named: snapshotName)
    }

    func test_controlSensorRows() async throws {
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
                readings: [SensorReading(quantity: .temperature, value: .number(37.5), unit: .celsius)],
                writable: [
                    SensorWritable(quantity: .temperature, min: 0, max: 80, step: 1, unit: .celsius)
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
        .padding(12)
        .frame(width: 390)
        .fixedSize(horizontal: false, vertical: true)
        assertSnapshot(of: host(rows), as: .image(on: .iPhone13), named: snapshotName)
    }

    private func host(_ view: some View) -> UIViewController {
        let host = UIHostingController(rootView: view)
        host.overrideUserInterfaceStyle = .light
        host.view.backgroundColor = .systemBackground
        return host
    }
}
