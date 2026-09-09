import Combine
import KeychainSwift
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
        model.configureAccess(serverID: serverID, accessCheck)
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

    private func makeViewModel(
        printer: Printer? = nil,
        capabilities: PrinterBackendCapabilities? = nil,
        accessCheck: @escaping @MainActor () -> String? = { nil }
    ) throws -> PrinterControlsViewModel {
        let p = try printer ?? TestData.decodePrinter() // online + state="printing" by default
        if let caps = capabilities {
            mockService.capabilitiesToReturn = caps
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
        service.capabilitiesToReturn = caps
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
        await model.moveTo(x: 0, y: nil, z: nil, feedrateMmMin: nil)
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
        XCTAssertFalse(sameMachine.isExecuting, "A settled response must release the shared lease, not wait for telemetry")
        XCTAssertNotNil(original.pendingCommand, "The old view's telemetry observation is separate from transport ownership")
    }

    func test_sharedLease_staleTelemetryOwnerCannotReleaseReplacementToken() async throws {
        let serverID = UUID()
        let (original, _) = try await leaseOwner(serverID: serverID)
        await original.setHeaterTarget(.hotend, target: 200)
        let originalToken = try XCTUnwrap(original.pendingCommand)
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

    func test_absoluteFeedrate_rejectsEveryCustomValueAndUsesEstablishedAxisRates() async throws {
        var caps = Self.fullCaps
        caps.supportsAbsoluteMovement = true
        let vm = try makeViewModel(printer: idlePrinter(), capabilities: caps)
        await vm.loadCapabilities()
        for rate in [Int.min, -1, 0, 1, 600, 3000, 3001, Int.max] {
            await vm.moveTo(x: 1, y: nil, z: nil, feedrateMmMin: rate)
            XCTAssertNil(mockService.moveToCalledWith)
            XCTAssertNil(vm.pendingCommand)
            XCTAssertEqual(vm.lastError?.message, ControlNumberInput.customFeedrateMessage)
        }
        for z in [nil, 0.0, -1.0] as [Double?] {
            await vm.moveTo(x: 1, y: 0, z: z, feedrateMmMin: nil)
            XCTAssertNil(vm.lastError)
            XCTAssertEqual(mockService.moveToCalledWith?.feedrateMmMin, z == nil ? 3000 : 600)
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
                await vm.moveTo(x: axis == "X" ? value : nil, y: axis == "Y" ? value : nil,
                                z: axis == "Z" ? value : nil, feedrateMmMin: nil)
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
        let vm = try makeViewModel(printer: idlePrinter(), capabilities: caps)
        await vm.loadCapabilities()
        for value in [1.001, -1.234, 0.1, 0.0] {
            await vm.moveTo(x: value, y: nil, z: nil, feedrateMmMin: nil)
            XCTAssertNil(vm.lastError)
            XCTAssertEqual(mockService.moveToCalledWith?.x, value)
            XCTAssertNil(mockService.moveToCalledWith?.y)
            XCTAssertNil(mockService.moveToCalledWith?.z)
            XCTAssertEqual(mockService.moveToCalledWith?.feedrateMmMin, 3000)
            XCTAssertNotNil(vm.pendingCommand)
            var reported = vm.printer
            reported.x = value
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
        mockService.capabilitiesToReturn = caps
        let barrier = AsyncBarrier()
        addTeardownBlock { barrier.close() }
        let service = ControlsDelayedService(base: mockService, beforeMoveTo: { await barrier.arriveAndWait() })
        let vm = PrinterControlsViewModel.configuredForTests(printerService: service, printer: printer)
        await vm.loadCapabilities()
        let command = Task { await vm.moveTo(x: 1.001, y: 0, z: -1.234, feedrateMmMin: nil) }
        await barrier.waitUntilArrived()
        var reported = printer
        reported.x = 1.001
        reported.y = 0
        reported.z = -1.234
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
        let printer = try idlePrinter()
        var caps = Self.fullCaps
        caps.supportsAbsoluteMovement = true
        let vm = try makeViewModel(printer: printer, capabilities: caps)
        await vm.loadCapabilities()
        await vm.moveTo(x: try XCTUnwrap(printer.x), y: nil, z: nil, feedrateMmMin: nil)
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

    func test_absoluteDispatch_omitsBlankAxesPreservesZeroAndFeedrate() async throws {
        var caps = Self.fullCaps
        caps.supportsAbsoluteMovement = true
        let vm = try makeViewModel(printer: idlePrinter(), capabilities: caps)
        await vm.loadCapabilities()
        await vm.moveTo(x: 0, y: nil, z: -2.5, feedrateMmMin: nil)
        XCTAssertEqual(mockService.moveToCalledWith?.x, 0)
        XCTAssertNil(mockService.moveToCalledWith?.y)
        XCTAssertEqual(mockService.moveToCalledWith?.z, -2.5, "Do not invent a zero-origin travel bound")
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
        let vm = try makeViewModel(printer: idlePrinter(), capabilities: caps)
        await vm.loadCapabilities()
        for (x, y, f) in [(nil, nil, nil), (Double.nan, nil, nil),
                           (1, nil, 0), (1, nil, -10), (nil, 2, 600)] as [(Double?, Double?, Int?)] {
            await vm.moveTo(x: x, y: y, z: nil, feedrateMmMin: f)
            XCTAssertNil(mockService.moveToCalledWith)
            XCTAssertNotNil(vm.lastError)
            XCTAssertNil(vm.pendingCommand)
        }
    }

    func test_newCommands_requireSpecificCapabilities() async throws {
        for caps in [Self.fullCaps, PrinterBackendCapabilities.fallback(for: .moonraker)] {
            let vm = try makeViewModel(printer: idlePrinter(), capabilities: caps)
            await vm.loadCapabilities()
            await vm.moveTo(x: 0, y: nil, z: nil, feedrateMmMin: nil)
            await vm.disableMotors()
            XCTAssertNil(mockService.moveToCalledWith)
            XCTAssertNil(mockService.disableMotorsCalledWith)
        }
        let unknown = try makeViewModel(printer: idlePrinter())
        await unknown.setHeaterTarget(.hotend, target: 200)
        await unknown.moveTo(x: 0, y: nil, z: nil, feedrateMmMin: nil)
        await unknown.disableMotors()
        XCTAssertNil(mockService.setTemperaturesCalledWith)
        XCTAssertNil(mockService.moveToCalledWith)
        XCTAssertNil(mockService.disableMotorsCalledWith)
    }

    func test_motorReleaseAndAbsolute_failureResultsAreNotSuccess() async throws {
        var caps = Self.fullCaps
        caps.supportsAbsoluteMovement = true
        caps.supportsDisableMotors = true
        let vm = try makeViewModel(printer: idlePrinter(), capabilities: caps)
        await vm.loadCapabilities()
        mockService.commandResultToReturn = CommandResult(success: false, message: "Guard rejected")
        await vm.disableMotors()
        XCTAssertEqual(vm.lastError?.message, "Guard rejected")
        XCTAssertNil(vm.pendingCommand)
        XCTAssertNil(vm.commandNotice)
        await vm.moveTo(x: 1, y: nil, z: nil, feedrateMmMin: nil)
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
            let vm = try makeViewModel(printer: printer, capabilities: caps)
            await vm.loadCapabilities()
            await vm.setHeaterTarget(.hotend, target: 200)
            await vm.moveTo(x: 1, y: nil, z: nil, feedrateMmMin: nil)
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
        let vm = try makeViewModel(printer: idlePrinter(), capabilities: caps)
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
            await vm.moveTo(x: 0, y: nil, z: nil, feedrateMmMin: nil)
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
        await vm.moveTo(x: 1, y: nil, z: nil, feedrateMmMin: nil)
        await vm.disableMotors()
        XCTAssertNil(mockService.setTemperaturesCalledWith)
        XCTAssertNil(mockService.moveToCalledWith)
        XCTAssertNil(mockService.disableMotorsCalledWith)
    }

    func test_pendingIndividualTarget_serializesAllRoutineCommands() async throws {
        var caps = Self.fullCaps
        caps.supportsAbsoluteMovement = true
        caps.supportsDisableMotors = true
        let vm = try makeViewModel(printer: idlePrinter(), capabilities: caps)
        await vm.loadCapabilities()
        await vm.setHeaterTarget(.hotend, target: 220)
        let pending = vm.pendingCommand
        mockService.setTemperaturesCalledWith = nil
        await vm.setHeaterTarget(.bed, target: 70)
        await vm.moveTo(x: 1, y: nil, z: nil, feedrateMmMin: nil)
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
