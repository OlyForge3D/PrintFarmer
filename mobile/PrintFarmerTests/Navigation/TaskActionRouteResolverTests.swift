import XCTest
@testable import PrintFarmer

/// Pure resolver proofs for F8-M2 (#788): every destination comes from stable
/// server identity (task type + entity type/id + source id + typed metadata),
/// never from titles/descriptions/display names. Fully deterministic — no
/// async, no sleeps.
final class TaskActionRouteResolverTests: XCTestCase {
    private let printerA = UUID(uuidString: "A0000000-0000-0000-0000-000000000001")!
    private let printerB = UUID(uuidString: "B0000000-0000-0000-0000-000000000002")!
    private let jobA = UUID(uuidString: "10000000-0000-0000-0000-000000000001")!
    private let alertA = UUID(uuidString: "C0000000-0000-0000-0000-000000000003")!

    // MARK: - Exact stable-ID routing (no title parsing)

    func testHarvestRoutesToMetadataJobID() {
        let task = makeTask(
            taskType: .harvestReady,
            entityType: "Printer",
            entityId: printerA.uuidString,
            title: "Harvest printer \(printerB.uuidString)", // decoy identity in title
            metadataJson: #"{"jobId":"\#(jobA.uuidString)"}"#
        )

        XCTAssertEqual(resolve(task), .success(.harvest(jobID: jobA)))
    }

    func testHarvestFallsBackToJobEntityWhenEntityIsJob() {
        let task = makeTask(
            taskType: .harvestReady,
            entityType: "Job",
            entityId: jobA.uuidString,
            metadataJson: nil
        )

        XCTAssertEqual(resolve(task), .success(.harvest(jobID: jobA)))
    }

    func testFilamentRunoutRoutesToPrinterEntity() {
        let task = makeTask(
            taskType: .filamentRunout,
            entityType: "Printer",
            entityId: printerA.uuidString,
            metadataJson: #"{"toolheadId":"T0"}"#
        )

        XCTAssertEqual(
            resolve(task),
            .success(.filamentSwap(printerID: printerA, toolheadID: "T0"))
        )
    }

    func testMaintenanceRoutesToPrinterAndAlertFromSourceId() {
        let task = makeTask(
            taskType: .maintenanceInIdleWindow,
            entityType: "Printer",
            entityId: printerA.uuidString,
            metadataJson: nil,
            sourceId: "maintenancealert:\(alertA.uuidString)"
        )

        XCTAssertEqual(
            resolve(task),
            .success(.maintenance(printerID: printerA, alertID: alertA, componentID: nil, toolheadID: nil))
        )
    }

    func testMaintenanceDueUsesMetadataComponentAndToolhead() {
        let task = makeTask(
            taskType: .maintenanceDue,
            entityType: "Printer",
            entityId: printerA.uuidString,
            metadataJson: #"{"componentId":"belt","toolheadId":"T1"}"#
        )

        XCTAssertEqual(
            resolve(task),
            .success(.maintenance(printerID: printerA, alertID: nil, componentID: "belt", toolheadID: "T1"))
        )
    }

    /// Two tasks whose display names are identical but whose stable IDs differ
    /// must resolve to different destinations — duplicate names cannot misroute.
    func testDuplicateDisplayNamesRouteByStableIDOnly() {
        let first = makeTask(
            taskType: .filamentRunout,
            entityType: "Printer",
            entityId: printerA.uuidString,
            title: "Filament runout — Voron A"
        )
        let second = makeTask(
            taskType: .filamentRunout,
            entityType: "Printer",
            entityId: printerB.uuidString,
            title: "Filament runout — Voron A" // identical display name
        )

        XCTAssertEqual(resolve(first), .success(.filamentSwap(printerID: printerA, toolheadID: nil)))
        XCTAssertEqual(resolve(second), .success(.filamentSwap(printerID: printerB, toolheadID: nil)))
    }

    // MARK: - Fail-safe (no navigation)

    func testNonActionableTypeFailsNotActionable() {
        let task = makeTask(taskType: .failureClear, entityType: "Printer", entityId: printerA.uuidString)
        XCTAssertEqual(resolve(task), .failure(.notActionable))
    }

    func testUnknownTypeFailsNotActionable() {
        let task = makeTask(taskType: .unknown("SomethingNew"), entityType: "Printer", entityId: printerA.uuidString)
        XCTAssertEqual(resolve(task), .failure(.notActionable))
    }

    func testHarvestMissingJobIdentityFailsSafe() {
        let task = makeTask(
            taskType: .harvestReady,
            entityType: "Printer",
            entityId: printerA.uuidString,
            metadataJson: nil
        )
        XCTAssertEqual(resolve(task), .failure(.missingIdentity(field: "jobId")))
    }

    func testHarvestMalformedMetadataJobIdFailsSafe() {
        let task = makeTask(
            taskType: .harvestReady,
            entityType: "Printer",
            entityId: printerA.uuidString,
            metadataJson: #"{"jobId":"not-a-uuid"}"#
        )
        XCTAssertEqual(resolve(task), .failure(.malformedIdentity(field: "jobId")))
    }

    func testMalformedMetadataJsonFailsSafe() {
        let task = makeTask(
            taskType: .filamentRunout,
            entityType: "Printer",
            entityId: printerA.uuidString,
            metadataJson: "{ this is : not json"
        )
        XCTAssertEqual(resolve(task), .failure(.malformedMetadata))
    }

    func testSwapMissingPrinterIdentityFailsSafe() {
        let task = makeTask(
            taskType: .filamentRunout,
            entityType: "Spool",
            entityId: "not-a-uuid",
            metadataJson: nil
        )
        XCTAssertEqual(resolve(task), .failure(.missingIdentity(field: "printerId")))
    }

    func testSwapMalformedPrinterEntityFailsSafe() {
        let task = makeTask(
            taskType: .filamentRunout,
            entityType: "Printer",
            entityId: "not-a-uuid",
            metadataJson: nil
        )
        XCTAssertEqual(resolve(task), .failure(.malformedIdentity(field: "entityId")))
    }

    func testMaintenanceMalformedSourceAlertFailsSafe() {
        let task = makeTask(
            taskType: .maintenanceInIdleWindow,
            entityType: "Printer",
            entityId: printerA.uuidString,
            sourceId: "maintenancealert:not-a-uuid"
        )
        XCTAssertEqual(resolve(task), .failure(.malformedIdentity(field: "sourceId")))
    }

    func testHarvestFeatureDisabledFailsSafe() {
        let task = makeTask(
            taskType: .harvestReady,
            entityType: "Job",
            entityId: jobA.uuidString
        )
        let caps = TaskActionCapabilities(harvestEnabled: false)
        XCTAssertEqual(TaskActionRouteResolver.destination(for: task, capabilities: caps), .failure(.featureDisabled))
    }

    func testSwapFeatureDisabledFailsSafe() {
        let task = makeTask(
            taskType: .filamentRunout,
            entityType: "Printer",
            entityId: printerA.uuidString
        )
        let caps = TaskActionCapabilities(guidedSwapEnabled: false)
        XCTAssertEqual(TaskActionRouteResolver.destination(for: task, capabilities: caps), .failure(.featureDisabled))
    }

    // MARK: - Helpers

    private func resolve(_ task: ShiftTask) -> Result<TaskActionDestination, TaskActionRouteError> {
        TaskActionRouteResolver.destination(for: task, capabilities: TaskActionCapabilities())
    }

    private func makeTask(
        taskType: ShiftTaskType,
        entityType: String,
        entityId: String,
        title: String = "Task",
        metadataJson: String? = nil,
        sourceId: String? = nil
    ) -> ShiftTask {
        ShiftTask(
            id: UUID().uuidString,
            taskType: taskType,
            entityType: entityType,
            entityId: entityId,
            title: title,
            description: "Detail \(entityId)", // decoy identity in detail
            status: .pending,
            priority: .high,
            createdAt: Date(timeIntervalSince1970: 1_773_000_000),
            dueAt: nil,
            completedAt: nil,
            relatedEntityCount: 1,
            metadataJson: metadataJson,
            anchorKind: .now,
            anchorAtUtc: nil,
            windowStartUtc: nil,
            windowEndUtc: nil,
            sourceKind: .unspecified,
            sourceId: sourceId
        )
    }
}
