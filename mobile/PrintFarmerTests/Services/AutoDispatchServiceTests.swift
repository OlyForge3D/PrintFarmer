import XCTest
@testable import PrintFarmer

final class AutoDispatchServiceTests: XCTestCase {
    private var mockAPIClient: MockAPIClient!
    private var service: AutoDispatchService!
    private let printerId = UUID()
    private let jobId = UUID()

    override func setUp() {
        super.setUp()
        mockAPIClient = MockAPIClient()
        service = AutoDispatchService(apiClient: mockAPIClient.apiClient)
    }

    override func tearDown() {
        service = nil
        mockAPIClient = nil
        super.tearDown()
    }

    func testCalibrationReadyAcceptedUsesExactJobAndReviewedHeaders() async throws {
        mockAPIClient.stubResponse(
            json: """
            {"message":"accepted","jobETag":"job-v2","dispatchStateETag":"dispatch-v2"}
            """,
            statusCode: 202
        )
        let status = calibrationStatus()

        let result = try await service.markReady(status: status)

        XCTAssertEqual(result.acknowledgementOutcome, .accepted)
        let request = try XCTUnwrap(mockAPIClient.capturedRequests.last)
        XCTAssertEqual(
            request.url?.path,
            "/api/job-queue/\(jobId)/acknowledge-bed-clear-and-start"
        )
        XCTAssertEqual(request.value(forHTTPHeaderField: "If-Match"), "\"job-v1\"")
        XCTAssertEqual(
            request.value(forHTTPHeaderField: "X-Dispatch-State-If-Match"),
            "\"dispatch-v1\""
        )
        XCTAssertNotNil(request.value(forHTTPHeaderField: "Idempotency-Key"))
    }

    func testCalibrationReadyReplayIsTyped() async throws {
        mockAPIClient.stubResponse(
            json: """
            {"message":"replayed","jobETag":"job-v2","dispatchStateETag":"dispatch-v2"}
            """,
            statusCode: 200
        )

        let result = try await service.markReady(status: calibrationStatus())

        XCTAssertEqual(result.acknowledgementOutcome, .replayed)
    }

    func testCalibrationReadyConflictIsTyped() async {
        await assertFailure(
            statusCode: 409,
            expectedDescription: "Conflict detail."
        ) { error in
            if case .conflict(let code, _) = error {
                XCTAssertEqual(code, "printer_busy")
            } else {
                XCTFail("Expected conflict, got \(error)")
            }
        }
    }

    func testCalibrationReadyStaleIsTypedAndRequiresReview() async {
        await assertFailure(
            statusCode: 412,
            expectedDescription: "Stale detail."
        ) { error in
            XCTAssertTrue(error.requiresReview)
            if case .stale(let code, _) = error {
                XCTAssertEqual(code, "dispatch_revision_conflict")
            } else {
                XCTFail("Expected stale, got \(error)")
            }
        }
    }

    func testCalibrationReadyIncompatibleIsTyped() async {
        await assertFailure(
            statusCode: 422,
            expectedDescription: "Incompatible detail."
        ) { error in
            if case .incompatible(let code, _) = error {
                XCTAssertEqual(code, "calibration_job_incompatible")
            } else {
                XCTFail("Expected incompatible, got \(error)")
            }
        }
    }

    func testCalibrationReadyPreconditionIsTypedAndRequiresReview() async {
        await assertFailure(
            statusCode: 428,
            expectedDescription: "Precondition detail."
        ) { error in
            XCTAssertTrue(error.requiresReview)
            if case .preconditionRequired(let code, _) = error {
                XCTAssertEqual(code, "precondition_required")
            } else {
                XCTFail("Expected precondition-required, got \(error)")
            }
        }
    }

    func testCalibrationReadyUnavailableIsTyped() async {
        await assertFailure(
            statusCode: 503,
            expectedDescription: "Unavailable detail."
        ) { error in
            if case .unavailable(let code, _) = error {
                XCTAssertEqual(code, "printer_offline_or_stale")
            } else {
                XCTFail("Expected unavailable, got \(error)")
            }
        }
    }

    private func assertFailure(
        statusCode: Int,
        expectedDescription: String,
        assertion: (BedClearAcknowledgementError) -> Void
    ) async {
        let code: String
        let detail: String
        switch statusCode {
        case 409:
            code = "printer_busy"
            detail = "Conflict detail."
        case 412:
            code = "dispatch_revision_conflict"
            detail = "Stale detail."
        case 422:
            code = "calibration_job_incompatible"
            detail = "Incompatible detail."
        case 428:
            code = "precondition_required"
            detail = "Precondition detail."
        default:
            code = "printer_offline_or_stale"
            detail = "Unavailable detail."
        }
        mockAPIClient.stubResponse(
            json: """
            {"error":"\(code)","detail":"\(detail)"}
            """,
            statusCode: statusCode
        )

        do {
            _ = try await service.markReady(status: calibrationStatus())
            XCTFail("Expected HTTP \(statusCode) failure")
        } catch let error as BedClearAcknowledgementError {
            XCTAssertEqual(error.localizedDescription, expectedDescription)
            assertion(error)
        } catch {
            XCTFail("Expected typed acknowledgement error, got \(error)")
        }
    }

    private func calibrationStatus() -> AutoDispatchStatus {
        AutoDispatchStatus(
            printerId: printerId,
            enabled: true,
            queueDepth: 1,
            state: "PendingReady",
            dispatchStateETag: "dispatch-v1",
            nextJobId: jobId,
            nextJobName: "Calibration",
            nextJobETag: "job-v1",
            nextJobKind: "FilamentCalibration",
            nextJobPrinterConfigRevision: 9
        )
    }
}
