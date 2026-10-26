import SwiftUI

/// Shared visual treatments for the Printer Controls subgroups (Preheat, Home, Jog).
///
/// Spec: `mobile/docs/design/printer-controls-section.md` §2.4 (state variants).
/// Issue: #288.
///
/// Two modifiers:
/// * `disabledControlStyle(isDisabled:)` — applies the 8% diagonal-stripe overlay
///   and 50% opacity treatment Newt specified for color-blind users. Greyscale
///   alone is not sufficient (printing red/green colorblind users can mistake
///   greyed buttons for active).
/// * `errorBorderHighlight(isActive:)` — applies the 1.5pt `pfError` border that
///   the spec calls for on error state. The caller is responsible for keeping
///   `isActive` true for the spec-defined 4-second window.

extension View {
    /// Overlays the disabled diagonal-stripe pattern (§2.4) when `isDisabled`
    /// is true. Stripes are 8% white at 45°, applied as a clipped overlay so
    /// the underlying button shape is preserved.
    func disabledControlStyle(isDisabled: Bool, cornerRadius: CGFloat = 10) -> some View {
        modifier(DisabledControlStyleModifier(isDisabled: isDisabled, cornerRadius: cornerRadius))
    }

    /// Adds the 1.5pt `pfError` border per §2.4 error state when `isActive` is
    /// true. Animated with a brief fade so the cue is noticeable but not jarring.
    func errorBorderHighlight(isActive: Bool, cornerRadius: CGFloat = 10) -> some View {
        modifier(ErrorBorderHighlightModifier(isActive: isActive, cornerRadius: cornerRadius))
    }
}

struct DisabledControlStyleModifier: ViewModifier {
    let isDisabled: Bool
    let cornerRadius: CGFloat

    func body(content: Content) -> some View {
        content
            .opacity(isDisabled ? 0.5 : 1.0)
            .overlay {
                if isDisabled {
                    DiagonalStripePattern()
                        .clipShape(RoundedRectangle(cornerRadius: cornerRadius, style: .continuous))
                        .allowsHitTesting(false)
                        .accessibilityHidden(true)
                }
            }
    }
}

struct ErrorBorderHighlightModifier: ViewModifier {
    let isActive: Bool
    let cornerRadius: CGFloat

    func body(content: Content) -> some View {
        content
            .overlay(
                RoundedRectangle(cornerRadius: cornerRadius, style: .continuous)
                    .strokeBorder(isActive ? Color.pfError : Color.clear, lineWidth: isActive ? 1.5 : 0)
            )
            .animation(.easeInOut(duration: 0.2), value: isActive)
    }
}

/// Repeating 45° diagonal stripe pattern at 8% opacity, drawn via Canvas so it
/// scales with the button. Matches §2.4: "subtle ... 8% white-on-charcoal
/// stripes at 45°". Honors `accessibilityReduceTransparency` by falling back
/// to a flat opacity treatment so the pattern doesn't read as visual noise to
/// users with reduced transparency preferences.
struct DiagonalStripePattern: View {
    @Environment(\.accessibilityReduceTransparency) private var reduceTransparency

    /// Spacing between stripe centers (pt). 6pt looks like a fine industrial
    /// hatch on iPhone and iPad without becoming a moire on retina displays.
    static let stripeSpacing: CGFloat = 6
    /// Width of each stripe line (pt).
    static let stripeWidth: CGFloat = 2

    var body: some View {
        if reduceTransparency {
            Color.pfTextTertiary.opacity(0.15)
        } else {
            Canvas { ctx, size in
                let diagonal = size.width + size.height
                let stripeColor = Color.white.opacity(0.08)
                var x: CGFloat = -size.height
                while x < diagonal {
                    var path = Path()
                    path.move(to: CGPoint(x: x, y: 0))
                    path.addLine(to: CGPoint(x: x + size.height, y: size.height))
                    ctx.stroke(path, with: .color(stripeColor), lineWidth: Self.stripeWidth)
                    x += Self.stripeSpacing
                }
            }
        }
    }
}
