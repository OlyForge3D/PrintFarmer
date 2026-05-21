import Foundation
@testable import PrintFarmer

final class MockDispatchService: DispatchServiceProtocol, @unchecked Sendable {
    var queueStatusToReturn: DispatchQueueStatus?
    var historyPageToReturn: DispatchHistoryPage?
    var errorToThrow: Error?
    
    // Call tracking
    var getQueueStatusCalled = false
    var getHistoryCalledWith: (page: Int?, pageSize: Int?)?
    
    func getQueueStatus() async throws -> DispatchQueueStatus {
        getQueueStatusCalled = true
        if let error = errorToThrow { throw error }
        return queueStatusToReturn!
    }
    
    func getHistory(page: Int? = nil, pageSize: Int? = nil) async throws -> DispatchHistoryPage {
        getHistoryCalledWith = (page, pageSize)
        if let error = errorToThrow { throw error }
        return historyPageToReturn!
    }
}
