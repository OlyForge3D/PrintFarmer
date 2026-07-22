# Skill: SwiftUI Form ViewModel Pattern

## When to Use
Any SwiftUI form that collects user input and submits to a service layer (login, create/edit entities, settings).

## Pattern

```swift
// 1. ViewModel: @MainActor @Observable for Swift 6 safety
@MainActor @Observable
final class MyFormViewModel {
    var field1 = ""
    var field2 = ""
    
    var isFormValid: Bool { /* computed validation */ }
    
    func submit(using serviceVM: SomeViewModel) async {
        // Normalize inputs, delegate to service-level VM
    }
}

// 2. View: @State for form VM, @Environment for app-level VM
struct MyFormView: View {
    @Environment(SomeViewModel.self) private var serviceVM
    @State private var viewModel = MyFormViewModel()
    @FocusState private var focusedField: Field?
    
    private enum Field: Hashable { case field1, field2 }
    
    var body: some View {
        // Use .focused + .submitLabel + .onSubmit for keyboard flow
        // Use .scrollDismissesKeyboard(.interactively) + .onTapGesture for dismiss
    }
}
```

## Key Details
- `@MainActor` on ViewModel prevents Swift 6 "sending risks data races" errors
- Form VM is `@State` (owned by view), service VM is `@Environment` (injected)
- Use `@FocusState` enum + `.submitLabel` + `.onSubmit` for return-key field chaining
- Validate with computed properties, not imperative methods
