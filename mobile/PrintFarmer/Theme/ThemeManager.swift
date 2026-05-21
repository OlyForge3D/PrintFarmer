import SwiftUI

/// User's preferred theme mode.
enum ThemeMode: String, CaseIterable, Identifiable, Sendable {
    case system
    case light
    case dark

    var id: String { rawValue }

    var displayName: String {
        switch self {
        case .system: "System"
        case .light: "Light"
        case .dark: "PrintFarmer Dark"
        }
    }

    var icon: String {
        switch self {
        case .system: "circle.lefthalf.filled"
        case .light: "sun.max.fill"
        case .dark: "moon.fill"
        }
    }

    /// Returns the explicit color scheme, or nil to follow system appearance.
    var colorScheme: ColorScheme? {
        switch self {
        case .system: nil
        case .light: .light
        case .dark: .dark
        }
    }
}

/// Manages the app-wide theme preference and persists it to UserDefaults.
@Observable
final class ThemeManager: @unchecked Sendable {
    private static let themeKey = "pf_theme_mode"

    var themeMode: ThemeMode {
        didSet {
            UserDefaults.standard.set(themeMode.rawValue, forKey: Self.themeKey)
        }
    }

    /// The color scheme to apply via `.preferredColorScheme()`, or nil for system default.
    var preferredColorScheme: ColorScheme? {
        themeMode.colorScheme
    }

    init() {
        let saved = UserDefaults.standard.string(forKey: Self.themeKey) ?? "system"
        self.themeMode = ThemeMode(rawValue: saved) ?? .system
    }
}
