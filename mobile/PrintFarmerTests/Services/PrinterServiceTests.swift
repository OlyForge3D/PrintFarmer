import XCTest
@testable import PrintFarmer

enum ControlOperationTestJSON {
    static let operationId = UUID(uuidString: "10000000-0000-0000-0000-000000000001")!

    static func operation(
        printerId: UUID = TestData.testUUID,
        kind: String = "HomeAll",
        state: String = "Queued",
        barrierHeld: Bool = true,
        evidence: String = "None",
        x: Double? = nil,
        y: Double? = 0,
        z: Double? = nil,
        f: Double? = 1200,
        requiresRecovery: Bool = false
    ) -> String {
        """
        {
          "operationId":"\(operationId)","printerId":"\(printerId)","kind":"\(kind)",
          "x":\(x.map { String($0) } ?? "null"),"y":\(y.map { String($0) } ?? "null"),
          "z":\(z.map { String($0) } ?? "null"),"f":\(f.map { String($0) } ?? "null"),
          "state":"\(state)","rowVersion":"opaque-r1",
          "createdAtUtc":"2026-09-12T17:00:00.1234567Z",
          "updatedAtUtc":"2026-09-12T17:00:01Z","startedAtUtc":null,"completedAtUtc":null,
          "barrierHeld":\(barrierHeld),"requiresRecovery":\(requiresRecovery),
          "completionEvidence":"\(evidence)","failure":null,"senderIsolation":"NotRequested"
        }
        """
    }

    static let unlockedProjection = """
    {"supportedOperations":["HomeAll","HomeXY","HomeZ","Jog","MoveTo"],
     "barrierHeld":false,"requiresRecovery":false,"operationId":null,"state":null}
    """
}

/// Tests for PrinterService: verifies correct endpoints, HTTP methods,
/// and error propagation. Now includes individual command endpoints.
final class PrinterServiceTests: XCTestCase {

    func testControlOperationAllKindsUse202AndCallerNonceWithoutLegacyFallback() async throws {
        for kind in PrinterControlOperationKind.allCases {
            mockAPIClient.reset()
            let intent: PrinterControlOperationRequest
            switch kind {
            case .homeAll, .homeXY, .homeZ:
                intent = .init(kind: kind)
            case .jog:
                intent = .init(kind: kind, x: 1, f: 1200)
            case .moveTo:
                intent = .init(kind: kind, x: 10, y: 0, z: 5, f: 1200)
            }
            mockAPIClient.stubResponse(json: ControlOperationTestJSON.operation(
                kind: kind.rawValue, x: intent.x, y: intent.y, z: intent.z, f: intent.f
            ), statusCode: 202)
            let operation = try await printerService.submitControlOperation(
                printerId: TestData.testUUID, operationId: ControlOperationTestJSON.operationId,
                request: intent
            )
            XCTAssertEqual(operation.state, .queued)
            XCTAssertFalse(operation.isSafelyComplete)
            XCTAssertEqual(operation.y, intent.y)
            XCTAssertNil(operation.startedAtUtc)
            XCTAssertEqual(mockAPIClient.capturedRequests.count, 1)
            let sent = try XCTUnwrap(mockAPIClient.capturedRequests.first)
            XCTAssertEqual(sent.httpMethod, "POST")
            XCTAssertEqual(sent.url?.path, "/api/printers/\(TestData.testUUID)/control-operations")
            XCTAssertEqual(sent.value(forHTTPHeaderField: "Idempotency-Key"), ControlOperationTestJSON.operationId.uuidString)
            let body = try XCTUnwrap(sent.capturedHTTPBody())
            let request = try JSONDecoder().decode(PrinterControlOperationRequest.self, from: body)
            XCTAssertEqual(request, intent)
        }
    }

    func testControlOperationReadsAreUncachedAuthoritativeEvidence() async throws {
        mockAPIClient.stubResponse(json: ControlOperationTestJSON.operation(
            state: "Succeeded", barrierHeld: false, evidence: "MotionQueueDrained"
        ))
        let result = try await printerService.getControlOperation(
            printerId: TestData.testUUID, operationId: ControlOperationTestJSON.operationId
        )
        XCTAssertTrue(result.isSafelyComplete)
        let sent = try XCTUnwrap(mockAPIClient.capturedRequests.first)
        XCTAssertEqual(sent.httpMethod, "GET")
        XCTAssertEqual(sent.url?.path, "/api/printers/\(TestData.testUUID)/control-operations/\(ControlOperationTestJSON.operationId)")
        XCTAssertEqual(sent.cachePolicy, .reloadIgnoringLocalAndRemoteCacheData)
        XCTAssertEqual(sent.value(forHTTPHeaderField: "Cache-Control"), "no-store")
        XCTAssertNil(sent.value(forHTTPHeaderField: "Idempotency-Key"))
    }

    func testHTTP200TerminalReplayRequiresCanonicalReadWithoutIssuingAnotherCommand() async throws {
        mockAPIClient.stubResponse(json: ControlOperationTestJSON.operation(
            state: "Succeeded", barrierHeld: false, evidence: "MotionQueueDrained"
        ), statusCode: 200)
        let record = try await printerService.submitControlOperation(
            printerId: TestData.testUUID, operationId: ControlOperationTestJSON.operationId,
            request: .init(kind: .homeAll)
        )
        // A durable replay may already be terminal. The service preserves the
        // record, not a CommandResult; the view model must confirm via GET.
        XCTAssertEqual(record.operationId, ControlOperationTestJSON.operationId)
        XCTAssertEqual(record.state, .succeeded)
        XCTAssertEqual(mockAPIClient.capturedRequests.count, 1)
        let confirmed = try await printerService.getControlOperation(
            printerId: TestData.testUUID, operationId: ControlOperationTestJSON.operationId
        )
        XCTAssertEqual(confirmed, record)
        XCTAssertEqual(mockAPIClient.capturedRequests.map(\.httpMethod), ["POST", "GET"])
        XCTAssertEqual(mockAPIClient.capturedRequests.first?.value(forHTTPHeaderField: "Idempotency-Key"),
                       ControlOperationTestJSON.operationId.uuidString)
    }

    func testLostSubmissionCanBeResolvedByReadUsingSameOperationID() async throws {
        mockAPIClient.stubError(.timedOut)
        do {
            _ = try await printerService.submitControlOperation(
                printerId: TestData.testUUID, operationId: ControlOperationTestJSON.operationId,
                request: .init(kind: .homeAll)
            )
            XCTFail("Expected transport timeout")
        } catch NetworkError.timeout { }
        mockAPIClient.stubResponse(json: ControlOperationTestJSON.operation(state: "Running"))
        let record = try await printerService.getControlOperation(
            printerId: TestData.testUUID, operationId: ControlOperationTestJSON.operationId
        )
        XCTAssertEqual(record.state, .running)
        XCTAssertTrue(record.barrierHeld)
        XCTAssertEqual(mockAPIClient.capturedRequests.map(\.httpMethod), ["POST", "GET"])
    }

    func testDeliberateResumePreservesUUIDAndOriginalIntentAfterAmbiguousAdmission() async throws {
        let intent = PrinterControlOperationRequest(kind: .moveTo, x: 10, y: 0, z: 5, f: 1200)
        for alreadyAdmitted in [false, true] {
            mockAPIClient.reset()
            mockAPIClient.stubError(alreadyAdmitted ? .networkConnectionLost : .cannotConnectToHost)
            do {
                _ = try await printerService.submitControlOperation(
                    printerId: TestData.testUUID, operationId: ControlOperationTestJSON.operationId,
                    request: intent
                )
                XCTFail("Expected ambiguous submission failure")
            } catch { }
            XCTAssertEqual(mockAPIClient.capturedRequests.count, 1, "No automatic admission retry")

            // This second call represents explicit operator confirmation, not
            // transport replay. The server owns the exactly-once physical send.
            let receipt = ControlOperationTestJSON.operation(
                kind: "MoveTo", state: alreadyAdmitted ? "Succeeded" : "Queued",
                barrierHeld: !alreadyAdmitted,
                evidence: alreadyAdmitted ? "MotionQueueDrained" : "None",
                x: intent.x, y: intent.y, z: intent.z, f: intent.f
            )
            mockAPIClient.stubResponse(json: receipt, statusCode: alreadyAdmitted ? 200 : 202)
            let resumed = try await printerService.submitControlOperation(
                printerId: TestData.testUUID, operationId: ControlOperationTestJSON.operationId,
                request: intent
            )
            XCTAssertEqual(resumed.operationId, ControlOperationTestJSON.operationId)
            let submissions = mockAPIClient.capturedRequests
            XCTAssertEqual(submissions.count, 2)
            for sent in submissions {
                XCTAssertEqual(sent.httpMethod, "POST")
                XCTAssertEqual(sent.url?.path, "/api/printers/\(TestData.testUUID)/control-operations")
                XCTAssertEqual(sent.value(forHTTPHeaderField: "Idempotency-Key"), ControlOperationTestJSON.operationId.uuidString)
                let body = try XCTUnwrap(sent.capturedHTTPBody())
                XCTAssertEqual(try JSONDecoder().decode(PrinterControlOperationRequest.self, from: body), intent)
            }

            mockAPIClient.stubResponse(json: receipt)
            let confirmed = try await printerService.getControlOperation(
                printerId: TestData.testUUID, operationId: ControlOperationTestJSON.operationId
            )
            XCTAssertEqual(confirmed, resumed)
            XCTAssertEqual(mockAPIClient.capturedRequests.map(\.httpMethod), ["POST", "POST", "GET"])
        }
    }

    func testCurrentControlOperationRequiresExplicitConsistentProjection() async throws {
        mockAPIClient.stubResponse(json: """
        {"physicalControl":\(ControlOperationTestJSON.unlockedProjection),"operation":null}
        """)
        let result = try await printerService.getCurrentControlOperation(printerId: TestData.testUUID)
        XCTAssertTrue(result.physicalControl.isExplicitlyUnlocked)
        XCTAssertNil(result.operation)
        XCTAssertEqual(mockAPIClient.capturedRequests.first?.url?.path,
                       "/api/printers/\(TestData.testUUID)/control-operations/current")
        mockAPIClient.stubResponse(json: """
        {"physicalControl":\(ControlOperationTestJSON.unlockedProjection),"operation":\(ControlOperationTestJSON.operation())}
        """)
        do {
            _ = try await printerService.getCurrentControlOperation(printerId: TestData.testUUID)
            XCTFail("Contradictory current evidence must not unlock")
        } catch PrinterControlOperationError.invalidResponse { }
    }

    func testCurrentDoesNotExposeCompletedRecordAsBarrierOwner() async {
        mockAPIClient.stubResponse(json: """
        {
          "physicalControl": {
            "supportedOperations":["HomeAll"],"barrierHeld":false,"requiresRecovery":false,
            "operationId":"\(ControlOperationTestJSON.operationId)","state":"Succeeded"
          },
          "operation":\(ControlOperationTestJSON.operation(state: "Succeeded", barrierHeld: false, evidence: "MotionQueueDrained"))
        }
        """)
        do {
            _ = try await printerService.getCurrentControlOperation(printerId: TestData.testUUID)
            XCTFail("Current contains only barrier ownership, not terminal history")
        } catch { }
    }

    func testCurrentPreservesUnrelatedPhysicalBarrierWithoutOperationOwner() async throws {
        for supported in [#"["HomeAll","HomeXY","HomeZ","Jog","MoveTo"]"#, "[]"] {
            mockAPIClient.stubResponse(json: """
            {
              "physicalControl":{
                "supportedOperations":\(supported),"barrierHeld":true,"requiresRecovery":true,
                "operationId":null,"state":null
              },
              "operation":null
            }
            """)
            let current = try await printerService.getCurrentControlOperation(printerId: TestData.testUUID)
            XCTAssertNil(current.operation)
            XCTAssertTrue(current.physicalControl.barrierHeld)
            XCTAssertTrue(current.physicalControl.requiresRecovery)
            XCTAssertFalse(current.physicalControl.isExplicitlyUnlocked)
        }
    }

    func testMissingCurrentIsAmbiguousNotDefinitiveUpgradeEvidence() async {
        mockAPIClient.stubResponse(json: "{}", statusCode: 404)
        do {
            _ = try await printerService.getCurrentControlOperation(printerId: TestData.testUUID)
            XCTFail("Missing current must not synthesize an unlocked record")
        } catch PrinterControlOperationError.problem(let status, _, let message) {
            XCTAssertEqual(status, 404)
            XCTAssertTrue(message?.contains("unavailable, inaccessible, or unsupported") == true)
        } catch {
            XCTFail("404 must not definitively classify server incompatibility: \(error)")
        }
        XCTAssertEqual(mockAPIClient.capturedRequests.count, 1)
    }

    func testControlOperationRejectsWrongIdentityMalformedEnumsAndFalseCompletion() async throws {
        let good = ControlOperationTestJSON.operation()
        for json in [
            ControlOperationTestJSON.operation(printerId: UUID()),
            good.replacingOccurrences(of: ControlOperationTestJSON.operationId.uuidString, with: UUID().uuidString),
            ControlOperationTestJSON.operation(state: "FutureState"),
            ControlOperationTestJSON.operation(kind: "FutureKind"),
            ControlOperationTestJSON.operation(state: "Running", barrierHeld: false),
            ControlOperationTestJSON.operation(state: "Succeeded", barrierHeld: false),
            good.replacingOccurrences(of: "\"opaque-r1\"", with: "\"\""),
            good.replacingOccurrences(of: "\"requiresRecovery\":false,", with: ""),
            good.replacingOccurrences(of: "\"senderIsolation\":\"NotRequested\"", with: "\"senderIsolation\":7"),
            good.replacingOccurrences(of: "2026-09-12T17:00:00.1234567Z", with: "invalid-date")
        ] {
            mockAPIClient.reset()
            mockAPIClient.stubResponse(json: json)
            do {
                _ = try await printerService.getControlOperation(
                    printerId: TestData.testUUID, operationId: ControlOperationTestJSON.operationId
                )
                XCTFail("Expected fail-closed response rejection")
            } catch { }
            XCTAssertEqual(mockAPIClient.capturedRequests.count, 1)
        }
    }

    func testControlOperationSubmissionRejectsUnrelatedSuccessStatusesEvenWithTerminalBody() async {
        for status in [201, 204, 206] {
            mockAPIClient.reset()
            mockAPIClient.stubResponse(json: ControlOperationTestJSON.operation(
                state: "Succeeded", barrierHeld: false, evidence: "MotionQueueDrained"
            ), statusCode: status)
            do {
                _ = try await printerService.submitControlOperation(
                    printerId: TestData.testUUID, operationId: ControlOperationTestJSON.operationId,
                    request: .init(kind: .homeAll)
                )
                XCTFail("Only 202 acceptance or 200 terminal replay is the submission contract")
            } catch { }
            XCTAssertEqual(mockAPIClient.capturedRequests.count, 1)
        }
    }

    func testControlOperationSubmissionRejectsHTTP200UnresolvedReceipt() async {
        mockAPIClient.stubResponse(json: ControlOperationTestJSON.operation(), statusCode: 200)
        do {
            _ = try await printerService.submitControlOperation(
                printerId: TestData.testUUID, operationId: ControlOperationTestJSON.operationId,
                request: .init(kind: .homeAll)
            )
            XCTFail("HTTP 200 submission is reserved for a terminal replay")
        } catch PrinterControlOperationError.invalidResponse { }
        catch { XCTFail("Unexpected error: \(error)") }
        XCTAssertEqual(mockAPIClient.capturedRequests.count, 1)
    }

    func testControlOperationSubmissionRejectsHTTP202TerminalReceiptUntilCanonicalRead() async throws {
        let receipt = ControlOperationTestJSON.operation(
            state: "Succeeded", barrierHeld: false, evidence: "MotionQueueDrained", y: nil, f: nil
        )
        mockAPIClient.stubResponse(json: receipt, statusCode: 202)
        var admission: PrinterControlOperation?
        do {
            admission = try await printerService.submitControlOperation(
                printerId: TestData.testUUID, operationId: ControlOperationTestJSON.operationId,
                request: .init(kind: .homeAll)
            )
            XCTFail("HTTP 202 must not return a terminal admission receipt")
        } catch PrinterControlOperationError.invalidResponse { }
        XCTAssertNil(admission, "An invalid status/state pair must not publish an admission record")
        XCTAssertEqual(mockAPIClient.capturedRequests.count, 1)

        mockAPIClient.stubResponse(json: receipt)
        let confirmed = try await printerService.getControlOperation(
            printerId: TestData.testUUID, operationId: ControlOperationTestJSON.operationId
        )
        XCTAssertTrue(confirmed.isSafelyComplete)
        XCTAssertEqual(confirmed.operationId, ControlOperationTestJSON.operationId)
        XCTAssertEqual(mockAPIClient.capturedRequests.map(\.httpMethod), ["POST", "GET"])
    }

    func testControlOperationSubmissionStatusStateMatrix() async throws {
        let states: [(state: PrinterControlOperationState, evidence: PrinterControlCompletionEvidence, terminal: Bool)] = [
            (.queued, .none, false),
            (.running, .none, false),
            (.unknown, .none, false),
            (.recovering, .none, false),
            (.succeeded, .motionQueueDrained, true),
            (.failed, .notSent, true),
            (.recovered, .operatorVerifiedRecovery, true)
        ]
        let intent = PrinterControlOperationRequest(kind: .homeAll)
        for status in [200, 202, 201, 204, 206, 299] {
            for (state, evidence, terminal) in states {
                mockAPIClient.reset()
                let shouldAccept = status == (terminal ? 200 : 202)
                let context = "HTTP \(status), state \(state.rawValue)"
                mockAPIClient.stubResponse(json: ControlOperationTestJSON.operation(
                    state: state.rawValue, barrierHeld: !terminal,
                    evidence: evidence.rawValue, y: nil, f: nil,
                    requiresRecovery: state == .unknown || state == .recovering
                ), statusCode: status)
                var admission: PrinterControlOperation?
                do {
                    admission = try await printerService.submitControlOperation(
                        printerId: TestData.testUUID, operationId: ControlOperationTestJSON.operationId,
                        request: intent
                    )
                    XCTAssertTrue(shouldAccept, context)
                } catch PrinterControlOperationError.invalidResponse {
                    XCTAssertFalse(shouldAccept, context)
                } catch {
                    XCTFail("Unexpected error for \(context): \(error)")
                }
                if shouldAccept {
                    XCTAssertEqual(admission?.state, state, context)
                    XCTAssertEqual(admission?.operationId, ControlOperationTestJSON.operationId, context)
                } else {
                    XCTAssertNil(admission, context)
                }
                XCTAssertEqual(mockAPIClient.capturedRequests.count, 1, context)
                let sent = try XCTUnwrap(mockAPIClient.capturedRequests.first)
                XCTAssertEqual(sent.httpMethod, "POST", context)
                XCTAssertEqual(sent.value(forHTTPHeaderField: "Idempotency-Key"),
                               ControlOperationTestJSON.operationId.uuidString, context)
                let body = try XCTUnwrap(sent.capturedHTTPBody())
                XCTAssertEqual(try JSONDecoder().decode(PrinterControlOperationRequest.self, from: body), intent, context)
            }
        }
    }

    func testControlOperationProblemCodePreservedAndOldServerNeverFallsBack() async {
        for status in [409, 412, 428, 404, 405, 501] {
            mockAPIClient.reset()
            mockAPIClient.stubResponse(json: """
            {"code":"async_control_required","detail":"Use durable controls"}
            """, statusCode: status)
            do {
                _ = try await printerService.submitControlOperation(
                    printerId: TestData.testUUID, operationId: ControlOperationTestJSON.operationId,
                    request: .init(kind: .homeAll)
                )
                XCTFail("Expected error")
            } catch PrinterControlOperationError.problem(let actual, let code, let message) {
                XCTAssertEqual(actual, status)
                XCTAssertEqual(code, "async_control_required")
                XCTAssertEqual(message, "Use durable controls")
            } catch PrinterControlOperationError.updateRequired {
                XCTAssertTrue([405, 501].contains(status))
            } catch { XCTFail("Unexpected error \(error)") }
            XCTAssertEqual(mockAPIClient.capturedRequests.count, 1)
        }
    }

    func testLostSubmissionAndCancellationNeverRetryOrFallBack() async {
        for code in [URLError.Code.timedOut, .networkConnectionLost, .cancelled] {
            mockAPIClient.reset()
            mockAPIClient.stubError(code)
            do {
                _ = try await printerService.submitControlOperation(
                    printerId: TestData.testUUID, operationId: ControlOperationTestJSON.operationId,
                    request: .init(kind: .homeAll)
                )
                XCTFail("Transport failure must not fabricate completion")
            } catch { }
            XCTAssertEqual(mockAPIClient.capturedRequests.count, 1)
        }
    }

    func testAlreadyCancelledSubmissionDoesNotSend() async {
        let gate = AsyncBarrier()
        defer { gate.close() }
        let service = printerService!
        let submission = Task {
            await gate.arriveAndWait()
            return try await service.submitControlOperation(
                printerId: TestData.testUUID, operationId: ControlOperationTestJSON.operationId,
                request: .init(kind: .homeAll)
            )
        }
        await gate.waitUntilArrived()
        submission.cancel()
        gate.release()
        do {
            _ = try await submission.value
            XCTFail("Cancelled submission must throw")
        } catch { }
        XCTAssertTrue(mockAPIClient.capturedRequests.isEmpty)
    }

    private var mockAPIClient: MockAPIClient!
    private var apiClient: APIClient!
    private var printerService: PrinterService!

    override func setUp() {
        super.setUp()
        mockAPIClient = MockAPIClient()
        apiClient = mockAPIClient.apiClient
        printerService = PrinterService(apiClient: apiClient)
    }

    override func tearDown() {
        apiClient = nil
        printerService = nil
        mockAPIClient = nil
        super.tearDown()
    }

    func testSharedSafetyEnvelopesDecodeThroughActualEndpoints() async throws {
        let date = "2026-09-09T12:00:00.1234567Z"
        let operation = """
        {"support":"Supported","source":"test:verified-operation","observedAtUtc":"\(date)"}
        """
        let unknown = """
        {"state":"Unknown","value":null,"source":"test:unknown","observedAtUtc":"\(date)"}
        """
        let capabilityJSON = """
        {
          "printerId":"\(TestData.testUUID)","supportsExtrusion":true,
          "verifiedSafety":{
            "contractVersion":1,
            "discovery":{"state":"Partial","observedAtUtc":"\(date)","sourceRevision":"0"},
            "operations":{
              "absoluteMovement":\(operation),"firmwareZOffsetSave":\(operation),
              "filamentLoad":\(operation),"filamentUnload":\(operation),"filamentChange":\(operation)
            },
            "extrusion":{"minimumSafeMeasuredHotendTemperatureC":{
              "state":"Verified","value":205,"source":"test:material-policy","observedAtUtc":"\(date)"
            }},
            "positioning":{
              "coordinateOriginMm":\(unknown),"travelEnvelopeMm":\(unknown),"minimumClearanceZMm":\(unknown)
            }
          }
        }
        """
        let missingScalar = """
        {"value":null,"observedAtUtc":null,"staleAfterSeconds":15,"source":null}
        """
        mockAPIClient.stubResponses([
            "backend-capabilities": (statusCode: 200, json: capabilityJSON),
            "/status": (statusCode: 200, json: """
            {
              "id":"\(TestData.testUUID)","isOnline":true,"state":"ready",
              "safetyTelemetry":{
                "measuredHotendTemperatureC":{"value":220,"observedAtUtc":"\(date)","staleAfterSeconds":15,"source":"test:hotend"},
                "targetHotendTemperatureC":\(missingScalar),
                "homedAxes":{"value":["x","y","z"],"observedAtUtc":"\(date)","staleAfterSeconds":15,"source":"test:homed"},
                "coordinateOriginOffsetMm":{"value":{"x":10,"y":20,"z":1},"observedAtUtc":"\(date)","staleAfterSeconds":15,"source":"test:frame"}
              }
            }
            """)
        ])
        let caps = try await printerService.getBackendCapabilities(printerId: TestData.testUUID)
        let status = try await printerService.getStatus(id: TestData.testUUID)
        XCTAssertEqual(caps.verifiedSafety?.contractVersion, 1)
        XCTAssertEqual(caps.verifiedSafety?.discovery.state, .partial)
        XCTAssertEqual(caps.verifiedSafety?.operations.filamentUnload.support, .supported)
        XCTAssertEqual(caps.verifiedSafety?.extrusion.minimumSafeMeasuredHotendTemperatureC.value, 205)
        XCTAssertEqual(caps.verifiedSafety?.positioning.coordinateOriginMm.state, .unknown)
        XCTAssertEqual(status.safetyTelemetry?.measuredHotendTemperatureC.value, 220)
        XCTAssertEqual(status.safetyTelemetry?.homedAxes.value, ["x", "y", "z"])
        XCTAssertEqual(status.safetyTelemetry?.coordinateOriginOffsetMm.value?.z, 1)
        XCTAssertNotNil(status.safetyTelemetry?.measuredHotendTemperatureC.observedAtUtc)
        XCTAssertEqual(mockAPIClient.capturedRequests.map(\.httpMethod), ["GET", "GET"])
        XCTAssertEqual(mockAPIClient.capturedRequests.last?.url?.path, "/api/printers/\(TestData.testUUID)/status")
    }

    // MARK: - list()

    func testListPrintersCallsCorrectEndpoint() async throws {
        mockAPIClient.stubResponse(json: TestJSON.printerArray)

        let printers = try await printerService.list()

        XCTAssertEqual(printers.count, 2)

        let captured = mockAPIClient.capturedRequests.first
        XCTAssertEqual(captured?.httpMethod, "GET")
        XCTAssertTrue(captured?.url?.path.contains("/api/printers") ?? false)
        XCTAssertFalse(captured?.url?.absoluteString.contains("includeDisabled") ?? true)
    }

    func testListPrintersIncludeDisabled() async throws {
        mockAPIClient.stubResponse(json: TestJSON.printerArray)

        _ = try await printerService.list(includeDisabled: true)

        let captured = mockAPIClient.capturedRequests.first
        XCTAssertTrue(captured?.url?.absoluteString.contains("includeDisabled=true") ?? false)
    }

    func testListPrintersReturnsEmptyArray() async throws {
        mockAPIClient.stubResponse(json: "[]")

        let printers = try await printerService.list()

        XCTAssertEqual(printers.count, 0)
    }

    func testListPrintersThrowsOnNetworkError() async {
        mockAPIClient.stubError(.notConnectedToInternet)

        do {
            _ = try await printerService.list()
            XCTFail("Expected error")
        } catch let error as NetworkError {
            if case .noConnection = error { } else {
                XCTFail("Expected .noConnection, got \(error)")
            }
        } catch {
            XCTFail("Unexpected error: \(error)")
        }
    }

    // MARK: - get()

    func testGetPrinterCallsCorrectEndpoint() async throws {
        mockAPIClient.stubResponse(json: TestJSON.printer)

        let printer = try await printerService.get(id: TestData.testUUID)

        XCTAssertEqual(printer.id, TestData.testUUID)
        XCTAssertEqual(printer.name, "Prusa MK4")

        let captured = mockAPIClient.capturedRequests.first
        XCTAssertEqual(captured?.httpMethod, "GET")
        XCTAssertTrue(captured?.url?.path.contains("/api/printers/\(TestData.testUUID)") ?? false)
    }

    func testGetPrinterThrows404WhenNotFound() async {
        mockAPIClient.stubResponse(json: "{}", statusCode: 404)

        do {
            _ = try await printerService.get(id: TestData.testUUID)
            XCTFail("Expected NetworkError.notFound")
        } catch let error as NetworkError {
            if case .notFound = error { } else {
                XCTFail("Expected .notFound, got \(error)")
            }
        } catch {
            XCTFail("Unexpected error: \(error)")
        }
    }

    // MARK: - update()

    func testUpdatePrinterCallsCorrectEndpoint() async throws {
        mockAPIClient.stubResponse(json: TestJSON.printer)

        let request = UpdatePrinterRequest(name: "Renamed MK4")
        _ = try await printerService.update(
            id: TestData.testUUID,
            request,
            reviewedRowVersion: "printer-v1"
        )

        let captured = mockAPIClient.capturedRequests.first
        XCTAssertEqual(captured?.httpMethod, "PUT")
        XCTAssertTrue(captured?.url?.path.contains("/api/printers/\(TestData.testUUID)") ?? false)
        XCTAssertEqual(
            captured?.value(forHTTPHeaderField: "If-Match"),
            "\"printer-v1\""
        )
        // URLSession/URLProtocol may relocate the request body onto an httpBodyStream
        // when the request is passed through the mock protocol. `capturedHTTPBody()`
        // reads whichever the runtime chose so the assertion is stable.
        XCTAssertNotNil(captured?.capturedHTTPBody())
    }

    // MARK: - delete()

    func testDeletePrinterCallsCorrectEndpoint() async throws {
        mockAPIClient.stubEmptySuccess()

        try await printerService.delete(id: TestData.testUUID)

        let captured = mockAPIClient.capturedRequests.first
        XCTAssertEqual(captured?.httpMethod, "DELETE")
        XCTAssertTrue(captured?.url?.path.contains("/api/printers/\(TestData.testUUID)") ?? false)
    }

    // MARK: - setMaintenanceMode()

    func testSetMaintenanceModeCallsCorrectEndpoint() async throws {
        mockAPIClient.stubResponse(json: TestJSON.printer)

        _ = try await printerService.setMaintenanceMode(
            id: TestData.testUUID,
            inMaintenance: true,
            reviewedRowVersion: "printer-v1"
        )

        let captured = mockAPIClient.capturedRequests.first
        XCTAssertEqual(captured?.httpMethod, "PUT")
        XCTAssertTrue(captured?.url?.path.contains("/api/printers/\(TestData.testUUID)/maintenance") ?? false)
        XCTAssertEqual(
            captured?.value(forHTTPHeaderField: "If-Match"),
            "\"printer-v1\""
        )
    }

    // MARK: - Individual Command Endpoints

    func testPauseCallsCorrectEndpoint() async throws {
        mockAPIClient.stubResponse(json: TestJSON.commandSuccess)

        let result = try await printerService.pause(id: TestData.testUUID)

        XCTAssertTrue(result.success)
        let captured = mockAPIClient.capturedRequests.first
        XCTAssertEqual(captured?.httpMethod, "POST")
        XCTAssertTrue(captured?.url?.path.contains("/api/printers/\(TestData.testUUID)/pause") ?? false)
    }

    func testResumeCallsCorrectEndpoint() async throws {
        mockAPIClient.stubResponse(json: TestJSON.commandSuccess)

        let result = try await printerService.resume(id: TestData.testUUID)

        XCTAssertTrue(result.success)
        let captured = mockAPIClient.capturedRequests.first
        XCTAssertTrue(captured?.url?.path.contains("/api/printers/\(TestData.testUUID)/resume") ?? false)
    }

    func testCancelCallsCorrectEndpoint() async throws {
        mockAPIClient.stubResponse(json: TestJSON.commandSuccess)

        let result = try await printerService.cancel(id: TestData.testUUID)

        XCTAssertTrue(result.success)
        let captured = mockAPIClient.capturedRequests.first
        XCTAssertTrue(captured?.url?.path.contains("/api/printers/\(TestData.testUUID)/cancel") ?? false)
    }

    func testStopCallsCorrectEndpoint() async throws {
        mockAPIClient.stubResponse(json: TestJSON.commandSuccess)

        let result = try await printerService.stop(id: TestData.testUUID)

        XCTAssertTrue(result.success)
        let captured = mockAPIClient.capturedRequests.first
        XCTAssertTrue(captured?.url?.path.contains("/api/printers/\(TestData.testUUID)/stop") ?? false)
    }

    func testEmergencyStopCallsCorrectEndpoint() async throws {
        mockAPIClient.stubResponse(json: TestJSON.commandSuccess)

        let result = try await printerService.emergencyStop(id: TestData.testUUID)

        XCTAssertTrue(result.success)
        let captured = mockAPIClient.capturedRequests.first
        XCTAssertTrue(captured?.url?.path.contains("/api/printers/\(TestData.testUUID)/emergency-stop") ?? false)
    }

    // MARK: - getStatus()

    func testGetStatusCallsCorrectEndpoint() async throws {
        let statusJSON = """
        {
            "id": "\(TestData.testUUID)",
            "isOnline": true,
            "state": "printing",
            "progress": 55.0,
            "hotendTemp": 215.0,
            "bedTemp": 60.0
        }
        """
        mockAPIClient.stubResponse(json: statusJSON)

        let status = try await printerService.getStatus(id: TestData.testUUID)

        XCTAssertEqual(status.state, "printing")
        XCTAssertEqual(status.progress, 55.0)
        let captured = mockAPIClient.capturedRequests.first
        XCTAssertTrue(captured?.url?.path.contains("/api/printers/\(TestData.testUUID)/status") ?? false)
    }

    // MARK: - Camera URLs

    func testListCameraUrlsCallsCorrectEndpoint() async throws {
        mockAPIClient.stubResponse(json: TestJSON.printerCameraUrls)

        let cameras = try await printerService.listCameraUrls()

        XCTAssertEqual(cameras.count, 2)
        XCTAssertEqual(cameras[1].cameraAccessMode, .snapshotOnly)
        XCTAssertEqual(cameras[1].cameraSnapshotStrategy, .snapmakerU1MonitorJpeg)

        let captured = mockAPIClient.capturedRequests.first
        XCTAssertEqual(captured?.httpMethod, "GET")
        XCTAssertTrue(captured?.url?.path.hasSuffix("/api/printers/camera-urls") ?? false)
    }

    func testGetCameraUrlCallsCorrectEndpoint() async throws {
        mockAPIClient.stubResponse(json: TestJSON.printerCameraUrl)

        let camera = try await printerService.getCameraUrl(id: TestData.testUUID)

        XCTAssertEqual(camera.accessMode, .snapshotOnly)
        XCTAssertEqual(camera.snapshotStrategy, .snapmakerU1MonitorJpeg)

        let captured = mockAPIClient.capturedRequests.first
        XCTAssertEqual(captured?.httpMethod, "GET")
        XCTAssertTrue(captured?.url?.path.hasSuffix("/api/printers/\(TestData.testUUID)/camera/url") ?? false)
    }

    // MARK: - Command Error Handling

    func testCommandThrowsOnServerError() async {
        mockAPIClient.stubResponse(json: "{}", statusCode: 500)

        do {
            _ = try await printerService.pause(id: TestData.testUUID)
            XCTFail("Expected error")
        } catch let error as NetworkError {
            if case .serverError(let code) = error {
                XCTAssertEqual(code, 500)
            } else {
                XCTFail("Expected .serverError, got \(error)")
            }
        } catch {
            XCTFail("Unexpected error: \(error)")
        }
    }

    func testCommandThrowsWhenPrinterOffline() async {
        mockAPIClient.stubError(.cannotConnectToHost)

        do {
            _ = try await printerService.pause(id: TestData.testUUID)
            XCTFail("Expected error")
        } catch let error as NetworkError {
            if case .serverUnreachable = error { } else {
                XCTFail("Expected .serverUnreachable, got \(error)")
            }
        } catch {
            XCTFail("Unexpected error: \(error)")
        }
    }

    // MARK: - setTemperatures()

    func testSetTemperaturesPostsBothFields() async throws {
        mockAPIClient.stubResponse(json: TestJSON.commandSuccess)

        try await printerService.setTemperatures(printerId: TestData.testUUID, hotend: 215, bed: 60)

        let captured = mockAPIClient.capturedRequests.first
        XCTAssertEqual(captured?.httpMethod, "POST")
        XCTAssertTrue(captured?.url?.path.contains("/api/printers/\(TestData.testUUID)/temps") ?? false)

        let body = captured?.capturedHTTPBody()
        XCTAssertNotNil(body)
        let json = try JSONSerialization.jsonObject(with: body ?? Data()) as? [String: Any]
        XCTAssertEqual(json?["hotend"] as? Double, 215)
        XCTAssertEqual(json?["bed"] as? Double, 60)
    }

    func testSetTemperaturesOmitsNilFields() async throws {
        mockAPIClient.stubResponse(json: TestJSON.commandSuccess)

        try await printerService.setTemperatures(printerId: TestData.testUUID, hotend: 200, bed: nil)

        let captured = mockAPIClient.capturedRequests.first
        let body = captured?.capturedHTTPBody()
        let json = try JSONSerialization.jsonObject(with: body ?? Data()) as? [String: Any]
        XCTAssertEqual(json?["hotend"] as? Double, 200)
        XCTAssertNil(json?["bed"], "nil bed must be omitted from request body")
    }

    func testSetTemperaturesCooldownSendsZeros() async throws {
        mockAPIClient.stubResponse(json: TestJSON.commandSuccess)

        try await printerService.setTemperatures(printerId: TestData.testUUID, hotend: 0, bed: 0)

        let captured = mockAPIClient.capturedRequests.first
        let body = captured?.capturedHTTPBody()
        let json = try JSONSerialization.jsonObject(with: body ?? Data()) as? [String: Any]
        XCTAssertEqual(json?["hotend"] as? Double, 0)
        XCTAssertEqual(json?["bed"] as? Double, 0)
    }

    // MARK: - home()

    func testHomeAllAxesRoutesToHomeEndpoint() async throws {
        mockAPIClient.stubResponse(json: TestJSON.commandSuccess)

        try await printerService.home(printerId: TestData.testUUID, axes: ["X", "Y", "Z"])

        let captured = mockAPIClient.capturedRequests.first
        XCTAssertEqual(captured?.httpMethod, "POST")
        XCTAssertTrue(captured?.url?.path.hasSuffix("/home") ?? false,
                      "axes [X,Y,Z] must route to /home, got \(captured?.url?.path ?? "nil")")
    }

    func testHomeXYAxesRoutesToHomeXYEndpoint() async throws {
        mockAPIClient.stubResponse(json: TestJSON.commandSuccess)

        try await printerService.home(printerId: TestData.testUUID, axes: ["X", "Y"])

        let captured = mockAPIClient.capturedRequests.first
        XCTAssertTrue(captured?.url?.path.hasSuffix("/homexy") ?? false,
                      "axes [X,Y] must route to /homexy")
    }

    func testHomeZAxisRoutesToHomeZEndpoint() async throws {
        mockAPIClient.stubResponse(json: TestJSON.commandSuccess)

        try await printerService.home(printerId: TestData.testUUID, axes: ["Z"])

        let captured = mockAPIClient.capturedRequests.first
        XCTAssertTrue(captured?.url?.path.hasSuffix("/homez") ?? false,
                      "axes [Z] must route to /homez")
    }

    func testHomeXYConvenienceWrapper() async throws {
        mockAPIClient.stubResponse(json: TestJSON.commandSuccess)

        try await printerService.homeXY(printerId: TestData.testUUID)

        let captured = mockAPIClient.capturedRequests.first
        XCTAssertEqual(captured?.httpMethod, "POST")
        XCTAssertTrue(captured?.url?.path.hasSuffix("/homexy") ?? false)
    }

    func testHomeZConvenienceWrapper() async throws {
        mockAPIClient.stubResponse(json: TestJSON.commandSuccess)

        try await printerService.homeZ(printerId: TestData.testUUID)

        let captured = mockAPIClient.capturedRequests.first
        XCTAssertEqual(captured?.httpMethod, "POST")
        XCTAssertTrue(captured?.url?.path.hasSuffix("/homez") ?? false)
    }

    // MARK: - move()

    func testMoveOnXAxisSendsCorrectBody() async throws {
        mockAPIClient.stubResponse(json: TestJSON.commandSuccess)

        try await printerService.move(printerId: TestData.testUUID, axis: "X", distanceMm: 10.0, feedrateMmMin: 3000)

        let captured = mockAPIClient.capturedRequests.first
        XCTAssertEqual(captured?.httpMethod, "POST")
        XCTAssertTrue(captured?.url?.path.contains("/api/printers/\(TestData.testUUID)/move") ?? false)

        let body = captured?.capturedHTTPBody()
        XCTAssertNotNil(body)
        let json = try JSONSerialization.jsonObject(with: body ?? Data()) as? [String: Any]
        XCTAssertEqual(json?["x"] as? Double, 10.0)
        XCTAssertNil(json?["y"], "non-target axes must be omitted")
        XCTAssertNil(json?["z"], "non-target axes must be omitted")
        XCTAssertEqual(json?["f"] as? Double, 3000)
    }

    func testMoveOnYAxisSendsCorrectBody() async throws {
        mockAPIClient.stubResponse(json: TestJSON.commandSuccess)

        try await printerService.move(printerId: TestData.testUUID, axis: "Y", distanceMm: -5.0, feedrateMmMin: 3000)

        let captured = mockAPIClient.capturedRequests.first
        let body = captured?.capturedHTTPBody()
        let json = try JSONSerialization.jsonObject(with: body ?? Data()) as? [String: Any]
        XCTAssertNil(json?["x"])
        XCTAssertEqual(json?["y"] as? Double, -5.0)
        XCTAssertNil(json?["z"])
        XCTAssertEqual(json?["f"] as? Double, 3000)
    }

    func testMoveOnZAxisUsesLockedFeedrate() async throws {
        mockAPIClient.stubResponse(json: TestJSON.commandSuccess)

        try await printerService.move(printerId: TestData.testUUID, axis: "Z", distanceMm: 0.1, feedrateMmMin: 600)

        let captured = mockAPIClient.capturedRequests.first
        let body = captured?.capturedHTTPBody()
        let json = try JSONSerialization.jsonObject(with: body ?? Data()) as? [String: Any]
        XCTAssertNil(json?["x"])
        XCTAssertNil(json?["y"])
        XCTAssertEqual(json?["z"] as? Double, 0.1)
        XCTAssertEqual(json?["f"] as? Double, 600)
    }

    func testSetTemperaturesPropagatesServerError() async {
        mockAPIClient.stubResponse(json: "{}", statusCode: 502)

        do {
            try await printerService.setTemperatures(printerId: TestData.testUUID, hotend: 200, bed: 60)
            XCTFail("Expected error")
        } catch let error as NetworkError {
            if case .serverError(let code) = error {
                XCTAssertEqual(code, 502)
            } else {
                XCTFail("Expected .serverError, got \(error)")
            }
        } catch {
            XCTFail("Unexpected error: \(error)")
        }
    }

    // MARK: - getBackendCapabilities()

    func testGetBackendCapabilities_happyPath_returnsMergedCapabilities() async throws {
        let capJson = """
        {
            "printerId": "\(TestData.testUUID)",
            "printerName": "Test Moonraker",
            "backend": "Moonraker",
            "supportsMovement": true,
            "supportsTemperatureControl": true,
            "supportsRelativeMovement": true,
            "supportsHotendTemperature": true,
            "supportsCamera": true,
            "supportsControlOperations": true,
            "supportsFileList": true,
            "supportsFileUpload": true,
            "supportsFileDownload": true,
            "supportsStartPrint": true,
            "supportsFilamentControl": true,
            "supportsFileMetadata": true,
            "supportsPrinterInformation": true,
            "supportsHistory": true
        }
        """
        mockAPIClient.stubResponse(json: capJson)

        let caps = try await printerService.getBackendCapabilities(printerId: TestData.testUUID)

        let captured = mockAPIClient.capturedRequests.first
        XCTAssertTrue(captured?.url?.path.contains("backend-capabilities") ?? false,
                      "Must hit the backend-capabilities endpoint first")
        XCTAssertTrue(caps.supportsMovement, "Moonraker wire DTO supportsMovement=true must be honoured")
        XCTAssertTrue(caps.supportsTemperatureControl)
    }

    func testGetBackendCapabilities_404_doesNotInferActuationFromBackendName() async throws {
        // Stub per-path: capabilities returns 404, printer get returns Moonraker printer
        mockAPIClient.stubResponses([
            "backend-capabilities": (statusCode: 404, json: "{}"),
            "/api/printers/\(TestData.testUUID)": (statusCode: 200, json: TestJSON.printer)
        ])

        let caps = try await printerService.getBackendCapabilities(printerId: TestData.testUUID)

        XCTAssertFalse(caps.supportsMovement)
        XCTAssertFalse(caps.supportsTemperatureControl)
        XCTAssertFalse(caps.supportsBedTemperature)
        XCTAssertFalse(caps.supportsFanControl)
        XCTAssertEqual(mockAPIClient.capturedRequests.count, 1)
    }

    func testGetBackendCapabilities_resin_sdcp_movementFalse() async throws {
        let sdcpPrinterJson = TestJSON.printer
            .replacingOccurrences(of: "\"backend\": \"Moonraker\"", with: "\"backend\": \"SDCP\"")

        mockAPIClient.stubResponses([
            "backend-capabilities": (statusCode: 404, json: "{}"),
            "/api/printers/\(TestData.testUUID)": (statusCode: 200, json: sdcpPrinterJson)
        ])

        let caps = try await printerService.getBackendCapabilities(printerId: TestData.testUUID)

        // SDCP (resin) fallback: no movement, no temperature
        XCTAssertFalse(caps.supportsMovement,
                       "Resin/SDCP fallback must report supportsMovement=false")
        XCTAssertFalse(caps.supportsTemperatureControl)
        XCTAssertFalse(caps.supportsBedTemperature)
        XCTAssertFalse(caps.supportsFanControl)
    }

    // Encoding-only coverage: nullable wire fields are preserved, not authorized.
    // The Controls owner requires verified, complete XYZ before calling this service.
    func testMoveToPreservesOriginOmittedAxesAndFeedrateUnits() async throws {
        mockAPIClient.stubResponse(json: TestJSON.commandSuccess)
        let result = try await printerService.moveTo(
            printerId: TestData.testUUID, x: 0, y: nil, z: 12.5, feedrateMmMin: 1200
        )
        XCTAssertTrue(result.success)
        let request = try XCTUnwrap(mockAPIClient.capturedRequests.first)
        XCTAssertEqual(request.httpMethod, "POST")
        XCTAssertTrue(request.url?.path.hasSuffix("/moveto") == true)
        let body = try requestBody(request)
        XCTAssertEqual(body["x"] as? Double, 0)
        XCTAssertNil(body["y"])
        XCTAssertEqual(body["z"] as? Double, 12.5)
        XCTAssertEqual(body["f"] as? Int, 1200)
    }

    func testMoveToOmitsUnspecifiedFeedrate() async throws {
        mockAPIClient.stubResponse(json: TestJSON.commandSuccess)
        _ = try await printerService.moveTo(printerId: TestData.testUUID, x: nil, y: 0, z: nil, feedrateMmMin: nil)
        let body = try requestBody(XCTUnwrap(mockAPIClient.capturedRequests.first))
        XCTAssertEqual(body.count, 1)
        XCTAssertEqual(body["y"] as? Int, 0)
    }

    func testExtrudePreservesRetractionAndDoesNotConvertMmMinAgain() async throws {
        mockAPIClient.stubResponse(json: TestJSON.commandSuccess)
        _ = try await printerService.extrude(printerId: TestData.testUUID, distanceMm: -8.5, feedrateMmPerMinute: 300)
        let request = try XCTUnwrap(mockAPIClient.capturedRequests.first)
        XCTAssertEqual(request.httpMethod, "POST")
        XCTAssertTrue(request.url?.path.hasSuffix("/extrude") == true)
        let body = try requestBody(request)
        XCTAssertEqual(body.count, 2)
        XCTAssertEqual(body["distanceMm"] as? Double, -8.5)
        XCTAssertEqual(body["feedrateMmPerMinute"] as? Int, 300)
    }

    func testDisableMotorsHasNoBodyAndPreservesRejectedResult() async throws {
        mockAPIClient.stubResponse(json: #"{"success":false,"message":"Unsupported"}"#)
        let result = try await printerService.disableMotors(printerId: TestData.testUUID)
        XCTAssertFalse(result.success)
        XCTAssertEqual(result.message, "Unsupported")
        let request = try XCTUnwrap(mockAPIClient.capturedRequests.first)
        XCTAssertEqual(request.httpMethod, "POST")
        XCTAssertTrue(request.url?.path.hasSuffix("/disable-motors") == true)
        XCTAssertNil(request.capturedHTTPBody())
        XCTAssertEqual(mockAPIClient.capturedRequests.count, 1)
    }

    func testSaveOffsetBindsReviewedRevisionAndDoesNotRefetch() async throws {
        mockAPIClient.stubResponse(json: TestJSON.commandSuccess)
        _ = try await printerService.saveZOffset(
            printerId: TestData.testUUID, offsetMm: -0.15, saveToFirmware: true, reviewedRowVersion: "AQIDBA=="
        )
        let request = try XCTUnwrap(mockAPIClient.capturedRequests.first)
        XCTAssertEqual(request.httpMethod, "POST")
        XCTAssertTrue(request.url?.path.hasSuffix("/z-offset") == true)
        XCTAssertEqual(request.value(forHTTPHeaderField: "If-Match"), "\"AQIDBA==\"")
        let body = try requestBody(request)
        XCTAssertEqual(body["offsetMm"] as? Double, -0.15)
        XCTAssertEqual(body["saveToFirmware"] as? Bool, true)
        XCTAssertEqual(mockAPIClient.capturedRequests.count, 1)
    }

    func testSaveOffsetErrorsNeverRetryOrRefreshRevision() async throws {
        for status in [400, 403, 404, 409, 412, 428, 503] {
            mockAPIClient.reset()
            mockAPIClient.stubResponse(json: #"{"success":false,"message":"Not applied or outcome unknown"}"#, statusCode: status)
            do {
                _ = try await printerService.saveZOffset(
                    printerId: TestData.testUUID, offsetMm: 0, saveToFirmware: true, reviewedRowVersion: "AQIDBA=="
                )
                XCTFail("Expected HTTP \(status) to surface")
            } catch let error as NetworkError {
                switch (status, error) {
                case (400, .clientError(400, _)), (403, .forbidden), (404, .notFound),
                     (409, .conflict), (412, .preconditionFailed), (428, .preconditionRequired),
                     (503, .serverError(503)): break
                default: XCTFail("Unexpected mapping for \(status): \(error)")
                }
            }
            XCTAssertEqual(mockAPIClient.capturedRequests.count, 1)
        }
    }

    func testDetailedUnloadPreservesToolZeroAndInventoryUnknowns() async throws {
        mockAPIClient.stubResponse(json: #"{"success":true,"spoolId":7,"material":"PETG","residualWeightG":0}"#)
        let result = try await printerService.unloadFilament(printerId: TestData.testUUID, toolheadIndex: 0)
        XCTAssertEqual(result.spoolId, 7)
        XCTAssertEqual(result.material, "PETG")
        XCTAssertEqual(result.residualWeightG, 0)
        let request = try XCTUnwrap(mockAPIClient.capturedRequests.first)
        XCTAssertTrue(request.url?.path.hasSuffix("/filament-unload") == true)
        XCTAssertEqual(request.url?.query, "toolheadIndex=0")
        XCTAssertNil(request.capturedHTTPBody())
        mockAPIClient.reset()
        mockAPIClient.stubResponse(json: TestJSON.commandSuccess)
        let unknown = try await printerService.unloadFilament(printerId: TestData.testUUID, toolheadIndex: nil)
        XCTAssertNil(unknown.residualWeightG)
        XCTAssertNil(mockAPIClient.capturedRequests.first?.url?.query)
    }

    func testLegacyVoidControlsRejectFalseAndMalformedSuccessBodies() async throws {
        for json in [#"{"success":false,"message":"Rejected"}"#, "{}", ""] {
            mockAPIClient.reset()
            mockAPIClient.stubResponse(json: json)
            do {
                try await printerService.setTemperatures(printerId: TestData.testUUID, hotend: 200, bed: nil)
                XCTFail("Must not silently accept \(json)")
            } catch {}
            XCTAssertEqual(mockAPIClient.capturedRequests.count, 1)
        }
    }

    func testInvalidAxisCannotSilentlyMoveX() async throws {
        mockAPIClient.stubResponse(json: TestJSON.commandSuccess)
        do {
            try await printerService.move(printerId: TestData.testUUID, axis: "E", distanceMm: 10, feedrateMmMin: 300)
            XCTFail("Expected invalid axis")
        } catch {}
        do {
            try await printerService.home(printerId: TestData.testUUID, axes: ["X"])
            XCTFail("A single X request must not home every axis")
        } catch {}
        XCTAssertTrue(mockAPIClient.capturedRequests.isEmpty)
    }

    func testInvalidExtrusionAndUnreviewedOffsetNeverReachTransport() async throws {
        for distance in [0, 101, -101, .infinity, .nan] {
            do {
                _ = try await printerService.extrude(printerId: TestData.testUUID, distanceMm: distance, feedrateMmPerMinute: 300)
                XCTFail("Invalid extrusion")
            } catch {}
        }
        for revision in ["", " ", "*", "\"AQID==\""] {
            do {
                _ = try await printerService.saveZOffset(
                    printerId: TestData.testUUID, offsetMm: 0, saveToFirmware: true, reviewedRowVersion: revision
                )
                XCTFail("Invalid revision")
            } catch {}
        }
        XCTAssertTrue(mockAPIClient.capturedRequests.isEmpty)
    }

    func testControlTransportCancellationIsNotRetried() async throws {
        mockAPIClient.stubError(.cancelled)
        do {
            _ = try await printerService.extrude(printerId: TestData.testUUID, distanceMm: 1, feedrateMmPerMinute: 60)
            XCTFail("Expected cancellation")
        } catch NetworkError.transportError(let error) {
            XCTAssertEqual(error.code, .cancelled)
        }
        XCTAssertEqual(mockAPIClient.capturedRequests.count, 1)
    }

    func testCancelledBeforeDispatchDoesNotActuate() async throws {
        let barrier = AsyncBarrier()
        addTeardownBlock { barrier.close() }
        let service = try XCTUnwrap(printerService)
        let pending = Task {
            await barrier.arriveAndWait()
            return try await service.disableMotors(printerId: TestData.testUUID)
        }
        await barrier.waitUntilArrived()
        pending.cancel()
        barrier.release()
        do {
            _ = try await pending.value
            XCTFail("Cancelled command must not be sent")
        } catch is CancellationError {}
        XCTAssertTrue(mockAPIClient.capturedRequests.isEmpty)
    }

    func testStalePhysicalResponseCannotAcknowledgeNewServer() async throws {
        let generation = ActiveServerGeneration()
        let client = APIClient(baseURL: TestData.testBaseURL, session: mockAPIClient.urlSession, serverGeneration: generation)
        let service = PrinterService(apiClient: client)
        let barrier = AsyncBarrier()
        addTeardownBlock { barrier.close() }
        mockAPIClient.asyncRequestHandler = { request in
            await barrier.arriveAndWait()
            return (TestData.httpResponse(url: request.url, statusCode: 200), Data(TestJSON.commandSuccess.utf8))
        }
        let pending = Task { try await service.disableMotors(printerId: TestData.testUUID) }
        await barrier.waitUntilArrived()
        generation.advance()
        barrier.release()
        do {
            _ = try await pending.value
            XCTFail("Old server response must not acknowledge new control state")
        } catch NetworkError.staleServerResponse {}
        XCTAssertEqual(mockAPIClient.capturedRequests.count, 1)
    }

    func testControlBoundariesAndDatabaseOnlySave() async throws {
        mockAPIClient.stubResponse(json: TestJSON.commandSuccess)
        for (distance, feedrate) in [(-100.0, 1), (100.0, 6000)] {
            _ = try await printerService.extrude(printerId: TestData.testUUID, distanceMm: distance, feedrateMmPerMinute: feedrate)
        }
        for offset in [-5.0, 5.0] {
            _ = try await printerService.saveZOffset(
                printerId: TestData.testUUID, offsetMm: offset, saveToFirmware: false, reviewedRowVersion: "AQIDBA=="
            )
            let body = try requestBody(XCTUnwrap(mockAPIClient.capturedRequests.last))
            XCTAssertEqual(body["saveToFirmware"] as? Bool, false)
        }
        XCTAssertEqual(mockAPIClient.capturedRequests.count, 4)
        mockAPIClient.reset()
        for feedrate in [0, 6001] {
            do {
                _ = try await printerService.extrude(printerId: TestData.testUUID, distanceMm: 1, feedrateMmPerMinute: feedrate)
                XCTFail("Invalid feedrate")
            } catch PrinterControlError.invalidRequest {}
        }
        for offset in [-5.01, 5.01, .nan, .infinity] {
            do {
                _ = try await printerService.saveZOffset(
                    printerId: TestData.testUUID, offsetMm: offset, saveToFirmware: false, reviewedRowVersion: "AQIDBA=="
                )
                XCTFail("Invalid offset")
            } catch PrinterControlError.invalidRequest {}
        }
        XCTAssertTrue(mockAPIClient.capturedRequests.isEmpty)
    }

    private func requestBody(_ request: URLRequest) throws -> [String: Any] {
        let data = try XCTUnwrap(request.capturedHTTPBody())
        return try XCTUnwrap(JSONSerialization.jsonObject(with: data) as? [String: Any])
    }
}
