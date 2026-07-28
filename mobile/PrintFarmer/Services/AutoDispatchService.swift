import Foundation

// MARK: - AutoDispatch Service

actor AutoDispatchService: AutoDispatchServiceProtocol {
    private let apiClient: APIClient

    init(apiClient: APIClient) {
        self.apiClient = apiClient
    }

    func getAllStatus() async throws -> AutoDispatchGlobalStatus {
        try await apiClient.get("/api/auto-dispatch/status")
    }

    func getStatus(printerId: UUID) async throws -> AutoDispatchStatus {
        try await apiClient.get("/api/auto-dispatch/\(printerId)/status")
    }

    func markReady(status: AutoDispatchStatus) async throws -> AutoDispatchReadyResult {
        let dispatchETag = try required(status.dispatchStateETag)
        if status.nextJobKind != "FilamentCalibration" {
            return try await apiClient.post(
                "/api/auto-dispatch/\(status.printerId)/ready",
                headers: ["If-Match": "\"\(dispatchETag)\""]
            )
        }

        guard let jobId = status.nextJobId,
              let jobETag = status.nextJobETag else {
            throw NetworkError.invalidResponse
        }
        let request = AcknowledgeBedClearRequest(
            printerId: status.printerId,
            expectedPrinterConfigRevision:
                status.nextJobPrinterConfigRevision
        )
        let response: HTTPDecodedResponse<AcknowledgeBedClearResponse> =
            try await apiClient.post(
            "/api/job-queue/\(jobId)/acknowledge-bed-clear-and-start",
            body: request,
            headers: [
                "If-Match": "\"\(jobETag)\"",
                "X-Dispatch-State-If-Match": "\"\(dispatchETag)\"",
                "Idempotency-Key": stableIdempotencyKey(status: status)
            ],
            accepting: [200, 202, 409, 412, 422, 428, 503]
        )
        let errorCode =
            response.value.error ?? "bed_clear_acknowledgement_failed"
        switch response.statusCode {
        case 409:
            throw BedClearAcknowledgementError.conflict(
                code: errorCode,
                detail: response.value.detail
            )
        case 412:
            throw BedClearAcknowledgementError.stale(
                code: errorCode,
                detail: response.value.detail
            )
        case 422:
            throw BedClearAcknowledgementError.incompatible(
                code: errorCode,
                detail: response.value.detail
            )
        case 428:
            throw BedClearAcknowledgementError.preconditionRequired(
                code: errorCode,
                detail: response.value.detail
            )
        case 503:
            throw BedClearAcknowledgementError.unavailable(
                code: errorCode,
                detail: response.value.detail
            )
        case 200, 202:
            break
        default:
            throw NetworkError.unexpectedStatus(response.statusCode)
        }

        return AutoDispatchReadyResult(
            status: status,
            nextJob: AutoDispatchNextJob(
                id: jobId,
                name: status.nextJobName ?? "Calibration job",
                estimatedFilamentUsageG: nil,
                requiredMaterialType: nil,
                estimatedPrintTime: nil,
                jobKind: "FilamentCalibration",
                jobETag: jobETag,
                expectedPrinterConfigRevision:
                    status.nextJobPrinterConfigRevision
            ),
            filamentCheck: nil,
            acknowledgementOutcome:
                response.statusCode == 202 ? .accepted : .replayed
        )
    }

    func skip(status: AutoDispatchStatus) async throws -> AutoDispatchStatus {
        guard let dispatchETag = status.dispatchStateETag,
              !dispatchETag.isEmpty,
              let jobETag = status.nextJobETag,
              !jobETag.isEmpty else {
            throw NetworkError.invalidResponse
        }
        let response: AutoDispatchStatus = try await apiClient.post(
            "/api/auto-dispatch/\(status.printerId)/skip",
            headers: [
                "If-Match": "\"\(dispatchETag)\"",
                "X-Job-If-Match": "\"\(jobETag)\""
            ]
        )
        return response
    }

    func cancel(status: AutoDispatchStatus) async throws -> AutoDispatchStatus {
        try await apiClient.post(
            "/api/auto-dispatch/\(status.printerId)/cancel",
            headers: [
                "If-Match": "\"\(try required(status.dispatchStateETag))\""
            ]
        )
    }

    func preClear(status: AutoDispatchStatus) async throws -> AutoDispatchStatus {
        try await apiClient.post(
            "/api/auto-dispatch/\(status.printerId)/pre-clear",
            headers: [
                "If-Match": "\"\(try required(status.dispatchStateETag))\""
            ]
        )
    }

    func setEnabled(status: AutoDispatchStatus, request: SetAutoDispatchEnabledRequest) async throws -> AutoDispatchStatus {
        guard let dispatchETag = status.dispatchStateETag,
              !dispatchETag.isEmpty,
              let printerETag = status.printerETag,
              !printerETag.isEmpty else {
            throw NetworkError.invalidResponse
        }
        let response: AutoDispatchStatus = try await apiClient.put(
            "/api/auto-dispatch/\(status.printerId)/enabled",
            body: request,
            headers: [
                "If-Match": "\"\(dispatchETag)\"",
                "X-Printer-If-Match": "\"\(printerETag)\""
            ]
        )
        return response
    }

    private func required(_ etag: String?) throws -> String {
        guard let etag, !etag.isEmpty else {
            throw NetworkError.invalidResponse
        }
        return etag
    }

    private func stableIdempotencyKey(status: AutoDispatchStatus) -> String {
        let storageKey = [
            "bed-clear",
            status.nextJobId?.uuidString ?? "missing",
            status.nextJobETag ?? "missing",
            status.dispatchStateETag ?? "missing"
        ].joined(separator: ":")
        if let existing = UserDefaults.standard.string(forKey: storageKey) {
            return existing
        }
        let created = UUID().uuidString
        UserDefaults.standard.set(created, forKey: storageKey)
        return created
    }
}
