import Foundation
import XCTest
@testable import PrintFarmer

/// Consumer-side checks for the read-only corpus produced by issue #2238.
///
/// Policy pinned here:
/// - unknown string enum tokens use each model's documented fallback;
/// - numeric enum tokens are rejected when the API contract requires strings;
/// - missing optional keys and explicit nulls decode as nil;
/// - missing required keys fail decoding;
/// - empty JSON collections decode as empty Swift collections.
final class WireContractCorpusTests: XCTestCase {
    func testAPIClientDecodesCanonicalPrinterStatusVariants() async throws {
        let populated: PrinterStatusUpdate = try await decodeThroughAPIClient(
            "api/printer-status/printerupdated.populated.json"
        )
        XCTAssertTrue(populated.isOnline)
        XCTAssertEqual(populated.state, "printing")
        XCTAssertEqual(populated.progress, 42.5)

        let minimal: PrinterStatusUpdate = try await decodeThroughAPIClient(
            "api/printer-status/printerupdated.missing-key.json"
        )
        XCTAssertFalse(minimal.isOnline)
        XCTAssertNil(minimal.state)
        XCTAssertNil(minimal.progress)
        XCTAssertNil(minimal.jobName)
    }

    func testAPIClientDecodesCanonicalQueueVariants() async throws {
        let populated: [QueueOverview] = try await decodeThroughAPIClient(
            "api/print-queue/queue.populated.json"
        )
        XCTAssertEqual(populated.count, 1)
        XCTAssertEqual(populated[0].printerName, "Wire Contract Queue Printer")
        XCTAssertEqual(populated[0].modelAliases, [])
        XCTAssertEqual(populated[0].supportedMaterials ?? [], ["PLA", "PETG"])

        let empty: [QueueOverview] = try await decodeThroughAPIClient(
            "api/print-queue/queue.empty-collection.json"
        )
        XCTAssertTrue(empty.isEmpty)
    }

    func testAPIClientDecodesCanonicalTaskVariants() async throws {
        let populated: ShiftTask = try await decodeThroughAPIClient(
            "api/tasks/tasks.populated.json"
        )
        XCTAssertEqual(populated.taskType, .custom)
        XCTAssertEqual(populated.status, .pending)
        XCTAssertEqual(populated.priority, .high)

        let additiveRequestResponse: ShiftTask = try await decodeThroughAPIClient(
            "api/tasks/tasks.unknown-additive-request-field.json"
        )
        XCTAssertEqual(additiveRequestResponse.title, "Wire contract additive field task")

        let empty: [ShiftTask] = try await decodeThroughAPIClient(
            "api/tasks/tasks.empty-collection.json"
        )
        XCTAssertEqual(empty, [])
    }

    func testAPIClientDecodesCanonicalSpoolmanInventoryVariants() async throws {
        let spools: SpoolmanPagedResult<SpoolmanSpool> = try await decodeThroughAPIClient(
            "api/inventory/spoolman-spools.populated.json"
        )
        XCTAssertEqual(spools.totalCount, 1)
        XCTAssertEqual(spools.items.first?.id, 501)
        XCTAssertEqual(spools.items.first?.filamentId, 77)
        XCTAssertNil(spools.items.first?.hasNfcTag)

        let emptySpools: SpoolmanPagedResult<SpoolmanSpool> = try await decodeThroughAPIClient(
            "api/inventory/spoolman-spools.empty-collection.json"
        )
        XCTAssertEqual(emptySpools.totalCount, 0)
        XCTAssertTrue(emptySpools.items.isEmpty)

        let filaments: SpoolmanPagedResult<SpoolmanFilament> = try await decodeThroughAPIClient(
            "api/inventory/spoolman-filaments.populated.json"
        )
        XCTAssertEqual(filaments.totalCount, 1)
        XCTAssertEqual(filaments.items.first?.gtin, "00012345678905")

        let emptyFilaments: SpoolmanPagedResult<SpoolmanFilament> = try await decodeThroughAPIClient(
            "api/inventory/spoolman-filaments.empty-collection.json"
        )
        XCTAssertEqual(emptyFilaments.totalCount, 0)
        XCTAssertTrue(emptyFilaments.items.isEmpty)

        let vendors: [SpoolmanVendor] = try await decodeThroughAPIClient(
            "api/inventory/spoolman-vendors.populated.json"
        )
        XCTAssertEqual(vendors.first?.name, "Wire Contract Vendor")
        let emptyVendors: [SpoolmanVendor] = try await decodeThroughAPIClient(
            "api/inventory/spoolman-vendors.empty-collection.json"
        )
        XCTAssertTrue(emptyVendors.isEmpty)

        let materials: [SpoolmanMaterial] = try await decodeThroughAPIClient(
            "api/inventory/spoolman-materials.populated.json"
        )
        XCTAssertEqual(materials.first?.name, "PLA")
        let emptyMaterials: [SpoolmanMaterial] = try await decodeThroughAPIClient(
            "api/inventory/spoolman-materials.empty-collection.json"
        )
        XCTAssertTrue(emptyMaterials.isEmpty)

        let availableMaterials: [String] = try await decodeThroughAPIClient(
            "api/inventory/spoolman-available-materials.populated.json"
        )
        XCTAssertEqual(availableMaterials, ["ASA", "PLA"])
        let emptyAvailableMaterials: [String] = try await decodeThroughAPIClient(
            "api/inventory/spoolman-available-materials.empty-collection.json"
        )
        XCTAssertTrue(emptyAvailableMaterials.isEmpty)
    }

    func testAPIClientDecodesCanonicalPrintedPartsInventoryVariants() async throws {
        let parts: [PartInventoryResponse] = try await decodeThroughAPIClient(
            "api/inventory/parts.populated.json"
        )
        XCTAssertEqual(parts.first?.sku, "PF-WIRE-01")
        XCTAssertEqual(parts.first?.defaultBinCode, "BIN-WIRE-01")
        XCTAssertEqual(parts.first?.needsReorder, true)
        let emptyParts: [PartInventoryResponse] = try await decodeThroughAPIClient(
            "api/inventory/parts.empty-collection.json"
        )
        XCTAssertTrue(emptyParts.isEmpty)

        let bins: [BinResponse] = try await decodeThroughAPIClient(
            "api/inventory/bins.populated.json"
        )
        XCTAssertEqual(bins.first?.code, "BIN-WIRE-01")
        let emptyBins: [BinResponse] = try await decodeThroughAPIClient(
            "api/inventory/bins.empty-collection.json"
        )
        XCTAssertTrue(emptyBins.isEmpty)

        let reorder: [ReorderCandidateResponse] = try await decodeThroughAPIClient(
            "api/inventory/reorder.populated.json"
        )
        XCTAssertEqual(reorder.first?.deficit, 3)
        let emptyReorder: [ReorderCandidateResponse] = try await decodeThroughAPIClient(
            "api/inventory/reorder.empty-collection.json"
        )
        XCTAssertTrue(emptyReorder.isEmpty)

        let mappings: [PartOutputMappingResponse] = try await decodeThroughAPIClient(
            "api/inventory/mappings.populated.json"
        )
        XCTAssertNotNil(mappings.first?.gcodeFileId)
        XCTAssertNil(mappings.first?.printProjectFileId)
        let emptyMappings: [PartOutputMappingResponse] = try await decodeThroughAPIClient(
            "api/inventory/mappings.empty-collection.json"
        )
        XCTAssertTrue(emptyMappings.isEmpty)

        let adjustment: PartAdjustmentResponse = try await decodeThroughAPIClient(
            "api/inventory/adjustment.populated.json"
        )
        XCTAssertEqual(adjustment.reason, .qcReject)
        XCTAssertEqual(adjustment.delta, -1)

        let harvest: HarvestJobResponse = try await decodeThroughAPIClient(
            "api/inventory/harvest.populated.json"
        )
        XCTAssertEqual(harvest.adjustments.first?.reason, .harvest)
        XCTAssertEqual(harvest.outputs.first?.origin, .explicitOutputs)
        XCTAssertEqual(harvest.outputs.first?.partSku, "PF-WIRE-01")
    }

    func testAPIClientDecodesCanonicalPrintedPartsInventoryConflicts() async throws {
        let wrongBin = try await decodeInventoryConflictThroughAPIClient(
            "api/inventory/harvest.wrong-bin.json"
        )
        XCTAssertTrue(wrongBin.isWrongBin)
        XCTAssertEqual(wrongBin.mismatches?.first?.partSku, "PF-WIRE-01")
        XCTAssertEqual(wrongBin.mismatches?.first?.scannedBinCode, "BIN-SCANNED")

        let mappingRequired = try await decodeInventoryConflictThroughAPIClient(
            "api/inventory/harvest.part-mapping-required.json"
        )
        XCTAssertTrue(mappingRequired.isPartMappingRequired)
        XCTAssertNotNil(mappingRequired.projectFileId)
        XCTAssertNil(mappingRequired.gcodeFileId)
        XCTAssertNotNil(mappingRequired.guidance)
    }

    func testUnknownStringEnumTokenUsesForwardCompatibleTaskFallback() async throws {
        let data = try WireContractCorpus.data("api/tasks/tasks.populated.json")
        var object = try XCTUnwrap(
            JSONSerialization.jsonObject(with: data) as? [String: Any]
        )
        object["taskType"] = "FutureTaskType"

        let task: ShiftTask = try await decodeThroughAPIClient(
            data: try JSONSerialization.data(withJSONObject: object)
        )
        XCTAssertEqual(task.taskType, .unknown("FutureTaskType"))
    }

    func testNumericEnumTokenIsRejectedWhenStringContractIsRequired() async throws {
        let data = try WireContractCorpus.data("api/tasks/tasks.populated.json")
        var object = try XCTUnwrap(
            JSONSerialization.jsonObject(with: data) as? [String: Any]
        )
        object["priority"] = 2

        do {
            let _: ShiftTask = try await decodeThroughAPIClient(
                data: try JSONSerialization.data(withJSONObject: object)
            )
            XCTFail("Numeric enum tokens must not be accepted by a string-enum contract")
        } catch NetworkError.decodingFailed(let failure) {
            XCTAssertEqual(failure.kind, "typeMismatch")
            XCTAssertTrue(failure.codingPath.contains("priority"))
        }
    }

    func testMissingAndExplicitNullPoliciesMatchCodableSemantics() async throws {
        let minimal: PrinterStatusUpdate = try await decodeThroughAPIClient(
            "api/printer-status/printerupdated.missing-key.json"
        )
        XCTAssertNil(minimal.state, "A missing optional key decodes as nil")

        let data = try WireContractCorpus.data("api/tasks/tasks.populated.json")
        var explicitNullObject = try XCTUnwrap(
            JSONSerialization.jsonObject(with: data) as? [String: Any]
        )
        explicitNullObject["description"] = NSNull()
        let explicitNullTask: ShiftTask = try await decodeThroughAPIClient(
            data: try JSONSerialization.data(withJSONObject: explicitNullObject)
        )
        XCTAssertNil(explicitNullTask.description, "An explicit null optional value decodes as nil")

        var missingRequiredObject = explicitNullObject
        missingRequiredObject.removeValue(forKey: "title")
        do {
            let _: ShiftTask = try await decodeThroughAPIClient(
                data: try JSONSerialization.data(withJSONObject: missingRequiredObject)
            )
            XCTFail("Missing required keys must fail decoding")
        } catch NetworkError.decodingFailed(let failure) {
            XCTAssertEqual(failure.kind, "keyNotFound")
            XCTAssertTrue(failure.codingPath.contains("title"))
        }
    }

    #if DEBUG
    func testSignalRServiceDecodesCanonicalPrinterUpdatedPayload() throws {
        let service = SignalRService(
            serverURL: try XCTUnwrap(URL(string: "http://signalr.test")),
            tokenProvider: { nil }
        )
        let recorder = WireEventRecorder()
        let subscription = service.onPrinterUpdated { recorder.record(printerID: $0.id) }
        defer { subscription.cancel() }

        let fixture = try WireContractCorpus.data(
            "api/printer-status/printerupdated.populated.json"
        )
        service.processIncomingDataForTesting(
            try signalRInvocation(target: "printerupdated", payload: fixture)
        )
        service.drainHubCoordinatorForTesting()

        XCTAssertEqual(
            recorder.printerIDs,
            [UUID(uuidString: "ca24825a-7d8c-49e9-9c99-f795b242474f")!]
        )
    }

    func testSignalRServiceAcceptsCanonicalTaskEventPayloadsAsInvalidations() throws {
        let service = SignalRService(
            serverURL: try XCTUnwrap(URL(string: "http://signalr.test")),
            tokenProvider: { nil }
        )
        let recorder = WireEventRecorder()
        let subscription = service.onTaskInvalidated { recorder.record(taskTarget: $0.target) }
        defer { subscription.cancel() }

        for (target, path) in [
            ("taskcreated", "api/signalr-events/taskcreated.populated.json"),
            ("taskupdated", "api/signalr-events/taskupdated.completed.json")
        ] {
            service.processIncomingDataForTesting(
                try signalRInvocation(
                    target: target,
                    payload: WireContractCorpus.data(path)
                )
            )
        }
        service.drainHubCoordinatorForTesting()

        XCTAssertEqual(recorder.taskTargets, ["taskcreated", "taskupdated"])
    }
    #endif

    func testSignalRParserPreservesPascalCaseSlicerRegistrationException() throws {
        let fixture = try WireContractCorpus.data(
            "api/signalr-events/SlicerRegistered.populated.json"
        )
        let frame = try signalRInvocation(
            target: "SlicerRegistered",
            payload: fixture,
            terminate: false
        )

        guard case .invocation(let target, let firstArgument) = SignalRProtocolMessage.decode(frame) else {
            return XCTFail("Expected a SignalR invocation")
        }
        XCTAssertEqual(target, "SlicerRegistered")

        let registration = try signalRDecoder().decode(
            CanonicalSlicerRegistration.self,
            from: try XCTUnwrap(firstArgument)
        )
        XCTAssertEqual(registration.slicerType, 1)
        XCTAssertEqual(registration.status, "Online")
        XCTAssertEqual(registration.maxConcurrentJobs, 1)
    }

    private func decodeThroughAPIClient<T: Decodable & Sendable>(
        _ relativePath: String
    ) async throws -> T {
        try await decodeThroughAPIClient(data: WireContractCorpus.data(relativePath))
    }

    private func decodeThroughAPIClient<T: Decodable & Sendable>(
        data: Data
    ) async throws -> T {
        let mock = MockAPIClient()
        mock.requestHandler = { request in
            (TestData.httpResponse(url: request.url, statusCode: 200), data)
        }
        return try await mock.apiClient.get("/api/wire-contract-corpus")
    }

    private func decodeInventoryConflictThroughAPIClient(
        _ relativePath: String
    ) async throws -> PartsInventoryConflict {
        let data = try WireContractCorpus.data(relativePath)
        let mock = MockAPIClient()
        mock.requestHandler = { request in
            (TestData.httpResponse(url: request.url, statusCode: 409), data)
        }

        var decodedConflict: PartsInventoryConflict?
        do {
            let _: HarvestJobResponse = try await mock.apiClient.get("/api/wire-contract-corpus")
            XCTFail("Expected APIClient to surface a typed printed-parts inventory conflict")
        } catch NetworkError.partsInventoryConflict(let conflict) {
            decodedConflict = conflict
        } catch {
            throw error
        }
        return try XCTUnwrap(decodedConflict)
    }

    private func signalRInvocation(
        target: String,
        payload: Data,
        terminate: Bool = true
    ) throws -> Data {
        let argument = try JSONSerialization.jsonObject(with: payload)
        var data = try JSONSerialization.data(
            withJSONObject: [
                "type": 1,
                "target": target,
                "arguments": [argument]
            ]
        )
        if terminate {
            data.append(SignalRFrameParser.recordSeparator)
        }
        return data
    }

    private func signalRDecoder() -> JSONDecoder {
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .custom { decoder in
            let container = try decoder.singleValueContainer()
            let text = try container.decode(String.self)
            if let date = APIClient.iso8601WithFractional.date(from: text) {
                return date
            }
            if let date = APIClient.iso8601Plain.date(from: text) {
                return date
            }
            throw DecodingError.dataCorruptedError(
                in: container,
                debugDescription: "Cannot decode date string: \(text)"
            )
        }
        return decoder
    }
}

private enum WireContractCorpus {
    private static let root = URL(fileURLWithPath: #filePath)
        .deletingLastPathComponent()
        .deletingLastPathComponent()
        .deletingLastPathComponent()
        .deletingLastPathComponent()
        .appendingPathComponent("fixtures/wire-contracts", isDirectory: true)

    static func data(_ relativePath: String) throws -> Data {
        try Data(contentsOf: root.appendingPathComponent(relativePath))
    }
}

private struct CanonicalSlicerRegistration: Decodable, Sendable {
    let id: UUID
    let name: String
    let slicerType: Int
    let version: String
    let maxConcurrentJobs: Int
    let status: String
    let lastSeen: Date
}

private final class WireEventRecorder: @unchecked Sendable {
    private let lock = NSLock()
    private var storedPrinterIDs: [UUID] = []
    private var storedTaskTargets: [String] = []

    var printerIDs: [UUID] {
        lock.lock()
        defer { lock.unlock() }
        return storedPrinterIDs
    }

    var taskTargets: [String] {
        lock.lock()
        defer { lock.unlock() }
        return storedTaskTargets
    }

    func record(printerID: UUID) {
        lock.lock()
        storedPrinterIDs.append(printerID)
        lock.unlock()
    }

    func record(taskTarget: String) {
        lock.lock()
        storedTaskTargets.append(taskTarget)
        lock.unlock()
    }
}
