import Foundation
import XCTest
@testable import PrintFarmer

final class ShiftTaskModelsServiceTests: XCTestCase {
    private var mockAPIClient: MockAPIClient!
    private var service: ShiftTaskService!

    override func setUp() {
        super.setUp()
        mockAPIClient = MockAPIClient()
        service = ShiftTaskService(apiClient: mockAPIClient.apiClient)
    }

    override func tearDown() {
        service = nil
        mockAPIClient = nil
        super.tearDown()
    }

    func testEveryShippedWireTokenDecodesAndRoundTrips() throws {
        try assertWireValues(
            [
                "None", "ProfileImport", "MaintenanceDue", "FirmwareUpdate",
                "CalibrationNeeded", "Custom", "FailureClear", "HarvestReady",
                "FilamentRunout", "MaintenanceInIdleWindow", "SpoolRestock",
                "PrintedPartRestock",
            ],
            as: ShiftTaskType.self,
            wireValue: \.wireValue
        )
        try assertWireValues(
            ["Pending", "InProgress", "Completed", "Dismissed", "Skipped"],
            as: ShiftTaskStatus.self,
            wireValue: \.wireValue
        )
        try assertWireValues(
            ["Low", "Normal", "High"],
            as: ShiftTaskPriority.self,
            wireValue: \.wireValue
        )
        try assertWireValues(
            ["unspecified", "now", "at", "window", "anytimeToday", "timeline"],
            as: ShiftTaskAnchorKind.self,
            wireValue: \.wireValue
        )
        try assertWireValues(
            [
                "unspecified", "attention", "failureIncident", "harvest",
                "filamentCoverage", "maintenance", "spoolReorder",
                "printedPartStock",
            ],
            as: ShiftTaskSourceKind.self,
            wireValue: \.wireValue
        )
    }

    func testUnknownTaskAnchorAndSourceRemainRenderable() throws {
        let taskType = try JSONDecoder().decode(
            ShiftTaskType.self,
            from: Data(#""FutureTaskKind""#.utf8)
        )
        let anchor = try JSONDecoder().decode(
            ShiftTaskAnchorKind.self,
            from: Data(#""afterLunch""#.utf8)
        )
        let source = try JSONDecoder().decode(
            ShiftTaskSourceKind.self,
            from: Data(#""robotCell""#.utf8)
        )

        XCTAssertEqual(taskType, .unknown("FutureTaskKind"))
        XCTAssertEqual(anchor, .unknown("afterLunch"))
        XCTAssertEqual(source, .unknown("robotCell"))
        XCTAssertEqual(taskType.displayName, "Task")
        XCTAssertFalse(taskType.supportsKnownLifecycleActions)
        XCTAssertEqual(anchor.groupTitle, "Other Tasks")
        XCTAssertEqual(source.displayName, "Other source")
    }

    func testIntegerEnumPayloadsAreRejected() {
        for type in [
            ShiftTaskType.self as any Decodable.Type,
            ShiftTaskStatus.self,
            ShiftTaskPriority.self,
            ShiftTaskAnchorKind.self,
            ShiftTaskSourceKind.self,
        ] {
            XCTAssertThrowsError(
                try JSONDecoder().decode(type, from: Data("1".utf8)),
                "\(type) must reject integer wire values"
            )
        }
    }

    func testGroupedAndFlatCamelCasePayloadsDecode() throws {
        let decoder = makeDecoder()
        let taskJSON = makeTaskJSON()
        let grouped = """
        {
          "groups": [
            { "anchorKind": "now", "tasks": [\(taskJSON)] }
          ],
          "generatedAt": "2026-03-08T09:30:00Z"
        }
        """
        let flat = "[\(taskJSON)]"

        let plan = try decoder.decode(ShiftPlan.self, from: Data(grouped.utf8))
        let tasks = try decoder.decode([ShiftTask].self, from: Data(flat.utf8))

        XCTAssertEqual(plan.groups.map(\.anchorKind), [.now])
        XCTAssertEqual(plan.groups.first?.tasks.first?.sourceKind, .harvest)
        XCTAssertEqual(tasks.first?.taskType, .harvestReady)
        XCTAssertEqual(tasks.first?.priority, .high)
    }

    func testGroupedLoadUsesExactShiftEndpoint() async throws {
        mockAPIClient.stubResponse(
            json: """
            { "groups": [], "generatedAt": "2026-03-08T09:30:00Z" }
            """
        )

        let snapshot = try await service.loadSnapshot(shiftPlanEnabled: true)

        XCTAssertEqual(snapshot.mode, .grouped)
        let request = try XCTUnwrap(mockAPIClient.capturedRequests.first)
        XCTAssertEqual(request.httpMethod, "GET")
        XCTAssertEqual(request.url?.path, "/api/tasks")
        XCTAssertEqual(request.url?.query, "view=shift")
    }

    func testFeatureDisabled404LoadsFlatCompatibilityAsCapabilityState() async throws {
        mockAPIClient.requestHandler = { request in
            if request.url?.query == "view=shift" {
                return (
                    TestData.httpResponse(url: request.url, statusCode: 404),
                    Data("""
                    {
                      "title": "Feature Disabled",
                      "status": 404,
                      "detail": "Shift plan disabled.",
                      "code": "featureDisabled"
                    }
                    """.utf8)
                )
            }
            return (
                TestData.httpResponse(url: request.url, statusCode: 200),
                Data("[\(self.makeTaskJSON())]".utf8)
            )
        }

        let snapshot = try await service.loadSnapshot(shiftPlanEnabled: true)

        XCTAssertEqual(snapshot.mode, .featureDisabled)
        XCTAssertEqual(snapshot.taskCount, 1)
        XCTAssertEqual(mockAPIClient.capturedRequests.count, 2)
    }

    func testFeatureDisabledFlatFailureIsVisibleInsteadOfFalseEmptySuccess() async throws {
        mockAPIClient.requestHandler = { request in
            if request.url?.query == "view=shift" {
                return (
                    TestData.httpResponse(url: request.url, statusCode: 404),
                    Data("""
                    {
                      "title": "Feature Disabled",
                      "status": 404,
                      "detail": "Shift plan disabled.",
                      "code": "featureDisabled"
                    }
                    """.utf8)
                )
            }
            return (
                TestData.httpResponse(url: request.url, statusCode: 500),
                Data()
            )
        }

        let snapshot = try await service.loadSnapshot(shiftPlanEnabled: true)

        XCTAssertEqual(snapshot.mode, .featureDisabled)
        XCTAssertEqual(snapshot.taskCount, 0)
        XCTAssertEqual(snapshot.compatibilityErrorMessage, "Server error (500)")
        XCTAssertEqual(mockAPIClient.capturedRequests.count, 2)
    }

    func testLegacy404FallsBackToFlatEndpoint() async throws {
        mockAPIClient.requestHandler = { request in
            if request.url?.query == "view=shift" {
                return (
                    TestData.httpResponse(url: request.url, statusCode: 404),
                    Data()
                )
            }
            return (
                TestData.httpResponse(url: request.url, statusCode: 200),
                Data("[\(self.makeTaskJSON())]".utf8)
            )
        }

        let snapshot = try await service.loadSnapshot(shiftPlanEnabled: true)

        XCTAssertEqual(snapshot.mode, .legacyFallback)
        XCTAssertEqual(snapshot.taskCount, 1)
        XCTAssertEqual(mockAPIClient.capturedRequests.map(\.url?.query), ["view=shift", nil])
    }

    func testMutationEndpointsEncodeIDAndCompleteHeaderExactly() async throws {
        mockAPIClient.stubEmptySuccess()
        let taskID = "harvest:782/task"
        let key = "782-complete-intent"

        try await service.complete(taskID: taskID, idempotencyKey: key)
        try await service.skip(taskID: taskID)
        try await service.dismiss(taskID: taskID)

        XCTAssertEqual(mockAPIClient.capturedRequests.count, 3)
        let complete = mockAPIClient.capturedRequests[0]
        XCTAssertEqual(complete.httpMethod, "POST")
        XCTAssertTrue(
            try XCTUnwrap(complete.url?.absoluteString)
                .contains("harvest%3A782%2Ftask/complete")
        )
        XCTAssertEqual(complete.value(forHTTPHeaderField: "Idempotency-Key"), key)
        XCTAssertTrue(
            try XCTUnwrap(mockAPIClient.capturedRequests[1].url?.absoluteString)
                .contains("harvest%3A782%2Ftask/skip")
        )
        XCTAssertTrue(
            try XCTUnwrap(mockAPIClient.capturedRequests[2].url?.absoluteString)
                .contains("harvest%3A782%2Ftask/dismiss")
        )
        XCTAssertNil(
            mockAPIClient.capturedRequests[1]
                .value(forHTTPHeaderField: "Idempotency-Key")
        )
    }

    private func assertWireValues<Value: Decodable>(
        _ values: [String],
        as type: Value.Type,
        wireValue: KeyPath<Value, String>
    ) throws {
        for value in values {
            let decoded = try JSONDecoder().decode(
                type,
                from: Data("\"\(value)\"".utf8)
            )
            XCTAssertEqual(decoded[keyPath: wireValue], value)
        }
    }

    private func makeDecoder() -> JSONDecoder {
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .custom { decoder in
            let container = try decoder.singleValueContainer()
            let value = try container.decode(String.self)
            if let date = APIClient.iso8601WithFractional.date(from: value) {
                return date
            }
            if let date = APIClient.iso8601Plain.date(from: value) {
                return date
            }
            throw DecodingError.dataCorruptedError(
                in: container,
                debugDescription: "Invalid test date"
            )
        }
        return decoder
    }

    private func makeTaskJSON() -> String {
        """
        {
          "id": "78200000-0000-0000-0000-000000000001",
          "taskType": "HarvestReady",
          "entityType": "Job",
          "entityId": "30000000-0003-0000-0000-000000000001",
          "title": "Harvest",
          "description": "Remove plate",
          "status": "Pending",
          "priority": "High",
          "createdAt": "2026-03-08T09:00:00Z",
          "dueAt": null,
          "completedAt": null,
          "relatedEntityCount": 1,
          "metadataJson": null,
          "anchorKind": "now",
          "anchorAtUtc": null,
          "windowStartUtc": null,
          "windowEndUtc": null,
          "sourceKind": "harvest",
          "sourceId": "harvest:proof"
        }
        """
    }
}
