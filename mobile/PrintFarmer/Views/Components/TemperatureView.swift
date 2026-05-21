import SwiftUI

/// Displays current / target temperature with a mini gauge or labeled format.
struct TemperatureView: View {
    let label: String
    let current: Double?
    let target: Double?
    let icon: TemperatureIcon

    var body: some View {
        HStack(spacing: 12) {
            icon.view
                .foregroundStyle(temperatureColor)
                .frame(width: 20, height: 20)
                .frame(width: 28)

            VStack(alignment: .leading, spacing: 2) {
                Text(label)
                    .font(.caption)
                    .foregroundStyle(.secondary)

                HStack(spacing: 4) {
                    Text(current?.temperatureFormatted ?? "--")
                        .font(.title3.monospacedDigit().weight(.semibold))

                    if let target, target > 0 {
                        Text("/ \(target.temperatureFormatted)")
                            .font(.callout.monospacedDigit())
                            .foregroundStyle(.secondary)
                    }
                }
            }

            Spacer()

            if let current, let target, target > 0 {
                CircularProgressView(progress: min(current / target, 1.0), color: temperatureColor)
                    .frame(width: 36, height: 36)
            }
        }
        .padding(.vertical, 4)
        .accessibilityElement(children: .combine)
        .accessibilityLabel(
            "\(label): \(current?.temperatureFormatted ?? "unknown")" +
            "\(target != nil && target! > 0 ? ", target \(target!.temperatureFormatted)" : "")"
        )
    }

    private var temperatureColor: Color {
        let isHeating = (target ?? 0) > 0
        switch icon {
        case .hotend:
            return isHeating ? .red : .red.opacity(0.35)
        case .bed:
            return isHeating ? .blue : .blue.opacity(0.35)
        }
    }
}

/// Small circular progress ring.
struct CircularProgressView: View {
    let progress: Double
    let color: Color

    var body: some View {
        ZStack {
            Circle()
                .stroke(color.opacity(0.2), lineWidth: 3)
            Circle()
                .trim(from: 0, to: progress)
                .stroke(color, style: StrokeStyle(lineWidth: 3, lineCap: .round))
                .rotationEffect(.degrees(-90))
            Text("\(Int(progress * 100))")
                .font(.system(size: 9, weight: .semibold).monospacedDigit())
        }
    }
}
