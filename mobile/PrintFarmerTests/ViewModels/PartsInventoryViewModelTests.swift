import XCTest
@testable import PrintFarmer

final class PartsInventoryViewModelTests: XCTestCase {
    @MainActor
    func testLoadPartsPopulatesPartsAndClearsFeatureDisabled() async {
        let (viewModel, service) = makeSubject()
        service.partsToReturn = [makePart(sku: "SKU-A"), makePart(sku: "SKU-B")]

        await viewModel.loadParts()

        XCTAssertEqual(viewModel.parts.map(\.sku), ["SKU-A", "SKU-B"])
        XCTAssertFalse(viewModel.featureDisabled)
        XCTAssertNil(viewModel.errorMessage)
        XCTAssertFalse(viewModel.isLoading)
    }

    @MainActor
    func testLoadPartsFeatureDisabledClearsPartsAndSetsFlag() async {
        let (viewModel, service) = makeSubject()
        viewModel.parts = [makePart(sku: "STALE")]
        service.listPartsError = NetworkError.featureDisabled(APIError(title: "Disabled", status: 404, detail: nil, errors: nil, message: nil, code: "featureDisabled"))

        await viewModel.loadParts()

        XCTAssertTrue(viewModel.featureDisabled)
        XCTAssertTrue(viewModel.parts.isEmpty)
        XCTAssertNil(viewModel.errorMessage)
    }

    @MainActor
    func testLoadPartsGenericErrorSurfacesMessage() async {
        let (viewModel, service) = makeSubject()
        service.listPartsError = NetworkError.serverError(500)

        await viewModel.loadParts()

        XCTAssertNotNil(viewModel.errorMessage)
        XCTAssertFalse(viewModel.featureDisabled)
    }

    @MainActor
    func testFilteredPartsAppliesReorderToggle() async {
        let (viewModel, service) = makeSubject()
        service.partsToReturn = [
            makePart(sku: "OK", needsReorder: false),
            makePart(sku: "LOW", needsReorder: true)
        ]
        await viewModel.loadParts()

        viewModel.showOnlyNeedingReorder = true

        XCTAssertEqual(viewModel.filteredParts.map(\.sku), ["LOW"])
    }

    @MainActor
    func testFilteredPartsAppliesSearchTextAcrossSkuNameAndDescription() async {
        let (viewModel, service) = makeSubject()
        service.partsToReturn = [
            makePart(sku: "BRK-100", name: "Bracket", description: "Mounting bracket"),
            makePart(sku: "GEAR-1", name: "Gear", description: nil)
        ]
        await viewModel.loadParts()

        viewModel.searchText = "mounting"
        XCTAssertEqual(viewModel.filteredParts.map(\.sku), ["BRK-100"])

        viewModel.searchText = "gear"
        XCTAssertEqual(viewModel.filteredParts.map(\.sku), ["GEAR-1"])

        viewModel.searchText = "nomatch"
        XCTAssertTrue(viewModel.filteredParts.isEmpty)
    }

    @MainActor
    func testClearFiltersResetsSearchAndReorderToggle() {
        let (viewModel, _) = makeSubject()
        viewModel.searchText = "foo"
        viewModel.showOnlyNeedingReorder = true

        viewModel.clearFilters()

        XCTAssertEqual(viewModel.searchText, "")
        XCTAssertFalse(viewModel.showOnlyNeedingReorder)
        XCTAssertFalse(viewModel.hasActiveSearch)
    }

    // MARK: - Helpers

    @MainActor
    private func makeSubject() -> (PartsInventoryViewModel, MockPartsInventoryService) {
        let viewModel = PartsInventoryViewModel()
        let service = MockPartsInventoryService()
        viewModel.configure(partsInventoryService: service)
        return (viewModel, service)
    }

    private func makePart(sku: String, name: String = "Part", description: String? = nil, needsReorder: Bool = false) -> PartInventoryResponse {
        PartInventoryResponse(
            id: UUID(), sku: sku, name: name, description: description, modelFileRef: nil,
            defaultBinId: nil, defaultBinCode: nil, defaultBinName: nil,
            onHand: needsReorder ? 1 : 10, reorderPoint: 5, needsReorder: needsReorder,
            isActive: true, createdAt: .now, updatedAt: .now
        )
    }
}
