import SwiftUI

struct PageIndicator: View {
    @Binding var currentPage: Int
    let pageCount: Int
    let labels: [String]
    var accessibilityIdentifierPrefix = "pageIndicator.page"
    
    var body: some View {
        VStack(spacing: 8) {
            // Dots
            HStack(spacing: 6) {
                ForEach(0..<pageCount, id: \.self) { index in
                    Circle()
                        .fill(index == currentPage ? Color.pfAccent : Color.pfTextTertiary.opacity(0.5))
                        .frame(width: 8, height: 8)
                        .onTapGesture {
                            withAnimation(.easeInOut(duration: 0.25)) {
                                currentPage = index
                            }
                        }
                }
            }
            .accessibilityHidden(true)
            
            // Labels
            HStack(spacing: 0) {
                ForEach(0..<pageCount, id: \.self) { index in
                    Button {
                        withAnimation(.easeInOut(duration: 0.25)) {
                            currentPage = index
                        }
                    } label: {
                        Text(labels[index])
                            .font(.caption2)
                            .foregroundStyle(index == currentPage ? Color.pfAccent : Color.pfTextSecondary)
                            .frame(maxWidth: .infinity, minHeight: 44)
                            .contentShape(Rectangle())
                    }
                    .buttonStyle(.plain)
                    .accessibilityLabel(labels[index])
                    .accessibilityValue(index == currentPage ? "Selected" : "Not selected")
                    .accessibilityHint("Shows the \(labels[index]) page.")
                    .accessibilityAddTraits(index == currentPage ? .isSelected : [])
                    .accessibilityIdentifier(
                        "\(accessibilityIdentifierPrefix).\(identifierComponent(labels[index]))"
                    )
                }
            }
        }
        .padding(.vertical, 8)
    }

    private func identifierComponent(_ label: String) -> String {
        label.lowercased().replacingOccurrences(of: " ", with: "-")
    }
}
