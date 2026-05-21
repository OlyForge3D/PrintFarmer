import SwiftUI

/// Horizontal progress bar with percentage label.
struct PrintProgressBar: View {
    let progress: Double
    var showLabel: Bool = true
    var height: CGFloat = 8
    var color: Color = .pfAccent

    var body: some View {
        VStack(alignment: .trailing, spacing: 4) {
            GeometryReader { geo in
                ZStack(alignment: .leading) {
                    Capsule()
                        .fill(color.opacity(0.15))
                        .frame(height: height)

                    Capsule()
                        .fill(color)
                        .frame(width: max(0, geo.size.width * clampedProgress), height: height)
                }
            }
            .frame(height: height)

            if showLabel {
                Text(clampedProgress.percentFormatted)
                    .font(.caption2.monospacedDigit().weight(.medium))
                    .foregroundStyle(.secondary)
            }
        }
    }

    private var clampedProgress: Double {
        min(max(progress, 0), 1)
    }
}
