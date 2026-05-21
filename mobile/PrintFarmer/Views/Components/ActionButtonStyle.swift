import SwiftUI

/// A custom button style that provides Apple HIG-compliant touch targets for full-width action buttons.
/// Minimum 44pt height per Apple Human Interface Guidelines, with options for prominent actions.
struct ActionButtonStyle: ButtonStyle {
    enum Size {
        case standard  // 44pt - minimum HIG compliance
        case prominent // 50pt - for primary actions that need extra emphasis
        
        var height: CGFloat {
            switch self {
            case .standard: return 44
            case .prominent: return 50
            }
        }
    }
    
    let size: Size
    
    init(size: Size = .standard) {
        self.size = size
    }
    
    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .frame(minHeight: size.height)
            .opacity(configuration.isPressed ? 0.7 : 1.0)
    }
}

/// View modifier to apply full-width action button styling with proper touch targets
struct FullWidthActionButton: ViewModifier {
    enum Prominence {
        case standard  // 44pt height
        case prominent // 50pt height for primary actions
    }
    
    let prominence: Prominence
    
    func body(content: Content) -> some View {
        content
            .frame(maxWidth: .infinity)
            .frame(minHeight: prominence == .prominent ? 50 : 44)
    }
}

extension View {
    /// Applies full-width action button styling with Apple HIG-compliant touch targets.
    /// - Parameter prominence: Use `.prominent` for primary actions (50pt), `.standard` for secondary (44pt)
    func fullWidthActionButton(prominence: FullWidthActionButton.Prominence = .standard) -> some View {
        modifier(FullWidthActionButton(prominence: prominence))
    }
}
