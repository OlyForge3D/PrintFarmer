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

    func testGetPreservesRowVersionForConditionalRetry() async throws {
        mockAPIClient.stubResponse(
            json: """
            {
                "id": "\(jobId)",
                "rowVersion": "failed-job-v3",
                "status": "Failed",
                "priority": "Normal",
                "queuePosition": 0,
                "gcodeFileName": "failed-job.gcode",
                "copies": 1,
                "completedCopies": 0,
                "remainingCopies": 1
            }
            """
        )

        let job = try await service.get(id: jobId)

        XCTAssertEqual(job.rowVersion, "failed-job-v3")
        XCTAssertEqual(
            mockAPIClient.capturedRequests.last?.url?.path,
            "/api/job-queue/\(jobId)"
        )
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
            {"id":"\(id)","name":"Scoped job","assignedPrinterId":"\(printerId)",
            "status":"Queued","priority":"Normal","queuePosition":\(position),
            "queuedAtUtc":"\(timestamp)","createdAtUtc":"\(timestamp)",
            "rowVersion":"revision-\(position)","jobKind":"Standard","toolRequirements":[],
            "updatedAtUtc":"\(timestamp)","wasSeededFromHistory":false,
            "copies":1,"completedCopies":0,"remainingCopies":1,"toolheadUsages":[]}
            """
        }
        mockAPIClient.stubResponse(json: "[\(entries.joined(separator: ","))]")

        let jobs = try await service.listPrinterQueue(printerId: printerId)

        XCTAssertEqual(jobs.map(\.id), [firstId.uuidString, secondId.uuidString])
        XCTAssertEqual(jobs.map(\.job.queuePosition), [2, 1])
        XCTAssertEqual(jobs.map(\.job.rowVersion), ["revision-2", "revision-1"])
        XCTAssertEqual(jobs.map(\.job.name), ["Scoped job", "Scoped job"])
        XCTAssertTrue(jobs.allSatisfy { $0.gcodeFile == nil && $0.assignedPrinter == nil })
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

    @MainActor
    func testFlatBackendPrinterQueueLoadsDetailAndDispatchesFirstWaitingRow() async throws {
            let printerService = MockPrinterService()
            var printer = try TestData.decodePrinter()
            printer.isOnline = true
            printer.state = "idle"
            printerService.printerToReturn = printer
            let firstId = UUID(uuidString: "11111111-1111-1111-1111-111111111111")!
            let secondId = UUID(uuidString: "33333333-3333-3333-3333-333333333333")!
            // QueuedPrintJobDto's camelCase/null-omitting shape, not the
            // cross-scope analytics {job,gcodeFile,assignedPrinter} response.
            let waitingRows = """
            {"id":"\(firstId)","rowVersion":"AQID","name":"queue-fixture.gcode",
            "assignedPrinterId":"\(printer.id)","jobKind":"Standard","status":"Queued",
            "priority":"Normal","queuePosition":10,"toolRequirements":[],
            "createdAtUtc":"2026-10-03T19:00:00Z","updatedAtUtc":"2026-10-03T19:00:00Z",
            "queuedAtUtc":"2026-10-03T19:00:00Z","wasSeededFromHistory":false,
            "copies":1,"completedCopies":0,"remainingCopies":1,"toolheadUsages":[]},
            {"id":"\(secondId)","rowVersion":"BAUG","name":"later-fixture.gcode",
            "assignedPrinterId":"\(printer.id)","jobKind":"Standard","status":"Queued",
            "priority":"Normal","queuePosition":1,"toolRequirements":[],
            "createdAtUtc":"2026-10-03T20:00:00Z","updatedAtUtc":"2026-10-03T20:00:00Z",
            "queuedAtUtc":"2026-10-03T20:00:00Z","wasSeededFromHistory":false,
            "copies":1,"completedCopies":0,"remainingCopies":1,"toolheadUsages":[]}
            """
            let occupyingRows = ["Starting", "Printing", "Paused", "Assigned"].map { status in
                """
                {"id":"\(UUID())","rowVersion":"active-revision","name":"\(status)-fixture.gcode",
                "assignedPrinterId":"\(printer.id)","jobKind":"Standard","status":"\(status)",
                "priority":"Normal","queuePosition":0,"toolRequirements":[],
                "createdAtUtc":"2026-10-03T18:00:00Z","updatedAtUtc":"2026-10-03T18:00:00Z",
                "queuedAtUtc":"2026-10-03T18:00:00Z","wasSeededFromHistory":false,
                "copies":1,"completedCopies":0,"remainingCopies":1,"toolheadUsages":[]}
                """
            }
            let json = "[\(occupyingRows.joined(separator: ",")),\(waitingRows)]"
            let dispatchBody = """
            {"id":"\(firstId)","rowVersion":"AQIE","status":"Starting",
            "dispatchResult":{"attemptId":"\(UUID())","attemptNumber":1,"outcome":"Accepted",
            "isRetryable":false,"requiresReconciliation":false,"jobRevision":"AQIE",
            "dispatchStateRevision":"printer-revision"}}
            """
            mockAPIClient.stubResponses([
                "/api/job-queue-analytics/printer/": (200, json),
                "/api/job-queue/\(firstId)/dispatch": (200, dispatchBody)
            ])
            let vm = PrinterDetailViewModel(printerId: printer.id)
            vm.configure(printerService: printerService)
            vm.configureOperatorServices(jobService: service, maintenanceService: MockMaintenanceService())
            defer { vm.stopSnapshotPolling() }

            await vm.loadPrinter()

            XCTAssertEqual(vm.nextQueuedJobs.map(\.id), [firstId.uuidString, secondId.uuidString])
            XCTAssertEqual(vm.displayedQueueJobs.map(\.job.status), ["Starting", "Printing", "Paused", "Assigned", "Queued", "Queued"])
            let head = try XCTUnwrap(vm.nextQueuedJobs.first)
            XCTAssertEqual(head.job.name, "queue-fixture.gcode")
            XCTAssertEqual(head.job.rowVersion, "AQID")
            XCTAssertNil(head.gcodeFile)
            await vm.startNextJob(head)
            XCTAssertNil(vm.dispatchError)

            let dispatch = try XCTUnwrap(mockAPIClient.capturedRequests.first {
                $0.httpMethod == "POST" && $0.url?.path.hasSuffix("/dispatch") == true
            })
            XCTAssertEqual(dispatch.url?.path, "/api/job-queue/\(firstId)/dispatch")
            XCTAssertEqual(dispatch.value(forHTTPHeaderField: "If-Match"), "\"AQID\"")
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

    func testMoveQueuedJobEncodesBeforeNeighborAndDecodesFlatResponse() async throws {
        mockAPIClient.stubResponse(
            json: """
            {"id":"\(jobId)","rowVersion":"AQIDBA==","status":"Queued","priority":"High","queuePosition":2}
            """
        )

        let response = try await service.moveQueuedJob(
            id: jobId,
            reviewedRowVersion: "moved-etag",
            neighbor: .before(id: UUID(), rowVersion: "neighbor-etag")
        )

        XCTAssertEqual(response.id, jobId.uuidString)
        XCTAssertEqual(response.rowVersion, "AQIDBA==")
        let request = try XCTUnwrap(mockAPIClient.capturedRequests.last)
        XCTAssertEqual(request.httpMethod, "PUT")
        XCTAssertEqual(request.url?.path, "/api/job-queue/jobs/\(jobId)/position")
        XCTAssertEqual(request.value(forHTTPHeaderField: "If-Match"), "\"moved-etag\"")
        let body = try XCTUnwrap(
            JSONSerialization.jsonObject(with: try XCTUnwrap(request.capturedHTTPBody())) as? [String: String]
        )
        XCTAssertEqual(Set(body.keys), ["beforeJobId", "beforeJobETag"])
        XCTAssertEqual(body["beforeJobETag"], "neighbor-etag")
    }

    func testMoveQueuedJobEncodesAfterNeighborWithoutBeforeFields() async throws {
        let neighborID = UUID()
        mockAPIClient.stubResponse(
            json: """
            {"id":"\(jobId)","rowVersion":"AQIDBA=="}
            """
        )

        _ = try await service.moveQueuedJob(
            id: jobId,
            reviewedRowVersion: "moved-etag",
            neighbor: .after(id: neighborID, rowVersion: "neighbor-etag")
        )

        let request = try XCTUnwrap(mockAPIClient.capturedRequests.last)
        let body = try XCTUnwrap(
            JSONSerialization.jsonObject(with: try XCTUnwrap(request.capturedHTTPBody())) as? [String: String]
        )
        XCTAssertEqual(Set(body.keys), ["afterJobId", "afterJobETag"])
        XCTAssertEqual(body["afterJobId"], neighborID.uuidString)
        XCTAssertEqual(body["afterJobETag"], "neighbor-etag")
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

final class JobAnalyticsServiceTests: XCTestCase {
    func testHistoryRequestsSmallNewestFailedPage() async throws {
        let mockAPIClient = MockAPIClient()
        mockAPIClient.stubResponse(
            json: """
            {"entries":[],"totalCount":0,"currentPage":1,"pageSize":5,"stats":null}
            """
        )
        let service = JobAnalyticsService(apiClient: mockAPIClient.apiClient)

        let page = try await service.getHistory(
            limit: 5,
            offset: 0,
            sortBy: "newest",
            statuses: "failed",
            dateStart: nil,
            dateEnd: nil
        )

        XCTAssertTrue(page.entries.isEmpty)
        let request = try XCTUnwrap(mockAPIClient.capturedRequests.last)
        XCTAssertEqual(request.httpMethod, "GET")
        XCTAssertEqual(request.url?.path, "/api/job-queue-analytics/history")
        XCTAssertEqual(request.url?.query, "limit=5&offset=0&sortBy=newest&statuses=failed")
    }

    func testDemoHistoryFiltersToRecentFailures() async throws {
        let service = DemoJobAnalyticsService()

        let page = try await service.getHistory(
            limit: 5,
            offset: 0,
            sortBy: "newest",
            statuses: "failed",
            dateStart: nil,
            dateEnd: nil
        )

        XCTAssertEqual(page.entries.map(\.status), ["Failed"])
        XCTAssertEqual(page.entries.map(\.jobName), ["vase_mode_spiral.gcode"])
    }
}
