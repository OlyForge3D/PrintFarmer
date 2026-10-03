import XCTest
@testable import PrintFarmer

final class JobServiceTests: XCTestCase {
    private var mockAPIClient: MockAPIClient!
    private var service: JobService!
    private let jobId = UUID()

    override func setUp() {
        super.setUp()
        mockAPIClient = MockAPIClient()
        service = JobService(apiClient: mockAPIClient.apiClient)
    }

    override func tearDown() {
        service = nil
        mockAPIClient = nil
        super.tearDown()
    }

    func testPrinterQueueUsesScopedEndpointAndPreservesServerOrderWithConflictingTimestamps() async throws {
        let printerId = UUID()
        let firstId = UUID()
        let secondId = UUID()
        let entries = [
            (firstId, 2, "2026-10-01T00:00:00Z"),
            (secondId, 1, "2026-10-02T00:00:00Z")
        ].map { id, position, timestamp in
            """
            {"job":{"id":"\(id)","name":"Scoped job","assignedPrinterId":"\(printerId)",
            "status":"Queued","priority":"Normal","queuePosition":\(position),
            "queuedAtUtc":"\(timestamp)","createdAtUtc":"\(timestamp)",
            "rowVersion":"revision-\(position)","copies":1,"completedCopies":0,"remainingCopies":1}}
            """
        }
        mockAPIClient.stubResponse(json: "[\(entries.joined(separator: ","))]")

        let jobs = try await service.listPrinterQueue(printerId: printerId)

        XCTAssertEqual(jobs.map(\.id), [firstId.uuidString, secondId.uuidString])
        XCTAssertEqual(jobs.map(\.job.queuePosition), [2, 1])
        let request = try XCTUnwrap(mockAPIClient.capturedRequests.last)
        XCTAssertEqual(request.url?.path, "/api/job-queue-analytics/printer/\(printerId)")
        XCTAssertEqual(request.url?.query, "limit=200")
        XCTAssertEqual(request.httpMethod, "GET")
    }

    func testDispatchAcceptedUsesReviewedETagAndTypedBody() async throws {
        stubDispatch(statusCode: 200, outcome: "Accepted")

        let result = try await service.dispatch(
            id: jobId,
            reviewedRowVersion: "job-v1"
        )

        guard case .accepted(let response) = result else {
            return XCTFail("Expected accepted dispatch")
        }
        XCTAssertEqual(response.dispatchResult?.outcome, .accepted)
        let request = try XCTUnwrap(mockAPIClient.capturedRequests.last)
        XCTAssertEqual(
            request.url?.path,
            "/api/job-queue/\(jobId)/dispatch"
        )
        XCTAssertEqual(
            request.value(forHTTPHeaderField: "If-Match"),
            "\"job-v1\""
        )
    }

    func testDispatchUnknownReturnsReconciliation() async throws {
        stubDispatch(statusCode: 202, outcome: "Unknown")

        let result = try await service.dispatch(
            id: jobId,
            reviewedRowVersion: "job-v1"
        )

        guard case .reconciliation(let response) = result else {
            return XCTFail("Expected reconciliation dispatch")
        }
        XCTAssertTrue(
            response.dispatchResult?.requiresReconciliation == true
        )
    }

    func testDispatchConflictDecodesRejectedBody() async throws {
        stubDispatch(statusCode: 409, outcome: "Rejected")

        let result = try await service.dispatch(
            id: jobId,
            reviewedRowVersion: "job-v1"
        )

        guard case .rejected(let response) = result else {
            return XCTFail("Expected rejected dispatch")
        }
        XCTAssertEqual(
            response.dispatchResult?.errorCode,
            "printer_busy"
        )
    }

    func testGetHydratesLatestAttemptFromAuthoritativeRecoveryBody() async throws {
        let attemptB = UUID()
        mockAPIClient.stubResponse(
            json: """
            {
              "id": "\(jobId)",
              "rowVersion": "job-v2",
              "status": "Starting",
              "priority": "Urgent",
              "queuePosition": 1,
              "gcodeFileName": "calibration.gcode",
              "copies": 1,
              "completedCopies": 0,
              "remainingCopies": 1,
              "dispatchResult": {
                "attemptId": "\(attemptB)",
                "attemptNumber": 2,
                "outcome": "Unknown",
                "errorCode": null,
                "errorDetail": null,
                "isRetryable": false,
                "requiresReconciliation": true,
                "jobRevision": "job-v2",
                "dispatchStateRevision": "dispatch-v2"
              }
            }
            """
        )

        let recovered = try await service.get(id: jobId)

        XCTAssertEqual(recovered.dispatchResult?.attemptId, attemptB)
        XCTAssertEqual(recovered.dispatchResult?.attemptNumber, 2)
        XCTAssertEqual(recovered.dispatchResult?.outcome, .unknown)
        XCTAssertEqual(
            mockAPIClient.capturedRequests.last?.url?.path,
            "/api/job-queue/\(jobId)"
        )
    }

    private func stubDispatch(statusCode: Int, outcome: String) {
        let requiresReconciliation = outcome == "Unknown"
        mockAPIClient.stubResponse(
            json: """
            {
              "id": "\(jobId)",
              "rowVersion": "job-v2",
              "status": "\(outcome == "Accepted" ? "Printing" : "Starting")",
              "dispatchResult": {
                "attemptId": "\(UUID())",
                "attemptNumber": 2,
                "outcome": "\(outcome)",
                "errorCode": \(outcome == "Rejected" ? "\"printer_busy\"" : "null"),
                "errorDetail": \(outcome == "Rejected" ? "\"Printer busy.\"" : "null"),
                "isRetryable": false,
                "requiresReconciliation": \(requiresReconciliation),
                "jobRevision": "job-v2",
                "dispatchStateRevision": "dispatch-v2"
              }
            }
            """,
            statusCode: statusCode
        )
    }
}
