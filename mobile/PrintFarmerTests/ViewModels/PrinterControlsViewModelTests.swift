import Combine
import KeychainSwift
import Observation
import XCTest
@testable import PrintFarmer

extension PrinterControlsViewModel {
    static func configuredForTests(
        printerService: any PrinterServiceProtocol,
        printer: Printer,
        clock: @escaping @Sendable () -> Date = Date.init,
        serverID: UUID = UUID(),
        accessCheck: @escaping @MainActor () -> String? = { nil }
    ) -> PrinterControlsViewModel {
        let composition = PrinterControlsComposition(
            identity: .init(serverID: serverID, generation: 0, revision: 0),
            printerService: printerService
        )
        let model = PrinterControlsViewModel(composition: composition, printer: printer, clock: clock)
        model.configureAccess(serverID: serverID, userID: UUID(), accessCheck)
        return model
    }
}

extension PrinterDetails {
    static func controlsLimitsFixture(
        for printer: Printer, hotend: Int? = 280, bed: Int? = 120, hasBed: Bool? = true
    ) -> PrinterDetails {
        PrinterDetails(
            id: printer.id, name: printer.name, backend: printer.backend,
            capabilities: PrinterHardwareCapabilities(
                maxBuildVolumeX: nil, maxBuildVolumeY: nil, maxBuildVolumeZ: nil,
                maxHotendTemp: hotend, maxBedTemp: bed, hasHeatedBed: hasBed
            )
        )
    }
}

@MainActor
final class PrinterControlsViewModelTests: XCTestCase {

    @MainActor
    private final class AccessGate {
        var isAllowed = true
    }

    private var mockService: MockPrinterService!

    override func setUp() async throws {
        try await super.setUp()
        mockService = MockPrinterService()
    }

    override func tearDown() async throws {
        mockService = nil
        try await super.tearDown()
    }

    // MARK: - Helpers

    func test_feedbackSection_coversEveryEssentialCommand() {
        let target = SafetyVector3Dto(x: 1, y: 1, z: 1)
        let commands: [(ControlCommand.Kind, ControlCommand.Section)] = [
            (.preheat(.pla, hotendTarget: 200, bedTarget: 60), .heat),
            (.heater(.hotend, target: 200), .heat),
            (.heaterTargets(hotend: 200, bed: 60), .heat),
            (.home(axes: ["X", "Y", "Z"]), .motion),
            (.jog(axis: "X", distanceMm: 1), .motion),
            (.moveTo(x: 1, y: 1, z: 1, feedrateMmMin: nil), .motion),
            (.disableMotors, .motion),
            (.calibrationHome, .motion),
            (.calibrationPosition(target: target, centered: true), .motion),
            (.calibrationAdjust(delta: 0.01, target: target), .motion),
            (.calibrationSave(offsetMm: 0), .motion),
            (.extrusion(distanceMm: 10, feedrateMmMin: 60), .material),
            (.filament(.load), .material),
            (.filament(.unload), .material),
            (.filament(.change), .material)
        ]
        for (kind, section) in commands {
            XCTAssertEqual(ControlCommand(kind: kind, startedAt: Date()).section, section)
        }
    }

    func test_feedbackSection_survivesOutcomesAndFollowsNextCommand() async throws {
        var printer = try idlePrinter()
        printer.homedAxes = ""
        let model = try makeViewModel(printer: printer, capabilities: Self.fullCaps)
        await model.loadCapabilities()
        await model.homeXY()
        XCTAssertEqual(model.feedbackSection, .motion)
        XCTAssertNotNil(model.pendingCommand)

        await model.preheat(.pla)
        XCTAssertEqual(model.feedbackSection, .motion, "A rejected concurrent action must not steal pending feedback")
        printer.homedAxes = "xy"
        model.handlePrinterUpdate(printer)
        XCTAssertNil(model.pendingCommand)
        XCTAssertEqual(model.feedbackSection, .motion)
        XCTAssertTrue(model.commandNotice?.contains("Matching telemetry") == true)

        mockService.errorToThrow = NetworkError.timeout
        await model.setHeaterTarget(.hotend, target: 200)
        XCTAssertEqual(model.feedbackSection, .heat)
        XCTAssertNotNil(model.lastError)
        XCTAssertNil(model.commandNotice)
        model.dismissError()
        XCTAssertNil(model.lastError)

        mockService.errorToThrow = nil
        await model.homeZ()
        XCTAssertEqual(model.feedbackSection, .motion)
        model.cancelPendingCommand()
        XCTAssertNil(model.pendingCommand)
        XCTAssertEqual(model.feedbackSection, .motion)
        XCTAssertTrue(model.commandNotice?.contains("may still execute") == true)

        await model.extrude(distanceMm: 2, speedMmPerSecond: 1)
        XCTAssertEqual(model.feedbackSection, .material)
        XCTAssertTrue(model.commandNotice?.contains("Choose") == true)
    }

    func test_feedbackSection_blockedCommandIsReportedAtItsOrigin() async throws {
        let model = try makeViewModel(capabilities: Self.fullCaps)
        await model.homeAll()
        XCTAssertEqual(model.feedbackSection, .motion)
        XCTAssertEqual(model.lastError?.command.section, .motion)
        XCTAssertNil(mockService.homeCalledWith)
    }

    private func makeViewModel(
        printer: Printer? = nil,
        capabilities: PrinterBackendCapabilities? = nil,
        verifiedAbsolute: Bool = false,
        accessCheck: @escaping @MainActor () -> String? = { nil }
    ) throws -> PrinterControlsViewModel {
        let p = try printer ?? TestData.decodePrinter() // online + state="printing" by default
        if let caps = capabilities {
            mockService.capabilitiesToReturn = caps
        }
        if verifiedAbsolute {
            mockService.capabilitiesToReturn?.verifiedSafety = VerifiedSafetyFixtures.discovery()
            mockService.statusToReturn = VerifiedSafetyFixtures.status(id: p.id)
        }
        if mockService.detailsToReturn == nil {
            mockService.detailsToReturn = .controlsLimitsFixture(for: p)
        }
        return PrinterControlsViewModel.configuredForTests(
            printerService: mockService, printer: p, accessCheck: accessCheck
        )
    }

    /// Returns a printer that is online and idle (state="ready").
    private func idlePrinter() throws -> Printer {
        let json = TestJSON.printer
            // Legacy transport/telemetry regression coverage uses a non-Moonraker backend.
            .replacingOccurrences(of: "\"backend\": \"Moonraker\"", with: "\"backend\": \"OctoPrint\"")
            .replacingOccurrences(of: "\"state\": \"printing\"", with: "\"state\": \"ready\"")
        return try TestData.decoder.decode(Printer.self, from: json.data(using: .utf8)!)
    }

    private static let fullCaps = PrinterBackendCapabilities(
        supportsMovement: true,
        supportsTemperatureControl: true,
        supportsBedTemperature: true,
        supportsFanControl: true,
        supportsHoming: true,
        supportedAxes: ["X", "Y", "Z"],
        supportsHomingXY: true, supportsHomingZ: true
    )

    // Synthetic field-omission case, not a FlashForge backend profile.
    private static let hotendOnlyCaps = PrinterBackendCapabilities.hotendOnlyFixture

    private func leaseOwner(
        serverID: UUID, printer: Printer? = nil
    ) async throws -> (PrinterControlsViewModel, MockPrinterService) {
        let printer = try printer ?? idlePrinter()
        let service = MockPrinterService()
        var caps = Self.fullCaps
        caps.supportsAbsoluteMovement = true
        caps.supportsDisableMotors = true
        caps.verifiedSafety = VerifiedSafetyFixtures.discovery()
        service.capabilitiesToReturn = caps
        service.statusToReturn = VerifiedSafetyFixtures.status(id: printer.id)
        service.detailsToReturn = .controlsLimitsFixture(for: printer)
        let model = PrinterControlsViewModel.configuredForTests(
            printerService: service, printer: printer, serverID: serverID
        )
        await model.loadCapabilities()
        return (model, service)
    }

    private func attemptRoutineCommands(on model: PrinterControlsViewModel) async {
        await model.setHeaterTarget(.bed, target: 70)
        await model.preheat(.coolDown)
        await model.jog(axis: "X", distanceMm: 1)
        await model.homeAll()
        await model.homeXY()
        await model.homeZ()
        await model.moveTo(x: 0, y: 0, z: 10, feedrateMmMin: nil)
        await model.disableMotors()
    }

    private func assertNoRoutineDispatch(_ service: MockPrinterService) {
        XCTAssertNil(service.setTemperaturesCalledWith)
        XCTAssertNil(service.moveCalledWith)
        XCTAssertNil(service.homeCalledWith)
        XCTAssertNil(service.homeXYCalledWith)
        XCTAssertNil(service.homeZCalledWith)
        XCTAssertNil(service.moveToCalledWith)
        XCTAssertNil(service.disableMotorsCalledWith)
    }

    private func compositionFixture(
        printer: Printer, temperatureGate: AsyncBarrier? = nil
    ) throws -> (
        services: ServiceContainer, registry: ServerRegistry,
        first: RegisteredServer, second: RegisteredServer,
        api: MockAPIClient, disconnect: AsyncBarrier
    ) {
        let suite = "PrinterControlsComposition-\(UUID().uuidString)"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        let root = try XCTUnwrap(FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask).first)
            .appendingPathComponent(suite, isDirectory: true)
        addTeardownBlock {
            UserDefaults(suiteName: suite)?.removePersistentDomain(forName: suite)
            try? FileManager.default.removeItem(at: root)
        }
        let registry = ServerRegistry(userDefaults: defaults, migrateLegacyServerURL: false)
        let first = try registry.add(displayName: "A", baseURL: URL(string: "https://controls-a.example.com")!)
        let second = try registry.add(displayName: "B", baseURL: URL(string: "https://controls-b.example.com")!)
        let api = MockAPIClient()
        let details = try JSONEncoder().encode(PrinterDetails.controlsLimitsFixture(for: printer))
        let capabilities = Data("""
        {"printerId":"\(printer.id)","backend":"Moonraker","supportsHotendTemperature":true,
         "supportsBedTemperature":true,"supportsRelativeMovement":true,"supportsAbsoluteMovement":true,
         "supportsDisableMotors":true,"supportsHoming":true,"supportsHomingXY":true,"supportsHomingZ":true,
         "supportedAxes":["X","Y","Z"]}
        """.utf8)
        api.asyncRequestHandler = { request in
            let path = request.url?.path ?? ""
            if request.httpMethod == "POST", path.hasSuffix("/temps"),
               request.url?.host == first.baseURL.host, let temperatureGate {
                await temperatureGate.arriveAndWait()
            }
            let data: Data
            if path.hasSuffix("/backend-capabilities") {
                data = capabilities
            } else if path.hasSuffix("/details") {
                data = details
            } else {
                data = Data(#"{"success":true,"message":"Accepted"}"#.utf8)
            }
            return (TestData.httpResponse(url: request.url, statusCode: 200), data)
        }
        let disconnect = AsyncBarrier()
        addTeardownBlock { disconnect.close(); temperatureGate?.close() }
        let services = ServiceContainer(
            serverRegistry: registry,
            credentialsStore: ServerCredentialsStore(keychain: KeychainSwift(keyPrefix: suite)),
            userDefaultsBox: AuthServiceUserDefaultsBox(defaults),
            farmSnapshotRootURL: root,
            synchronizeOfflineQueueOnStartup: false,
            apiClientFactory: { url, generation, _, _, _ in
                APIClient(baseURL: url, session: api.urlSession, serverGeneration: generation)
            },
            signalRServiceFactory: { url, _ in
                let signal = MockSignalRService()
                if url == first.baseURL { signal.disconnectHook = { await disconnect.arriveAndWait() } }
                return signal
            }
        )
        return (services, registry, first, second, api, disconnect)
    }

    private func bindComposition(
        _ model: PrinterControlsViewModel, services: ServiceContainer, registry: ServerRegistry
    ) {
        model.configureAccess(serverID: model.registeredServerID) { [weak model] in
            guard let model, AdvancedPrinterControlsAccess.matchesComposition(
                model, selectedServerID: registry.activeServerID, composition: services.printerControlsComposition
            ) else { return "Server composition changed. Reopen this printer." }
            return nil
        }
    }

    private func printerMutations(_ api: MockAPIClient) -> [URLRequest] {
        api.capturedRequests.filter {
            $0.httpMethod != "GET" && $0.url?.path.hasPrefix("/api/printers/") == true
        }
    }

    // MARK: - Tests

    func test_compositionSnapshot_blocksEagerRegistrySelectionAndLateLifecycleBinding() async throws {
        let printer = try idlePrinter()
        let fixture = try compositionFixture(printer: printer)
        let original = try XCTUnwrap(fixture.services.printerControlsComposition)
        XCTAssertEqual(original.identity.serverID, fixture.first.id)
        let old = PrinterControlsViewModel(composition: original, printer: printer)
        let delayed = PrinterControlsViewModel(composition: original, printer: printer)
        await old.loadCapabilities()
        await delayed.loadCapabilities()

        try fixture.registry.setActive(id: fixture.second.id)
        XCTAssertNil(fixture.services.printerControlsComposition, "Intent is not service composition, even before a worker starts")
        await fixture.disconnect.waitUntilArrived()
        XCTAssertEqual(fixture.services.activeServerGeneration, original.identity.generation)
        XCTAssertEqual(fixture.services.currentActiveServerID, fixture.second.id, "This existing API exposes intent only")
        bindComposition(old, services: fixture.services, registry: fixture.registry)
        XCTAssertFalse(old.canControl)
        await attemptRoutineCommands(on: old)
        old.configureAccess(serverID: fixture.second.id) { nil }
        XCTAssertEqual(old.registeredServerID, fixture.first.id)
        XCTAssertTrue(printerMutations(fixture.api).isEmpty)
        XCTAssertNil(fixture.services.printerControlsComposition)

        fixture.disconnect.release()
        await fixture.services.awaitActiveServerSettled()
        let current = try XCTUnwrap(fixture.services.printerControlsComposition)
        XCTAssertEqual(current.identity.serverID, fixture.second.id)
        XCTAssertNotEqual(current.identity.generation, original.identity.generation)
        bindComposition(delayed, services: fixture.services, registry: fixture.registry)
        XCTAssertFalse(delayed.canControl, "Configuration after a rebuild cannot relabel an earlier service")
        await attemptRoutineCommands(on: delayed)
        XCTAssertTrue(printerMutations(fixture.api).isEmpty)
        XCTAssertEqual(delayed.compositionIdentity, original.identity)

        let fresh = PrinterControlsViewModel(composition: current, printer: printer)
        bindComposition(fresh, services: fixture.services, registry: fixture.registry)
        await fresh.loadCapabilities()
        XCTAssertTrue(fresh.canControl)
        await fresh.homeAll()
        XCTAssertNil(fresh.lastError)
        XCTAssertEqual(printerMutations(fixture.api).count, 1)
        XCTAssertEqual(printerMutations(fixture.api).first?.url?.host, fixture.second.baseURL.host)
    }

    func test_compositionReconstruction_preservesRegisteredMachineLeaseAcrossReconnect() async throws {
        let printer = try idlePrinter()
        let response = AsyncBarrier()
        let fixture = try compositionFixture(printer: printer, temperatureGate: response)
        let original = try XCTUnwrap(fixture.services.printerControlsComposition)
        let old = PrinterControlsViewModel(composition: original, printer: printer)
        bindComposition(old, services: fixture.services, registry: fixture.registry)
        await old.loadCapabilities()
        let request = Task { await old.setHeaterTarget(.hotend, target: 200) }
        await response.waitUntilArrived()
        try fixture.registry.setActive(id: fixture.second.id)
        await fixture.disconnect.waitUntilArrived()
        old.refreshAccess()
        XCTAssertNotNil(old.pendingCommand)
        fixture.disconnect.release()
        await fixture.services.awaitActiveServerSettled()
        let second = PrinterControlsViewModel(
            composition: try XCTUnwrap(fixture.services.printerControlsComposition), printer: printer
        )
        bindComposition(second, services: fixture.services, registry: fixture.registry)
        await second.loadCapabilities()
        await second.homeAll()
        XCTAssertNil(second.lastError, "A different server remains independent of the original lease")
        XCTAssertEqual(printerMutations(fixture.api).count, 2)

        try fixture.registry.setActive(id: fixture.first.id)
        await fixture.services.switchToServer(fixture.first)
        let restored = try XCTUnwrap(fixture.services.printerControlsComposition)
        XCTAssertEqual(restored.identity.serverID, original.identity.serverID)
        XCTAssertNotEqual(restored.identity.generation, original.identity.generation)
        let replacement = PrinterControlsViewModel(composition: restored, printer: printer)
        bindComposition(replacement, services: fixture.services, registry: fixture.registry)
        await replacement.loadCapabilities()
        XCTAssertTrue(replacement.isExecuting)
        XCTAssertFalse(old.canControl, "An old generation never rebinds when the same registered server returns")
        await replacement.homeAll()
        XCTAssertEqual(printerMutations(fixture.api).count, 2, "A generation change must not create a new lease namespace")
        response.release()
        await request.value
        XCTAssertFalse(replacement.isExecuting)
        await replacement.homeAll()
        XCTAssertNil(replacement.lastError)
        XCTAssertEqual(printerMutations(fixture.api).count, 3)
        XCTAssertEqual(printerMutations(fixture.api).last?.url?.host, fixture.first.baseURL.host)
    }

    func test_compositionRevision_rejectsSameGenerationServiceReplacement() async throws {
        let printer = try idlePrinter()
        let fixture = try compositionFixture(printer: printer)
        let original = try XCTUnwrap(fixture.services.printerControlsComposition)
        let old = PrinterControlsViewModel(composition: original, printer: printer)
        await old.loadCapabilities()
        fixture.services.switchToReal()
        let current = try XCTUnwrap(fixture.services.printerControlsComposition)
        XCTAssertEqual(current.identity.serverID, original.identity.serverID)
        XCTAssertEqual(current.identity.generation, original.identity.generation)
        XCTAssertNotEqual(current.identity.revision, original.identity.revision)
        bindComposition(old, services: fixture.services, registry: fixture.registry)
        await attemptRoutineCommands(on: old)
        XCTAssertFalse(old.canControl)
        XCTAssertTrue(printerMutations(fixture.api).isEmpty)
    }

    func test_compositionSnapshot_hidesDemoTransitionAndPublishesRestoredAvailability() async throws {
        let printer = try idlePrinter()
        let fixture = try compositionFixture(printer: printer)
        let original = try XCTUnwrap(fixture.services.printerControlsComposition)
        let model = PrinterControlsViewModel(composition: original, printer: printer)
        bindComposition(model, services: fixture.services, registry: fixture.registry)
        await model.loadCapabilities()
        let transition = Task { await fixture.services.switchToDemo() }
        await fixture.disconnect.waitUntilArrived()
        XCTAssertEqual(fixture.services.activeServerGeneration, original.identity.generation)
        XCTAssertEqual(fixture.registry.activeServerID, fixture.first.id)
        XCTAssertNil(fixture.services.printerControlsComposition, "A demo intent cannot expose the still-real service")
        await model.homeAll()
        XCTAssertTrue(printerMutations(fixture.api).isEmpty)
        fixture.disconnect.release()
        let switched = await transition.value
        XCTAssertTrue(switched)
        XCTAssertNil(fixture.services.printerControlsComposition, "Demo services have no production registered context")
        let generation = fixture.services.activeServerGeneration
        let restored = expectation(description: "Composition availability publishes without a generation change")
        withObservationTracking {
            _ = fixture.services.printerControlsComposition
        } onChange: {
            restored.fulfill()
        }
        fixture.services.switchToReal()
        await fulfillment(of: [restored], timeout: 5)
        XCTAssertEqual(fixture.services.activeServerGeneration, generation)
        XCTAssertEqual(fixture.services.printerControlsComposition?.identity.serverID, fixture.first.id)
        XCTAssertFalse(model.canControl)
    }

    func test_sharedLease_survivesOwnerAndServiceRecreationUntilResponseSettles() async throws {
        for rejects in [false, true] {
            let serverID = UUID()
            let (original, originalService) = try await leaseOwner(serverID: serverID)
            let barrier = AsyncBarrier()
            addTeardownBlock { barrier.close() }
            originalService.beforeSetTemperatures = { await barrier.arriveAndWait() }
            let request = Task { await original.setHeaterTarget(.hotend, target: 200) }
            await barrier.waitUntilArrived()
            let token = try XCTUnwrap(original.pendingCommand)
            original.deactivate()
            let (replacement, replacementService) = try await leaseOwner(
                serverID: serverID, printer: original.printer
            )
            XCTAssertFalse(originalService === replacementService)
            XCTAssertTrue(replacement.isExecuting)
            XCTAssertNil(replacement.pendingCommand, "The replacement must not create a second local command owner")
            XCTAssertTrue(replacement.blockedReason?.contains("Another controls view") == true)
            for _ in 0..<2 {
                replacement.cancelPendingCommand()
                await attemptRoutineCommands(on: replacement)
            }
            assertNoRoutineDispatch(replacementService)
            XCTAssertEqual(original.pendingCommand, token)

            let released = expectation(description: "Replacement observes shared lease release")
            let observation = replacement.objectWillChange.prefix(1).sink { _ in released.fulfill() }
            defer { observation.cancel() }
            if rejects { originalService.errorToThrow = NetworkError.forbidden }
            barrier.release()
            await request.value
            await fulfillment(of: [released], timeout: 5)
            XCTAssertFalse(replacement.isExecuting)
            XCTAssertNil(replacement.blockedReason)
            XCTAssertNil(replacement.lastError)
            XCTAssertNil(replacement.commandNotice, "Old success or failure must not become replacement feedback")
            XCTAssertNil(original.lastError)
            await replacement.setHeaterTarget(.bed, target: 70)
            XCTAssertEqual(replacementService.setTemperaturesCalledWith?.bed, 70)
            XCTAssertNotEqual(replacement.pendingCommand?.id, token.id)
        }
    }

    func test_sharedLease_independentRegisteredServersAndPrintersDoNotBlockEachOther() async throws {
        let serverID = UUID()
        let (original, service) = try await leaseOwner(serverID: serverID)
        let barrier = AsyncBarrier()
        addTeardownBlock { barrier.close() }
        service.beforeSetTemperatures = { await barrier.arriveAndWait() }
        let request = Task { await original.setHeaterTarget(.hotend, target: 200) }
        await barrier.waitUntilArrived()
        let (otherServer, otherServerService) = try await leaseOwner(
            serverID: UUID(), printer: original.printer
        )
        var otherPrinter = try TestData.decodePrinter(from: TestJSON.printerMinimal)
        otherPrinter.isOnline = true
        otherPrinter.state = "idle"
        XCTAssertNotEqual(otherPrinter.id, original.printer.id)
        let (otherMachine, otherMachineService) = try await leaseOwner(serverID: serverID, printer: otherPrinter)
        await otherServer.setHeaterTarget(.bed, target: 70)
        await otherMachine.setHeaterTarget(.bed, target: 70)
        XCTAssertEqual(otherServerService.setTemperaturesCalledWith?.printerId, original.printer.id)
        XCTAssertEqual(otherMachineService.setTemperaturesCalledWith?.printerId, otherPrinter.id)
        let (sameMachine, sameService) = try await leaseOwner(serverID: serverID, printer: original.printer)
        await sameMachine.homeAll()
        XCTAssertNil(sameService.homeCalledWith)
        barrier.release()
        await request.value
        XCTAssertTrue(sameMachine.isExecuting, "HTTP acceptance must not bypass another owner's pending telemetry")
        XCTAssertNotNil(original.pendingCommand)
        var confirmed = original.printer
        confirmed.hotendTarget = 200
        original.handlePrinterUpdate(confirmed)
        XCTAssertFalse(sameMachine.isExecuting)
    }

    func test_sharedLease_pendingTelemetryBlocksSecondOwnerAcrossCommandDomains() async throws {
        let kinds: [ControlCommand.Kind] = [
            .heater(.hotend, target: 200),
            .heaterTargets(hotend: 210, bed: 70),
            .preheat(.pla, hotendTarget: 200, bedTarget: 60),
            .jog(axis: "X", distanceMm: 1),
            .home(axes: ["X", "Y", "Z"]), .home(axes: ["X", "Y"]), .home(axes: ["Z"]),
            .moveTo(x: 50, y: 30, z: 10, feedrateMmMin: nil)
        ]
        for kind in kinds {
            var printer = try idlePrinter()
            printer.hotendTarget = 0
            printer.bedTarget = 0
            printer.homedAxes = nil
            printer.x = 10
            let serverID = UUID()
            let (original, _) = try await leaseOwner(serverID: serverID, printer: printer)
            var confirmed = printer
            switch kind {
            case .heater(let heater, let target):
                await original.setHeaterTarget(heater, target: target)
                confirmed.hotendTarget = target
            case .heaterTargets(let hotend, let bed):
                await original.setHeaterTargets(hotend: hotend, bed: bed)
                confirmed.hotendTarget = hotend
                confirmed.bedTarget = bed
            case .preheat(let preset, let hotend, let bed):
                await original.preheat(preset)
                confirmed.hotendTarget = hotend
                confirmed.bedTarget = bed
            case .jog(let axis, let distance):
                await original.jog(axis: axis, distanceMm: distance)
                confirmed.x = 11
            case .home(let axes):
                if axes == ["X", "Y"] { await original.homeXY() }
                else if axes == ["Z"] { await original.homeZ() }
                else { await original.homeAll() }
                confirmed.homedAxes = axes.joined()
            case .moveTo(let x, let y, let z, let feedrate):
                await original.moveTo(x: x, y: y, z: z, feedrateMmMin: feedrate)
                confirmed.x = x
                confirmed.y = y
                confirmed.z = z
            default:
                XCTFail("Unexpected command domain")
            }
            let token = try XCTUnwrap(original.pendingCommand, "\(kind)")
            XCTAssertNil(original.lastError, "\(kind)")
            let (replacement, service) = try await leaseOwner(serverID: serverID, printer: printer)
            var notifications = 0
            let observation = replacement.objectWillChange.sink { notifications += 1 }
            defer { observation.cancel() }
            XCTAssertTrue(replacement.isExecuting, "\(kind)")
            await attemptRoutineCommands(on: replacement)
            assertNoRoutineDispatch(service)

            var noise = printer
            noise.hotendTemp = 55
            noise.y = 21
            original.handlePrinterUpdate(noise)
            original.handlePrinterUpdate(try TestData.decodePrinter(from: TestJSON.printerMinimal))
            XCTAssertEqual(original.pendingCommand, token, "\(kind)")
            XCTAssertTrue(replacement.isExecuting, "\(kind)")
            _ = try await service.emergencyStop(id: printer.id)
            XCTAssertEqual(service.emergencyStopCalledWith, printer.id)
            XCTAssertTrue(replacement.isExecuting, "Emergency dispatch does not clear routine ownership")

            notifications = 0
            original.handlePrinterUpdate(confirmed)
            XCTAssertNil(original.pendingCommand, "\(kind)")
            XCTAssertFalse(replacement.isExecuting, "\(kind)")
            XCTAssertGreaterThan(notifications, 0, "The second owner must observe telemetry-based release")
            await replacement.disableMotors()
            XCTAssertEqual(service.disableMotorsCalledWith, printer.id)
        }
    }

    func test_sharedLease_matchingTelemetryBeforeHTTPCannotUnlockSecondOwner() async throws {
        for rejects in [false, true] {
            let serverID = UUID()
            let (original, service) = try await leaseOwner(serverID: serverID)
            let (replacement, replacementService) = try await leaseOwner(
                serverID: serverID, printer: original.printer
            )
            let barrier = AsyncBarrier()
            addTeardownBlock { barrier.close() }
            service.beforeSetTemperatures = { await barrier.arriveAndWait() }
            let request = Task { await original.setHeaterTarget(.hotend, target: 200) }
            await barrier.waitUntilArrived()
            var confirmed = original.printer
            confirmed.hotendTarget = 200
            original.handlePrinterUpdate(confirmed)
            XCTAssertNotNil(original.pendingCommand)
            XCTAssertTrue(replacement.isExecuting)
            await attemptRoutineCommands(on: replacement)
            assertNoRoutineDispatch(replacementService)
            if rejects { service.errorToThrow = NetworkError.forbidden }
            barrier.release()
            await request.value
            XCTAssertNil(original.pendingCommand)
            XCTAssertEqual(original.lastError != nil, rejects)
            XCTAssertFalse(replacement.isExecuting)
        }
    }

    func test_sharedLease_endingSettledObservationDoesNotClaimPhysicalCancellation() async throws {
        for deactivate in [false, true] {
            let serverID = UUID()
            let (original, service) = try await leaseOwner(serverID: serverID)
            await original.setHeaterTarget(.hotend, target: 200)
            let (replacement, replacementService) = try await leaseOwner(
                serverID: serverID, printer: original.printer
            )
            XCTAssertNotNil(original.pendingCommand)
            XCTAssertTrue(replacement.isExecuting)
            if deactivate { original.deactivate() }
            else { original.cancelPendingCommand() }
            XCTAssertNil(original.pendingCommand)
            XCTAssertFalse(replacement.isExecuting, "Only settled observation ended, not physical execution")
            XCTAssertTrue(original.commandNotice?.contains("printer may still execute") == true)
            XCTAssertFalse(original.commandNotice?.contains("No printer command was sent") == true)
            XCTAssertEqual(service.setTemperaturesCalledWith?.hotend, 200, "Cancellation cannot undo dispatch")
            await replacement.jog(axis: "X", distanceMm: 1)
            XCTAssertNotNil(replacementService.moveCalledWith)
            replacement.cancelPendingCommand()
        }
    }

    func test_sharedLease_droppedSettledObserverCannotStrandReplacement() async throws {
        for calibration in [false, true] {
            var printer = try idlePrinter()
            printer.hotendTarget = 0
            let serverID = UUID()
            let service = MockPrinterService()
            service.capabilitiesToReturn = Self.fullCaps
            service.detailsToReturn = .controlsLimitsFixture(for: printer)
            var original: PrinterControlsViewModel? = .configuredForTests(
                printerService: service, printer: printer, serverID: serverID
            )
            weak var releasedOwner = original
            await original?.loadCapabilities()
            if calibration { await original?.startCalibration() }
            else { await original?.setHeaterTarget(.hotend, target: 200) }
            let (replacement, replacementService) = try await leaseOwner(serverID: serverID, printer: printer)
            XCTAssertTrue(replacement.isExecuting)
            let released = expectation(description: "Dropped observer releases only its own token")
            let observation = replacement.objectWillChange.prefix(1).sink { _ in released.fulfill() }
            defer { observation.cancel() }
            original = nil
            await fulfillment(of: [released], timeout: 5)
            XCTAssertNil(releasedOwner, "The lease registry must not retain a completed observer")
            XCTAssertFalse(replacement.isExecuting)
            await replacement.disableMotors()
            XCTAssertEqual(replacementService.disableMotorsCalledWith, printer.id)
        }
    }

    func test_sharedLease_deferredLifetimeCleanupCannotUnlockNewPendingToken() async throws {
        let serverID = UUID()
        let (original, _) = try await leaseOwner(serverID: serverID)
        let (replacement, _) = try await leaseOwner(serverID: serverID, printer: original.printer)
        let (third, service) = try await leaseOwner(serverID: serverID, printer: original.printer)
        await original.setHeaterTarget(.hotend, target: 200)
        var confirmed = original.printer
        confirmed.hotendTarget = 200
        original.handlePrinterUpdate(confirmed)
        // Acquire the new token before yielding to the old handle's cleanup.
        await replacement.setHeaterTarget(.hotend, target: 220)
        XCTAssertNotNil(replacement.pendingCommand)
        XCTAssertTrue(third.isExecuting)
        await attemptRoutineCommands(on: third)
        assertNoRoutineDispatch(service)
        replacement.cancelPendingCommand()
    }

    func test_sharedLease_staleTelemetryOwnerCannotReleaseReplacementToken() async throws {
        let serverID = UUID()
        let (original, _) = try await leaseOwner(serverID: serverID)
        await original.setHeaterTarget(.hotend, target: 200)
        let originalToken = try XCTUnwrap(original.pendingCommand)
        var originalConfirmation = original.printer
        originalConfirmation.hotendTarget = 200
        original.handlePrinterUpdate(originalConfirmation)
        XCTAssertNil(original.pendingCommand)
        let (replacement, service) = try await leaseOwner(serverID: serverID, printer: original.printer)
        let barrier = AsyncBarrier()
        addTeardownBlock { barrier.close() }
        service.beforeSetTemperatures = { await barrier.arriveAndWait() }
        let request = Task { await replacement.setHeaterTarget(.hotend, target: 220) }
        await barrier.waitUntilArrived()
        let replacementToken = try XCTUnwrap(replacement.pendingCommand)
        XCTAssertNotEqual(originalToken.id, replacementToken.id)
        original.cancelPendingCommand()
        original.deactivate()
        original.configureAccess(serverID: serverID) { nil }
        XCTAssertNil(original.pendingCommand)
        XCTAssertTrue(original.isExecuting, "Old-token cleanup must not release the replacement lease")
        await original.homeAll()
        let (third, thirdService) = try await leaseOwner(serverID: serverID, printer: original.printer)
        await attemptRoutineCommands(on: third)
        assertNoRoutineDispatch(thirdService)
        XCTAssertEqual(replacement.pendingCommand, replacementToken)
        barrier.release()
        await request.value
        XCTAssertTrue(third.isExecuting, "The replacement token remains owned during its telemetry wait")
        var replacementConfirmation = replacement.printer
        replacementConfirmation.hotendTarget = 220
        replacement.handlePrinterUpdate(replacementConfirmation)
        XCTAssertFalse(third.isExecuting)
        await third.jog(axis: "X", distanceMm: 1)
        XCTAssertNotNil(thirdService.moveCalledWith)
    }

    func test_sharedLease_validationFailureAndPreDispatchCancellationReleaseForOtherOwners() async throws {
        let serverID = UUID()
        let (observer, _) = try await leaseOwner(serverID: serverID)
        let service = MockPrinterService()
        service.capabilitiesToReturn = Self.fullCaps
        service.detailsToReturn = .controlsLimitsFixture(for: observer.printer)
        let composition = PrinterControlsComposition(
            identity: .init(serverID: serverID, generation: 0, revision: 0), printerService: service
        )
        let original = PrinterControlsViewModel(composition: composition, printer: observer.printer)
        let cancelAtDispatch = AccessGate()
        cancelAtDispatch.isAllowed = false
        original.configureAccess(serverID: serverID) { [weak original] in
            if cancelAtDispatch.isAllowed, original?.pendingCommand != nil {
                original?.cancelPendingCommand()
            }
            return nil
        }
        await original.loadCapabilities()
        await original.setHeaterTarget(.hotend, target: .nan)
        XCTAssertNotNil(original.lastError)
        XCTAssertFalse(observer.isExecuting, "Validation failure must not strand a lease")
        cancelAtDispatch.isAllowed = true
        await original.setHeaterTarget(.hotend, target: 200)
        XCTAssertNil(service.setTemperaturesCalledWith)
        XCTAssertFalse(observer.isExecuting)
        XCTAssertEqual(original.commandNotice, "Request canceled before dispatch. No printer command was sent.")
        await observer.jog(axis: "X", distanceMm: 1)
        XCTAssertNotNil(observer.pendingCommand)
    }

    func test_sharedLease_missingIdentityFailsClosedAndConfiguredIdentityCannotBeRebound() async throws {
        let printer = try idlePrinter()
        mockService.capabilitiesToReturn = Self.fullCaps
        mockService.detailsToReturn = .controlsLimitsFixture(for: printer)
        let unbound = PrinterControlsViewModel(printerService: mockService, printer: printer)
        await unbound.loadCapabilities()
        XCTAssertNil(unbound.registeredServerID)
        XCTAssertFalse(unbound.canControl)
        await attemptRoutineCommands(on: unbound)
        assertNoRoutineDispatch(mockService)
        XCTAssertEqual(unbound.blockedReason, "Controls require a registered server identity.")
        unbound.configureAccess(serverID: nil) { nil }
        XCTAssertFalse(unbound.canControl)
        let serverID = UUID()
        unbound.configureAccess(serverID: serverID) { nil }
        XCTAssertFalse(unbound.canControl, "Lifecycle configuration cannot retrofit provenance onto a bare service")
        XCTAssertNil(unbound.registeredServerID)
        let composition = PrinterControlsComposition(
            identity: .init(serverID: serverID, generation: 0, revision: 0), printerService: mockService
        )
        let model = PrinterControlsViewModel(composition: composition, printer: printer)
        await model.loadCapabilities()
        model.configureAccess(serverID: serverID) { nil }
        XCTAssertTrue(model.canControl)
        let barrier = AsyncBarrier()
        addTeardownBlock { barrier.close() }
        mockService.beforeSetTemperatures = { await barrier.arriveAndWait() }
        let request = Task { await model.setHeaterTarget(.hotend, target: 200) }
        await barrier.waitUntilArrived()
        model.configureAccess(serverID: UUID()) { nil }
        XCTAssertEqual(model.registeredServerID, serverID)
        XCTAssertFalse(model.canControl)
        let (replacement, _) = try await leaseOwner(serverID: serverID, printer: printer)
        XCTAssertTrue(replacement.isExecuting)
        barrier.release()
        await request.value
        XCTAssertFalse(replacement.isExecuting)
    }

    func test_sharedLease_retainsUnresolvedCallButDoesNotLeakCompletedOwner() async throws {
        let printer = try idlePrinter()
        let serverID = UUID()
        mockService.capabilitiesToReturn = Self.fullCaps
        mockService.detailsToReturn = .controlsLimitsFixture(for: printer)
        let barrier = AsyncBarrier()
        addTeardownBlock { barrier.close() }
        mockService.beforeSetTemperatures = { await barrier.arriveAndWait() }
        weak var releasedOwner: PrinterControlsViewModel?
        var request: Task<Void, Never>?
        do {
            let model = PrinterControlsViewModel.configuredForTests(
                printerService: mockService, printer: printer, serverID: serverID
            )
            releasedOwner = model
            await model.loadCapabilities()
            request = Task { await model.setHeaterTarget(.hotend, target: 200) }
            await barrier.waitUntilArrived()
            model.deactivate()
        }
        XCTAssertNotNil(releasedOwner, "An unresolved call must retain its cleanup owner")
        let (replacement, _) = try await leaseOwner(serverID: serverID, printer: printer)
        XCTAssertTrue(replacement.isExecuting)
        barrier.release()
        await request?.value
        request = nil
        XCTAssertNil(releasedOwner, "Neither the registry nor its weak observation relay may retain completed owners")
        XCTAssertFalse(replacement.isExecuting)
    }

    func test_missingOrInvalidHeaterMaxima_blockPositiveTargetsAndPresetsButPermitSupportedZero() async throws {
        for limit in [nil, 0, -1] as [Int?] {
            let printer = try idlePrinter()
            let service = MockPrinterService()
            service.capabilitiesToReturn = Self.fullCaps
            service.detailsToReturn = .controlsLimitsFixture(for: printer, hotend: limit, bed: limit)
            let vm = PrinterControlsViewModel.configuredForTests(printerService: service, printer: printer)
            await vm.loadCapabilities()
            XCTAssertTrue(vm.needsHeaterLimits)
            for heater in Heater.allCases {
                XCTAssertNil(vm.maximum(for: heater))
                for value in [1.0, 200, 1e100] {
                    await vm.setHeaterTarget(heater, target: value)
                    XCTAssertNil(service.setTemperaturesCalledWith)
                    XCTAssertNotNil(vm.lastError)
                    XCTAssertNil(vm.pendingCommand)
                }
            }
            for preset in [PreheatPreset.pla, .petg, .abs] {
                XCTAssertNotNil(vm.preheatBlockedReason(preset))
                await vm.preheat(preset)
                XCTAssertNil(service.setTemperaturesCalledWith)
                XCTAssertNotNil(vm.lastError)
            }
            for heater in Heater.allCases {
                await vm.setHeaterTarget(heater, target: 0)
                XCTAssertNil(vm.lastError)
                XCTAssertEqual(service.setTemperaturesCalledWith?.hotend, heater == .hotend ? 0 : nil)
                XCTAssertEqual(service.setTemperaturesCalledWith?.bed, heater == .bed ? 0 : nil)
                vm.cancelPendingCommand()
            }
            XCTAssertNil(vm.preheatBlockedReason(.coolDown))
            await vm.preheat(.coolDown)
            XCTAssertNil(vm.lastError)
            XCTAssertEqual(service.setTemperaturesCalledWith?.hotend, 0)
            XCTAssertEqual(service.setTemperaturesCalledWith?.bed, 0)
        }
    }

    func test_partialMaxima_blockWholePresetWithoutPartialHeaterDispatch() async throws {
        let printer = try idlePrinter()
        let service = MockPrinterService()
        service.capabilitiesToReturn = Self.fullCaps
        service.detailsToReturn = .controlsLimitsFixture(for: printer, bed: nil)
        let vm = PrinterControlsViewModel.configuredForTests(printerService: service, printer: printer)
        await vm.loadCapabilities()
        await vm.preheat(.pla)
        XCTAssertNil(service.setTemperaturesCalledWith, "A safe hotend does not authorize an unbounded bed")
        await vm.setHeaterTarget(.hotend, target: 280)
        XCTAssertEqual(service.setTemperaturesCalledWith?.hotend, 280)
        vm.cancelPendingCommand()
        service.setTemperaturesCalledWith = nil
        await vm.setHeaterTarget(.hotend, target: 281)
        XCTAssertNil(service.setTemperaturesCalledWith)
        service.detailsToReturn = .controlsLimitsFixture(for: printer, bed: nil, hasBed: false)
        await vm.loadHardware()
        await vm.preheat(.pla)
        XCTAssertEqual(service.setTemperaturesCalledWith?.hotend, 200)
        XCTAssertNil(service.setTemperaturesCalledWith?.bed)
    }

    func test_presets_requireBoundsAtBothHeatersAndKeepOriginalTargets() async throws {
        let printer = try idlePrinter()
        let service = MockPrinterService()
        service.capabilitiesToReturn = Self.fullCaps
        let vm = PrinterControlsViewModel.configuredForTests(printerService: service, printer: printer)
        await vm.loadCapabilities()
        for preset in [PreheatPreset.pla, .petg, .abs] {
            for insufficientHotend in [true, false] {
                service.detailsToReturn = .controlsLimitsFixture(
                    for: printer, hotend: Int(preset.hotend) - (insufficientHotend ? 1 : 0),
                    bed: Int(preset.bed) - (insufficientHotend ? 0 : 1)
                )
                await vm.loadHardware()
                service.setTemperaturesCalledWith = nil
                await vm.preheat(preset)
                XCTAssertNil(service.setTemperaturesCalledWith)
            }
            service.detailsToReturn = .controlsLimitsFixture(for: printer, hotend: Int(preset.hotend), bed: Int(preset.bed))
            await vm.loadHardware()
            await vm.preheat(preset)
            XCTAssertNil(vm.lastError)
            XCTAssertEqual(service.setTemperaturesCalledWith?.hotend, preset.hotend)
            XCTAssertEqual(service.setTemperaturesCalledWith?.bed, preset.bed)
            vm.cancelPendingCommand()
        }
    }

    func test_hardwareFailureAndMissingPayload_areExplicitAndCanRetryWithoutActuation() async throws {
        let printer = try idlePrinter()
        let service = MockPrinterService()
        service.capabilitiesToReturn = Self.fullCaps
        let vm = PrinterControlsViewModel.configuredForTests(printerService: service, printer: printer)
        await vm.loadCapabilities()
        XCTAssertNotNil(vm.hardwareLoadError)
        XCTAssertTrue(vm.needsHeaterLimits)
        await vm.setHeaterTarget(.hotend, target: 200)
        XCTAssertNil(service.setTemperaturesCalledWith)
        service.detailsToReturn = PrinterDetails(id: printer.id, name: printer.name, backend: printer.backend)
        await vm.loadHardware()
        XCTAssertNil(vm.hardwareLoadError)
        XCTAssertTrue(vm.needsHeaterLimits, "A successful response with no maxima is not proof")
        service.detailsToReturn = .controlsLimitsFixture(for: printer)
        await vm.loadHardware()
        XCTAssertFalse(vm.needsHeaterLimits)
        XCTAssertNil(service.setTemperaturesCalledWith, "Read retry must not replay a failed physical command")
        await vm.setHeaterTarget(.hotend, target: 200)
        XCTAssertEqual(service.setTemperaturesCalledWith?.hotend, 200)
    }

    func test_loadingHardware_blocksPositiveTargetsButAllowsZeroAndReadIsSingleFlight() async throws {
        let printer = try idlePrinter()
        let base = MockPrinterService()
        base.capabilitiesToReturn = Self.fullCaps
        base.detailsToReturn = .controlsLimitsFixture(for: printer)
        let barrier = AsyncBarrier()
        addTeardownBlock { barrier.close() }
        let service = ControlsDelayedService(base: base, beforeDetails: { await barrier.arriveAndWait() })
        let vm = PrinterControlsViewModel.configuredForTests(printerService: service, printer: printer)
        let read = Task { await vm.loadCapabilities() }
        await barrier.waitUntilArrived()
        XCTAssertTrue(vm.isLoadingHardware)
        XCTAssertNil(vm.maximum(for: .hotend))
        await vm.loadHardware()
        await vm.preheat(.pla)
        XCTAssertNil(base.setTemperaturesCalledWith)
        await vm.setHeaterTarget(.hotend, target: 0)
        XCTAssertEqual(base.setTemperaturesCalledWith?.hotend, 0)
        vm.cancelPendingCommand()
        barrier.release()
        await read.value
        XCTAssertFalse(vm.isLoadingHardware)
        XCTAssertEqual(vm.maximum(for: .hotend), 280)
    }

    func test_absoluteFeedrate_rejectsEveryCustomValueAndUsesEstablishedZRate() async throws {
        var caps = Self.fullCaps
        caps.supportsAbsoluteMovement = true
        let vm = try makeViewModel(printer: idlePrinter(), capabilities: caps, verifiedAbsolute: true)
        await vm.loadCapabilities()
        for rate in [Int.min, -1, 0, 1, 600, 3000, 3001, Int.max] {
            await vm.moveTo(x: 1, y: 0, z: 10, feedrateMmMin: rate)
            XCTAssertNil(mockService.moveToCalledWith)
            XCTAssertNil(vm.pendingCommand)
            XCTAssertEqual(vm.lastError?.message, ControlNumberInput.customFeedrateMessage)
        }
        for z in [0.0, -0.5, 10.0] {
            await vm.moveTo(x: 1, y: 0, z: z, feedrateMmMin: nil)
            XCTAssertNil(vm.lastError)
            XCTAssertEqual(mockService.moveToCalledWith?.feedrateMmMin, 600)
            XCTAssertEqual(mockService.moveToCalledWith?.z, z)
            vm.cancelPendingCommand()
        }
    }

    func test_heaterPrecision_rejectsFractionalValuesWithoutDispatchOrRounding() async throws {
        let vm = try makeViewModel(printer: idlePrinter(), capabilities: Self.fullCaps)
        await vm.loadCapabilities()
        for heater in Heater.allCases {
            for value in [200.5, 0.5, 200.0001, Double.leastNonzeroMagnitude] {
                await vm.setHeaterTarget(heater, target: value)
                XCTAssertNil(mockService.setTemperaturesCalledWith)
                XCTAssertNil(vm.pendingCommand)
                XCTAssertEqual(vm.lastError?.message, ControlNumberInput.heaterPrecisionMessage)
            }
        }
    }

    func test_wholeDegreeHeaters_matchExactReportedTargetsAndPreserveOmissions() async throws {
        let vm = try makeViewModel(printer: idlePrinter(), capabilities: Self.fullCaps)
        await vm.loadCapabilities()
        for heater in Heater.allCases {
            for target in heater == .hotend ? [0.0, 200.0, 240.0] : [0.0, 60.0, 80.0] {
                await vm.setHeaterTarget(heater, target: target)
                XCTAssertNil(vm.lastError)
                XCTAssertEqual(mockService.setTemperaturesCalledWith?.hotend, heater == .hotend ? target : nil)
                XCTAssertEqual(mockService.setTemperaturesCalledWith?.bed, heater == .bed ? target : nil)
                var reported = vm.printer
                if heater == .hotend { reported.hotendTarget = target } else { reported.bedTarget = target }
                vm.handlePrinterUpdate(reported)
                XCTAssertNil(vm.pendingCommand)
                XCTAssertEqual(vm.commandNotice, "Matching telemetry received. A heater target is a setpoint, not a measured temperature.")
            }

        }
    }

    func test_failedOrWrongPrinterLimitsRefresh_doesNotRetainHeatingAuthority() async throws {
        let printer = try idlePrinter()
        let vm = try makeViewModel(printer: printer, capabilities: Self.fullCaps)
        await vm.loadCapabilities()
        XCTAssertEqual(vm.maximum(for: .hotend), 280)
        mockService.errorToThrow = NetworkError.serverError(503)
        await vm.loadHardware()
        XCTAssertNotNil(vm.hardwareLoadError)
        XCTAssertNil(vm.maximum(for: .hotend))
        await vm.setHeaterTarget(.hotend, target: 200)
        XCTAssertNil(mockService.setTemperaturesCalledWith)
        mockService.errorToThrow = nil
        mockService.detailsToReturn = PrinterDetails(
            id: UUID(), name: printer.name, backend: printer.backend,
            capabilities: PrinterDetails.controlsLimitsFixture(for: printer).capabilities
        )
        await vm.loadHardware()
        XCTAssertTrue(vm.hardwareLoadError?.contains("different printer") == true)
        XCTAssertNil(vm.maximum(for: .hotend))
        await vm.preheat(.pla)
        XCTAssertNil(mockService.setTemperaturesCalledWith)
        mockService.detailsToReturn = .controlsLimitsFixture(for: printer)
        await vm.loadHardware()
        XCTAssertNil(vm.hardwareLoadError)
        XCTAssertEqual(vm.maximum(for: .hotend), 280)
        XCTAssertNil(mockService.setTemperaturesCalledWith)
    }

    func test_coordinatePrecision_rejectsExcessOnEveryAxisAndRelativeJog() async throws {
        var caps = Self.fullCaps
        caps.supportsAbsoluteMovement = true
        let vm = try makeViewModel(printer: idlePrinter(), capabilities: caps)
        await vm.loadCapabilities()
        for value in [1.2345, -0.0001, Double.leastNonzeroMagnitude, (1.001).nextUp] {
            for axis in ["X", "Y", "Z"] {
                await vm.moveTo(x: axis == "X" ? value : 0, y: axis == "Y" ? value : 0,
                                z: axis == "Z" ? value : 0, feedrateMmMin: nil)
                XCTAssertNil(mockService.moveToCalledWith)
                XCTAssertNil(vm.pendingCommand)
                XCTAssertEqual(vm.lastError?.message, ControlNumberInput.coordinatePrecisionMessage)
                await vm.jog(axis: axis, distanceMm: value)
                XCTAssertNil(mockService.moveCalledWith)
                XCTAssertNil(vm.pendingCommand)
                XCTAssertEqual(vm.lastError?.message, ControlNumberInput.coordinatePrecisionMessage)
            }
        }
    }

    func test_coordinatePrecision_preservesDecimalValuesAndExactReportedPosition() async throws {
        var caps = Self.fullCaps
        caps.supportsAbsoluteMovement = true
        let vm = try makeViewModel(printer: idlePrinter(), capabilities: caps, verifiedAbsolute: true)
        await vm.loadCapabilities()
        for value in [1.001, -1.234, 0.1, 0.0] {
            await vm.moveTo(x: value, y: 0, z: 10, feedrateMmMin: nil)
            XCTAssertNil(vm.lastError)
            XCTAssertEqual(mockService.moveToCalledWith?.x, value)
            XCTAssertEqual(mockService.moveToCalledWith?.y, 0)
            XCTAssertEqual(mockService.moveToCalledWith?.z, 10)
            XCTAssertEqual(mockService.moveToCalledWith?.feedrateMmMin, 600)
            XCTAssertNotNil(vm.pendingCommand)
            var reported = vm.printer
            reported.x = value
            reported.y = 0
            reported.z = 10
            vm.handlePrinterUpdate(reported)
            XCTAssertNil(vm.pendingCommand)
            XCTAssertEqual(vm.commandNotice, "Matching telemetry received. Check the machine before further setup.")
        }
    }

    func test_freshIndividualTelemetryBeforeResponse_isStillReportedAsTelemetry() async throws {
        let printer = try idlePrinter()
        let vm = try makeViewModel(printer: printer, capabilities: Self.fullCaps)
        await vm.loadCapabilities()
        let barrier = AsyncBarrier()
        addTeardownBlock { barrier.close() }
        mockService.beforeSetTemperatures = { await barrier.arriveAndWait() }
        let command = Task { await vm.setHeaterTarget(.hotend, target: 200) }
        await barrier.waitUntilArrived()
        var reported = printer
        reported.hotendTarget = 200
        vm.handlePrinterUpdate(reported)
        XCTAssertNotNil(vm.pendingCommand)
        barrier.release()
        await command.value
        XCTAssertNil(vm.pendingCommand)
        XCTAssertEqual(vm.commandNotice, "Matching telemetry received. A heater target is a setpoint, not a measured temperature.")
    }

    func test_freshAbsoluteTelemetryBeforeResponse_isStillReportedAsTelemetry() async throws {
        let printer = try idlePrinter()
        var caps = Self.fullCaps
        caps.supportsAbsoluteMovement = true
        caps.verifiedSafety = VerifiedSafetyFixtures.discovery()
        mockService.capabilitiesToReturn = caps
        mockService.statusToReturn = VerifiedSafetyFixtures.status(id: printer.id)
        let barrier = AsyncBarrier()
        addTeardownBlock { barrier.close() }
        let service = ControlsDelayedService(base: mockService, beforeMoveTo: { await barrier.arriveAndWait() })
        let vm = PrinterControlsViewModel.configuredForTests(printerService: service, printer: printer)
        await vm.loadCapabilities()
        let command = Task { await vm.moveTo(x: 1.001, y: 0, z: -0.234, feedrateMmMin: nil) }
        await barrier.waitUntilArrived()
        var reported = printer
        reported.x = 1.001
        reported.y = 0
        reported.z = -0.234
        vm.handlePrinterUpdate(reported)
        XCTAssertNotNil(vm.pendingCommand)
        await vm.homeAll()
        XCTAssertNil(mockService.homeCalledWith)
        barrier.release()
        await command.value
        XCTAssertNil(vm.pendingCommand)
        XCTAssertNil(vm.lastError)
        XCTAssertEqual(vm.commandNotice, "Matching telemetry received. Check the machine before further setup.")
    }

    func test_delayedCapabilities_surviveInitialStateAndReadinessChanges() async throws {
        for state in ["idle", "printing"] {
            var printer = try idlePrinter()
            printer.state = nil
            printer.isOnline = false
            let vm = try makeViewModel(printer: printer, capabilities: Self.fullCaps)
            let barrier = AsyncBarrier()
            addTeardownBlock { barrier.close() }
            mockService.beforeGetBackendCapabilities = { await barrier.arriveAndWait() }
            let read = Task { await vm.loadCapabilities() }
            await barrier.waitUntilArrived()
            printer.state = state
            printer.isOnline = true
            vm.handlePrinterUpdate(printer)
            barrier.release()
            await read.value
            XCTAssertEqual(vm.capabilities, Self.fullCaps)
            XCTAssertNil(vm.capabilityLoadError)
            XCTAssertFalse(vm.isLoadingCapabilities)
            XCTAssertEqual(vm.canControl, state == "idle")
        }
    }

    func test_delayedHardware_survivesInitialStateChangeAndEnforcesBounds() async throws {
        var printer = try idlePrinter()
        printer.state = nil
        mockService.capabilitiesToReturn = Self.fullCaps
        mockService.detailsToReturn = hardwareDetails(for: printer)
        let barrier = AsyncBarrier()
        addTeardownBlock { barrier.close() }
        let service = ControlsDelayedService(base: mockService, beforeDetails: { await barrier.arriveAndWait() })
        let vm = PrinterControlsViewModel.configuredForTests(printerService: service, printer: printer)
        let read = Task { await vm.loadCapabilities() }
        await barrier.waitUntilArrived()
        printer.state = "idle"
        vm.handlePrinterUpdate(printer)
        barrier.release()
        await read.value
        XCTAssertEqual(vm.maximum(for: .hotend), 260)
        XCTAssertFalse(vm.supports(.bed))
        await vm.setHeaterTarget(.hotend, target: 261)
        XCTAssertNil(mockService.setTemperaturesCalledWith)
        XCTAssertNotNil(vm.lastError)
    }

    func test_deactivation_discardsDelayedCapabilitySuccessAndFailure_thenAllowsRetry() async throws {
        for error in [nil, NetworkError.serverError(503)] as [NetworkError?] {
            let vm = try makeViewModel(printer: idlePrinter(), capabilities: Self.fullCaps)
            let barrier = AsyncBarrier()
            addTeardownBlock { barrier.close() }
            mockService.beforeGetBackendCapabilities = { await barrier.arriveAndWait() }
            let read = Task { await vm.loadCapabilities() }
            await barrier.waitUntilArrived()
            vm.deactivate()
            vm.configureAccess(serverID: vm.registeredServerID) { nil }
            mockService.errorToThrow = error
            barrier.release()
            await read.value
            XCTAssertNil(vm.capabilities, "Reactivation must not accept the prior lifecycle's response")
            XCTAssertNil(vm.capabilityLoadError)
            XCTAssertFalse(vm.isLoadingCapabilities)
            mockService.errorToThrow = nil
            await vm.loadCapabilities()
            XCTAssertEqual(vm.capabilities, Self.fullCaps)
        }
    }

    func test_deactivation_discardsDelayedHardware_thenReloadsWithCachedCapabilities() async throws {
        let printer = try idlePrinter()
        mockService.capabilitiesToReturn = Self.fullCaps
        mockService.detailsToReturn = hardwareDetails(for: printer)
        let barrier = AsyncBarrier()
        addTeardownBlock { barrier.close() }
        let service = ControlsDelayedService(base: mockService, beforeDetails: { await barrier.arriveAndWait() })
        let vm = PrinterControlsViewModel.configuredForTests(printerService: service, printer: printer)
        let read = Task { await vm.loadCapabilities() }
        await barrier.waitUntilArrived()
        vm.deactivate()
        vm.configureAccess(serverID: vm.registeredServerID) { nil }
        barrier.release()
        await read.value
        XCTAssertEqual(vm.capabilities, Self.fullCaps)
        XCTAssertNil(vm.hardware)
        await vm.loadCapabilities()
        XCTAssertEqual(vm.maximum(for: .hotend), 260)
        XCTAssertEqual(mockService.getBackendCapabilitiesCallCount, 1)
    }

    func test_serverChange_discardsDelayedCapabilityAndHardwareReads() async throws {
        for delayHardware in [false, true] {
            let printer = try idlePrinter()
            let base = MockPrinterService()
            base.capabilitiesToReturn = Self.fullCaps
            base.detailsToReturn = hardwareDetails(for: printer)
            let barrier = AsyncBarrier()
            addTeardownBlock { barrier.close() }
            if !delayHardware { base.beforeGetBackendCapabilities = { await barrier.arriveAndWait() } }
            let service = ControlsDelayedService(base: base, beforeDetails: {
                if delayHardware { await barrier.arriveAndWait() }
            })
            let access = AccessGate()
            let vm = PrinterControlsViewModel.configuredForTests(
                printerService: service, printer: printer,
                accessCheck: { access.isAllowed ? nil : "Server changed" }
            )
            let read = Task { await vm.loadCapabilities() }
            await barrier.waitUntilArrived()
            access.isAllowed = false
            vm.refreshAccess()
            vm.configureAccess(serverID: vm.registeredServerID) { nil }
            barrier.release()
            await read.value
            XCTAssertNil(vm.hardware)
            if !delayHardware { XCTAssertNil(vm.capabilities) }
            XCTAssertNil(vm.capabilityLoadError)
            XCTAssertFalse(vm.canControl)
        }
    }

    private func hardwareDetails(for printer: Printer) -> PrinterDetails {
        PrinterDetails(
            id: printer.id, name: printer.name, backend: printer.backend,
            capabilities: PrinterHardwareCapabilities(
                maxBuildVolumeX: nil, maxBuildVolumeY: nil, maxBuildVolumeZ: nil,
                maxHotendTemp: 260, maxBedTemp: nil, hasHeatedBed: false
            )
        )
    }

    func test_repeatedHomeAllXYAndZ_reportAcceptanceWithoutFreshCompletion() async throws {
        let vm = try makeViewModel(printer: idlePrinter(), capabilities: Self.fullCaps)
        await vm.loadCapabilities()
        for _ in 0..<2 {
            for operation in ["all", "xy", "z"] {
                switch operation {
                case "all": await vm.homeAll()
                case "xy": await vm.homeXY()
                default: await vm.homeZ()
                }
                XCTAssertNil(vm.pendingCommand)
                XCTAssertNil(vm.lastError)
                XCTAssertTrue(vm.commandNotice?.contains("Homing request accepted") == true)
                XCTAssertTrue(vm.commandNotice?.contains("fresh physical completion is not confirmed") == true)
                XCTAssertFalse(vm.commandNotice?.contains("Matching telemetry") == true)
                XCTAssertEqual(vm.printer.homedAxes, "xyz")
            }
        }
        XCTAssertEqual(mockService.homeCalledWith?.axes, ["X", "Y", "Z"])
        XCTAssertEqual(mockService.homeXYCalledWith, vm.printer.id)
        XCTAssertEqual(mockService.homeZCalledWith, vm.printer.id)
    }

    func test_alreadyHomed_axesDoNotReleaseInFlightRequestOrHideRejection() async throws {
        let printer = try idlePrinter()
        mockService.capabilitiesToReturn = Self.fullCaps
        let barrier = AsyncBarrier()
        addTeardownBlock { barrier.close() }
        let service = ControlsDelayedService(base: mockService, beforeHome: { await barrier.arriveAndWait() })
        let vm = PrinterControlsViewModel.configuredForTests(printerService: service, printer: printer)
        await vm.loadCapabilities()
        let command = Task { await vm.homeAll() }
        await barrier.waitUntilArrived()
        vm.handlePrinterUpdate(printer)
        XCTAssertNotNil(vm.pendingCommand)
        await vm.setHeaterTarget(.hotend, target: 200)
        XCTAssertNil(mockService.setTemperaturesCalledWith)
        mockService.errorToThrow = NetworkError.forbidden
        barrier.release()
        await command.value
        XCTAssertEqual(vm.lastError?.message, "Access denied.")
        XCTAssertNil(vm.pendingCommand)
        XCTAssertNil(vm.commandNotice)
    }

    func test_statusChurn_keepsRequestSingleFlightAndPreservesServerRejection() async throws {
        for state in ["idle", "printing"] {
            let printer = try idlePrinter()
            let vm = try makeViewModel(printer: printer, capabilities: Self.fullCaps)
            await vm.loadCapabilities()
            let barrier = AsyncBarrier()
            addTeardownBlock { barrier.close() }
            mockService.beforeSetTemperatures = { await barrier.arriveAndWait() }
            let command = Task { await vm.setHeaterTarget(.hotend, target: 200) }
            await barrier.waitUntilArrived()
            var update = printer
            update.state = state
            vm.handlePrinterUpdate(update)
            XCTAssertNotNil(vm.pendingCommand)
            XCTAssertEqual(vm.canControl, state == "idle")
            vm.handlePrinterUpdate(printer)
            await vm.homeAll()
            XCTAssertNil(mockService.homeCalledWith, "Returning to ready must not overlap requests")
            mockService.errorToThrow = NetworkError.forbidden
            barrier.release()
            await command.value
            XCTAssertEqual(vm.lastError?.message, "Access denied.")
            XCTAssertNil(vm.pendingCommand)
            XCTAssertNil(vm.commandNotice)
            mockService.errorToThrow = nil
        }
    }

    func test_offlineReconnect_preservesResponseButNeverClaimsTelemetryConfirmation() async throws {
        let printer = try idlePrinter()
        let vm = try makeViewModel(printer: printer, capabilities: Self.fullCaps)
        await vm.loadCapabilities()
        let barrier = AsyncBarrier()
        addTeardownBlock { barrier.close() }
        mockService.beforeSetTemperatures = { await barrier.arriveAndWait() }
        let command = Task { await vm.setHeaterTarget(.hotend, target: 200) }
        await barrier.waitUntilArrived()
        var update = printer
        update.isOnline = false
        vm.handlePrinterUpdate(update)
        XCTAssertFalse(vm.canControl)
        XCTAssertNotNil(vm.pendingCommand)
        update.isOnline = true
        update.hotendTarget = 200
        vm.handlePrinterUpdate(update)
        XCTAssertNotNil(vm.pendingCommand)
        barrier.release()
        await command.value
        XCTAssertNil(vm.pendingCommand)
        XCTAssertNil(vm.lastError)
        XCTAssertTrue(vm.commandNotice?.contains("Request accepted") == true)
        XCTAssertTrue(vm.commandNotice?.contains("Physical outcome is unknown") == true)
        XCTAssertFalse(vm.commandNotice?.contains("Matching telemetry") == true)
    }

    func test_failedCommand_clearsStaleAcceptanceNotice() async throws {
        let vm = try makeViewModel(printer: idlePrinter(), capabilities: Self.fullCaps)
        await vm.loadCapabilities()
        await vm.homeAll()
        XCTAssertNotNil(vm.commandNotice)
        vm.deactivate()
        await vm.homeZ()
        XCTAssertNotNil(vm.lastError)
        XCTAssertNil(vm.commandNotice)
    }

    func test_motionNotices_distinguishCachedAcceptanceAndFreshTelemetry() async throws {
        var printer = try idlePrinter()
        printer.y = 0
        printer.z = 10
        var caps = Self.fullCaps
        caps.supportsAbsoluteMovement = true
        let vm = try makeViewModel(printer: printer, capabilities: caps, verifiedAbsolute: true)
        await vm.loadCapabilities()
        await vm.moveTo(x: try XCTUnwrap(printer.x), y: 0, z: 10, feedrateMmMin: nil)
        XCTAssertNil(vm.pendingCommand)
        XCTAssertTrue(vm.commandNotice?.hasPrefix("Request accepted.") == true)
        XCTAssertTrue(vm.commandNotice?.contains("fresh physical completion is not confirmed") == true)
        XCTAssertFalse(vm.commandNotice?.contains("Matching telemetry") == true)
        await vm.jog(axis: "X", distanceMm: 10)
        var moved = printer
        moved.x = (printer.x ?? 0) + 10
        vm.handlePrinterUpdate(moved)
        XCTAssertNil(vm.pendingCommand)
        XCTAssertEqual(vm.commandNotice, "Matching telemetry received. Check the machine before further setup.")
    }

    func test_freshHomingTelemetryBeforeResponse_keepsSlotThenReportsGenericConfirmation() async throws {
        var printer = try idlePrinter()
        printer.homedAxes = nil
        mockService.capabilitiesToReturn = Self.fullCaps
        let barrier = AsyncBarrier()
        addTeardownBlock { barrier.close() }
        let service = ControlsDelayedService(base: mockService, beforeHome: { await barrier.arriveAndWait() })
        let vm = PrinterControlsViewModel.configuredForTests(printerService: service, printer: printer)
        await vm.loadCapabilities()
        let command = Task { await vm.homeAll() }
        await barrier.waitUntilArrived()
        printer.homedAxes = "xyz"
        vm.handlePrinterUpdate(printer)
        XCTAssertNotNil(vm.pendingCommand)
        barrier.release()
        await command.value
        XCTAssertNil(vm.pendingCommand)
        XCTAssertEqual(vm.commandNotice, "Matching telemetry received. Check the machine before further setup.")
    }

    func test_individualHeaters_preserveOmissionAndZero() async throws {
        let vm = try makeViewModel(printer: idlePrinter(), capabilities: Self.fullCaps)
        await vm.loadCapabilities()
        await vm.setHeaterTarget(.hotend, target: 0)
        XCTAssertEqual(mockService.setTemperaturesCalledWith?.hotend, 0)
        XCTAssertNil(mockService.setTemperaturesCalledWith?.bed)
        vm.cancelPendingCommand()
        await vm.setHeaterTarget(.bed, target: 75)
        XCTAssertNil(mockService.setTemperaturesCalledWith?.hotend)
        XCTAssertEqual(mockService.setTemperaturesCalledWith?.bed, 75)
        XCTAssertEqual(vm.printer.hotendTemp, try idlePrinter().hotendTemp)
        XCTAssertEqual(vm.printer.bedTarget, try idlePrinter().bedTarget, "HTTP must not manufacture telemetry")
    }

    func test_heaters_rejectInvalidValuesAndConfiguredMaxima() async throws {
        let printer = try idlePrinter()
        mockService.detailsToReturn = PrinterDetails(
            id: printer.id, name: printer.name, backend: printer.backend,
            capabilities: PrinterHardwareCapabilities(
                maxBuildVolumeX: nil, maxBuildVolumeY: nil, maxBuildVolumeZ: nil,
                maxHotendTemp: 260, maxBedTemp: 110, hasHeatedBed: true
            )
        )
        let vm = try makeViewModel(printer: printer, capabilities: Self.fullCaps)
        await vm.loadCapabilities()
        for (heater, value) in [(Heater.hotend, -1.0), (.hotend, .nan), (.bed, .infinity),
                                (.hotend, 261), (.bed, 111)] {
            await vm.setHeaterTarget(heater, target: value)
            XCTAssertNil(mockService.setTemperaturesCalledWith)
            XCTAssertNotNil(vm.lastError)
            XCTAssertNil(vm.pendingCommand)
        }
        await vm.setHeaterTarget(.hotend, target: 260)
        XCTAssertEqual(mockService.setTemperaturesCalledWith?.hotend, 260)
    }

    func test_bedlessHardware_omitsPresetBedAndRejectsBedEditor() async throws {
        let printer = try idlePrinter()
        mockService.detailsToReturn = PrinterDetails(
            id: printer.id, name: printer.name, backend: printer.backend,
            capabilities: PrinterHardwareCapabilities(
                maxBuildVolumeX: nil, maxBuildVolumeY: nil, maxBuildVolumeZ: nil,
                maxHotendTemp: 280, maxBedTemp: nil, hasHeatedBed: false
            )
        )
        let vm = try makeViewModel(printer: printer, capabilities: Self.fullCaps)
        await vm.loadCapabilities()
        XCTAssertFalse(vm.supports(.bed))
        await vm.setHeaterTarget(.bed, target: 0)
        XCTAssertNil(mockService.setTemperaturesCalledWith)
        await vm.preheat(.pla)
        XCTAssertEqual(mockService.setTemperaturesCalledWith?.hotend, 200)
        XCTAssertNil(mockService.setTemperaturesCalledWith?.bed)
    }

    func test_individualHeater_specificSupportDoesNotRequireOtherHeater() async throws {
        let caps = PrinterBackendCapabilities(
            supportsMovement: false, supportsTemperatureControl: false,
            supportsBedTemperature: true, supportsFanControl: false,
            supportsHoming: false, supportedAxes: []
        )
        let vm = try makeViewModel(printer: idlePrinter(), capabilities: caps)
        await vm.loadCapabilities()
        await vm.setHeaterTarget(.hotend, target: 200)
        XCTAssertNil(mockService.setTemperaturesCalledWith)
        await vm.setHeaterTarget(.bed, target: 60)
        XCTAssertNil(mockService.setTemperaturesCalledWith?.hotend)
        XCTAssertEqual(mockService.setTemperaturesCalledWith?.bed, 60)
    }

    func test_absoluteDispatch_requiresXYZPreservesZeroSignedValuesAndFeedrate() async throws {
        var caps = Self.fullCaps
        caps.supportsAbsoluteMovement = true
        let vm = try makeViewModel(printer: idlePrinter(), capabilities: caps, verifiedAbsolute: true)
        await vm.loadCapabilities()
        await vm.moveTo(x: 0, y: -2.5, z: -0.5, feedrateMmMin: nil)
        XCTAssertEqual(mockService.moveToCalledWith?.x, 0)
        XCTAssertEqual(mockService.moveToCalledWith?.y, -2.5)
        XCTAssertEqual(mockService.moveToCalledWith?.z, -0.5, "The verified frame, not a guessed zero origin, sets bounds")
        XCTAssertEqual(mockService.moveToCalledWith?.feedrateMmMin, 600)
        XCTAssertNil(mockService.moveCalledWith)
    }

    func test_absoluteValidation_rejectsEmptyNonfiniteUnsupportedAxesAndFeedrate() async throws {
        var caps = PrinterBackendCapabilities(
            supportsMovement: true, supportsTemperatureControl: true,
            supportsBedTemperature: true, supportsFanControl: false,
            supportsHoming: true, supportedAxes: ["X"]
        )
        caps.supportsAbsoluteMovement = true
        let vm = try makeViewModel(printer: idlePrinter(), capabilities: caps, verifiedAbsolute: true)
        await vm.loadCapabilities()
        for (x, y, z, f) in [(nil, nil, nil, nil), (Double.nan, 0, 10, nil),
                            (1, 0, 10, 0), (1, 0, 10, -10), (0, 2, 10, nil)]
            as [(Double?, Double?, Double?, Int?)] {
            await vm.moveTo(x: x, y: y, z: z, feedrateMmMin: f)
            XCTAssertNil(mockService.moveToCalledWith)
            XCTAssertNotNil(vm.lastError)
            XCTAssertNil(vm.pendingCommand)
        }
    }

    func test_newCommands_requireSpecificCapabilities() async throws {
        for caps in [Self.fullCaps, PrinterBackendCapabilities.fallback(for: .moonraker)] {
            let vm = try makeViewModel(printer: idlePrinter(), capabilities: caps, verifiedAbsolute: true)
            await vm.loadCapabilities()
            await vm.moveTo(x: 0, y: 0, z: 10, feedrateMmMin: nil)
            await vm.disableMotors()
            XCTAssertNil(mockService.moveToCalledWith)
            XCTAssertNil(mockService.disableMotorsCalledWith)
        }
        let unknown = try makeViewModel(printer: idlePrinter())
        await unknown.setHeaterTarget(.hotend, target: 200)
        await unknown.moveTo(x: 0, y: 0, z: 10, feedrateMmMin: nil)
        await unknown.disableMotors()
        XCTAssertNil(mockService.setTemperaturesCalledWith)
        XCTAssertNil(mockService.moveToCalledWith)
        XCTAssertNil(mockService.disableMotorsCalledWith)
    }

    func test_motorReleaseAndAbsolute_failureResultsAreNotSuccess() async throws {
        var caps = Self.fullCaps
        caps.supportsAbsoluteMovement = true
        caps.supportsDisableMotors = true
        let vm = try makeViewModel(printer: idlePrinter(), capabilities: caps, verifiedAbsolute: true)
        await vm.loadCapabilities()
        mockService.commandResultToReturn = CommandResult(success: false, message: "Guard rejected")
        await vm.disableMotors()
        XCTAssertEqual(vm.lastError?.message, "Guard rejected")
        XCTAssertNil(vm.pendingCommand)
        XCTAssertNil(vm.commandNotice)
        await vm.moveTo(x: 1, y: 0, z: 10, feedrateMmMin: nil)
        XCTAssertEqual(vm.lastError?.message, "Guard rejected")
        XCTAssertNil(vm.pendingCommand)
    }

    func test_motorRelease_acceptanceDoesNotInventMotorOrPositionTelemetry() async throws {
        var caps = Self.fullCaps
        caps.supportsDisableMotors = true
        let vm = try makeViewModel(printer: idlePrinter(), capabilities: caps)
        await vm.loadCapabilities()
        let previous = vm.printer
        await vm.disableMotors()
        XCTAssertEqual(mockService.disableMotorsCalledWith, previous.id)
        XCTAssertNil(vm.pendingCommand, "No motor-state callback exists")
        XCTAssertTrue(vm.commandNotice?.contains("Motor state is not reported") == true)
        XCTAssertEqual(vm.printer.homedAxes, previous.homedAxes)
        XCTAssertEqual(vm.printer.x, previous.x)
    }

    func test_newCommands_blockAllUnsafeStatesBeforeDispatch() async throws {
        var caps = Self.fullCaps
        caps.supportsAbsoluteMovement = true
        caps.supportsDisableMotors = true
        for (state, online) in [("printing", true), ("paused", true), ("starting", true), ("ready", false)] {
            var printer = try idlePrinter()
            printer.state = state
            printer.isOnline = online
            let vm = try makeViewModel(printer: printer, capabilities: caps, verifiedAbsolute: true)
            await vm.loadCapabilities()
            await vm.setHeaterTarget(.hotend, target: 200)
            await vm.moveTo(x: 1, y: 0, z: 10, feedrateMmMin: nil)
            await vm.disableMotors()
            XCTAssertNil(mockService.setTemperaturesCalledWith)
            XCTAssertNil(mockService.moveToCalledWith)
            XCTAssertNil(mockService.disableMotorsCalledWith)
        }
    }

    func test_stopWaiting_keepsNoncancellableRequestSingleFlightUntilResponse() async throws {
        let barrier = AsyncBarrier()
        addTeardownBlock { barrier.close() }
        mockService.beforeSetTemperatures = { await barrier.arriveAndWait() }
        var caps = Self.fullCaps
        caps.supportsAbsoluteMovement = true
        caps.supportsDisableMotors = true
        let vm = try makeViewModel(printer: idlePrinter(), capabilities: caps, verifiedAbsolute: true)
        await vm.loadCapabilities()
        let task = Task { await vm.setHeaterTarget(.hotend, target: 200) }
        await barrier.waitUntilArrived()
        let owner = try XCTUnwrap(vm.pendingCommand)
        // The first call is already suspended on a continuation that ignores
        // cancellation. Any accidental second call must return, not deadlock.
        mockService.beforeSetTemperatures = nil
        for _ in 0..<2 {
            vm.cancelPendingCommand()
            XCTAssertEqual(vm.pendingCommand, owner)
            XCTAssertTrue(vm.commandNotice?.contains("outcome is unresolved") == true)
            await vm.setHeaterTarget(.bed, target: 70)
            await vm.preheat(.coolDown)
            await vm.jog(axis: "X", distanceMm: 1)
            await vm.homeAll()
            await vm.homeXY()
            await vm.homeZ()
            await vm.moveTo(x: 0, y: 0, z: 10, feedrateMmMin: nil)
            await vm.disableMotors()
        }
        XCTAssertNil(mockService.setTemperaturesCalledWith)
        XCTAssertNil(mockService.moveCalledWith)
        XCTAssertNil(mockService.homeCalledWith)
        XCTAssertNil(mockService.homeXYCalledWith)
        XCTAssertNil(mockService.homeZCalledWith)
        XCTAssertNil(mockService.moveToCalledWith)
        XCTAssertNil(mockService.disableMotorsCalledWith)
        var update = vm.printer
        update.hotendTarget = 200
        vm.handlePrinterUpdate(update)
        XCTAssertEqual(vm.pendingCommand, owner, "Telemetry must not release an unresolved response")

        _ = try await mockService.emergencyStop(id: vm.printer.id)
        XCTAssertEqual(mockService.emergencyStopCalledWith, vm.printer.id)
        XCTAssertEqual(vm.pendingCommand, owner, "Independent emergency dispatch cannot clear routine ownership")
        barrier.release()
        await task.value
        XCTAssertEqual(mockService.setTemperaturesCalledWith?.hotend, 200)
        XCTAssertNil(vm.pendingCommand)
        XCTAssertNil(vm.lastError)
        XCTAssertTrue(vm.commandNotice?.contains("Request accepted") == true)
        XCTAssertTrue(vm.commandNotice?.contains("Physical outcome is unknown") == true)
        XCTAssertFalse(vm.commandNotice?.contains("Matching telemetry") == true)
    }

    func test_accessRevocationAndDismissal_retainRequestAcrossReactivationAndFenceLateResults() async throws {
        for dismiss in [false, true] {
            for rejects in [false, true] {
                let service = MockPrinterService()
                let printer = try idlePrinter()
                service.capabilitiesToReturn = Self.fullCaps
                service.detailsToReturn = .controlsLimitsFixture(for: printer)
                let access = AccessGate()
                let vm = PrinterControlsViewModel.configuredForTests(
                    printerService: service, printer: printer,
                    accessCheck: { access.isAllowed ? nil : "Preference or permission revoked" }
                )
                await vm.loadCapabilities()
                let barrier = AsyncBarrier()
                addTeardownBlock { barrier.close() }
                service.beforeSetTemperatures = { await barrier.arriveAndWait() }
                let task = Task { await vm.setHeaterTarget(.hotend, target: 200) }
                await barrier.waitUntilArrived()
                let owner = try XCTUnwrap(vm.pendingCommand)
                service.beforeSetTemperatures = nil
                if dismiss {
                    vm.deactivate()
                } else {
                    access.isAllowed = false
                    vm.refreshAccess()
                }
                XCTAssertFalse(vm.canControl)
                XCTAssertEqual(vm.pendingCommand, owner)
                await vm.homeAll()
                XCTAssertNil(service.homeCalledWith)
                access.isAllowed = true
                vm.configureAccess(serverID: vm.registeredServerID) { nil }
                XCTAssertTrue(vm.canControl)
                await vm.setHeaterTarget(.bed, target: 70)
                await vm.homeAll()
                XCTAssertNil(service.setTemperaturesCalledWith)
                XCTAssertNil(service.homeCalledWith)
                XCTAssertEqual(vm.pendingCommand, owner)
                if rejects { service.errorToThrow = NetworkError.forbidden }
                barrier.release()
                await task.value
                XCTAssertNil(vm.pendingCommand)
                XCTAssertNil(vm.lastError, "An earlier lifecycle's error must not become the reactivated owner's error")
                XCTAssertTrue(vm.commandNotice?.contains("check the original printer") == true)
                XCTAssertFalse(vm.commandNotice?.contains("Matching telemetry") == true)
                service.errorToThrow = nil
                await vm.jog(axis: "X", distanceMm: 1)
                XCTAssertNotEqual(vm.pendingCommand?.id, owner.id)
                XCTAssertNotNil(service.moveCalledWith)
            }
        }
    }

    func test_callerCancellation_doesNotCancelDispatchedTransportOrReleaseItsOwner() async throws {
        let vm = try makeViewModel(printer: idlePrinter(), capabilities: Self.fullCaps)
        await vm.loadCapabilities()
        let barrier = AsyncBarrier()
        addTeardownBlock { barrier.close() }
        mockService.beforeSetTemperatures = { await barrier.arriveAndWait() }
        let task = Task { await vm.setHeaterTarget(.hotend, target: 200) }
        await barrier.waitUntilArrived()
        let owner = vm.pendingCommand
        let stopped = expectation(description: "Caller cancellation invalidates observation")
        let observation = vm.$commandNotice
            .filter { $0?.contains("outcome is unresolved") == true }
            .prefix(1)
            .sink { _ in stopped.fulfill() }
        defer { observation.cancel() }
        task.cancel()
        await fulfillment(of: [stopped], timeout: 5)
        XCTAssertEqual(vm.pendingCommand, owner)
        await vm.jog(axis: "X", distanceMm: 1)
        XCTAssertNil(mockService.moveCalledWith)
        barrier.release()
        await task.value
        XCTAssertNil(vm.pendingCommand)
        XCTAssertNil(vm.lastError)
        XCTAssertTrue(vm.commandNotice?.contains("Request accepted") == true,
                      "Caller cancellation must not replace the actual response with CancellationError")
        XCTAssertTrue(vm.commandNotice?.contains("Physical outcome is unknown") == true)
    }

    func test_stopBeforeDispatch_releasesSafelyAndDoesNotClobberNextInvocation() async throws {
        let printer = try idlePrinter()
        mockService.capabilitiesToReturn = Self.fullCaps
        mockService.detailsToReturn = .controlsLimitsFixture(for: printer)
        let serverID = UUID()
        let composition = PrinterControlsComposition(
            identity: .init(serverID: serverID, generation: 0, revision: 0), printerService: mockService
        )
        let vm = PrinterControlsViewModel(composition: composition, printer: printer)
        await vm.loadCapabilities()
        let stopBeforeDispatch = AccessGate()
        vm.configureAccess(serverID: serverID) { [weak vm] in
            // The dispatch-time access check runs after the slot is acquired,
            // but before the service is called. No scheduling guesses needed.
            if stopBeforeDispatch.isAllowed, vm?.pendingCommand != nil {
                vm?.cancelPendingCommand()
            }
            return nil
        }
        await vm.setHeaterTarget(.hotend, target: 200)
        XCTAssertNil(mockService.setTemperaturesCalledWith)
        XCTAssertNil(vm.pendingCommand)
        XCTAssertEqual(vm.commandNotice, "Request canceled before dispatch. No printer command was sent.")
        stopBeforeDispatch.isAllowed = false
        await vm.setHeaterTarget(.bed, target: 70)
        XCTAssertEqual(mockService.setTemperaturesCalledWith?.bed, 70)
        XCTAssertNotNil(vm.pendingCommand)
        XCTAssertNil(vm.lastError)
    }

    func test_serverChange_lateResponseCannotRebindOldOwnerOrMutateDifferentPrinterOwner() async throws {
        let access = AccessGate()
        let old = try makeViewModel(
            printer: idlePrinter(), capabilities: Self.fullCaps,
            accessCheck: { access.isAllowed ? nil : "Server changed" }
        )
        await old.loadCapabilities()
        let barrier = AsyncBarrier()
        addTeardownBlock { barrier.close() }
        mockService.beforeSetTemperatures = { await barrier.arriveAndWait() }
        let oldTask = Task { await old.setHeaterTarget(.hotend, target: 200) }
        await barrier.waitUntilArrived()
        access.isAllowed = false
        old.refreshAccess()
        old.configureAccess(serverID: old.registeredServerID) { nil }
        XCTAssertFalse(old.canControl)
        XCTAssertNotNil(old.pendingCommand)

        var otherPrinter = try TestData.decodePrinter(from: TestJSON.printerMinimal)
        otherPrinter.isOnline = true
        otherPrinter.state = "idle"
        XCTAssertNotEqual(otherPrinter.id, old.printer.id)
        let otherService = MockPrinterService()
        otherService.capabilitiesToReturn = Self.fullCaps
        otherService.detailsToReturn = .controlsLimitsFixture(for: otherPrinter)
        let other = PrinterControlsViewModel.configuredForTests(printerService: otherService, printer: otherPrinter)
        await other.loadCapabilities()
        await other.setHeaterTarget(.bed, target: 70)
        let otherOwner = try XCTUnwrap(other.pendingCommand)
        let otherNotice = other.commandNotice
        mockService.errorToThrow = NetworkError.forbidden
        barrier.release()
        await oldTask.value
        XCTAssertNil(old.pendingCommand)
        XCTAssertNil(old.lastError)
        XCTAssertFalse(old.canControl)
        XCTAssertEqual(other.pendingCommand, otherOwner)
        XCTAssertEqual(other.commandNotice, otherNotice)
        XCTAssertNil(other.lastError)
    }

    func test_cancelledCaller_neverDispatches() async throws {
        let vm = try makeViewModel(printer: idlePrinter(), capabilities: Self.fullCaps)
        await vm.loadCapabilities()
        let task = Task {
            await vm.setHeaterTarget(.hotend, target: 200)
            await vm.jog(axis: "X", distanceMm: 10)
        }
        task.cancel()
        await task.value
        XCTAssertNil(mockService.setTemperaturesCalledWith)
        XCTAssertNil(mockService.moveCalledWith)
        XCTAssertNil(vm.pendingCommand)
    }

    func test_serverChange_cannotRebindOldOwnerAndBlocksEveryNewOperation() async throws {
        var caps = Self.fullCaps
        caps.supportsAbsoluteMovement = true
        caps.supportsDisableMotors = true
        let access = AccessGate()
        let vm = try makeViewModel(
            printer: idlePrinter(), capabilities: caps,
            verifiedAbsolute: true,
            accessCheck: { access.isAllowed ? nil : "Server changed" }
        )
        await vm.loadCapabilities()
        await vm.setHeaterTarget(.bed, target: 70)
        access.isAllowed = false
        vm.refreshAccess()
        XCTAssertNil(vm.pendingCommand)
        vm.configureAccess(serverID: vm.registeredServerID) { nil }
        XCTAssertFalse(vm.canControl, "Reappearing must not silently rebind an old service to a new server")
        mockService.setTemperaturesCalledWith = nil
        await vm.setHeaterTarget(.hotend, target: 200)
        await vm.moveTo(x: 1, y: 0, z: 10, feedrateMmMin: nil)
        await vm.disableMotors()
        XCTAssertNil(mockService.setTemperaturesCalledWith)
        XCTAssertNil(mockService.moveToCalledWith)
        XCTAssertNil(mockService.disableMotorsCalledWith)
    }

    func test_pendingIndividualTarget_serializesAllRoutineCommands() async throws {
        var caps = Self.fullCaps
        caps.supportsAbsoluteMovement = true
        caps.supportsDisableMotors = true
        let vm = try makeViewModel(printer: idlePrinter(), capabilities: caps, verifiedAbsolute: true)
        await vm.loadCapabilities()
        await vm.setHeaterTarget(.hotend, target: 220)
        let pending = vm.pendingCommand
        mockService.setTemperaturesCalledWith = nil
        await vm.setHeaterTarget(.bed, target: 70)
        await vm.moveTo(x: 1, y: 0, z: 10, feedrateMmMin: nil)
        await vm.disableMotors()
        await vm.preheat(.pla)
        await vm.jog(axis: "X", distanceMm: 1)
        await vm.homeAll()
        XCTAssertEqual(vm.pendingCommand, pending)
        XCTAssertNil(mockService.setTemperaturesCalledWith)
        XCTAssertNil(mockService.moveToCalledWith)
        XCTAssertNil(mockService.disableMotorsCalledWith)
        XCTAssertNil(mockService.moveCalledWith)
        XCTAssertNil(mockService.homeCalledWith)
    }

    func test_relativeValidation_rejectsInvalidAxisNonfiniteAndZero() async throws {
        let vm = try makeViewModel(printer: idlePrinter(), capabilities: Self.fullCaps)
        await vm.loadCapabilities()
        for (axis, distance) in [("E", 1.0), ("X", .nan), ("Z", .infinity), ("Y", 0)] {
            await vm.jog(axis: axis, distanceMm: distance)
            XCTAssertNil(mockService.moveCalledWith)
            XCTAssertNotNil(vm.lastError)
        }
    }

    func test_capabilityReadsAreSingleFlightAndCancellationDoesNotPublishSupport() async throws {
        let vm = try makeViewModel(printer: idlePrinter(), capabilities: Self.fullCaps)
        let barrier = AsyncBarrier()
        addTeardownBlock { barrier.close() }
        mockService.beforeGetBackendCapabilities = { await barrier.arriveAndWait() }
        let pending = Task { await vm.loadCapabilities() }
        await barrier.waitUntilArrived()
        await vm.loadCapabilities()
        XCTAssertEqual(mockService.getBackendCapabilitiesCallCount, 1)
        XCTAssertTrue(vm.isLoadingCapabilities)
        pending.cancel()
        barrier.release()
        await pending.value
        XCTAssertNil(vm.capabilities)
        XCTAssertNil(vm.capabilityLoadError)
        XCTAssertFalse(vm.isLoadingCapabilities)
    }

    func test_capabilityFetchFailureRemainsUnavailableAndCanRetryWithoutActuation() async throws {
        let vm = try makeViewModel(printer: idlePrinter(), capabilities: Self.fullCaps)
        mockService.errorToThrow = NetworkError.serverError(503)
        await vm.loadCapabilities()
        XCTAssertNil(vm.capabilities)
        XCTAssertNotNil(vm.capabilityLoadError)
        XCTAssertFalse(vm.isLoadingCapabilities)
        mockService.errorToThrow = nil
        await vm.loadCapabilities()
        XCTAssertEqual(vm.capabilities, Self.fullCaps)
        XCTAssertNil(vm.capabilityLoadError)
        XCTAssertNil(mockService.homeCalledWith)
        XCTAssertNil(mockService.moveCalledWith)
        XCTAssertNil(mockService.setTemperaturesCalledWith)
    }

    func test_homingUsesIndependentOperationEvidenceWithoutJogging() async throws {
        var caps = PrinterBackendCapabilities.fallback(for: .unknown)
        caps.supportsHomingXY = true
        let xy = try makeViewModel(printer: idlePrinter(), capabilities: caps)
        await xy.loadCapabilities()
        await xy.homeXY()
        XCTAssertEqual(mockService.homeXYCalledWith, xy.printer.id)
        XCTAssertNil(xy.lastError)

        let z = try makeViewModel(printer: idlePrinter(), capabilities: caps)
        await z.loadCapabilities()
        await z.homeZ()
        XCTAssertNil(mockService.homeZCalledWith)
        XCTAssertNotNil(z.lastError)
        XCTAssertEqual(z.lastError?.isRetryable, false)

        let all = try makeViewModel(printer: idlePrinter(), capabilities: caps)
        await all.loadCapabilities()
        await all.homeAll()
        XCTAssertNil(mockService.homeCalledWith)
        XCTAssertNotNil(all.lastError)
    }

    func test_setupCommands_remainBlockedWhilePrintingPausedOrOffline() async throws {
        for (state, online) in [("printing", true), ("paused", true), ("ready", false)] {
            var printer = try idlePrinter()
            printer.state = state
            printer.isOnline = online
            let service = MockPrinterService()
            service.capabilitiesToReturn = Self.fullCaps
            let model = PrinterControlsViewModel.configuredForTests(printerService: service, printer: printer)
            await model.loadCapabilities()

            await model.preheat(.pla)
            await model.preheat(.coolDown)
            await model.homeAll()
            await model.homeXY()
            await model.homeZ()
            await model.jog(axis: "X", distanceMm: 10)

            XCTAssertFalse(model.canControl)
            XCTAssertNil(model.pendingCommand)
            XCTAssertNotNil(model.lastError)
            XCTAssertNil(service.setTemperaturesCalledWith)
            XCTAssertNil(service.homeCalledWith)
            XCTAssertNil(service.homeXYCalledWith)
            XCTAssertNil(service.homeZCalledWith)
            XCTAssertNil(service.moveCalledWith)
        }
    }

    func test_loadCapabilities_cachesSecondCallNoFetch() async throws {
        let vm = try makeViewModel(printer: try idlePrinter(), capabilities: Self.fullCaps)

        await vm.loadCapabilities()
        XCTAssertEqual(vm.capabilities, Self.fullCaps)
        XCTAssertEqual(mockService.getBackendCapabilitiesCalledWith, vm.printer.id)

        mockService.getBackendCapabilitiesCalledWith = nil
        await vm.loadCapabilities()
        XCTAssertNil(mockService.getBackendCapabilitiesCalledWith, "Second loadCapabilities should not refetch")
    }

    func test_preheatPLA_callsSetTemperaturesWith200_60() async throws {
        let vm = try makeViewModel(printer: try idlePrinter(), capabilities: Self.fullCaps)
        await vm.loadCapabilities()

        await vm.preheat(.pla)

        XCTAssertEqual(mockService.setTemperaturesCalledWith?.printerId, vm.printer.id)
        XCTAssertEqual(mockService.setTemperaturesCalledWith?.hotend, 200)
        XCTAssertEqual(mockService.setTemperaturesCalledWith?.bed, 60)
        XCTAssertNil(vm.lastError)
    }

    func test_preheatPETG_withExplicitHotendOnlyEvidence_omitsBed() async throws {
        let vm = try makeViewModel(printer: try idlePrinter(), capabilities: Self.hotendOnlyCaps)
        await vm.loadCapabilities()

        await vm.preheat(.petg)

        XCTAssertEqual(mockService.setTemperaturesCalledWith?.hotend, 240)
        XCTAssertNil(mockService.setTemperaturesCalledWith?.bed, "Bed must be silently dropped when unsupported")
        XCTAssertNil(vm.lastError, "Dropping the bed value must not surface as an error")
    }

    func test_coolDown_omitsBedWhenSupportIsUnconfirmed() async throws {
        let vm = try makeViewModel(printer: try idlePrinter(), capabilities: Self.hotendOnlyCaps)
        await vm.loadCapabilities()

        await vm.preheat(.coolDown)

        XCTAssertEqual(mockService.setTemperaturesCalledWith?.hotend, 0)
        XCTAssertNil(mockService.setTemperaturesCalledWith?.bed)
    }

    func test_allThermalPresetsRejectUnknownAndDeniedSupportWithoutDispatch() async throws {
        for preset in PreheatSubgroup.presets {
            let vm = try makeViewModel(printer: idlePrinter())
            await vm.preheat(preset)
            XCTAssertNil(mockService.setTemperaturesCalledWith)
            XCTAssertNotNil(vm.lastError)
            XCTAssertNil(vm.pendingCommand)

            mockService.capabilitiesToReturn = .fallback(for: .unknown)
            await vm.loadCapabilities()
            await vm.preheat(preset)
            XCTAssertNil(mockService.setTemperaturesCalledWith)
            XCTAssertNotNil(vm.lastError)
            XCTAssertNil(vm.pendingCommand)
        }
    }

    func test_failedCapabilityReadBlocksCooldownUntilSuccessfulRetry() async throws {
        let vm = try makeViewModel(printer: idlePrinter(), capabilities: Self.fullCaps)
        mockService.errorToThrow = NetworkError.serverError(503)
        await vm.loadCapabilities()
        XCTAssertNotNil(vm.capabilityLoadError)
        XCTAssertFalse(PreheatSubgroup.isVisible(capabilities: vm.capabilities))
        await vm.preheat(.coolDown)
        XCTAssertNil(mockService.setTemperaturesCalledWith)
        XCTAssertNil(vm.pendingCommand)

        mockService.errorToThrow = nil
        await vm.loadCapabilities()
        XCTAssertNil(vm.capabilityLoadError)
        XCTAssertTrue(PreheatSubgroup.isVisible(capabilities: vm.capabilities))
        XCTAssertNil(mockService.setTemperaturesCalledWith, "Capability retry must not replay cooldown")
        await vm.preheat(.coolDown)
        XCTAssertEqual(mockService.setTemperaturesCalledWith?.hotend, 0)
        XCTAssertEqual(mockService.setTemperaturesCalledWith?.bed, 0)
    }

    func test_homeAll_callsHomeWithAllAxes() async throws {
        let vm = try makeViewModel(printer: try idlePrinter(), capabilities: Self.fullCaps)
        await vm.loadCapabilities()

        await vm.homeAll()

        XCTAssertEqual(mockService.homeCalledWith?.printerId, vm.printer.id)
        XCTAssertEqual(mockService.homeCalledWith?.axes, ["X", "Y", "Z"])
    }

    func test_jogX_usesXYFeedrate() async throws {
        let vm = try makeViewModel(printer: try idlePrinter(), capabilities: Self.fullCaps)
        await vm.loadCapabilities()

        await vm.jog(axis: "X", distanceMm: 10)

        XCTAssertEqual(mockService.moveCalledWith?.axis, "X")
        XCTAssertEqual(mockService.moveCalledWith?.distanceMm, 10)
        XCTAssertEqual(mockService.moveCalledWith?.feedrateMmMin, 3000)
    }

    func test_jogZ_usesZFeedrate() async throws {
        let vm = try makeViewModel(printer: try idlePrinter(), capabilities: Self.fullCaps)
        await vm.loadCapabilities()

        await vm.jog(axis: "Z", distanceMm: -1)

        XCTAssertEqual(mockService.moveCalledWith?.feedrateMmMin, 600)
    }

    func test_singleFlightDropsConcurrentCommands() async throws {
        // Deterministic single-flight proof — no yields, sleeps, polling,
        // retries, or elapsed-time criteria.
        //
        // Design:
        //   * `entered` — opened by the mock the first time
        //     `beforeSetTemperatures` fires. Because `preheat` calls
        //     `beginCommand` (which publishes `pendingCommand`) synchronously
        //     before awaiting `setTemperatures`, waiting on `entered` proves
        //     both "first command is in flight" and "pendingCommand is set".
        //   * `release` — awaited by the mock only on the FIRST invocation;
        //     the test opens it to let the first command complete.
        //   * `hookInvocations` — counts hook entries so ONLY the first
        //     service invocation is gated. If single-flight regresses and the
        //     concurrent `preheat(.abs)` reaches the mock, its hook returns
        //     immediately, its call is recorded, and no task is blocked on a
        //     closed gate — so the regression is captured as a failed
        //     assertion rather than a deadlock.
        //   * `capture-then-drain-then-assert` — we do not assert while the
        //     first task is held behind `release`. We capture pending state
        //     and the second-call evidence into locals, unconditionally open
        //     `release`, await the first task to completion, and only then
        //     assert. This guarantees no assertion failure can strand either
        //     task before cleanup.
        //   * Unstructured `Task` — the first command runs as an unstructured
        //     child task rather than `async let`, so an unexpected early exit
        //     from the method (thrown error, teardown) does not implicitly
        //     await the gate waiter before `addTeardownBlock` can run.
        //   * Teardown safety net — the teardown block idempotently opens
        //     `release` so any unexpected early exit still drains the mock.
        let entered = AsyncGate()
        let release = AsyncGate()
        let hookInvocations = HookCounter()

        addTeardownBlock { await release.open() }

        mockService.beforeSetTemperatures = {
            let ordinal = await hookInvocations.next()
            guard ordinal == 1 else { return }
            await entered.open()
            await release.wait()
        }
        let vm = try makeViewModel(printer: try idlePrinter(), capabilities: Self.fullCaps)
        await vm.loadCapabilities()

        // Unstructured task so no implicit awaiter of `first` blocks an early
        // exit path before teardown can open `release`.
        let first = Task { await vm.preheat(.pla) }

        // Deterministic handshake — resumes only after the first command has
        // entered the gated service path, i.e. after `beginCommand` has set
        // `pendingCommand`.
        await entered.wait()

        // Capture, do not assert. Clearing `setTemperaturesCalledWith` here
        // isolates any second-invocation side effect for post-drain inspection.
        let capturedPending = vm.pendingCommand
        mockService.setTemperaturesCalledWith = nil

        // Concurrent second command. Under correct behavior it is dropped
        // inside `beginCommand` and never reaches the mock. Under regression
        // it would reach the mock; the second hook invocation returns without
        // waiting (see `guard ordinal == 1 else { return }`) so this call
        // returns promptly and its evidence is captured — no deadlock.
        await vm.preheat(.abs)
        let capturedSetTempCall = mockService.setTemperaturesCalledWith
        let capturedError = vm.lastError

        // Release and drain BEFORE asserting so no assertion failure can
        // strand the first task.
        await release.open()
        await first.value

        XCTAssertNotNil(capturedPending, "First command must be in flight before the concurrent call")
        XCTAssertNil(capturedSetTempCall, "Concurrent command must be dropped")
        XCTAssertNil(capturedError, "Dropped command must not surface as an error")
    }

    func test_signalRClearsPendingCommand() async throws {
        let base = try idlePrinter()
        let vm = try makeViewModel(printer: base, capabilities: Self.fullCaps)
        await vm.loadCapabilities()

        await vm.preheat(.pla)
        XCTAssertNotNil(vm.pendingCommand, "Pending stays set after success — cleared by SignalR")

        // A *confirming* snapshot must move the preheat's own domain (targets).
        // An identical snapshot carries no evidence and must NOT clear pending.
        vm.handlePrinterUpdate(base)
        XCTAssertNotNil(vm.pendingCommand, "No-op snapshot must not release the command")

        var warmed = base
        warmed.hotendTarget = 200
        vm.handlePrinterUpdate(warmed)
        XCTAssertNil(vm.pendingCommand)
    }

    func test_canControlFalse_whilePrinting() async throws {
        let printer = try TestData.decodePrinter() // state="printing"
        let vm = PrinterControlsViewModel.configuredForTests(printerService: mockService, printer: printer)

        XCTAssertFalse(vm.canControl)
        XCTAssertNotNil(vm.blockedReason)

        await vm.preheat(.pla)
        XCTAssertNil(mockService.setTemperaturesCalledWith)
        XCTAssertNotNil(vm.lastError)
        XCTAssertEqual(vm.lastError?.isRetryable, false)
    }

    func test_errorMapping_5xx_isRetryable() async throws {
        let vm = try makeViewModel(printer: try idlePrinter(), capabilities: Self.fullCaps)
        await vm.loadCapabilities()
        mockService.errorToThrow = NetworkError.serverError(503)

        await vm.preheat(.pla)

        XCTAssertNotNil(mockService.setTemperaturesCalledWith)
        XCTAssertNotNil(vm.lastError)
        XCTAssertEqual(vm.lastError?.isRetryable, true)
        XCTAssertNil(vm.pendingCommand, "Pending must clear on failure so user can retry")
    }

    func test_errorMapping_4xx_unauthorized_notRetryable() async throws {
        let vm = try makeViewModel(printer: try idlePrinter(), capabilities: Self.fullCaps)
        await vm.loadCapabilities()
        mockService.errorToThrow = NetworkError.unauthorized

        await vm.homeAll()

        XCTAssertNotNil(mockService.homeCalledWith)
        XCTAssertEqual(vm.lastError?.isRetryable, false)
    }

    func test_errorMapping_preconditions_requireRefreshAndAreNotRetryable() {
        let stale = PrinterControlsViewModel.mapError(
            NetworkError.preconditionFailed(
                APIError(
                    title: nil,
                    status: 412,
                    detail: "Printer changed after review.",
                    errors: nil,
                    message: nil,
                    code: nil
                )
            )
        )
        let missing = PrinterControlsViewModel.mapError(
            NetworkError.preconditionRequired(nil)
        )

        XCTAssertEqual(stale.message, "Printer changed after review.")
        XCTAssertFalse(stale.isRetryable)
        XCTAssertEqual(
            missing.message,
            "A reviewed revision is required. Refresh and confirm again."
        )
        XCTAssertFalse(missing.isRetryable)
    }

    func test_conflictRejectionShowsServerTextWithoutUnknownOutcomeWarning() async throws {
        let cases: [(APIError?, String)] = [
            (APIError(
                title: "Conflict", status: 409, detail: "Another physical operation owns the printer barrier.",
                errors: nil, message: nil, code: "FenceConflict"
            ), "Another physical operation owns the printer barrier."),
            (nil, "Printer is busy.")
        ]
        for (api, expected) in cases {
            mockService.errorToThrow = nil
            let vm = try makeViewModel(printer: try idlePrinter(), capabilities: Self.fullCaps)
            await vm.loadCapabilities()
            mockService.errorToThrow = NetworkError.conflict(api)

            await vm.preheat(.pla)

            XCTAssertNotNil(mockService.setTemperaturesCalledWith)
            XCTAssertEqual(vm.lastError?.message, expected)
            XCTAssertEqual(vm.lastError?.isRetryable, false)
            XCTAssertNil(vm.pendingCommand)
            XCTAssertFalse(vm.isExecuting)
        }
    }

    func test_errorMapping_network_isRetryable() async throws {
        let vm = try makeViewModel(printer: try idlePrinter(), capabilities: Self.fullCaps)
        await vm.loadCapabilities()
        mockService.errorToThrow = NetworkError.noConnection

        await vm.jog(axis: "X", distanceMm: 1)

        XCTAssertNotNil(mockService.moveCalledWith)
        XCTAssertEqual(vm.lastError?.isRetryable, true)
    }

    func test_dismissError_clearsLastError() async throws {
        let vm = try makeViewModel(printer: try idlePrinter(), capabilities: Self.fullCaps)
        await vm.loadCapabilities()
        mockService.errorToThrow = NetworkError.serverError(500)
        await vm.preheat(.pla)
        XCTAssertNotNil(mockService.setTemperaturesCalledWith)
        XCTAssertNotNil(vm.lastError)

        vm.dismissError()
        XCTAssertNil(vm.lastError)
    }


    func test_isExecuting_falseInitially() async throws {
        let vm = try makeViewModel(printer: try idlePrinter(), capabilities: Self.fullCaps)
        await vm.loadCapabilities()
        XCTAssertFalse(vm.isExecuting)
    }

    func test_isExecuting_trueWhileInFlight_falseAfterError() async throws {
        let gate = AsyncGate()
        mockService.beforeSetTemperatures = { await gate.wait() }
        let vm = try makeViewModel(printer: try idlePrinter(), capabilities: Self.fullCaps)
        await vm.loadCapabilities()

        async let first: Void = vm.preheat(.pla)

        // Deterministically wait until the task is blocked at the gate.
        while await !gate.hasWaiters { await Task.yield() }
        XCTAssertTrue(vm.isExecuting)

        // Fail the command so pendingCommand (and isExecuting) clears.
        mockService.errorToThrow = NetworkError.serverError(500)
        await gate.open()
        await first
        XCTAssertFalse(vm.isExecuting)
    }

    func test_isExecuting_staysTrueAfterSuccessfulSend_untilConfirmingTargetSnapshot() async throws {
        let gate = AsyncGate()
        mockService.beforeSetTemperatures = { await gate.wait() }
        let base = try idlePrinter()
        let vm = try makeViewModel(printer: base, capabilities: Self.fullCaps)
        await vm.loadCapabilities()

        async let first: Void = vm.preheat(.pla)

        // Wait until blocked at gate.
        while await !gate.hasWaiters { await Task.yield() }
        XCTAssertTrue(vm.isExecuting)

        // A *successful* HTTP response (no error set) must NOT clear the
        // command: pending persists until SignalR confirms the effect.
        await gate.open()
        await first
        XCTAssertTrue(vm.isExecuting,
                      "A successful send keeps the command pending until a confirming snapshot arrives")
        XCTAssertNil(vm.lastError)

        // Only a confirming target snapshot releases it.
        var warmed = base
        warmed.hotendTarget = 200 // base is 215 → moves
        vm.handlePrinterUpdate(warmed)
        XCTAssertFalse(vm.isExecuting,
                       "The confirming target snapshot clears pending / isExecuting")
    }

    // MARK: - Live snapshot forwarding (issue #706 F1 review)
    //
    // These tests pin the regression where `PrinterControlsSection` only
    // forwarded snapshots that changed `state` or `isOnline`, leaving
    // jog/preheat/home controls jammed after position/temperature-only
    // updates. The fix is a `PrinterControlsUpdateSignal` that captures
    // every field the VM reads; the VM itself must clear `pendingCommand`
    // when a matching-id snapshot arrives regardless of which field
    // moved, and must ignore snapshots for other printers.

    func test_handlePrinterUpdate_clearsPending_onPositionOnlyUpdate() async throws {
        let base = try idlePrinter()
        let vm = try makeViewModel(printer: base, capabilities: Self.fullCaps)
        await vm.loadCapabilities()

        await vm.jog(axis: "X", distanceMm: 10)
        XCTAssertNotNil(vm.pendingCommand, "jog leaves pending set until SignalR confirms")

        // Snapshot mutates only x/y/z; state and isOnline are unchanged.
        var moved = base
        moved.x = (base.x ?? 0) + 10
        XCTAssertEqual(moved.state, base.state)
        XCTAssertEqual(moved.isOnline, base.isOnline)
        XCTAssertNotEqual(
            PrinterControlsUpdateSignal(printer: base),
            PrinterControlsUpdateSignal(printer: moved),
            "Position-only drift must produce a distinct signal so `.onChange` fires"
        )

        vm.handlePrinterUpdate(moved)
        XCTAssertNil(vm.pendingCommand,
                     "Position-only update must clear pending — regression from #706 review")
    }

    func test_handlePrinterUpdate_measuredTemperatureOnly_doesNotClearPendingPreheat() async throws {
        let base = try idlePrinter()
        let vm = try makeViewModel(printer: base, capabilities: Self.fullCaps)
        await vm.loadCapabilities()

        await vm.preheat(.pla)
        XCTAssertNotNil(vm.pendingCommand, "preheat leaves pending set until SignalR confirms")

        // Measured hotend/bed drift only — the commanded *targets* are unchanged.
        var drifted = base
        drifted.hotendTemp = 42
        drifted.bedTemp = 55
        XCTAssertEqual(drifted.hotendTarget, base.hotendTarget)
        XCTAssertEqual(drifted.bedTarget, base.bedTarget)
        // The update is still forwarded, but must not confirm the preheat.
        XCTAssertNotEqual(
            PrinterControlsUpdateSignal(printer: base),
            PrinterControlsUpdateSignal(printer: drifted)
        )

        vm.handlePrinterUpdate(drifted)
        XCTAssertNotNil(vm.pendingCommand,
                        "Measured-temperature drift must NOT clear a pending preheat — only the target confirms it")
        XCTAssertEqual(vm.printer.hotendTemp, 42, "Cached snapshot must still advance")
    }

    func test_handlePrinterUpdate_clearsPending_onHomingOnlyUpdate() async throws {
        // Start from a printer that reports no axes homed, so the
        // update we craft below actually moves `homedAxes`.
        let unhomedJSON = TestJSON.printer
            .replacingOccurrences(of: "\"state\": \"printing\"", with: "\"state\": \"ready\"")
            .replacingOccurrences(of: "\"homedAxes\": \"xyz\"", with: "\"homedAxes\": \"\"")
        let base = try TestData.decoder.decode(Printer.self, from: unhomedJSON.data(using: .utf8)!)
        let vm = PrinterControlsViewModel.configuredForTests(printerService: mockService, printer: base)
        mockService.capabilitiesToReturn = Self.fullCaps
        await vm.loadCapabilities()

        await vm.homeAll()
        XCTAssertNotNil(vm.pendingCommand)

        // Home reports as new `homedAxes` without a state transition.
        var homed = base
        homed.homedAxes = "xyz"
        XCTAssertEqual(homed.state, base.state)
        XCTAssertNotEqual(
            PrinterControlsUpdateSignal(printer: base),
            PrinterControlsUpdateSignal(printer: homed),
            "Homing drift must produce a distinct signal so `.onChange` fires"
        )

        vm.handlePrinterUpdate(homed)
        XCTAssertNil(vm.pendingCommand,
                     "Homing-only update must clear pending — regression from #706 review")
    }

    func test_handlePrinterUpdate_ignoresUpdatesForDifferentPrinter() async throws {
        let base = try idlePrinter()
        let vm = try makeViewModel(printer: base, capabilities: Self.fullCaps)
        await vm.loadCapabilities()

        await vm.jog(axis: "Y", distanceMm: 1)
        XCTAssertNotNil(vm.pendingCommand)

        // Decode a second, unrelated printer (different id via printerMinimal fixture).
        let otherJSON = TestJSON.printerMinimal
            .replacingOccurrences(of: "\"isOnline\": false", with: "\"isOnline\": true")
        let other = try TestData.decoder.decode(Printer.self, from: otherJSON.data(using: .utf8)!)
        XCTAssertNotEqual(other.id, base.id)

        vm.handlePrinterUpdate(other)
        XCTAssertNotNil(vm.pendingCommand,
                        "Snapshots for a different printer id must never clear pending state")
    }

    // MARK: - Command correlation: cross-talk must NOT clear pending
    //
    // The controls surface consumes a merged telemetry stream. Ambient churn
    // in a field the pending command does not drive must leave it pending
    // (issue #706 F1 review defect A). Each negative test also proves the
    // cached snapshot is still advanced so the *next* diff is measured from
    // the freshly received values.

    func test_handlePrinterUpdate_temperatureNoise_doesNotClearPendingJog() async throws {
        let base = try idlePrinter()
        let vm = try makeViewModel(printer: base, capabilities: Self.fullCaps)
        await vm.loadCapabilities()

        await vm.jog(axis: "X", distanceMm: 10)
        XCTAssertNotNil(vm.pendingCommand)

        var warmed = base
        warmed.hotendTemp = (base.hotendTemp ?? 0) + 3
        warmed.bedTemp = (base.bedTemp ?? 0) + 1
        warmed.hotendTarget = 240
        warmed.bedTarget = 80
        XCTAssertEqual(warmed.x, base.x)

        vm.handlePrinterUpdate(warmed)
        XCTAssertNotNil(vm.pendingCommand,
                        "Temperature/target noise must not clear a pending jog")
        XCTAssertEqual(vm.printer.hotendTarget, 240,
                       "Cached snapshot must still advance even when pending is retained")
    }

    func test_handlePrinterUpdate_positionNoise_doesNotClearPendingPreheat() async throws {
        let base = try idlePrinter()
        let vm = try makeViewModel(printer: base, capabilities: Self.fullCaps)
        await vm.loadCapabilities()

        await vm.preheat(.pla)
        XCTAssertNotNil(vm.pendingCommand)

        var moved = base
        moved.x = (base.x ?? 0) + 25
        moved.y = (base.y ?? 0) - 5
        moved.z = (base.z ?? 0) + 1
        XCTAssertEqual(moved.hotendTarget, base.hotendTarget)

        vm.handlePrinterUpdate(moved)
        XCTAssertNotNil(vm.pendingCommand,
                        "Position noise must not clear a pending preheat")
        XCTAssertEqual(vm.printer.x, moved.x, "Cached snapshot must still advance")
    }

    func test_handlePrinterUpdate_otherAxisMotion_doesNotClearPendingJogX() async throws {
        let base = try idlePrinter()
        let vm = try makeViewModel(printer: base, capabilities: Self.fullCaps)
        await vm.loadCapabilities()

        await vm.jog(axis: "X", distanceMm: 10)
        XCTAssertNotNil(vm.pendingCommand)

        // Only Z moves; the pending jog is on X.
        var moved = base
        moved.z = (base.z ?? 0) + 5
        XCTAssertEqual(moved.x, base.x)

        vm.handlePrinterUpdate(moved)
        XCTAssertNotNil(vm.pendingCommand,
                        "Motion on an unrelated axis must not clear a jog on X")
    }

    func test_handlePrinterUpdate_homingNoise_doesNotClearPendingPreheat() async throws {
        let base = try idlePrinter()
        let vm = try makeViewModel(printer: base, capabilities: Self.fullCaps)
        await vm.loadCapabilities()

        await vm.preheat(.abs)
        XCTAssertNotNil(vm.pendingCommand)

        var rehomed = base
        rehomed.homedAxes = "xy"
        XCTAssertEqual(rehomed.hotendTarget, base.hotendTarget)

        vm.handlePrinterUpdate(rehomed)
        XCTAssertNotNil(vm.pendingCommand,
                        "Homed-axes churn must not clear a pending preheat")
    }

    func test_handlePrinterUpdate_positionAndTempNoise_doesNotClearPendingHome() async throws {
        // Unknown homing requires fresh evidence, never position/temperature noise.
        var base = try idlePrinter()
        base.homedAxes = nil
        let vm = try makeViewModel(printer: base, capabilities: Self.fullCaps)
        await vm.loadCapabilities()

        await vm.homeAll()
        XCTAssertNotNil(vm.pendingCommand)

        var drifted = base
        drifted.x = (base.x ?? 0) + 10
        drifted.hotendTemp = (base.hotendTemp ?? 0) + 4
        XCTAssertEqual(drifted.homedAxes, base.homedAxes)

        vm.handlePrinterUpdate(drifted)
        XCTAssertNotNil(vm.pendingCommand,
                        "Position/temperature noise must not clear a pending home; homedAxes is the signal")
    }

    // MARK: - Command correlation: relevant evidence DOES clear pending

    func test_handlePrinterUpdate_targetChange_clearsPendingPreheat() async throws {
        let base = try idlePrinter()
        let vm = try makeViewModel(printer: base, capabilities: Self.fullCaps)
        await vm.loadCapabilities()

        await vm.preheat(.pla)
        XCTAssertNotNil(vm.pendingCommand)

        var warmed = base
        warmed.hotendTarget = 200 // base is 215 → moves
        XCTAssertEqual(warmed.hotendTemp, base.hotendTemp, "Target-only change: measured temp stays put")
        vm.handlePrinterUpdate(warmed)
        XCTAssertNil(vm.pendingCommand, "Target change confirms a preheat")
    }

    func test_handlePrinterUpdate_offlineTransition_clearsAnyPending() async throws {
        let base = try idlePrinter()
        let vm = try makeViewModel(printer: base, capabilities: Self.fullCaps)
        await vm.loadCapabilities()

        await vm.jog(axis: "X", distanceMm: 10)
        XCTAssertNotNil(vm.pendingCommand)

        var offline = base
        offline.isOnline = false // no position/temp change — lifecycle only
        XCTAssertEqual(offline.x, base.x)

        vm.handlePrinterUpdate(offline)
        XCTAssertNil(vm.pendingCommand,
                     "Going offline invalidates any in-flight command")
    }

    func test_handlePrinterUpdate_stateTransition_clearsAnyPending() async throws {
        let base = try idlePrinter()
        let vm = try makeViewModel(printer: base, capabilities: Self.fullCaps)
        await vm.loadCapabilities()

        await vm.preheat(.pla)
        XCTAssertNotNil(vm.pendingCommand)

        var printing = base
        printing.state = "printing" // no temp/target change — lifecycle only
        XCTAssertEqual(printing.hotendTarget, base.hotendTarget)

        vm.handlePrinterUpdate(printing)
        XCTAssertNil(vm.pendingCommand,
                     "A state transition supersedes any in-flight command")
    }

    // MARK: - PrinterControlsUpdateSignal

    func test_updateSignal_equalWhenIrrelevantFieldsChange() throws {
        let base = try idlePrinter()
        var churned = base
        // Camera / progress / job / spool churn must not trigger `.onChange`.
        churned.progress = (base.progress ?? 0) + 0.1
        churned.jobName = "\(base.jobName ?? "job")-v2"
        churned.thumbnailUrl = "http://example.com/other.png"
        churned.cameraStreamUrl = "http://example.com/stream"

        XCTAssertEqual(
            PrinterControlsUpdateSignal(printer: base),
            PrinterControlsUpdateSignal(printer: churned),
            "Signal must ignore fields the controls VM does not consume"
        )
    }

    func test_updateSignal_differentWhenStateChanges() throws {
        let base = try idlePrinter()
        var printing = base
        printing.state = "printing"
        XCTAssertNotEqual(
            PrinterControlsUpdateSignal(printer: base),
            PrinterControlsUpdateSignal(printer: printing)
        )
    }
}

// MARK: - Test gate helper

private final class SafetyTestClock: @unchecked Sendable {
    private let lock = NSLock()
    private var date = Date()
    func now() -> Date { lock.withLock { date } }
    func advance(_ seconds: TimeInterval) { lock.withLock { date.addTimeInterval(seconds) } }
}

enum VerifiedSafetyFixtures {
    static func discovery(at date: Date = Date().addingTimeInterval(-1)) -> PrinterVerifiedSafetyDto {
        let operation = VerifiedSafetyOperationCapabilityDto(
            support: .supported, source: "synthetic:authoritative-probe", observedAtUtc: date
        )
        return PrinterVerifiedSafetyDto(
            contractVersion: 1,
            discovery: .init(state: .partial, observedAtUtc: date, sourceRevision: "0"),
            operations: .init(
                absoluteMovement: operation, firmwareZOffsetSave: operation,
                filamentLoad: operation, filamentUnload: operation, filamentChange: operation
            ),
            extrusion: .init(minimumSafeMeasuredHotendTemperatureC: .init(
                state: .verified, value: 205, source: "synthetic:material-policy", observedAtUtc: date
            )),
            positioning: .init(
                coordinateOriginMm: .init(
                    state: .verified, value: .init(x: 10, y: 20, z: 1),
                    source: "synthetic:origin", observedAtUtc: date
                ),
                travelEnvelopeMm: .init(
                    state: .verified, value: .init(
                        minimum: .init(x: -100, y: -50, z: 0), maximum: .init(x: 200, y: 150, z: 100)
                    ), source: "synthetic:travel", observedAtUtc: date
                ),
                minimumClearanceZMm: .init(
                    state: .verified, value: 0.2, source: "synthetic:clearance", observedAtUtc: date
                )
            )
        )
    }

    static func status(
        id: UUID, at date: Date = Date(), position: SafetyVector3Dto = .init(x: 20, y: 30, z: 10),
        isOnline: Bool = true
    ) -> PrinterStatusDetail {
        var status = PrinterStatusDetail(
            id: id, isOnline: isOnline, state: "ready", progress: nil, jobName: nil,
            thumbnailUrl: nil, cameraStreamUrl: nil, cameraSnapshotUrl: nil,
            x: position.x, y: position.y, z: position.z,
            hotendTemp: 220, bedTemp: nil, hotendTarget: 220, bedTarget: nil,
            homedAxes: "xyz", spoolInfo: nil, mmuStatus: nil
        )
        status.safetyTelemetry = .init(
            measuredHotendTemperatureC: .init(value: 220, observedAtUtc: date, staleAfterSeconds: 15, source: "synthetic:hotend"),
            targetHotendTemperatureC: .init(value: 220, observedAtUtc: date, staleAfterSeconds: 15, source: "synthetic:target"),
            homedAxes: .init(value: ["x", "y", "z"], observedAtUtc: date, staleAfterSeconds: 15, source: "synthetic:homing"),
            coordinateOriginOffsetMm: .init(
                value: .init(x: 10, y: 20, z: 1), observedAtUtc: date, staleAfterSeconds: 15, source: "synthetic:frame"
            )
        )
        return status
    }
}

@MainActor
final class GuardedMaterialControlsTests: XCTestCase {
    private func idlePrinter() throws -> Printer {
        try TestData.decodePrinter(from: TestJSON.printer
            .replacingOccurrences(of: "\"backend\": \"Moonraker\"", with: "\"backend\": \"OctoPrint\"")
            .replacingOccurrences(of: "\"state\": \"printing\"", with: "\"state\": \"ready\""))
    }

    private func fixture(
        caps: PrinterBackendCapabilities? = nil,
        access: @escaping @MainActor () -> String? = { nil }
    ) async throws -> (PrinterControlsViewModel, MockPrinterService) {
        var printer = try idlePrinter()
        printer.state = "ready"
        printer.homedAxes = nil
        let service = MockPrinterService()
        var supported = PrinterBackendCapabilities.allControlsFixture
        supported.supportsExtrusion = true
        supported.supportsFilamentLoad = true
        supported.supportsFilamentUnload = true
        supported.supportsFilamentChange = true
        supported.supportsZOffset = true
        supported.supportsZOffsetFirmwareSave = true
        supported.supportsAbsoluteMovement = true
        supported.verifiedSafety = VerifiedSafetyFixtures.discovery()
        service.capabilitiesToReturn = caps ?? supported
        service.statusToReturn = VerifiedSafetyFixtures.status(id: printer.id)
        service.detailsToReturn = PrinterDetails(
            id: printer.id, name: printer.name, backend: printer.backend,
            rowVersion: "reviewed-revision", zOffsetMm: 0.12,
            capabilities: .init(
                maxBuildVolumeX: 256, maxBuildVolumeY: 256, maxBuildVolumeZ: 256,
                maxHotendTemp: 300, maxBedTemp: 120, hasHeatedBed: true
            )
        )
        let model = PrinterControlsViewModel.configuredForTests(
            printerService: service, printer: printer, accessCheck: access
        )
        await model.loadCapabilities()
        return (model, service)
    }

    func test_absoluteMoveAcceptsPartialCoordinatesAndRequiresAtLeastOne() async throws {
        let (blank, blankService) = try await fixture()
        XCTAssertEqual(blank.absoluteMoveBlockedReason(x: nil, y: nil, z: nil),
                       ControlNumberInput.absoluteCoordinatesMessage)
        await blank.moveTo(x: nil, y: nil, z: nil, feedrateMmMin: nil)
        XCTAssertNil(blankService.moveToCalledWith)
        XCTAssertNil(blank.pendingCommand)
        XCTAssertEqual(blank.lastError?.message, ControlNumberInput.absoluteCoordinatesMessage)

        // A partial destination is permitted; unspecified axes stay unchanged and are
        // dispatched as nil rather than being back-filled from telemetry. Each point
        // needs its own fixture because a command lease is consumed per dispatch.
        for point in [(nil, 30.0, 10.0), (20.0, nil, 10.0), (20.0, 30.0, nil), (0.0, -2.5, 10.0)]
            as [(Double?, Double?, Double?)] {
            let (model, service) = try await fixture()
            XCTAssertNil(model.absoluteMoveBlockedReason(x: point.0, y: point.1, z: point.2))
            await model.moveTo(x: point.0, y: point.1, z: point.2, feedrateMmMin: nil)
            XCTAssertNil(model.lastError)
            XCTAssertEqual(service.moveToCalledWith?.x, point.0)
            XCTAssertEqual(service.moveToCalledWith?.y, point.1)
            XCTAssertEqual(service.moveToCalledWith?.z, point.2)
            XCTAssertEqual(service.moveToCalledWith?.feedrateMmMin, 600)
            XCTAssertNil(service.moveCalledWith)
        }
    }

    func test_absoluteMoveRejectsUnknownUnsupportedOrStaleVerifiedEvidence() async throws {
        for variant in 0..<17 {
            let (model, service) = try await fixture()
            switch variant {
            case 0: service.capabilitiesToReturn?.verifiedSafety = nil
            case 1: service.capabilitiesToReturn?.verifiedSafety?.operations.absoluteMovement.support = .unknown
            case 2: service.capabilitiesToReturn?.verifiedSafety?.operations.absoluteMovement.support = .unsupported
            case 3: service.capabilitiesToReturn?.verifiedSafety?.positioning.coordinateOriginMm.state = .unknown
            case 4: service.capabilitiesToReturn?.verifiedSafety?.positioning.travelEnvelopeMm.state = .unknown
            case 5: service.capabilitiesToReturn?.verifiedSafety?.positioning.minimumClearanceZMm.state = .unknown
            case 6: service.statusToReturn?.safetyTelemetry?.homedAxes.value = nil
            case 7: service.statusToReturn?.safetyTelemetry?.homedAxes.value = ["X", "Y"]
            case 8: service.statusToReturn?.safetyTelemetry?.homedAxes.observedAtUtc = Date().addingTimeInterval(-60)
            case 9: service.statusToReturn?.safetyTelemetry?.coordinateOriginOffsetMm.value = nil
            case 10: service.statusToReturn?.safetyTelemetry?.coordinateOriginOffsetMm.observedAtUtc = Date().addingTimeInterval(-60)
            case 11: service.statusToReturn?.safetyTelemetry?.coordinateOriginOffsetMm.value?.x = 11
            case 12: service.capabilitiesToReturn?.verifiedSafety?.discovery.sourceRevision = "other-revision"
            case 13: service.capabilitiesToReturn?.verifiedSafety?.positioning.travelEnvelopeMm.value?.minimum.x = 1000
            case 14: service.capabilitiesToReturn?.verifiedSafety?.operations.absoluteMovement.source = nil
            case 15: service.statusToReturn?.safetyTelemetry?.coordinateOriginOffsetMm.observedAtUtc = Date().addingTimeInterval(60)
            default:
                var limited = PrinterBackendCapabilities(
                    supportsMovement: true, supportsTemperatureControl: true,
                    supportsBedTemperature: true, supportsFanControl: false,
                    supportsHoming: true, supportedAxes: ["X", "Y"]
                )
                limited.supportsAbsoluteMovement = true
                limited.verifiedSafety = VerifiedSafetyFixtures.discovery()
                service.capabilitiesToReturn = limited
            }
            await model.refreshSafetyEvidence()
            XCTAssertNotNil(model.absoluteMoveBlockedReason(x: 20, y: 30, z: 10), "\(variant)")
            await model.moveTo(x: 20, y: 30, z: 10, feedrateMmMin: nil)
            XCTAssertNil(service.moveToCalledWith, "\(variant)")
            XCTAssertNotNil(model.lastError, "\(variant)")
            XCTAssertNil(model.pendingCommand, "\(variant)")
        }
    }

    func test_absoluteMoveChecksEffectiveEnvelopeClearanceAndManualPrecision() async throws {
        let (model, service) = try await fixture()
        for point in [
            SafetyVector3Dto(x: -111, y: 30, z: 10), .init(x: 191, y: 30, z: 10),
            .init(x: 20, y: -71, z: 10), .init(x: 20, y: 131, z: 10),
            .init(x: 20, y: 30, z: 100), .init(x: 20, y: 30, z: -0.9),
            .init(x: 1.2345, y: 30, z: 10), .init(x: .nan, y: 30, z: 10)
        ] {
            await model.moveTo(x: point.x, y: point.y, z: point.z, feedrateMmMin: nil)
            XCTAssertNil(service.moveToCalledWith)
            XCTAssertNotNil(model.lastError)
        }
        await model.moveTo(x: -1.234, y: 0, z: -0.5, feedrateMmMin: nil)
        XCTAssertNil(model.lastError)
        XCTAssertEqual(service.moveToCalledWith?.x, -1.234)
        XCTAssertEqual(service.moveToCalledWith?.y, 0)
        XCTAssertEqual(service.moveToCalledWith?.z, -0.5)
        XCTAssertEqual(service.moveToCalledWith?.feedrateMmMin, 600)
    }

    func test_signedChoicesConvertFeedrateExactlyOnceAndRejectUnboundedInputs() throws {
        for distance in MaterialControlInput.distances {
            for speed in MaterialControlInput.speeds {
                XCTAssertEqual(try MaterialControlInput.feedrate(distance: distance, speed: speed), speed * 60)
                XCTAssertEqual(try MaterialControlInput.feedrate(distance: -distance, speed: speed), speed * 60)
            }
        }
        for distance in [0, 1, 101, -101, Double.nan, Double.infinity] {
            XCTAssertThrowsError(try MaterialControlInput.feedrate(distance: distance, speed: 1))
        }
        for speed in [-1, 0, 2, 11, 600] {
            XCTAssertThrowsError(try MaterialControlInput.feedrate(distance: 10, speed: speed))
        }
    }

    func test_verifiedExtrusionDispatchesEverySignedChoiceWithOneConversion() async throws {
        let (model, service) = try await fixture()
        for distance in MaterialControlInput.distances {
            for speed in MaterialControlInput.speeds {
                for sign in [-1.0, 1.0] {
                    XCTAssertNil(model.extrusionBlockedReason)
                    await model.extrude(distanceMm: sign * distance, speedMmPerSecond: speed)
                    XCTAssertEqual(service.extrudeCalledWith?.distanceMm, sign * distance)
                    XCTAssertEqual(service.extrudeCalledWith?.feedrateMmPerMinute, speed * 60)
                    XCTAssertNil(model.lastError)
                    XCTAssertNil(model.pendingCommand)
                }
            }
        }
        XCTAssertEqual(service.extrudeCallCount, 24)
        XCTAssertNil(service.setActiveSpoolCalledWith)
        XCTAssertTrue(service.bindToolheadSpoolCalls.isEmpty)
    }

    func test_missingStaleFutureColdNonfiniteAndTargetOnlyEvidenceCannotAuthorizeMaterial() async throws {
        for variant in 0..<11 {
            let (model, service) = try await fixture()
            switch variant {
            case 0: service.statusToReturn?.safetyTelemetry = nil
            case 1: service.statusToReturn?.safetyTelemetry?.measuredHotendTemperatureC.value = nil
            case 2: service.statusToReturn?.safetyTelemetry?.measuredHotendTemperatureC.observedAtUtc = Date().addingTimeInterval(-16)
            case 3: service.statusToReturn?.safetyTelemetry?.measuredHotendTemperatureC.observedAtUtc = Date().addingTimeInterval(60)
            case 4: service.statusToReturn?.safetyTelemetry?.measuredHotendTemperatureC.value = 204.9
            case 5: service.statusToReturn?.safetyTelemetry?.measuredHotendTemperatureC.value = .nan
            case 6: service.statusToReturn?.safetyTelemetry?.measuredHotendTemperatureC.source = nil
            case 7: service.statusToReturn?.safetyTelemetry?.measuredHotendTemperatureC.staleAfterSeconds = 0
            case 8: service.capabilitiesToReturn?.verifiedSafety?.extrusion.minimumSafeMeasuredHotendTemperatureC.state = .unknown
            case 9: service.capabilitiesToReturn?.verifiedSafety?.extrusion.minimumSafeMeasuredHotendTemperatureC.source = nil
            default: service.capabilitiesToReturn?.verifiedSafety?.contractVersion = 2
            }
            service.statusToReturn?.safetyTelemetry?.targetHotendTemperatureC.value = 300
            await model.refreshSafetyEvidence()
            XCTAssertNotNil(model.extrusionBlockedReason, "\(variant)")
            await model.extrude(distanceMm: -25, speedMmPerSecond: 5)
            for operation in PhysicalFilamentOperation.allCases { await model.performFilament(operation) }
            XCTAssertNil(service.extrudeCalledWith, "\(variant)")
            XCTAssertTrue(service.physicalFilamentCalls.isEmpty, "\(variant)")
        }
        let (model, service) = try await fixture()
        service.statusToReturn?.safetyTelemetry?.measuredHotendTemperatureC.value = 205
        await model.refreshSafetyEvidence()
        XCTAssertNil(model.extrusionBlockedReason, "Exact verified minimum is permitted")
    }

    func test_verifiedSupportOverridesOptimisticLegacyFlagsAndRetainsPartialFacts() async throws {
        let (model, service) = try await fixture()
        service.capabilitiesToReturn?.verifiedSafety?.operations.filamentLoad.support = .unsupported
        service.capabilitiesToReturn?.verifiedSafety?.operations.filamentChange.support = .unknown
        await model.refreshSafetyEvidence()
        XCTAssertTrue(model.filamentBlockedReason(.load)?.contains("unsupported") == true)
        XCTAssertTrue(model.filamentBlockedReason(.change)?.contains("unknown") == true)
        await model.performFilament(.load)
        await model.performFilament(.change)
        await model.performFilament(.unload)
        XCTAssertEqual(service.physicalFilamentCalls, ["unload"], "Partial discovery still enables proven facts")
    }

    private func reachAdjustment(_ model: PrinterControlsViewModel, _ service: MockPrinterService) async throws {
        await model.startCalibration()
        model.beginCalibrationHome()
        await model.homeForCalibration()
        XCTAssertEqual(model.calibrationStep, .home)
        service.statusToReturn = VerifiedSafetyFixtures.status(id: model.printer.id)
        await model.refreshSafetyEvidence()
        XCTAssertEqual(model.calibrationStep, .position)
        await model.positionForCalibration()
        XCTAssertEqual(service.moveToCalledWith?.x, 40, "Envelope center minus actual frame offset, not catalog bed center")
        XCTAssertEqual(service.moveToCalledWith?.y, 30)
        XCTAssertEqual(service.moveToCalledWith?.z, 10)
        XCTAssertEqual(model.calibrationStep, .position, "HTTP acceptance alone cannot confirm a position")
        service.statusToReturn = VerifiedSafetyFixtures.status(id: model.printer.id, position: .init(x: 40, y: 30, z: 10))
        await model.refreshSafetyEvidence()
        XCTAssertEqual(model.calibrationStep, .adjust)
    }

    func test_verifiedCalibrationCompletesAllStepsWithExactSignedIncrementsAndReviewedRevision() async throws {
        let (model, service) = try await fixture()
        try await reachAdjustment(model, service)
        var z = 10.0
        var offset = 0.12
        for delta in [-0.01, -0.05, -0.1, 0.01, 0.05, 0.1] {
            await model.adjustCalibration(delta: delta)
            z = (z * 1000 + delta * 1000).rounded() / 1000
            XCTAssertEqual(service.moveToCalledWith?.z, z)
            XCTAssertEqual(service.moveToCalledWith?.x, 40)
            XCTAssertEqual(service.moveToCalledWith?.y, 30)
            XCTAssertEqual(model.calibrationOffset, offset, "No draft update before matching telemetry")
            service.statusToReturn = VerifiedSafetyFixtures.status(id: model.printer.id, position: .init(x: 40, y: 30, z: z))
            await model.refreshSafetyEvidence()
            offset = try MaterialControlInput.adjustedOffset(offset, delta: delta)
            XCTAssertEqual(model.calibrationOffset, offset)
        }
        await model.reviewCalibration()
        XCTAssertEqual(model.calibrationStep, .save)
        await model.saveCalibration()
        XCTAssertEqual(service.saveZOffsetCalledWith?.reviewedRowVersion, "reviewed-revision")
        XCTAssertEqual(service.saveZOffsetCalledWith?.saveToFirmware, true)
        XCTAssertEqual(service.saveZOffsetCalledWith?.offsetMm, 0.12)
        XCTAssertEqual(model.calibrationStep, .done)
        await model.saveCalibration()
        XCTAssertEqual(service.saveZOffsetCallCount, 1)
        XCTAssertNil(service.setActiveSpoolCalledWith)
    }

    func test_calibrationLiftsVerticallyBeforeCenteringAndNeverCrossesClearance() async throws {
        let (model, service) = try await fixture()
        service.capabilitiesToReturn?.verifiedSafety?.positioning.minimumClearanceZMm.value = 12
        await model.refreshSafetyEvidence()
        await model.startCalibration()
        model.beginCalibrationHome()
        await model.homeForCalibration()
        service.statusToReturn = VerifiedSafetyFixtures.status(id: model.printer.id)
        await model.refreshSafetyEvidence()
        await model.positionForCalibration()
        XCTAssertEqual(service.moveToCalledWith?.x, 20)
        XCTAssertEqual(service.moveToCalledWith?.y, 30)
        XCTAssertEqual(service.moveToCalledWith?.z, 11, "Effective Z includes the verified 1 mm frame offset")
        service.statusToReturn = VerifiedSafetyFixtures.status(id: model.printer.id, position: .init(x: 20, y: 30, z: 11))
        await model.refreshSafetyEvidence()
        XCTAssertEqual(model.calibrationStep, .position)
        await model.positionForCalibration()
        service.statusToReturn = VerifiedSafetyFixtures.status(id: model.printer.id, position: .init(x: 40, y: 30, z: 11))
        await model.refreshSafetyEvidence()
        XCTAssertEqual(model.calibrationStep, .adjust)
        service.moveToCalledWith = nil
        await model.adjustCalibration(delta: -0.01)
        XCTAssertNil(service.moveToCalledWith)
        XCTAssertTrue(model.calibrationMessage?.contains("clearance") == true)
    }

    func test_positionRequiresVerifiedGeometryFreshHomingAndMatchingFrame() async throws {
        for variant in 0..<8 {
            let (model, service) = try await fixture()
            await model.startCalibration()
            model.beginCalibrationHome()
            await model.homeForCalibration()
            service.statusToReturn = VerifiedSafetyFixtures.status(id: model.printer.id)
            await model.refreshSafetyEvidence()
            switch variant {
            case 0: service.capabilitiesToReturn?.verifiedSafety?.positioning.travelEnvelopeMm.state = .unknown
            case 1: service.capabilitiesToReturn?.verifiedSafety?.positioning.minimumClearanceZMm.state = .unknown
            case 2: service.capabilitiesToReturn?.verifiedSafety?.positioning.travelEnvelopeMm.value?.minimum.x = 1000
            case 3: service.statusToReturn?.safetyTelemetry?.homedAxes.value = ["x", "y"]
            case 4: service.statusToReturn?.safetyTelemetry?.homedAxes.observedAtUtc = Date().addingTimeInterval(-20)
            case 5: service.statusToReturn?.safetyTelemetry?.coordinateOriginOffsetMm.value?.x = 99
            case 6: service.statusToReturn?.safetyTelemetry?.coordinateOriginOffsetMm.observedAtUtc = Date().addingTimeInterval(-20)
            default: service.capabilitiesToReturn?.verifiedSafety?.discovery.sourceRevision = "new-configuration"
            }
            await model.refreshSafetyEvidence()
            await model.positionForCalibration()
            XCTAssertNotNil(model.calibrationPositionBlockedReason, "\(variant)")
            XCTAssertNil(service.moveToCalledWith, "\(variant)")
            XCTAssertNotEqual(model.calibrationStep, .adjust)
        }
    }

    private func reachPositionWithGeometry(
        frame: SafetyVector3Dto,
        envelope: SafetyTravelEnvelopeDto,
        clearance: Double,
        position: SafetyVector3Dto
    ) async throws -> (PrinterControlsViewModel, MockPrinterService) {
        let (model, service) = try await fixture()
        service.capabilitiesToReturn?.verifiedSafety?.positioning.coordinateOriginMm.value = frame
        service.capabilitiesToReturn?.verifiedSafety?.positioning.travelEnvelopeMm.value = envelope
        service.capabilitiesToReturn?.verifiedSafety?.positioning.minimumClearanceZMm.value = clearance
        func status() -> PrinterStatusDetail {
            var value = VerifiedSafetyFixtures.status(id: model.printer.id, position: position)
            value.safetyTelemetry?.coordinateOriginOffsetMm.value = frame
            return value
        }
        service.statusToReturn = status()
        await model.refreshSafetyEvidence()
        await model.startCalibration()
        model.beginCalibrationHome()
        await model.homeForCalibration()
        service.statusToReturn = status()
        await model.refreshSafetyEvidence()
        XCTAssertEqual(model.calibrationStep, .position)
        return (model, service)
    }

    func test_derivedClearanceLiftsUseTransportPrecisionAndRoundOnlyUpward() async throws {
        let cases: [(clearance: Double, frameZ: Double, expected: Double)] = [
            (0.2, 0.05, 0.15), (0.3, 0.07, 0.23),
            (0.2004, 0.05, 0.151), (0.3, -0.07, 0.37),
            (-0.2004, 0.05, -0.25)
        ]
        let envelope = SafetyTravelEnvelopeDto(
            minimum: .init(x: -100, y: -50, z: -1), maximum: .init(x: 200, y: 150, z: 100)
        )
        for item in cases {
            let frame = SafetyVector3Dto(x: 10, y: 20, z: item.frameZ)
            let (model, service) = try await reachPositionWithGeometry(
                frame: frame, envelope: envelope, clearance: item.clearance,
                position: .init(x: 20, y: 30, z: -0.5)
            )
            await model.positionForCalibration()
            let request = try XCTUnwrap(service.moveToCalledWith)
            let z = try XCTUnwrap(request.z)
            XCTAssertEqual(z, item.expected)
            XCTAssertTrue(ControlNumberInput.hasCoordinatePrecision(z))
            XCTAssertGreaterThanOrEqual(z + frame.z, item.clearance)
            XCTAssertEqual(request.x, 20, "Lift preserves reported X; never rounds a lateral coordinate")
            XCTAssertEqual(request.y, 30)
            XCTAssertNil(model.lastError)
            XCTAssertEqual(model.calibrationStep, .position)

            var status = VerifiedSafetyFixtures.status(id: model.printer.id, position: .init(x: 20, y: 30, z: z))
            status.safetyTelemetry?.coordinateOriginOffsetMm.value = frame
            service.statusToReturn = status
            await model.refreshSafetyEvidence()
            XCTAssertEqual(model.calibrationStep, .position, "Lift acknowledgement is not centering")
            await model.positionForCalibration()
            XCTAssertEqual(service.moveToCalledWith?.x, 40)
            XCTAssertEqual(service.moveToCalledWith?.y, 30)
            XCTAssertEqual(service.moveToCalledWith?.z, z)
            status = VerifiedSafetyFixtures.status(id: model.printer.id, position: .init(x: 40, y: 30, z: z))
            status.safetyTelemetry?.coordinateOriginOffsetMm.value = frame
            service.statusToReturn = status
            await model.refreshSafetyEvidence()
            XCTAssertEqual(model.calibrationStep, .adjust, "Quantized targets correlate without widening equality")
        }
    }

    func test_derivedCentersQuantizeInsideAwkwardAndNarrowBoundsOrBlock() async throws {
        let cases: [(minimum: Double, maximum: Double, frame: Double, expected: Double?)] = [
            (-100.0003, 200.0004, 10.0002, 40),
            (20.0002, 20.0012, 10.00005, 10.001),
            (0.0002, 0.001, 0, 0.001),
            (-0.001, -0.0002, 0, -0.001),
            (0.0002, 0.0008, 0, nil)
        ]
        for item in cases {
            let frame = SafetyVector3Dto(x: item.frame, y: 20.00009, z: 0.07)
            let envelope = SafetyTravelEnvelopeDto(
                minimum: .init(x: item.minimum, y: -50.0004, z: 0),
                maximum: .init(x: item.maximum, y: 50.0006, z: 100)
            )
            let (model, service) = try await reachPositionWithGeometry(
                frame: frame, envelope: envelope, clearance: 0.3, position: .init(x: 20, y: 30, z: 1)
            )
            await model.positionForCalibration()
            if let expected = item.expected {
                let request = try XCTUnwrap(service.moveToCalledWith)
                let point = SafetyVector3Dto(
                    x: try XCTUnwrap(request.x), y: try XCTUnwrap(request.y), z: try XCTUnwrap(request.z)
                )
                XCTAssertEqual(point.x, expected)
                XCTAssertEqual(point.y, -20)
                XCTAssertEqual(point.z, 1, "Centering preserves reported Z")
                XCTAssertTrue([point.x, point.y, point.z].allSatisfy(ControlNumberInput.hasCoordinatePrecision))
                XCTAssertTrue(envelope.contains(.init(x: point.x + frame.x, y: point.y + frame.y, z: point.z + frame.z)))
                XCTAssertNil(model.lastError)
            } else {
                XCTAssertNil(service.moveToCalledWith)
                XCTAssertTrue(model.calibrationMessage?.contains("0.001 mm transport precision") == true)
            }
            XCTAssertEqual(model.calibrationStep, .position)
        }
    }

    func test_derivedLiftBlocksWhenNoTransportQuantumFitsAboveClearance() async throws {
        let (model, service) = try await reachPositionWithGeometry(
            frame: .init(x: 10, y: 20, z: 0.05),
            envelope: .init(minimum: .init(x: -100, y: -50, z: 0), maximum: .init(x: 200, y: 150, z: 0.2008)),
            clearance: 0.2004, position: .init(x: 20, y: 30, z: 0)
        )
        await model.positionForCalibration()
        XCTAssertNil(service.moveToCalledWith)
        XCTAssertEqual(model.calibrationStep, .position)
        XCTAssertTrue(model.calibrationMessage?.contains("clearance cannot be reached") == true)
    }

    func test_derivedQuantizationDoesNotRoundUserInputOrReportedLateralPosition() async throws {
        let (model, service) = try await fixture()
        XCTAssertThrowsError(try ControlNumberInput.coordinate("0.15000000000000002"))
        XCTAssertEqual(try ControlNumberInput.coordinate("0.150"), 0.15)
        await model.moveTo(x: 0.2 - 0.05, y: 0, z: 10, feedrateMmMin: nil)
        XCTAssertNil(service.moveToCalledWith)
        XCTAssertEqual(model.lastError?.message, ControlNumberInput.coordinatePrecisionMessage)

        let (calibration, calibrationService) = try await reachPositionWithGeometry(
            frame: .init(x: 10, y: 20, z: 0.05),
            envelope: .init(minimum: .init(x: -100, y: -50, z: 0), maximum: .init(x: 200, y: 150, z: 100)),
            clearance: 0.2, position: .init(x: 20.0001, y: 30, z: 0)
        )
        await calibration.positionForCalibration()
        XCTAssertNil(calibrationService.moveToCalledWith, "A vertical lift must not silently round reported X/Y")
        XCTAssertTrue(calibration.calibrationMessage?.contains("coordinate precision") == true)
    }

    func test_failedAndUncertainSaveConsumeReviewWithoutAutomaticRetry() async throws {
        for variant in 0..<5 {
            let (model, service) = try await fixture()
            try await reachAdjustment(model, service)
            await model.reviewCalibration()
            switch variant {
            case 0: service.errorToThrow = NetworkError.preconditionFailed(nil)
            case 1: service.errorToThrow = NetworkError.preconditionRequired(nil)
            case 2: service.errorToThrow = NetworkError.timeout
            case 3: service.errorToThrow = NetworkError.serverError(503)
            default: service.commandResultToReturn = .init(success: false, message: "Firmware did not prove save")
            }
            await model.saveCalibration()
            XCTAssertNotEqual(model.calibrationStep, .done)
            XCTAssertNil(model.calibrationReview)
            XCTAssertNotNil(model.lastError)
            await model.saveCalibration()
            XCTAssertEqual(service.saveZOffsetCallCount, 1)
            XCTAssertNil(model.commandNotice)
        }
    }

    func test_baselineAndReviewedRevisionChangesPreventSave() async throws {
        for baselineChanged in [true, false] {
            let (model, service) = try await fixture()
            try await reachAdjustment(model, service)
            if baselineChanged {
                service.detailsToReturn = PrinterDetails(
                    id: model.printer.id, name: model.printer.name, backend: model.printer.backend,
                    rowVersion: "changed", zOffsetMm: 1
                )
            }
            await model.reviewCalibration()
            if !baselineChanged { model.handlePrinterUpdate(model.printer) }
            await model.saveCalibration()
            XCTAssertNil(service.saveZOffsetCalledWith)
            XCTAssertNil(model.calibrationReview)
        }
    }

    func test_duplicateSaveAndCancelOrServerSwitchFenceLateWrites() async throws {
        for deactivate in [true, false] {
            let (model, service) = try await fixture()
            try await reachAdjustment(model, service)
            await model.reviewCalibration()
            let barrier = AsyncBarrier()
            addTeardownBlock { barrier.close() }
            service.beforeSaveZOffset = { await barrier.arriveAndWait() }
            let save = Task { await model.saveCalibration() }
            await barrier.waitUntilArrived()
            await model.saveCalibration()
            if deactivate { model.deactivate() } else { model.cancelCalibration() }
            await model.performFilament(.load)
            XCTAssertTrue(model.isExecuting)
            XCTAssertEqual(service.saveZOffsetCallCount, 1)
            XCTAssertTrue(service.physicalFilamentCalls.isEmpty)
            barrier.release()
            await save.value
            XCTAssertNil(model.calibrationStep)
            XCTAssertNil(model.calibrationReview)
            XCTAssertNil(model.pendingCommand)
            XCTAssertFalse(model.commandNotice?.contains("Firmware save request accepted") == true)
        }
    }

    func test_lateSafetyReadCannotRestoreEvidenceAfterDeactivation() async throws {
        let (model, service) = try await fixture()
        let barrier = AsyncBarrier()
        addTeardownBlock { barrier.close() }
        service.beforeSafetyStatus = { await barrier.arriveAndWait() }
        let read = Task { await model.refreshSafetyEvidence() }
        await barrier.waitUntilArrived()
        model.deactivate()
        barrier.release()
        await read.value
        XCTAssertNil(model.safetyStatus)
        XCTAssertNil(model.capabilities?.verifiedSafety)
        XCTAssertNil(model.safetyCheckedAt)
    }

    func test_typedSafetyWireDecodingPreservesVersionTimestampsAndUnknownEnums() throws {
        let encoder = JSONEncoder()
        encoder.dateEncodingStrategy = .iso8601
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601
        let fixture = VerifiedSafetyFixtures.discovery(at: Date(timeIntervalSince1970: 1_800_000_000))
        let json = try XCTUnwrap(String(data: encoder.encode(fixture), encoding: .utf8))
        let decoded = try decoder.decode(PrinterVerifiedSafetyDto.self, from: Data(json.utf8))
        XCTAssertEqual(decoded, fixture)
        XCTAssertEqual(decoded.positioning.coordinateOriginMm.value?.x, 10)
        let futureEnum = json.replacingOccurrences(of: "\"Supported\"", with: "\"FutureSupport\"")
        let unknown = try decoder.decode(PrinterVerifiedSafetyDto.self, from: Data(futureEnum.utf8))
        XCTAssertEqual(unknown.operations.filamentLoad.support, .unknown)
        var status = VerifiedSafetyFixtures.status(id: UUID(), at: Date(timeIntervalSince1970: 1_800_000_000))
        let roundTrip = try decoder.decode(PrinterStatusDetail.self, from: encoder.encode(status))
        XCTAssertEqual(roundTrip.safetyTelemetry, status.safetyTelemetry)
        status.safetyTelemetry = nil
        XCTAssertNil(try decoder.decode(PrinterStatusDetail.self, from: encoder.encode(status)).safetyTelemetry)
    }

    func test_dispatchRechecksTemperatureAgeAfterButtonAvailability() async throws {
        let (original, service) = try await fixture()
        let clock = SafetyTestClock()
        service.statusToReturn = VerifiedSafetyFixtures.status(id: original.printer.id, at: clock.now())
        let model = PrinterControlsViewModel.configuredForTests(
            printerService: service, printer: original.printer, clock: { clock.now() }
        )
        await model.loadCapabilities()
        XCTAssertNil(model.extrusionBlockedReason)
        clock.advance(16)
        XCTAssertNotNil(model.extrusionBlockedReason)
        await model.extrude(distanceMm: 10, speedMmPerSecond: 1)
        await model.performFilament(.load)
        XCTAssertNil(service.extrudeCalledWith)
        XCTAssertNil(service.loadFilamentCalledWith)
        await model.refreshSafetyEvidence()
        XCTAssertNotNil(model.extrusionBlockedReason, "A repeated response must not renew its old sample timestamp")
    }

    func test_calibrationWorkflowLeaseBlocksOtherOwnersBetweenPhysicalRequests() async throws {
        let (model, service) = try await fixture()
        let other = PrinterControlsViewModel.configuredForTests(
            printerService: service, printer: model.printer, serverID: try XCTUnwrap(model.registeredServerID)
        )
        await other.loadCapabilities()
        await model.startCalibration()
        XCTAssertFalse(model.isExecuting, "Its own idle workflow does not disable Next")
        XCTAssertTrue(other.isExecuting)
        await other.performFilament(.load)
        await other.homeAll()
        XCTAssertTrue(service.physicalFilamentCalls.isEmpty)
        XCTAssertNil(service.homeCalledWith)
        model.cancelCalibration()
        await other.performFilament(.load)
        XCTAssertEqual(service.loadFilamentCalledWith, model.printer.id)
    }

    func test_measuredColdHotUnknownAndCachedValuesNeverReplaceMissingSafetyEvidence() async throws {
        let (model, service) = try await fixture()
        service.capabilitiesToReturn?.verifiedSafety = nil
        await model.refreshSafetyEvidence()
        for measured: Double? in [nil, 20, 180, 240, .nan, .infinity] {
            var update = model.printer
            update.hotendTemp = measured
            update.hotendTarget = 280
            // Existing spoolInfo is deliberately retained: assignment is not proof.
            model.handlePrinterUpdate(update)
            XCTAssertNotNil(model.extrusionBlockedReason)
            await model.extrude(distanceMm: 10, speedMmPerSecond: 5)
            XCTAssertNil(service.extrudeCalledWith)
            XCTAssertNotNil(model.lastError)
            await model.extrude(distanceMm: -100, speedMmPerSecond: 10)
            XCTAssertNil(service.extrudeCalledWith)
            XCTAssertNil(model.pendingCommand)
        }
        XCTAssertNil(service.setActiveSpoolCalledWith)
        XCTAssertTrue(service.bindToolheadSpoolCalls.isEmpty)
    }

    func test_eachPhysicalOperationUsesItsTypedResultAndNeverChangesAssignment() async throws {
        let (model, service) = try await fixture()
        service.commandResultToReturn = .init(success: true, message: "Follow printer prompts")
        service.unloadResultToReturn = .init(
            success: true, message: "Check outgoing material", spoolId: 99, material: "PLA", residualWeightG: 500
        )
        for operation in PhysicalFilamentOperation.allCases {
            await model.performFilament(operation)
            XCTAssertNil(model.lastError)
            XCTAssertNil(model.pendingCommand, "No nonexistent physical-loaded telemetry is awaited")
            XCTAssertTrue(model.commandNotice?.contains("request accepted") == true)
            XCTAssertTrue(model.commandNotice?.contains("verify physical completion") == true)
        }
        XCTAssertEqual(service.physicalFilamentCalls, ["load", "unload", "change"])
        XCTAssertEqual(service.loadFilamentCalledWith, model.printer.id)
        XCTAssertEqual(service.unloadFilamentCalledWith, model.printer.id)
        XCTAssertEqual(service.changeFilamentCalledWith, model.printer.id)
        XCTAssertNil(service.unloadFilamentToolheadIndex)
        XCTAssertNil(service.setActiveSpoolCalledWith)
        XCTAssertTrue(service.bindToolheadSpoolCalls.isEmpty)
    }

    func test_unloadFailureIsNotMaskedByCommandResultOrResidualInventory() async throws {
        let (model, service) = try await fixture()
        service.commandResultToReturn = .init(success: true, message: nil)
        service.unloadResultToReturn = .init(
            success: false, message: "Firmware outcome unknown", spoolId: 99, material: "PLA", residualWeightG: 500
        )
        await model.performFilament(.unload)
        XCTAssertEqual(model.lastError?.message, "Firmware outcome unknown")
        XCTAssertNil(model.commandNotice)
        XCTAssertNil(model.pendingCommand)
        XCTAssertNil(service.setActiveSpoolCalledWith)
    }

    func test_rejectedLoadAndChangeAreNotSuccessfulSteps() async throws {
        for operation in [PhysicalFilamentOperation.load, .change] {
            let (model, service) = try await fixture()
            service.commandResultToReturn = .init(success: false, message: "Macro unavailable")
            await model.performFilament(operation)
            XCTAssertEqual(model.lastError?.message, "Macro unavailable")
            XCTAssertNil(model.commandNotice)
            XCTAssertNil(model.pendingCommand)
        }
    }

    func test_individualCapabilityIsRequiredForEveryOperation() async throws {
        var caps = PrinterBackendCapabilities.allControlsFixture
        caps.supportsFilamentUnload = true
        caps.verifiedSafety = VerifiedSafetyFixtures.discovery()
        let (model, service) = try await fixture(caps: caps)
        for operation in [PhysicalFilamentOperation.load, .change] {
            XCTAssertNotNil(model.filamentBlockedReason(operation))
            await model.performFilament(operation)
        }
        XCTAssertTrue(service.physicalFilamentCalls.isEmpty)
        await model.performFilament(.unload)
        XCTAssertEqual(service.physicalFilamentCalls, ["unload"])
    }

    func test_offlineStartingPrintingPausedAndAccessGatesRejectMaterialAndCalibration() async throws {
        for state in ["starting", "printing", "paused", "offline"] {
            let (model, service) = try await fixture()
            var update = model.printer
            update.state = state
            update.isOnline = state != "offline"
            model.handlePrinterUpdate(update)
            for operation in PhysicalFilamentOperation.allCases { await model.performFilament(operation) }
            await model.startCalibration()
            await model.extrude(distanceMm: 10, speedMmPerSecond: 1)
            XCTAssertTrue(service.physicalFilamentCalls.isEmpty)
            XCTAssertNil(service.extrudeCalledWith)
            XCTAssertNil(model.calibrationStep)
        }
        for reason in ["Queue.Start permission required.", "Advanced controls disabled.", "Server changed."] {
            let (model, service) = try await fixture(access: { reason })
            await model.performFilament(.load)
            await model.startCalibration()
            XCTAssertTrue(service.physicalFilamentCalls.isEmpty)
            XCTAssertNil(model.calibrationStep)
        }
    }

    func test_duplicateTapsAndUnrelatedTelemetryCannotReleasePhysicalRequest() async throws {
        let (model, service) = try await fixture()
        let barrier = AsyncBarrier()
        addTeardownBlock { barrier.close() }
        service.beforeUnloadFilament = { await barrier.arriveAndWait() }
        let first = Task { await model.performFilament(.unload) }
        await barrier.waitUntilArrived()
        var update = model.printer
        update.hotendTemp = 240
        update.z = 12
        model.handlePrinterUpdate(update)
        await model.performFilament(.load)
        await model.performFilament(.change)
        await model.startCalibration()
        XCTAssertEqual(service.physicalFilamentCalls, ["unload"])
        XCTAssertNotNil(model.pendingCommand)
        XCTAssertNil(model.calibrationStep)
        // Emergency is deliberately outside routine request ownership.
        _ = try await service.emergencyStop(id: model.printer.id)
        XCTAssertEqual(service.emergencyStopCalledWith, model.printer.id)
        barrier.release()
        await first.value
        XCTAssertNil(model.pendingCommand)
    }

    func test_cancelAndServerEpochChangeNeverPublishLatePhysicalSuccess() async throws {
        for deactivate in [false, true] {
            let (model, service) = try await fixture()
            let barrier = AsyncBarrier()
            addTeardownBlock { barrier.close() }
            service.beforeUnloadFilament = { await barrier.arriveAndWait() }
            let first = Task { await model.performFilament(.unload) }
            await barrier.waitUntilArrived()
            if deactivate { model.deactivate() } else { model.cancelPendingCommand() }
            await model.performFilament(.load)
            XCTAssertEqual(service.physicalFilamentCalls, ["unload"])
            XCTAssertTrue(model.isExecuting)
            barrier.release()
            await first.value
            XCTAssertNil(model.pendingCommand)
            XCTAssertFalse(model.commandNotice?.contains("Spool assignment was not changed") == true)
            XCTAssertTrue(model.commandNotice?.lowercased().contains("unknown") == true)
        }
    }

    func test_offsetSignIncrementsAndSaveBounds() throws {
        for increment in MaterialControlInput.increments {
            XCTAssertEqual(try MaterialControlInput.adjustedOffset(0, delta: -increment), -increment)
            XCTAssertEqual(try MaterialControlInput.adjustedOffset(0, delta: increment), increment)
        }
        XCTAssertEqual(try MaterialControlInput.adjustedOffset(4.9, delta: 0.1), 5)
        XCTAssertEqual(try MaterialControlInput.adjustedOffset(-4.9, delta: -0.1), -5)
        XCTAssertEqual(try MaterialControlInput.adjustedOffset(0.123, delta: 0.01), 0.133)
        XCTAssertThrowsError(try MaterialControlInput.adjustedOffset(5, delta: 0.01))
        XCTAssertThrowsError(try MaterialControlInput.adjustedOffset(-5, delta: -0.01))
        XCTAssertThrowsError(try MaterialControlInput.adjustedOffset(.nan, delta: 0.01))
        XCTAssertThrowsError(try MaterialControlInput.adjustedOffset(0, delta: .infinity))
        XCTAssertThrowsError(try MaterialControlInput.adjustedOffset(0, delta: 0.2))
    }

    func test_databaseOnlySupportCannotStartPhysicalCalibration() async throws {
        var caps = PrinterBackendCapabilities.allControlsFixture
        caps.supportsZOffset = true
        let (model, service) = try await fixture(caps: caps)
        await model.startCalibration()
        XCTAssertEqual(model.calibrationStep, .introduction)
        XCTAssertTrue(model.calibrationBlockedReason?.contains("Database-only") == true)
        model.beginCalibrationHome()
        await model.homeForCalibration()
        await model.positionForCalibration()
        await model.adjustCalibration(delta: -0.01)
        await model.reviewCalibration()
        await model.saveCalibration()
        XCTAssertEqual(model.calibrationStep, .introduction)
        XCTAssertNil(service.homeCalledWith)
        XCTAssertNil(service.moveToCalledWith)
        XCTAssertNil(service.saveZOffsetCalledWith)
    }

    func test_missingOffsetDoesNotInventZero() async throws {
        let (model, service) = try await fixture()
        service.detailsToReturn = .controlsLimitsFixture(for: model.printer)
        await model.startCalibration()
        model.beginCalibrationHome()
        XCTAssertNil(model.calibrationOffset)
        XCTAssertEqual(model.calibrationStep, .introduction)
        XCTAssertTrue(model.calibrationMessage?.contains("No zero baseline") == true)
    }

    func test_homeRequiresRealTelemetryAndUnknownGeometryBlocksAllFurtherPhysicalSteps() async throws {
        let (model, service) = try await fixture()
        await model.startCalibration()
        model.beginCalibrationHome()
        await model.homeForCalibration()
        XCTAssertEqual(model.calibrationStep, .home)
        var update = model.printer
        update.hotendTemp = 220
        update.z = 0
        model.handlePrinterUpdate(update)
        XCTAssertEqual(model.calibrationStep, .home)
        update.homedAxes = "xyz"
        model.handlePrinterUpdate(update)
        XCTAssertEqual(model.calibrationStep, .home, "Legacy homedAxes cannot confirm timestamped homing")
        service.statusToReturn = VerifiedSafetyFixtures.status(id: model.printer.id)
        await model.refreshSafetyEvidence()
        XCTAssertEqual(model.calibrationStep, .position)
        XCTAssertNil(model.pendingCommand)
        service.capabilitiesToReturn?.verifiedSafety?.positioning.coordinateOriginMm.state = .unknown
        await model.refreshSafetyEvidence()
        XCTAssertNotNil(model.calibrationPositionBlockedReason)
        XCTAssertEqual(model.hardware?.maxBuildVolumeX, 256, "Even catalog dimensions do not prove an origin")
        await model.positionForCalibration()
        await model.adjustCalibration(delta: -0.01)
        await model.reviewCalibration()
        await model.saveCalibration()
        XCTAssertEqual(model.calibrationStep, .position)
        XCTAssertEqual(model.calibrationOffset, 0.12)
        XCTAssertNil(service.moveToCalledWith)
        XCTAssertNil(service.saveZOffsetCalledWith)
        model.cancelCalibration()
        XCTAssertNil(model.calibrationStep)
    }

    func test_cachedHomingOrCancellationCannotCompleteCalibrationHome() async throws {
        let (model, _) = try await fixture()
        var update = model.printer
        update.homedAxes = "xyz"
        model.handlePrinterUpdate(update)
        await model.startCalibration()
        model.beginCalibrationHome()
        await model.homeForCalibration()
        model.handlePrinterUpdate(update)
        XCTAssertEqual(model.calibrationStep, .home)
        XCTAssertNotNil(model.pendingCommand)
        model.cancelCalibration()
        model.handlePrinterUpdate(update)
        XCTAssertNil(model.calibrationStep)
        XCTAssertNil(model.calibrationOffset)
    }

    func test_homeRejectionAndLostAccessDoNotAdvanceCalibration() async throws {
        let (model, service) = try await fixture()
        await model.startCalibration()
        model.beginCalibrationHome()
        service.errorToThrow = PrinterControlError.rejected("Homing rejected")
        await model.homeForCalibration()
        XCTAssertEqual(model.lastError?.message, "Homing rejected")
        var update = model.printer
        update.homedAxes = "xyz"
        model.handlePrinterUpdate(update)
        XCTAssertEqual(model.calibrationStep, .home)
        model.deactivate()
        model.handlePrinterUpdate(update)
        XCTAssertNil(model.calibrationStep)
        XCTAssertNil(service.saveZOffsetCalledWith)
    }

    func test_canceledCalibrationReadCannotPopulateNewFlowOrAnotherEpoch() async throws {
        let (_, service) = try await fixture()
        let barrier = AsyncBarrier()
        let count = HookCounter()
        addTeardownBlock { barrier.close() }
        let delayed = ControlsDelayedService(base: service, beforeDetails: {
            if await count.next() > 1 { await barrier.arriveAndWait() }
        })
        var printer = try idlePrinter()
        printer.state = "ready"
        let model = PrinterControlsViewModel.configuredForTests(printerService: delayed, printer: printer)
        await model.loadCapabilities()
        let read = Task { await model.startCalibration() }
        await barrier.waitUntilArrived()
        model.cancelCalibration()
        model.deactivate()
        barrier.release()
        await read.value
        XCTAssertNil(model.calibrationStep)
        XCTAssertNil(model.calibrationOffset)
        XCTAssertNil(model.calibrationReview)
        XCTAssertFalse(model.isReviewingCalibration)
    }

    func test_inFlightHomeTelemetryCannotOutrunRejectedResponseOrAllowRoutineCommands() async throws {
        let (_, service) = try await fixture()
        let barrier = AsyncBarrier()
        addTeardownBlock { barrier.close() }
        let delayed = ControlsDelayedService(base: service, beforeHome: { await barrier.arriveAndWait() })
        var printer = try idlePrinter()
        printer.state = "ready"
        printer.homedAxes = nil
        let model = PrinterControlsViewModel.configuredForTests(printerService: delayed, printer: printer)
        await model.loadCapabilities()
        await model.startCalibration()
        model.beginCalibrationHome()
        let home = Task { await model.homeForCalibration() }
        await barrier.waitUntilArrived()
        printer.homedAxes = "xyz"
        model.handlePrinterUpdate(printer)
        XCTAssertEqual(model.calibrationStep, .home)
        service.errorToThrow = PrinterControlError.rejected("Homing uncertain")
        barrier.release()
        await home.value
        XCTAssertEqual(model.calibrationStep, .home)
        XCTAssertNotNil(model.lastError)
        service.errorToThrow = nil
        await model.setHeaterTarget(.hotend, target: 200)
        XCTAssertNil(service.setTemperaturesCalledWith)
        XCTAssertTrue(model.commandNotice?.contains("Cancel calibration") == true)
        model.cancelCalibration()
        await model.setHeaterTarget(.hotend, target: 200)
        XCTAssertNotNil(service.setTemperaturesCalledWith)
        model.cancelPendingCommand()
    }
}

private actor AsyncGate {
    private var waiters: [CheckedContinuation<Void, Never>] = []
    private var opened = false

    var hasWaiters: Bool { !waiters.isEmpty || opened }

    func wait() async {
        if opened { return }
        await withCheckedContinuation { c in waiters.append(c) }
    }

    func open() {
        opened = true
        let toResume = waiters
        waiters.removeAll()
        for c in toResume { c.resume() }
    }
}

/// Serialized invocation counter used to gate only the FIRST mock hook entry
/// so a regressed single-flight cannot deadlock on a closed gate.
private actor HookCounter {
    private var n = 0
    func next() -> Int { n += 1; return n }
}

extension PrinterControlOperation {
    static func controlsFixture(
        printerID: UUID, operationID: UUID = UUID(),
        request: PrinterControlOperationRequest = .init(kind: .homeAll),
        state: PrinterControlOperationState = .running,
        evidence: PrinterControlCompletionEvidence = .none,
        held: Bool = true, recovery: Bool = false, version: String = "opaque-version"
    ) -> Self {
        .init(
            operationId: operationID, printerId: printerID, kind: request.kind,
            x: request.x, y: request.y, z: request.z, f: request.f,
            state: state, rowVersion: version,
            createdAtUtc: Date(timeIntervalSince1970: 1), updatedAtUtc: Date(timeIntervalSince1970: 2),
            barrierHeld: held, requiresRecovery: recovery, completionEvidence: evidence,
            senderIsolation: .notRequested
        )
    }
}

extension PrinterCurrentControlOperation {
    static func controlsFixture(_ operation: PrinterControlOperation) -> Self {
        guard operation.barrierHeld else {
            return .init(physicalControl: .init(
                supportedOperations: PrinterControlOperationKind.allCases,
                barrierHeld: false, requiresRecovery: false
            ), operation: nil)
        }
        return .init(physicalControl: .init(
            supportedOperations: PrinterControlOperationKind.allCases,
            barrierHeld: operation.barrierHeld, operationId: operation.operationId,
            state: operation.state, requiresRecovery: operation.requiresRecovery
        ), operation: operation)
    }
}

@MainActor
final class DurablePrinterMotionControlsTests: XCTestCase {
    @MainActor
    private final class AuthEpoch {
        var value = 0
    }

    private var defaults: UserDefaults!
    private var suite: String!
    private let serverID = UUID()
    private let userID = UUID()

    override func setUp() async throws {
        suite = "DurablePrinterMotion-\(UUID())"
        defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
    }

    override func tearDown() async throws {
        defaults.removePersistentDomain(forName: suite)
        defaults = nil
    }

    private func fixture(
        service: MockPrinterService = MockPrinterService(),
        server: UUID? = nil, user: UUID? = nil,
        clock: @escaping @Sendable () -> Date = Date.init,
        access: @escaping @MainActor () -> String? = { nil }
    ) async throws -> (PrinterControlsViewModel, MockPrinterService) {
        var printer = try TestData.decodePrinter()
        printer.state = "ready"
        var caps = PrinterBackendCapabilities.allControlsFixture
        caps.supportsAbsoluteMovement = true
        caps.supportsZOffset = true
        caps.supportsZOffsetFirmwareSave = true
        caps.verifiedSafety = VerifiedSafetyFixtures.discovery()
        service.capabilitiesToReturn = caps
        service.statusToReturn = VerifiedSafetyFixtures.status(id: printer.id)
        service.detailsToReturn = PrinterDetails(
            id: printer.id, name: printer.name, backend: printer.backend,
            rowVersion: "calibration-review", zOffsetMm: 0.12
        )
        let identity = server ?? serverID
        let composition = PrinterControlsComposition(
            identity: .init(serverID: identity, generation: 0, revision: 0), printerService: service
        )
        let model = PrinterControlsViewModel(
            composition: composition, printer: printer, clock: clock, motionDefaults: defaults
        )
        model.configureAccess(
            serverID: identity,
            userID: user ?? userID,
            serverURL: URL(string: "https://printfarmer.test")!,
            access
        )
        await model.loadCapabilities()
        return (model, service)
    }

    private func admitRunning(_ service: MockPrinterService) {
        service.submitControlOperationHandler = { [service] printerID, operationID, request in
            let operation = PrinterControlOperation.controlsFixture(
                printerID: printerID, operationID: operationID, request: request
            )
            service.controlOperationToReturn = operation
            service.currentControlOperationToReturn = .controlsFixture(operation)
            return operation
        }
    }

    private func finish(
        _ model: PrinterControlsViewModel, _ service: MockPrinterService,
        state: PrinterControlOperationState = .succeeded,
        evidence: PrinterControlCompletionEvidence = .motionQueueDrained
    ) async throws {
        let sent = try XCTUnwrap(service.submittedControlOperations.last)
        let operation = PrinterControlOperation.controlsFixture(
            printerID: sent.printerID, operationID: sent.operationID, request: sent.request,
            state: state, evidence: evidence, held: false
        )
        service.controlOperationToReturn = operation
        service.currentControlOperationToReturn = .controlsFixture(operation)
        await model.refreshControlOperation()
    }

    func test_acceptedHomeRemainsPendingPast27SecondsAndTelemetryUntilLateServerSuccess() async throws {
        let clock = SafetyTestClock()
        let (model, service) = try await fixture(clock: clock.now)
        admitRunning(service)
        await model.homeAll()
        let operationID = try XCTUnwrap(model.motionOperationID)
        XCTAssertEqual(service.submittedControlOperations.count, 1)
        XCTAssertNotNil(model.pendingCommand)
        clock.advance(27.110)
        var telemetry = model.printer
        telemetry.homedAxes = "xyz"
        telemetry.x = 0
        telemetry.y = 0
        telemetry.z = 0
        model.handlePrinterUpdate(telemetry)
        await model.refreshControlOperation()
        await model.homeXY()
        await model.homeZ()
        await model.jog(axis: "X", distanceMm: 10)
        XCTAssertEqual(service.submittedControlOperations.count, 1)
        XCTAssertEqual(model.motionOperationID, operationID)
        XCTAssertTrue(model.hasUnresolvedMotion)
        XCTAssertNil(service.homeCalledWith)
        XCTAssertNil(service.moveCalledWith)
        try await finish(model, service)
        XCTAssertFalse(model.hasUnresolvedMotion)
        XCTAssertNil(model.pendingCommand)
        XCTAssertFalse(model.isExecuting)
    }

    func test_allHomeJogAndAbsoluteEntryPointsUseDurableIntentWithoutLegacyFallback() async throws {
        let (model, service) = try await fixture()
        admitRunning(service)
        await model.homeAll()
        try await finish(model, service)
        await model.homeXY()
        try await finish(model, service)
        await model.homeZ()
        try await finish(model, service)
        await model.jog(axis: "X", distanceMm: -1.001)
        try await finish(model, service)
        await model.moveTo(x: 0, y: 30, z: 10, feedrateMmMin: nil)
        XCTAssertEqual(service.submittedControlOperations.map(\.request.kind), [.homeAll, .homeXY, .homeZ, .jog, .moveTo])
        XCTAssertEqual(service.submittedControlOperations[3].request, .init(kind: .jog, x: -1.001, f: 3000))
        XCTAssertEqual(service.submittedControlOperations[4].request, .init(kind: .moveTo, x: 0, y: 30, z: 10, f: 600))
        XCTAssertNil(service.homeCalledWith)
        XCTAssertNil(service.homeXYCalledWith)
        XCTAssertNil(service.homeZCalledWith)
        XCTAssertNil(service.moveCalledWith)
        XCTAssertNil(service.moveToCalledWith)
        try await finish(model, service)
    }

    func test_durableMoveToRequiresCompleteXYZWithoutGuessingMissingAxes() async throws {
        let points: [(Double?, Double?, Double?)] = [
            (nil, 30, 10), (20, nil, 10), (20, 30, nil), (nil, nil, nil)
        ]
        for (x, y, z) in points {
            let (model, service) = try await fixture(server: UUID())
            XCTAssertEqual(model.absoluteMoveBlockedReason(x: x, y: y, z: z),
                           ControlNumberInput.durableAbsoluteCoordinatesMessage)
            await model.moveTo(x: x, y: y, z: z, feedrateMmMin: nil)
            XCTAssertEqual(model.lastError?.message, ControlNumberInput.durableAbsoluteCoordinatesMessage)
            XCTAssertTrue(service.submittedControlOperations.isEmpty)
            XCTAssertNil(service.moveToCalledWith)
            XCTAssertNil(model.motionOperationID)
            model.deactivate()
        }
    }

    func test_savedPartialMoveToCannotBeResubmittedOrSilentlyBackfilled() async throws {
        let printer = try TestData.decodePrinter()
        let operationID = UUID()
        let key = "printer-motion.v1.\(serverID.uuidString).\(userID.uuidString).\(printer.id.uuidString)"
        let data = Data("""
        {"operationID":"\(operationID.uuidString)","request":{"kind":"MoveTo","x":40,"f":600}}
        """.utf8)
        defaults.set(data, forKey: key)
        let (model, service) = try await fixture()
        XCTAssertNil(model.motionAdmissionResubmissionID)
        XCTAssertTrue(model.motionBlockedReason?.contains("incomplete XYZ") == true)
        await model.resubmitUnconfirmedMotionAdmission(operationID: operationID)
        XCTAssertTrue(service.submittedControlOperations.isEmpty)
        XCTAssertNil(service.moveToCalledWith)
        XCTAssertTrue(model.hasUnresolvedMotion)
        XCTAssertTrue(model.operationReadError?.contains("could not be verified") == true)
        XCTAssertEqual(defaults.data(forKey: key), data)
        XCTAssertEqual(model.motionOperationID, operationID)
    }

    func test_responseLossPersistsIdentityAcrossNavigationAndOnlyReadsOnReopen() async throws {
        let (original, service) = try await fixture()
        service.submitControlOperationHandler = { _, _, _ in throw NetworkError.timeout }
        await original.homeZ()
        let sent = try XCTUnwrap(service.submittedControlOperations.first)
        XCTAssertTrue(original.hasUnresolvedMotion)
        original.cancelPendingCommand()
        original.deactivate()
        let replacementService = MockPrinterService()
        replacementService.controlOperationToReturn = .controlsFixture(
            printerID: sent.printerID, operationID: sent.operationID, request: sent.request,
            state: .unknown, recovery: true
        )
        let (replacement, _) = try await fixture(service: replacementService)
        XCTAssertEqual(replacement.motionOperationID, sent.operationID)
        XCTAssertTrue(replacement.hasUnresolvedMotion)
        XCTAssertTrue(replacement.motionBlockedReason?.contains("queue:reconcile permission and printer Submit access") == true)
        XCTAssertNotNil(replacement.motionRecoveryURL)
        XCTAssertTrue(replacementService.controlOperationReadIDs.contains(sent.operationID))
        await replacement.homeAll()
        XCTAssertTrue(replacementService.submittedControlOperations.isEmpty)
        replacementService.controlOperationToReturn = .controlsFixture(
            printerID: sent.printerID, operationID: sent.operationID, request: sent.request,
            state: .recovered, evidence: .operatorVerifiedRecovery, held: false
        )
        await replacement.refreshControlOperation()
        XCTAssertFalse(replacement.isExecuting, "Authoritative recovery also releases an old in-process owner's matching lease")
        XCTAssertTrue(replacement.motionStatusMessage?.contains("did not succeed") == true)
        XCTAssertTrue(replacement.motionStatusMessage?.contains("authorized operator") == true)
        XCTAssertNil(replacement.lastError)
    }

    func test_callerCancellationDoesNotCancelAdmittedSubmissionOrReplayIt() async throws {
        let (model, service) = try await fixture()
        let barrier = AsyncBarrier()
        addTeardownBlock { barrier.close() }
        service.submitControlOperationHandler = { [service] printerID, operationID, request in
            await barrier.arriveAndWait()
            XCTAssertFalse(Task.isCancelled)
            let operation = PrinterControlOperation.controlsFixture(
                printerID: printerID, operationID: operationID, request: request
            )
            service.currentControlOperationToReturn = .controlsFixture(operation)
            return operation
        }
        let task = Task { await model.homeAll() }
        await barrier.waitUntilArrived()
        let id = try XCTUnwrap(model.motionOperationID)
        task.cancel()
        model.cancelPendingCommand()
        await model.homeAll()
        XCTAssertEqual(service.submittedControlOperations.count, 1)
        barrier.close()
        await task.value
        XCTAssertEqual(model.motionOperationID, id)
        XCTAssertTrue(model.hasUnresolvedMotion)
        try await finish(model, service)
        XCTAssertFalse(model.isExecuting)
    }

    func test_serverAndAccountScopesNeverRestoreOrPublishAnotherIdentity() async throws {
        let (old, service) = try await fixture()
        service.submitControlOperationHandler = { _, _, _ in throw NetworkError.timeout }
        await old.homeAll()
        let oldID = old.motionOperationID
        let (otherServer, _) = try await fixture(server: UUID())
        let (otherUser, _) = try await fixture(user: UUID())
        XCTAssertNil(otherServer.motionOperationID)
        XCTAssertNil(otherUser.motionOperationID)
        XCTAssertFalse(otherServer.isExecuting)
        XCTAssertFalse(otherUser.isExecuting)
        let barrier = AsyncBarrier()
        addTeardownBlock { barrier.close() }
        let sent = try XCTUnwrap(service.submittedControlOperations.first)
        service.currentControlOperationHandler = { printerID in
            await barrier.arriveAndWait()
            return .controlsFixture(.controlsFixture(
                printerID: printerID, operationID: sent.operationID, request: sent.request,
                state: .succeeded, evidence: .motionQueueDrained, held: false
            ))
        }
        let read = Task { await old.refreshControlOperation() }
        await barrier.waitUntilArrived()
        old.deactivate()
        barrier.close()
        await read.value
        XCTAssertEqual(old.motionOperationID, oldID)
        XCTAssertTrue(old.hasUnresolvedMotion, "Old-generation reads cannot clear a persisted operation")
        XCTAssertNil(otherServer.controlOperation)
        XCTAssertNil(otherUser.controlOperation)
    }

    func test_outOfOrderReadsAndMalformedMissingTelemetryCannotReleaseKnownLock() async throws {
        let (model, service) = try await fixture()
        admitRunning(service)
        await model.homeAll()
        let running = service.currentControlOperationToReturn
        let barrier = AsyncBarrier()
        addTeardownBlock { barrier.close() }
        service.currentControlOperationHandler = { _ in
            await barrier.arriveAndWait()
            return running
        }
        let staleRead = Task { await model.refreshControlOperation() }
        await barrier.waitUntilArrived()
        service.currentControlOperationHandler = nil
        try await finish(model, service)
        barrier.close()
        await staleRead.value
        XCTAssertEqual(model.controlOperation?.state, .succeeded)
        XCTAssertFalse(model.hasUnresolvedMotion)
        await model.homeXY()
        let pendingID = model.motionOperationID
        service.controlOperationError = NetworkError.invalidResponse
        service.currentControlOperationHandler = { _ in throw NetworkError.invalidResponse }
        var telemetry = model.printer
        telemetry.physicalControl = nil
        telemetry.homedAxes = "xyz"
        telemetry.isOnline = false
        model.handlePrinterUpdate(telemetry)
        await model.refreshControlOperation()
        XCTAssertEqual(model.motionOperationID, pendingID)
        XCTAssertTrue(model.hasUnresolvedMotion)
        XCTAssertNotNil(model.operationReadError)
        XCTAssertNil(model.lastError, "A read failure is not physical failure")
    }

    func test_unknownAndRecoveredNeverAdvanceCalibrationButSucceededUsesFreshSafety() async throws {
        for terminal in [PrinterControlOperationState.recovered, .succeeded] {
            let (model, service) = try await fixture(server: UUID())
            admitRunning(service)
            await model.startCalibration()
            model.beginCalibrationHome()
            await model.homeForCalibration()
            XCTAssertEqual(model.calibrationStep, .home, "202 is not completion")
            service.statusToReturn = VerifiedSafetyFixtures.status(id: model.printer.id)
            await model.refreshSafetyEvidence()
            XCTAssertEqual(model.calibrationStep, .home, "Fresh homed axes alone cannot advance durable calibration")
            let sent = try XCTUnwrap(service.submittedControlOperations.last)
            service.currentControlOperationToReturn = .controlsFixture(.controlsFixture(
                printerID: sent.printerID, operationID: sent.operationID, request: sent.request,
                state: .unknown, recovery: true
            ))
            await model.refreshControlOperation()
            XCTAssertEqual(model.calibrationStep, .home)
            XCTAssertTrue(model.motionBlockedReason?.contains("isolation") == true)
            try await finish(model, service, state: terminal,
                             evidence: terminal == .recovered ? .operatorVerifiedRecovery : .motionQueueDrained)
            XCTAssertEqual(model.calibrationStep, terminal == .succeeded ? .position : .home)
            if terminal == .succeeded {
                await model.positionForCalibration()
                XCTAssertEqual(service.submittedControlOperations.last?.request.kind, .moveTo)
                service.statusToReturn = VerifiedSafetyFixtures.status(
                    id: model.printer.id, position: .init(x: 40, y: 30, z: 10)
                )
                try await finish(model, service)
                XCTAssertEqual(model.calibrationStep, .adjust)
                await model.adjustCalibration(delta: -0.05)
                XCTAssertEqual(service.submittedControlOperations.last?.request, .init(kind: .moveTo, x: 40, y: 30, z: 9.95, f: 600))
                service.statusToReturn = VerifiedSafetyFixtures.status(
                    id: model.printer.id, position: .init(x: 40, y: 30, z: 9.95)
                )
                try await finish(model, service)
                XCTAssertEqual(model.calibrationOffset, 0.07)
            }
            XCTAssertNil(service.homeCalledWith)
            XCTAssertNil(service.moveToCalledWith)
            model.cancelCalibration()
        }
    }

    func test_missingAsyncSupportRequiresServerUpdateAndNeverUsesLegacyMotion() async throws {
        let service = MockPrinterService()
        service.controlOperationError = PrinterControlOperationError.updateRequired
        let (model, _) = try await fixture(service: service)
        await model.homeAll()
        await model.homeXY()
        await model.homeZ()
        await model.jog(axis: "X", distanceMm: 1)
        XCTAssertTrue(model.motionBlockedReason?.contains("Server update required") == true)
        XCTAssertTrue(service.submittedControlOperations.isEmpty)
        XCTAssertNil(service.homeCalledWith)
        XCTAssertNil(service.homeXYCalledWith)
        XCTAssertNil(service.homeZCalledWith)
        XCTAssertNil(service.moveCalledWith)
    }

    func test_explicitAdmissionResubmissionAfterLostBeforeAdmissionPreservesSavedKeyAndIntent() async throws {
        let (original, service) = try await fixture()
        service.submitControlOperationHandler = { _, _, _ in throw NetworkError.timeout }
        await original.jog(axis: "Z", distanceMm: -1.001)
        let saved = try XCTUnwrap(service.submittedControlOperations.first)
        original.deactivate()
        let (model, _) = try await fixture(service: service)
        XCTAssertEqual(model.motionAdmissionResubmissionID, saved.operationID)
        for _ in 0..<3 { await model.refreshControlOperation() }
        await model.handleControlOperationInvalidation(.init(
            printerId: model.printer.id, operationId: saved.operationID, rowVersion: "hint"
        ))
        XCTAssertEqual(service.submittedControlOperations.count, 1, "404, reopen and invalidation never submit")
        XCTAssertTrue(model.hasUnresolvedMotion, "Declining confirmation leaves the gate held")

        admitRunning(service)
        await model.resubmitUnconfirmedMotionAdmission(operationID: saved.operationID)
        let repeated = try XCTUnwrap(service.submittedControlOperations.last)
        XCTAssertEqual(service.submittedControlOperations.count, 2)
        XCTAssertEqual(repeated.operationID, saved.operationID)
        XCTAssertEqual(repeated.printerID, saved.printerID)
        XCTAssertEqual(repeated.request, saved.request)
        XCTAssertEqual(repeated.request, .init(kind: .jog, z: -1.001, f: 600))
        XCTAssertTrue(model.hasUnresolvedMotion, "Acceptance still requires terminal REST evidence")
        XCTAssertNil(model.motionAdmissionResubmissionID)
        await model.resubmitUnconfirmedMotionAdmission(operationID: saved.operationID)
        XCTAssertEqual(service.submittedControlOperations.count, 2, "Known admission cannot be resubmitted")
        XCTAssertNil(service.moveCalledWith)
        try await finish(model, service)
    }

    func test_lostAfterAdmissionExplicitSameKeyReturnsExistingWithoutSecondActuation() async throws {
        let (model, service) = try await fixture()
        let actuations = HookCounter()
        service.controlOperationHandler = { _, _ in throw NetworkError.notFound }
        service.submitControlOperationHandler = { [service] printerID, operationID, request in
            if let existing = service.controlOperationToReturn { return existing }
            _ = await actuations.next()
            service.controlOperationToReturn = .controlsFixture(
                printerID: printerID, operationID: operationID, request: request
            )
            throw NetworkError.timeout
        }
        await model.homeXY()
        let saved = try XCTUnwrap(service.submittedControlOperations.first)
        XCTAssertEqual(model.motionAdmissionResubmissionID, saved.operationID)
        await model.resubmitUnconfirmedMotionAdmission(operationID: saved.operationID)
        let nextActuationNumber = await actuations.next()
        XCTAssertEqual(nextActuationNumber, 2, "The idempotent fake admitted exactly one physical operation")
        XCTAssertEqual(service.submittedControlOperations.map(\.operationID), [saved.operationID, saved.operationID])
        XCTAssertEqual(service.submittedControlOperations.map(\.request), [saved.request, saved.request])
        XCTAssertNil(model.motionAdmissionResubmissionID, "POST admission knowledge survives a failed GET")
        XCTAssertTrue(model.hasUnresolvedMotion)
        model.deactivate()
        let (reopened, _) = try await fixture(service: service)
        XCTAssertNil(reopened.motionAdmissionResubmissionID, "Known admission is persisted across navigation")
        XCTAssertTrue(reopened.hasUnresolvedMotion)
        service.controlOperationHandler = nil
        try await finish(reopened, service)
    }

    func test_terminalAdmissionReplayStillWaitsForCanonicalReadBeforeSuccess() async throws {
        let (model, service) = try await fixture()
        service.submitControlOperationHandler = { _, _, _ in throw NetworkError.timeout }
        await model.moveTo(x: -0.0, y: 30.125, z: 10, feedrateMmMin: nil)
        let saved = try XCTUnwrap(service.submittedControlOperations.first)
        let terminal = PrinterControlOperation.controlsFixture(
            printerID: saved.printerID, operationID: saved.operationID, request: saved.request,
            state: .succeeded, evidence: .motionQueueDrained, held: false
        )
        service.submitControlOperationHandler = { [service] _, _, _ in
            service.controlOperationToReturn = terminal
            return terminal
        }
        let barrier = AsyncBarrier()
        addTeardownBlock { barrier.close() }
        let reads = HookCounter()
        let unlocked = service.currentControlOperationToReturn
        service.currentControlOperationHandler = { _ in
            if await reads.next() > 1 { await barrier.arriveAndWait() }
            return unlocked
        }
        let resend = Task { await model.resubmitUnconfirmedMotionAdmission(operationID: saved.operationID) }
        await barrier.waitUntilArrived()
        XCTAssertTrue(model.hasUnresolvedMotion, "A terminal POST 200 replay is not direct completion")
        XCTAssertNil(model.motionAdmissionResubmissionID)
        XCTAssertEqual(service.submittedControlOperations.last?.request, saved.request)
        XCTAssertEqual(service.submittedControlOperations.last?.request.x?.sign, saved.request.x?.sign)
        barrier.close()
        await resend.value
        XCTAssertFalse(model.hasUnresolvedMotion)
        XCTAssertTrue(service.controlOperationReadIDs.contains(saved.operationID))
        XCTAssertEqual(service.submittedControlOperations.count, 2)
    }

    func test_explicitAdmissionPreflightDiscoveringUnknownNeverResubmits() async throws {
        for state in [PrinterControlOperationState.unknown, .recovering] {
            let (model, service) = try await fixture(server: UUID())
            service.submitControlOperationHandler = { _, _, _ in throw NetworkError.timeout }
            await model.homeAll()
            let sent = try XCTUnwrap(service.submittedControlOperations.first)
            service.currentControlOperationToReturn = .controlsFixture(.controlsFixture(
                printerID: sent.printerID, operationID: sent.operationID, request: sent.request,
                state: state, recovery: true
            ))
            await model.resubmitUnconfirmedMotionAdmission(operationID: sent.operationID)
            XCTAssertEqual(service.submittedControlOperations.count, 1)
            XCTAssertEqual(model.controlOperation?.state, state)
            XCTAssertNil(model.motionAdmissionResubmissionID)
            XCTAssertTrue(model.motionBlockedReason?.contains("isolation") == true)
            model.deactivate()
        }
    }

    func test_admissionConfirmationCannotCrossAuthorityOrOperationIdentity() async throws {
        let epoch = AuthEpoch()
        let (model, service) = try await fixture(access: { epoch.value == 0 ? nil : "Account changed" })
        service.submitControlOperationHandler = { _, _, _ in throw NetworkError.timeout }
        await model.homeAll()
        let saved = try XCTUnwrap(model.motionAdmissionResubmissionID)
        await model.resubmitUnconfirmedMotionAdmission(operationID: UUID())
        let (otherServer, _) = try await fixture(service: service, server: UUID())
        let (otherUser, _) = try await fixture(service: service, user: UUID())
        await otherServer.resubmitUnconfirmedMotionAdmission(operationID: saved)
        await otherUser.resubmitUnconfirmedMotionAdmission(operationID: saved)
        XCTAssertEqual(service.submittedControlOperations.count, 1)
        let barrier = AsyncBarrier()
        addTeardownBlock { barrier.close() }
        service.beforeSafetyStatus = { await barrier.arriveAndWait() }
        let resend = Task { await model.resubmitUnconfirmedMotionAdmission(operationID: saved) }
        await barrier.waitUntilArrived()
        epoch.value = 1
        model.refreshAccess()
        barrier.close()
        await resend.value
        XCTAssertEqual(service.submittedControlOperations.count, 1)
        XCTAssertTrue(model.hasUnresolvedMotion)
        XCTAssertNil(model.motionAdmissionResubmissionID)
    }

    func test_admissionResubmissionPreflightRejectsFreshUnsafeReadiness() async throws {
        let (model, service) = try await fixture()
        service.submitControlOperationHandler = { _, _, _ in throw NetworkError.timeout }
        await model.homeZ()
        let saved = try XCTUnwrap(model.motionAdmissionResubmissionID)
        service.statusToReturn = VerifiedSafetyFixtures.status(id: model.printer.id, isOnline: false)
        await model.resubmitUnconfirmedMotionAdmission(operationID: saved)
        XCTAssertEqual(service.submittedControlOperations.count, 1)
        XCTAssertTrue(model.hasUnresolvedMotion)
        XCTAssertNil(service.homeZCalledWith)
    }

    func test_cancelingExplicitAdmissionPreflightDoesNotSendOrReplay() async throws {
        let (model, service) = try await fixture()
        service.submitControlOperationHandler = { _, _, _ in throw NetworkError.timeout }
        await model.homeAll()
        let saved = try XCTUnwrap(model.motionAdmissionResubmissionID)
        let barrier = AsyncBarrier()
        addTeardownBlock { barrier.close() }
        service.beforeSafetyStatus = { await barrier.arriveAndWait() }
        let resend = Task { await model.resubmitUnconfirmedMotionAdmission(operationID: saved) }
        await barrier.waitUntilArrived()
        resend.cancel()
        barrier.close()
        await resend.value
        XCTAssertEqual(service.submittedControlOperations.count, 1)
        XCTAssertTrue(model.hasUnresolvedMotion)
        await model.refreshControlOperation()
        XCTAssertEqual(service.submittedControlOperations.count, 1)
    }

    func test_explicitAdmissionLateResponseCannotSettleChangedAuthority() async throws {
        let epoch = AuthEpoch()
        let (old, service) = try await fixture(access: { epoch.value == 0 ? nil : "Authentication changed" })
        service.submitControlOperationHandler = { _, _, _ in throw NetworkError.timeout }
        await old.homeZ()
        let saved = try XCTUnwrap(old.motionAdmissionResubmissionID)
        let barrier = AsyncBarrier()
        addTeardownBlock { barrier.close() }
        service.submitControlOperationHandler = { [service] printerID, operationID, request in
            await barrier.arriveAndWait()
            XCTAssertFalse(Task.isCancelled, "Admitted submission outlives observation")
            let terminal = PrinterControlOperation.controlsFixture(
                printerID: printerID, operationID: operationID, request: request,
                state: .succeeded, evidence: .motionQueueDrained, held: false
            )
            service.controlOperationToReturn = terminal
            return terminal
        }
        let resend = Task { await old.resubmitUnconfirmedMotionAdmission(operationID: saved) }
        await barrier.waitUntilArrived()
        resend.cancel()
        epoch.value = 1
        old.refreshAccess()
        barrier.close()
        await resend.value
        XCTAssertTrue(old.hasUnresolvedMotion)
        XCTAssertNil(old.controlOperation)
        XCTAssertEqual(service.submittedControlOperations.count, 2)
        let (reopened, _) = try await fixture(service: service)
        XCTAssertFalse(reopened.hasUnresolvedMotion)
        XCTAssertEqual(reopened.controlOperation?.operationId, saved)
        XCTAssertEqual(service.submittedControlOperations.count, 2)
    }

    func test_savedAbsoluteAdmissionRechecksFreshGeometryWithoutChangingIntent() async throws {
        let (model, service) = try await fixture()
        service.submitControlOperationHandler = { _, _, _ in throw NetworkError.timeout }
        await model.moveTo(x: 40, y: 30, z: 10, feedrateMmMin: nil)
        let saved = try XCTUnwrap(service.submittedControlOperations.first)
        service.statusToReturn = VerifiedSafetyFixtures.status(
            id: model.printer.id, position: .init(x: 40, y: 30, z: 10)
        )
        service.capabilitiesToReturn?.verifiedSafety = nil
        await model.resubmitUnconfirmedMotionAdmission(operationID: saved.operationID)
        XCTAssertEqual(service.submittedControlOperations.count, 1)
        XCTAssertEqual(model.motionOperationID, saved.operationID)
        XCTAssertTrue(model.hasUnresolvedMotion)
        XCTAssertNil(service.moveToCalledWith)
    }

    func test_explicitCalibrationAdmissionOnlyAdvancesAfterCanonicalSuccessAndSafety() async throws {
        let (model, service) = try await fixture()
        await model.startCalibration()
        model.beginCalibrationHome()
        service.submitControlOperationHandler = { _, _, _ in throw NetworkError.timeout }
        await model.homeForCalibration()
        let saved = try XCTUnwrap(model.motionAdmissionResubmissionID)
        admitRunning(service)
        await model.resubmitUnconfirmedMotionAdmission(operationID: saved)
        XCTAssertEqual(model.calibrationStep, .home)
        XCTAssertEqual(service.submittedControlOperations.count, 2)
        try await finish(model, service)
        XCTAssertEqual(model.calibrationStep, .position)
        model.cancelCalibration()
    }

    func test_newerCurrentReadSupersedesAdmissionConfirmationPreflight() async throws {
        let (model, service) = try await fixture()
        service.submitControlOperationHandler = { _, _, _ in throw NetworkError.timeout }
        await model.homeAll()
        let saved = try XCTUnwrap(model.motionAdmissionResubmissionID)
        let unlocked = service.currentControlOperationToReturn
        let barrier = AsyncBarrier()
        addTeardownBlock { barrier.close() }
        service.currentControlOperationHandler = { _ in
            await barrier.arriveAndWait()
            return unlocked
        }
        let resend = Task { await model.resubmitUnconfirmedMotionAdmission(operationID: saved) }
        await barrier.waitUntilArrived()
        service.currentControlOperationHandler = nil
        service.currentControlOperationToReturn = .init(physicalControl: .init(
            supportedOperations: PrinterControlOperationKind.allCases,
            barrierHeld: true, requiresRecovery: true
        ), operation: nil)
        await model.refreshControlOperation()
        barrier.close()
        await resend.value
        XCTAssertEqual(service.submittedControlOperations.count, 1)
        XCTAssertTrue(model.hasUnresolvedMotion)
    }

    func test_falseBarrierFlagWithUnknownOrPartialProjectionNeverAuthorizesMotion() async throws {
        for projection in [
            PrinterPhysicalControl(supportedOperations: [.homeAll], barrierHeld: false,
                                   operationId: UUID(), state: .unknown, requiresRecovery: false),
            PrinterPhysicalControl(supportedOperations: [.homeAll], barrierHeld: false,
                                   operationId: UUID(), requiresRecovery: false)
        ] {
            let (model, service) = try await fixture(server: UUID())
            var telemetry = model.printer
            telemetry.physicalControl = projection
            model.handlePrinterUpdate(telemetry)
            await model.homeAll()
            XCTAssertTrue(model.hasUnresolvedMotion)
            XCTAssertTrue(model.isExecuting)
            XCTAssertTrue(service.submittedControlOperations.isEmpty)
            XCTAssertNil(service.homeCalledWith)
            model.deactivate()
        }
    }

    func test_unrelatedBarrierWithoutOperationBlocksUntilAuthoritativeRelease() async throws {
        let service = MockPrinterService()
        service.currentControlOperationToReturn = .init(physicalControl: .init(
            supportedOperations: PrinterControlOperationKind.allCases,
            barrierHeld: true, requiresRecovery: true
        ), operation: nil)
        let (model, _) = try await fixture(service: service)
        XCTAssertTrue(model.hasUnresolvedMotion)
        XCTAssertNil(model.operationReadError)
        await model.homeAll()
        XCTAssertTrue(service.submittedControlOperations.isEmpty)

        var telemetry = model.printer
        telemetry.physicalControl = .init(
            supportedOperations: PrinterControlOperationKind.allCases,
            barrierHeld: false, requiresRecovery: false
        )
        model.handlePrinterUpdate(telemetry)
        XCTAssertTrue(model.hasUnresolvedMotion)

        service.currentControlOperationToReturn = .init(
            physicalControl: try XCTUnwrap(telemetry.physicalControl), operation: nil
        )
        await model.refreshControlOperation()
        XCTAssertFalse(model.hasUnresolvedMotion)
        XCTAssertNil(model.motionBlockedReason)
        XCTAssertTrue(service.submittedControlOperations.isEmpty)
    }

    func test_unrelatedBarrierReleaseCannotDiscardUnreadableSavedMotion() async throws {
        let printer = try TestData.decodePrinter()
        let key = "printer-motion.v1.\(serverID.uuidString).\(userID.uuidString).\(printer.id.uuidString)"
        defaults.set(Data("invalid motion identity".utf8), forKey: key)
        let service = MockPrinterService()
        service.currentControlOperationToReturn = .init(physicalControl: .init(
            supportedOperations: PrinterControlOperationKind.allCases,
            barrierHeld: true, requiresRecovery: true
        ), operation: nil)
        let (model, _) = try await fixture(service: service)
        service.currentControlOperationToReturn = .init(physicalControl: .init(
            supportedOperations: PrinterControlOperationKind.allCases,
            barrierHeld: false, requiresRecovery: false
        ), operation: nil)
        await model.refreshControlOperation()
        XCTAssertTrue(model.hasUnresolvedMotion)
        XCTAssertNotNil(defaults.data(forKey: key))
        XCTAssertTrue(service.submittedControlOperations.isEmpty)
    }

    func test_notFoundStatusIsAmbiguousRatherThanServerUpdateRequired() async throws {
        let errors: [Error] = [
            PrinterControlOperationError.problem(statusCode: 404, code: nil, message: nil),
            NetworkError.notFound
        ]
        for error in errors {
            let service = MockPrinterService()
            service.controlOperationError = error
            let (model, _) = try await fixture(service: service, server: UUID())
            await model.homeAll()
            XCTAssertTrue(model.operationReadError?.contains("could not be verified") == true)
            XCTAssertFalse(model.operationReadError?.contains("Server update required") == true)
            XCTAssertTrue(service.submittedControlOperations.isEmpty)
            XCTAssertNil(service.homeCalledWith)
            model.deactivate()
        }
    }

    func test_sameSubjectAuthEpochChangeCannotSettleOldOwnerButReopenReadsSavedIdentity() async throws {
        let epoch = AuthEpoch()
        let (old, service) = try await fixture(access: {
            epoch.value == 0 ? nil : "Authentication changed. Reopen this printer."
        })
        let barrier = AsyncBarrier()
        addTeardownBlock { barrier.close() }
        service.submitControlOperationHandler = { [service] printerID, operationID, request in
            await barrier.arriveAndWait()
            let result = PrinterControlOperation.controlsFixture(
                printerID: printerID, operationID: operationID, request: request,
                state: .succeeded, evidence: .motionQueueDrained, held: false
            )
            service.controlOperationToReturn = result
            service.currentControlOperationToReturn = .controlsFixture(result)
            return result
        }
        let submission = Task { await old.homeAll() }
        await barrier.waitUntilArrived()
        let operationID = try XCTUnwrap(old.motionOperationID)
        epoch.value = 1
        old.refreshAccess()
        barrier.close()
        await submission.value
        XCTAssertTrue(old.hasUnresolvedMotion)
        XCTAssertEqual(old.motionOperationID, operationID)
        let (reopened, _) = try await fixture(service: service)
        XCTAssertEqual(reopened.controlOperation?.operationId, operationID)
        XCTAssertFalse(reopened.hasUnresolvedMotion)
        XCTAssertFalse(reopened.isExecuting)
        XCTAssertEqual(service.submittedControlOperations.count, 1)
    }

    func test_observedRemoteOperationLooksUpTerminalWhenCurrentEnvelopeClears() async throws {
        let service = MockPrinterService()
        let printer = try TestData.decodePrinter()
        let operation = PrinterControlOperation.controlsFixture(printerID: printer.id)
        service.currentControlOperationToReturn = .controlsFixture(operation)
        let (model, _) = try await fixture(service: service)
        XCTAssertTrue(model.hasUnresolvedMotion)
        let succeeded = PrinterControlOperation.controlsFixture(
            printerID: printer.id, operationID: operation.operationId,
            state: .succeeded, evidence: .motionQueueDrained, held: false
        )
        service.currentControlOperationToReturn = .controlsFixture(succeeded)
        service.controlOperationToReturn = succeeded
        await model.refreshControlOperation()
        XCTAssertTrue(service.controlOperationReadIDs.contains(operation.operationId))
        XCTAssertFalse(model.hasUnresolvedMotion)
        XCTAssertFalse(model.isExecuting)
        XCTAssertTrue(service.submittedControlOperations.isEmpty)
    }

    func test_duplicateAndOutOfOrderInvalidationsOnlyReadAuthoritativeState() async throws {
        let (model, service) = try await fixture()
        admitRunning(service)
        await model.homeAll()
        let operationID = try XCTUnwrap(model.motionOperationID)
        let reads = service.currentControlOperationReadCount
        for version in ["new-opaque", "old-opaque", "old-opaque"] {
            await model.handleControlOperationInvalidation(.init(
                printerId: model.printer.id, operationId: operationID, rowVersion: version
            ))
            XCTAssertTrue(model.hasUnresolvedMotion)
            XCTAssertEqual(model.controlOperation?.state, .running)
        }
        XCTAssertEqual(service.currentControlOperationReadCount, reads + 3)
        await model.handleControlOperationInvalidation(.init(
            printerId: UUID(), operationId: operationID, rowVersion: "unrelated"
        ))
        XCTAssertEqual(service.currentControlOperationReadCount, reads + 3)
        XCTAssertEqual(service.submittedControlOperations.count, 1)
        try await finish(model, service)
    }

    func test_reconnectRefreshesWithoutResubmitting() async throws {
        let (model, service) = try await fixture()
        admitRunning(service)
        await model.homeAll()
        let signal = MockSignalRService()
        model.observeControlOperations(using: signal)
        let barrier = AsyncBarrier()
        addTeardownBlock { barrier.close() }
        let current = service.currentControlOperationToReturn
        service.currentControlOperationHandler = { _ in
            await barrier.arriveAndWait()
            return current
        }
        signal.connectionState = .connected
        await barrier.waitUntilArrived()
        XCTAssertEqual(service.submittedControlOperations.count, 1)
        barrier.close()
        service.currentControlOperationHandler = nil
        try await finish(model, service)
        model.deactivate()
    }

    func test_instanceScopedInvalidationStartsReadWithoutTrustingPayloadOperation() async throws {
        let (model, service) = try await fixture()
        admitRunning(service)
        await model.homeAll()
        let operationID = try XCTUnwrap(model.motionOperationID)
        let signal = MockSignalRService()
        model.observeControlOperations(using: signal)
        let barrier = AsyncBarrier()
        addTeardownBlock { barrier.close() }
        let current = service.currentControlOperationToReturn
        service.currentControlOperationHandler = { _ in
            await barrier.arriveAndWait()
            return current
        }
        signal.simulateControlOperationUpdated(.init(
            printerId: model.printer.id, operationId: UUID(), rowVersion: "opaque-other-operation"
        ))
        await barrier.waitUntilArrived()
        XCTAssertEqual(model.motionOperationID, operationID)
        XCTAssertTrue(model.hasUnresolvedMotion)
        XCTAssertEqual(service.submittedControlOperations.count, 1)
        model.deactivate()
        barrier.close()
    }
}

/// Local decorator keeps delayed read/home seams inside this issue's test ownership.
private struct ControlsDelayedService: PrinterServiceProtocol {
    let base: MockPrinterService
    var beforeDetails: @Sendable () async -> Void = {}
    var beforeHome: @Sendable () async -> Void = {}
    var beforeMoveTo: @Sendable () async -> Void = {}

    func getDetails(id: UUID) async throws -> PrinterDetails {
        await beforeDetails()
        return try await base.getDetails(id: id)
    }
    func home(printerId: UUID, axes: [String]) async throws {
        await beforeHome()
        try await base.home(printerId: printerId, axes: axes)
    }
    func getBackendCapabilities(printerId: UUID) async throws -> PrinterBackendCapabilities {
        try await base.getBackendCapabilities(printerId: printerId)
    }
    func setTemperatures(printerId: UUID, hotend: Double?, bed: Double?) async throws {
        try await base.setTemperatures(printerId: printerId, hotend: hotend, bed: bed)
    }
    func list(includeDisabled: Bool) async throws -> [Printer] { try await base.list(includeDisabled: includeDisabled) }
    func get(id: UUID) async throws -> Printer { try await base.get(id: id) }
    func getStatus(id: UUID) async throws -> PrinterStatusDetail { try await base.getStatus(id: id) }
    func listCameraUrls() async throws -> [PrinterCameraUrls] { try await base.listCameraUrls() }
    func getCameraUrl(id: UUID) async throws -> PrinterCameraUrl { try await base.getCameraUrl(id: id) }
    func getSnapshot(id: UUID) async throws -> Data { try await base.getSnapshot(id: id) }
    func getCurrentJob(id: UUID) async throws -> PrintJobStatusInfo? { try await base.getCurrentJob(id: id) }
    func getHistory(id: UUID, limit: Int?) async throws -> PrinterHistoryList { try await base.getHistory(id: id, limit: limit) }
    func pause(id: UUID) async throws -> CommandResult { try await base.pause(id: id) }
    func resume(id: UUID) async throws -> CommandResult { try await base.resume(id: id) }
    func cancel(id: UUID) async throws -> CommandResult { try await base.cancel(id: id) }
    func stop(id: UUID) async throws -> CommandResult { try await base.stop(id: id) }
    func emergencyStop(id: UUID) async throws -> CommandResult { try await base.emergencyStop(id: id) }
    func setMaintenanceMode(id: UUID, inMaintenance: Bool, reviewedRowVersion: String) async throws -> Printer {
        try await base.setMaintenanceMode(id: id, inMaintenance: inMaintenance, reviewedRowVersion: reviewedRowVersion)
    }
    func getQueueOverview(model: String?, nozzle: Double?, material: String?) async throws -> [QueueOverview] {
        try await base.getQueueOverview(model: model, nozzle: nozzle, material: material)
    }
    func setActiveSpool(printerId: UUID, spoolId: Int?, reviewedRowVersion: String) async throws -> CommandResult {
        try await base.setActiveSpool(printerId: printerId, spoolId: spoolId, reviewedRowVersion: reviewedRowVersion)
    }
    func bindToolheadSpool(printerId: UUID, toolheadIndex: Int, request: ToolheadSpoolBindRequest, idempotencyKey: String) async throws -> CommandResult {
        try await base.bindToolheadSpool(printerId: printerId, toolheadIndex: toolheadIndex, request: request, idempotencyKey: idempotencyKey)
    }
    func listAvailableSpools(printerId: UUID) async throws -> [SpoolmanSpool] { try await base.listAvailableSpools(printerId: printerId) }
    func loadFilament(printerId: UUID) async throws -> CommandResult { try await base.loadFilament(printerId: printerId) }
    func unloadFilament(printerId: UUID) async throws -> CommandResult { try await base.unloadFilament(printerId: printerId) }
    func changeFilament(printerId: UUID) async throws -> CommandResult { try await base.changeFilament(printerId: printerId) }
    func homeXY(printerId: UUID) async throws { try await base.homeXY(printerId: printerId) }
    func homeZ(printerId: UUID) async throws { try await base.homeZ(printerId: printerId) }
    func move(printerId: UUID, axis: String, distanceMm: Double, feedrateMmMin: Int) async throws {
        try await base.move(printerId: printerId, axis: axis, distanceMm: distanceMm, feedrateMmMin: feedrateMmMin)
    }
    func moveTo(printerId: UUID, x: Double?, y: Double?, z: Double?, feedrateMmMin: Int?) async throws -> CommandResult {
        await beforeMoveTo()
        return try await base.moveTo(printerId: printerId, x: x, y: y, z: z, feedrateMmMin: feedrateMmMin)
    }
    func extrude(printerId: UUID, distanceMm: Double, feedrateMmPerMinute: Int) async throws -> CommandResult {
        try await base.extrude(printerId: printerId, distanceMm: distanceMm, feedrateMmPerMinute: feedrateMmPerMinute)
    }
    func disableMotors(printerId: UUID) async throws -> CommandResult { try await base.disableMotors(printerId: printerId) }
    func saveZOffset(printerId: UUID, offsetMm: Double, saveToFirmware: Bool, reviewedRowVersion: String) async throws -> CommandResult {
        try await base.saveZOffset(printerId: printerId, offsetMm: offsetMm, saveToFirmware: saveToFirmware, reviewedRowVersion: reviewedRowVersion)
    }
    func unloadFilament(printerId: UUID, toolheadIndex: Int?) async throws -> FilamentUnloadResult {
        try await base.unloadFilament(printerId: printerId, toolheadIndex: toolheadIndex)
    }
    func listFallbackGroups(printerId: UUID) async throws -> [FilamentFallbackGroup] { try await base.listFallbackGroups(printerId: printerId) }
    func getFallbackGroup(printerId: UUID, groupId: UUID) async throws -> FilamentFallbackGroup {
        try await base.getFallbackGroup(printerId: printerId, groupId: groupId)
    }
    func createFallbackGroup(printerId: UUID, _ request: CreateFilamentFallbackGroupRequest) async throws -> FilamentFallbackGroup {
        try await base.createFallbackGroup(printerId: printerId, request)
    }
    func updateFallbackGroup(printerId: UUID, groupId: UUID, _ request: UpdateFilamentFallbackGroupRequest) async throws -> FilamentFallbackGroup {
        try await base.updateFallbackGroup(printerId: printerId, groupId: groupId, request)
    }
    func deleteFallbackGroup(printerId: UUID, groupId: UUID) async throws { try await base.deleteFallbackGroup(printerId: printerId, groupId: groupId) }
    func getAvailableFallback(printerId: UUID, sourceToolheadId: UUID, material: String) async throws -> AvailableFallbackMember? {
        try await base.getAvailableFallback(printerId: printerId, sourceToolheadId: sourceToolheadId, material: material)
    }
}
