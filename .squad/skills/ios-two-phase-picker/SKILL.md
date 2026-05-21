# SKILL: Two-Phase Picker Pattern

**Category:** SwiftUI UI Patterns  
**Complexity:** Intermediate  
**Author:** Ripley  
**Date:** 2026-03-09

## Summary

A SwiftUI pattern for selection flows that are too large or complex for a single list. Split into two phases: category selection → item selection within that category. Improves performance and UX by reducing cognitive load and loading only relevant items.

## Use Cases

- Large inventory selection (e.g., 200+ items)
- Hierarchical data (category → item)
- When backend supports filtered queries
- When user needs context before detailed selection

## Implementation Pattern

### 1. ViewModel Structure

```swift
enum PickerPhase {
    case selectCategory
    case selectItem
}

@MainActor @Observable
final class TwoPhasePickerViewModel {
    var phase: PickerPhase = .selectCategory
    var categories: [String] = []
    var items: [Item] = []
    var selectedCategory: String?
    var isLoading = false
    var errorMessage: String?
    
    func loadCategories() async {
        // Load category list from backend
        isLoading = true
        categories = try await service.listCategories()
        isLoading = false
    }
    
    func selectCategory(_ category: String) {
        selectedCategory = category
        phase = .selectItem
        Task { await loadItems() }
    }
    
    func loadItems() async {
        // Load ONLY items for selected category
        isLoading = true
        items = try await service.listItems(category: selectedCategory)
        isLoading = false
    }
    
    func backToCategories() {
        phase = .selectCategory
        items = []
        selectedCategory = nil
    }
}
```

### 2. View Structure

```swift
struct TwoPhasePickerView: View {
    @State private var viewModel = TwoPhasePickerViewModel()
    let onSelect: (Item) -> Void
    
    var body: some View {
        NavigationStack {
            Group {
                if viewModel.phase == .selectCategory {
                    categoryView
                } else {
                    itemView
                }
            }
            .navigationTitle(viewModel.phase == .selectCategory ? "Select Category" : "Select Item")
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    if viewModel.phase == .selectCategory {
                        Button("Cancel") { dismiss() }
                    } else {
                        Button("Back") {
                            withAnimation {
                                viewModel.backToCategories()
                            }
                        }
                    }
                }
            }
            .task {
                viewModel.configure(service: services.itemService)
                await viewModel.loadCategories()
            }
        }
    }
    
    private var categoryView: some View {
        List(viewModel.categories, id: \.self) { category in
            Button {
                withAnimation {
                    viewModel.selectCategory(category)
                }
            } label: {
                HStack {
                    Text(category)
                    Spacer()
                    Image(systemName: "chevron.right")
                }
            }
        }
    }
    
    private var itemView: some View {
        List(viewModel.items) { item in
            Button {
                onSelect(item)
                dismiss()
            } label: {
                ItemRowView(item: item)
            }
        }
    }
}
```

### 3. Service Layer

```swift
protocol ItemServiceProtocol {
    func listCategories() async throws -> [String]
    func listItems(category: String?) async throws -> [Item]
}

actor ItemService: ItemServiceProtocol {
    func listCategories() async throws -> [String] {
        try await apiClient.get("/api/items/categories/available")
    }
    
    func listItems(category: String?) async throws -> [Item] {
        var params = "limit=100"
        if let category {
            params += "&category=\(category)"
        }
        return try await apiClient.get("/api/items?\(params)")
    }
}
```

## Key Design Principles

### 1. Phase Enum Drives UI
- Single source of truth for which view is displayed
- Use `if phase == .selectCategory` rather than navigation push
- Keeps both phases in same NavigationStack for simpler state management

### 2. Dynamic Toolbar
- Phase 1: "Cancel" button to dismiss
- Phase 2: "Back" button to return to phase 1
- Maintains clear navigation metaphor

### 3. Lazy Loading
- Load categories immediately (small list)
- Load items ONLY after category selection (large, filtered list)
- Clear items when returning to phase 1 to save memory

### 4. Backend-Driven Categories
- Use specialized endpoint (e.g., `/categories/available`)
- Backend returns only categories with available items
- Avoids empty states in phase 2

### 5. Bypass Pattern for Direct Selection
If you have QR/NFC/deep-link flows that know the item ID directly:

```swift
func selectItemDirectly(id: Int) async {
    // Load all items temporarily to find the one
    let allItems = try await service.listItems(category: nil)
    guard let item = allItems.first(where: { $0.id == id }) else {
        scanError = "Item #\(id) not found"
        return
    }
    
    // Auto-set category and filter
    selectedCategory = item.category
    phase = .selectItem
    items = allItems.filter { $0.category == item.category }
    
    // Auto-select the item
    onAutoSelect?(item)
}
```

## Real-World Example: SpoolPickerView

**Context:** Selecting from 200+ 3D printer filament spools

**Phase 1:** Material selection (PLA, PETG, ABS, etc.)
- Backend: `GET /api/spoolman/materials/available` → `["PLA", "PETG", "ABS"]`
- UI: Simple list with chevrons

**Phase 2:** Spool selection within chosen material
- Backend: `GET /api/spoolman/spools?material=PLA` → 15 spools
- UI: Detailed list with color swatches, weights, status filters

**Scan Bypass:**
- QR/NFC scan with spool ID → load all spools → find match → auto-set material → filter → select

## Benefits

1. **Performance:** Load ~10-20 items instead of 200+
2. **UX:** Clear two-step process reduces decision paralysis
3. **Scalability:** Works for hundreds or thousands of items
4. **Memory:** Only holds one category's items at a time
5. **Backend:** Leverages server-side filtering

## When NOT to Use

- Fewer than ~50 total items (single list is fine)
- No natural category grouping
- Backend doesn't support category filtering
- Items are inherently cross-category (tags, not hierarchy)

## Testing Considerations

```swift
final class MockItemService: ItemServiceProtocol {
    var categoriesToReturn: [String] = []
    var itemsToReturn: [Item] = []
    var listCategoriesCalled = false
    var listItemsCalledWith: String?
    
    func listCategories() async throws -> [String] {
        listCategoriesCalled = true
        return categoriesToReturn
    }
    
    func listItems(category: String?) async throws -> [Item] {
        listItemsCalledWith = category
        return itemsToReturn
    }
}

// Test phase transitions
func testCategorySelection() async {
    let mock = MockItemService()
    mock.categoriesToReturn = ["Cat1", "Cat2"]
    mock.itemsToReturn = [mockItem1, mockItem2]
    
    let vm = TwoPhasePickerViewModel()
    vm.configure(service: mock)
    
    await vm.loadCategories()
    XCTAssertEqual(vm.phase, .selectCategory)
    
    vm.selectCategory("Cat1")
    XCTAssertEqual(vm.phase, .selectItem)
    XCTAssertEqual(mock.listItemsCalledWith, "Cat1")
}
```

## Related Patterns

- **Master-Detail:** Similar concept, but typically for browsing rather than selection
- **Drill-Down Navigation:** Use NavigationLink for persistent drill-down; use phase enum for ephemeral modal selection
- **Filter Chips:** Use for small orthogonal filters (status, tags); use two-phase for large hierarchical data

## References

- SpoolPickerView implementation: `PrintFarmer/Views/Filament/SpoolPickerView.swift`
- Decision doc: `.squad/decisions/inbox/ripley-material-first-spool-picker.md`
