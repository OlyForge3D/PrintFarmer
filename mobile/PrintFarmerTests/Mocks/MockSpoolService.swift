import Foundation
@testable import PrintFarmer

final class MockSpoolService: SpoolServiceProtocol, @unchecked Sendable {
    var spoolsPageToReturn = SpoolmanPagedResult<SpoolmanSpool>(items: [], totalCount: 0)
    var spoolToReturn: SpoolmanSpool?
    var filamentsToReturn: [SpoolmanFilament] = []
    var vendorsToReturn: [SpoolmanVendor] = []
    var materialsToReturn: [SpoolmanMaterial] = []
    var availableMaterialsToReturn: [String] = []
    var errorToThrow: Error?

    // Call tracking
    var listSpoolsCalled = false
    // swiftlint:disable:next large_tuple
    var listSpoolsArgs: (limit: Int, offset: Int, search: String?, material: String?, vendor: String?)?
    var createSpoolCalledWith: SpoolmanSpoolRequest?
    var updateSpoolCalledWith: (id: Int, request: SpoolmanSpoolRequest)?
    var deleteSpoolCalledWith: Int?
    var listFilamentsCalled = false
    var listVendorsCalled = false
    var listMaterialsCalled = false
    var listAvailableMaterialsCalled = false

    func listSpools(limit: Int, offset: Int, search: String?, material: String?, vendor: String?) async throws -> SpoolmanPagedResult<SpoolmanSpool> {
        listSpoolsCalled = true
        listSpoolsArgs = (limit, offset, search, material, vendor)
        if let error = errorToThrow { throw error }
        return spoolsPageToReturn
    }

    func createSpool(_ request: SpoolmanSpoolRequest) async throws -> SpoolmanSpool {
        createSpoolCalledWith = request
        if let error = errorToThrow { throw error }
        guard let spool = spoolToReturn else { throw NetworkError.notFound }
        return spool
    }

    func updateSpool(id: Int, _ request: SpoolmanSpoolRequest) async throws -> SpoolmanSpool {
        updateSpoolCalledWith = (id, request)
        if let error = errorToThrow { throw error }
        guard let spool = spoolToReturn else { throw NetworkError.notFound }
        return spool
    }

    func deleteSpool(id: Int) async throws {
        deleteSpoolCalledWith = id
        if let error = errorToThrow { throw error }
    }

    func listFilaments() async throws -> [SpoolmanFilament] {
        listFilamentsCalled = true
        if let error = errorToThrow { throw error }
        return filamentsToReturn
    }

    func listVendors() async throws -> [SpoolmanVendor] {
        listVendorsCalled = true
        if let error = errorToThrow { throw error }
        return vendorsToReturn
    }

    func listMaterials() async throws -> [SpoolmanMaterial] {
        listMaterialsCalled = true
        if let error = errorToThrow { throw error }
        return materialsToReturn
    }

    func listAvailableMaterials() async throws -> [String] {
        listAvailableMaterialsCalled = true
        if let error = errorToThrow { throw error }
        return availableMaterialsToReturn
    }

    func reset() {
        spoolsPageToReturn = SpoolmanPagedResult<SpoolmanSpool>(items: [], totalCount: 0)
        spoolToReturn = nil
        filamentsToReturn = []
        vendorsToReturn = []
        materialsToReturn = []
        availableMaterialsToReturn = []
        errorToThrow = nil
        listSpoolsCalled = false
        listSpoolsArgs = nil
        createSpoolCalledWith = nil
        updateSpoolCalledWith = nil
        deleteSpoolCalledWith = nil
        listFilamentsCalled = false
        listVendorsCalled = false
        listMaterialsCalled = false
        listAvailableMaterialsCalled = false
    }
}
