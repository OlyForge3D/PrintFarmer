---
name: "swiftui-observable-app-struct"
description: "Avoid gating views on @Observable properties inside App struct bodies — extract into a View"
domain: "swiftui, architecture, auth-flow"
confidence: "high"
source: "earned — blank-screen-after-login bug fix"
---

## Context
SwiftUI's `@Observable` property tracking can be unreliable when accessed
inside an `App` struct's `body`. If you gate conditional view rendering
(e.g., auth check) on an `@Observable` property there, the body may not
re-evaluate when the property changes, producing a blank or stale screen.

## Patterns
1. **Extract gating logic into a `View` struct** that reads the observable
   via `@Environment`. SwiftUI's observation tracking is reliable inside
   `View.body`.
2. **Provide all environments at the `App` level** so every child inherits them.
3. **Use a tri-state model** (checking / authenticated / unauthenticated) with
   a `hasCheckedAuth` flag so something always renders — no blank frames.

```swift
// ✅ Good — gating inside a View
struct RootView: View {
    @Environment(AuthViewModel.self) private var authViewModel
    var body: some View {
        if authViewModel.isAuthenticated {
            ContentView()
        } else if !authViewModel.hasCheckedAuth {
            LaunchScreen()
        } else {
            LoginView()
        }
    }
}

// App struct just provides environments
@main struct MyApp: App {
    @State private var authVM = AuthViewModel()
    var body: some Scene {
        WindowGroup {
            RootView()
                .environment(authVM)
                .task { await authVM.restoreSession() }
        }
    }
}
```

## Anti-Patterns
```swift
// ❌ Bad — reading @Observable inside App struct body
@main struct MyApp: App {
    @State private var authVM = AuthViewModel()
    var body: some Scene {
        WindowGroup {
            if authVM.isAuthenticated {   // may not re-evaluate!
                ContentView()
            } else {
                LoginView()
            }
        }
    }
}
```
