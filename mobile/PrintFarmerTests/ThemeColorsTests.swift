import SwiftUI
import UIKit
import XCTest
@testable import PrintFarmer

@MainActor
final class ThemeColorsTests: XCTestCase {
    private let minimumTextContrast = 4.5

    func test_statusTextColorsMeetContrastFloorInBothAppearances() {
        assertContrast(Color.pfError, against: Color.pfBackground, style: .light)
        assertContrast(Color.pfError, against: Color.pfBackground, style: .dark)
        assertContrast(Color.pfWarning, against: Color.pfBackground, style: .light)
        assertContrast(Color.pfWarning, against: Color.pfBackground, style: .dark)
    }

    func test_primaryButtonWhiteLabelMeetsContrastFloorInBothAppearances() {
        assertContrast(Color.pfButtonPrimaryText, against: Color.pfButtonPrimary, style: .light)
        assertContrast(Color.pfButtonPrimaryText, against: Color.pfButtonPrimary, style: .dark)
    }

    func test_whiteLabelStatusFillsMeetContrastFloor() {
        assertContrast(.white, against: Color.pfErrorFill, style: .light)
        assertContrast(.white, against: Color.pfErrorFill, style: .dark)
        assertContrast(.white, against: Color.pfWarningFill, style: .light)
        assertContrast(.white, against: Color.pfWarningFill, style: .dark)
    }

    func test_brandSideValuesArePreserved() {
        assertColor(Color.pfError, equals: "#dc2626", style: .light)
        assertColor(Color.pfWarning, equals: "#d97706", style: .dark)
    }

    private func assertContrast(
        _ foreground: Color,
        against background: Color,
        style: UIUserInterfaceStyle,
        file: StaticString = #filePath,
        line: UInt = #line
    ) {
        let ratio = contrastRatio(
            foreground: components(for: foreground, style: style),
            background: components(for: background, style: style)
        )

        XCTAssertGreaterThanOrEqual(
            ratio,
            minimumTextContrast,
            "Expected \(ratio):1 contrast in \(style == .dark ? "dark" : "light") mode",
            file: file,
            line: line
        )
    }

    private func assertColor(
        _ color: Color,
        equals hex: String,
        style: UIUserInterfaceStyle,
        file: StaticString = #filePath,
        line: UInt = #line
    ) {
        let actual = components(for: color, style: style)
        let expected = components(for: Color(hex: hex), style: style)

        XCTAssertEqual(actual.red, expected.red, accuracy: 0.0001, file: file, line: line)
        XCTAssertEqual(actual.green, expected.green, accuracy: 0.0001, file: file, line: line)
        XCTAssertEqual(actual.blue, expected.blue, accuracy: 0.0001, file: file, line: line)
    }

    private func components(
        for color: Color,
        style: UIUserInterfaceStyle
    ) -> (red: CGFloat, green: CGFloat, blue: CGFloat) {
        let resolved = UIColor(color).resolvedColor(
            with: UITraitCollection(userInterfaceStyle: style)
        )
        var red: CGFloat = 0
        var green: CGFloat = 0
        var blue: CGFloat = 0
        var alpha: CGFloat = 0

        XCTAssertTrue(resolved.getRed(&red, green: &green, blue: &blue, alpha: &alpha))
        return (red, green, blue)
    }

    private func contrastRatio(
        foreground: (red: CGFloat, green: CGFloat, blue: CGFloat),
        background: (red: CGFloat, green: CGFloat, blue: CGFloat)
    ) -> Double {
        let foregroundLuminance = relativeLuminance(foreground)
        let backgroundLuminance = relativeLuminance(background)
        let lighter = max(foregroundLuminance, backgroundLuminance)
        let darker = min(foregroundLuminance, backgroundLuminance)
        return (lighter + 0.05) / (darker + 0.05)
    }

    private func relativeLuminance(
        _ color: (red: CGFloat, green: CGFloat, blue: CGFloat)
    ) -> Double {
        0.2126 * linearized(color.red)
            + 0.7152 * linearized(color.green)
            + 0.0722 * linearized(color.blue)
    }

    private func linearized(_ component: CGFloat) -> Double {
        let value = Double(component)
        return value <= 0.04045
            ? value / 12.92
            : pow((value + 0.055) / 1.055, 2.4)
    }
}
