import SwiftUI

// MARK: - Adaptive Color Helper

extension Color {
    /// Creates a color that automatically adapts between light and dark mode.
    /// Uses the PrintFarmer branded palette for each appearance.
    static func adaptive(light: String, dark: String) -> Color {
        #if canImport(UIKit)
        return Color(uiColor: UIColor { traits in
            traits.userInterfaceStyle == .dark
                ? UIColor(hex: dark)
                : UIColor(hex: light)
        })
        #else
        return Color(hex: light)
        #endif
    }
}

// MARK: - PrintFarmer Theme Colors

extension Color {

    // MARK: Backgrounds

    /// Main view background — white / dark navy
    static let pfBackground = adaptive(light: "#ffffff", dark: "#0b1020")
    /// Secondary panels — off-white / dark slate
    static let pfBackgroundSecondary = adaptive(light: "#f8fafc", dark: "#0f172a")
    /// Tertiary areas — cool gray / charcoal
    static let pfBackgroundTertiary = adaptive(light: "#f1f5f9", dark: "#111827")
    /// Card and panel surfaces
    static let pfCard = adaptive(light: "#f8fafc", dark: "#0f172a")

    // MARK: Text

    /// Primary text — dark slate / light gray
    static let pfTextPrimary = adaptive(light: "#1e293b", dark: "#e5e7eb")
    /// Secondary text — medium slate / dim gray
    static let pfTextSecondary = adaptive(light: "#475569", dark: "#9ca3af")
    /// Tertiary text — cool gray / muted gray
    static let pfTextTertiary = adaptive(light: "#64748b", dark: "#6b7280")

    // MARK: Brand Accent

    /// Primary brand accent — green (same in both themes)
    static let pfAccent = Color(hex: "#10b981")
    /// Accent suitable for backgrounds (lighter in light, deeper in dark)
    static let pfAccentBg = adaptive(light: "#10b981", dark: "#047857")
    /// Accent hover/pressed state
    static let pfAccentHover = adaptive(light: "#059669", dark: "#036b4b")
    /// Secondary accent — blue (same in both themes)
    static let pfSecondaryAccent = Color(hex: "#1d4ed8")

    // MARK: Borders

    /// Standard border color
    static let pfBorder = adaptive(light: "#e2e8f0", dark: "#243145")
    /// Light/subtle border color
    static let pfBorderLight = adaptive(light: "#cbd5e1", dark: "#475569")

    // MARK: Status

    /// Success — green (slightly different shades per mode)
    static let pfSuccess = adaptive(light: "#059669", dark: "#10b981")
    /// Error — red (same in both themes)
    static let pfError = Color(hex: "#dc2626")
    /// Warning — amber (same in both themes)
    static let pfWarning = Color(hex: "#d97706")

    // MARK: Buttons

    /// Primary button background — green / deeper green in dark
    static let pfButtonPrimary = adaptive(light: "#10b981", dark: "#047857")
    /// Primary button text — always white
    static let pfButtonPrimaryText = Color.white

    // MARK: Hardware Status (identical in both themes)

    /// Homed indicator — blue
    static let pfHomed = Color(hex: "#2096f3")
    /// Not-homed indicator — orange
    static let pfNotHomed = Color(hex: "#fb8c00")

    // MARK: Extended Status

    /// Maintenance mode — purple
    static let pfMaintenance = adaptive(light: "#7c3aed", dark: "#a78bfa")
    /// Assigned/pending — teal
    static let pfAssigned = adaptive(light: "#0891b2", dark: "#22d3ee")
    /// Temperature mild (>50°C) — yellow
    static let pfTempMild = adaptive(light: "#ca8a04", dark: "#facc15")
}
