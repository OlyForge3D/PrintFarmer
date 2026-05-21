import SwiftUI

struct JobStatsCard: View {
    let stats: QueueStats

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Queue Summary")
                .font(.title2.bold())

            LazyVGrid(columns: [
                GridItem(.flexible()),
                GridItem(.flexible()),
                GridItem(.flexible()),
                GridItem(.flexible())
            ], spacing: 12) {
                statItem(title: "Queued", count: stats.totalQueued, color: .pfSecondaryAccent)
                statItem(title: "Printing", count: stats.totalPrinting, color: .pfAccent)
                statItem(title: "Paused", count: stats.totalPaused, color: .pfWarning)
                statItem(title: "Avg Wait", count: stats.averageWaitTimeMinutes, color: .pfSuccess)
            }

            if stats.averageWaitTimeMinutes > 0 {
                HStack(spacing: 6) {
                    Image(systemName: "clock")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                    Text("Avg wait: \(stats.averageWaitTimeMinutes)m")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }
            }
        }
        .padding()
        .background(Color.pfCard, in: RoundedRectangle(cornerRadius: 12))
        .overlay(
            RoundedRectangle(cornerRadius: 12)
                .strokeBorder(Color.pfBorder, lineWidth: 1)
        )
    }

    private func statItem(title: String, count: Int, color: Color) -> some View {
        VStack(spacing: 4) {
            Text("\(count)")
                .font(.title3.bold().monospacedDigit())
                .foregroundStyle(color)

            Text(title)
                .font(.caption2)
                .foregroundStyle(.secondary)
        }
    }
}
