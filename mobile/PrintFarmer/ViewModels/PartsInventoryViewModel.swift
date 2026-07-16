import Foundation
import os

/// Drives the printed-parts inventory list (#714, F9). Distinct from
/// `SpoolInventoryViewModel` (filament spools) — this tracks SKUs *produced*
/// by prints, with on-hand/reorder state populated from the merged backend
/// contract (PR #741).
@MainActor @Observable
final class PartsInventoryViewModel {
    var parts: [PartInventoryResponse] = []
    var searchText = ""
    var showOnlyNeedingReorder = false
    var isLoading = false
    var errorMessage: String?
    var featureDisabled = false
    var isViewActive = true

    private let logger = Logger(subsystem: "com.printfarmer.ios", category: "PartsInventory")
    private var partsInventoryService: (any PartsInventoryServiceProtocol)?

    func configure(partsInventoryService: any PartsInventoryServiceProtocol) {
        self.partsInventoryService = partsInventoryService
    }

    var filteredParts: [PartInventoryResponse] {
        var result = parts

        if showOnlyNeedingReorder {
            result = result.filter(\.needsReorder)
        }

        guard !searchText.isEmpty else { return result }
        let query = searchText.lowercased()
        return result.filter { part in
            part.sku.lowercased().contains(query)
                || part.name.lowercased().contains(query)
                || (part.description?.lowercased().contains(query) ?? false)
        }
    }

    var hasActiveSearch: Bool {
        !searchText.isEmpty || showOnlyNeedingReorder
    }

    func clearFilters() {
        searchText = ""
        showOnlyNeedingReorder = false
    }

    func loadParts() async {
        guard let partsInventoryService else {
            errorMessage = "Parts inventory service not available"
            return
        }

        isLoading = true
        errorMessage = nil

        do {
            let result = try await partsInventoryService.listParts()
            guard isViewActive else { return }
            parts = result
            featureDisabled = false
        } catch NetworkError.featureDisabled {
            guard isViewActive else { return }
            featureDisabled = true
            parts = []
        } catch {
            guard isViewActive else { return }
            logger.warning("Failed to load parts inventory: \(error.localizedDescription)")
            errorMessage = error.localizedDescription
        }

        guard isViewActive else { return }
        isLoading = false
    }
}
