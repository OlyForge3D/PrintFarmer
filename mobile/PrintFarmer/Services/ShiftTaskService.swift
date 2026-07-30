import Foundation

protocol ShiftTaskServiceProtocol: AnyObject, Sendable {
    func loadSnapshot(shiftPlanEnabled: Bool) async throws -> ShiftTaskSnapshot
    func complete(taskID: String, idempotencyKey: String) async throws
    func skip(taskID: String) async throws
    func dismiss(taskID: String) async throws
}

actor ShiftTaskService: ShiftTaskServiceProtocol {
    private let apiClient: APIClient

    init(apiClient: APIClient) {
        self.apiClient = apiClient
    }

    func loadSnapshot(shiftPlanEnabled: Bool) async throws -> ShiftTaskSnapshot {
        guard shiftPlanEnabled else {
            return await featureDisabledSnapshot()
        }

        do {
            let plan: ShiftPlan = try await apiClient.get("/api/tasks?view=shift")
            return .grouped(plan)
        } catch NetworkError.featureDisabled {
            return await featureDisabledSnapshot()
        } catch NetworkError.notFound {
            return try await flatSnapshot(mode: .legacyFallback)
        } catch NetworkError.methodNotAllowed {
            return try await flatSnapshot(mode: .legacyFallback)
        }
    }

    func complete(taskID: String, idempotencyKey: String) async throws {
        try await apiClient.postVoid(
            mutationPath(taskID: taskID, operation: .complete),
            headers: ["Idempotency-Key": idempotencyKey]
        )
    }

    func skip(taskID: String) async throws {
        try await apiClient.postVoid(mutationPath(taskID: taskID, operation: .skip))
    }

    func dismiss(taskID: String) async throws {
        try await apiClient.postVoid(mutationPath(taskID: taskID, operation: .dismiss))
    }

    private func featureDisabledSnapshot() async -> ShiftTaskSnapshot {
        do {
            return try await flatSnapshot(mode: .featureDisabled)
        } catch {
            return ShiftTaskSnapshot(
                groups: [],
                generatedAt: nil,
                mode: .featureDisabled,
                compatibilityErrorMessage: error.localizedDescription
            )
        }
    }

    private func flatSnapshot(mode: ShiftTaskSnapshotMode) async throws -> ShiftTaskSnapshot {
        let tasks: [ShiftTask] = try await apiClient.get("/api/tasks")
        return .flat(tasks, mode: mode)
    }

    private func mutationPath(taskID: String, operation: ShiftTaskMutationOperation) -> String {
        "/api/tasks/\(Self.encodePathSegment(taskID))/\(operation.rawValue)"
    }

    private static func encodePathSegment(_ value: String) -> String {
        var allowed = CharacterSet.urlPathAllowed
        allowed.remove(charactersIn: ":/?#[]@")
        return value.addingPercentEncoding(withAllowedCharacters: allowed) ?? value
    }
}
