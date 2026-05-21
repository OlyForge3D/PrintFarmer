import SwiftUI

struct JobTimelineView: View {
    @Environment(ServiceContainer.self) private var services
    @State private var viewModel = JobHistoryViewModel()

    var body: some View {
        Group {
            if viewModel.isLoading && viewModel.timeline.isEmpty {
                ProgressView("Loading timeline…")
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
            } else if viewModel.timeline.isEmpty {
                EmptyStateView(
                    icon: "chart.line.text.clipboard",
                    title: "No Timeline Events",
                    message: "Job state changes will appear here."
                )
            } else {
                timelineContent
            }
        }
        .navigationTitle("Job Timeline")
        #if os(iOS)
        .navigationBarTitleDisplayMode(.large)
        #endif
        .refreshable {
            await viewModel.loadTimeline(dateFrom: nil, dateTo: nil)
        }
        .task {
            viewModel.configure(jobAnalyticsService: services.jobAnalyticsService)
            await viewModel.loadTimeline(dateFrom: nil, dateTo: nil)
        }
        .onDisappear {
            viewModel.isViewActive = false
        }
    }

    // MARK: - Timeline Content

    private var timelineContent: some View {
        ScrollView {
            LazyVStack(spacing: 0) {
                ForEach(Array(viewModel.timeline.enumerated()), id: \.offset) { index, event in
                    timelineEventRow(event, isLast: index == viewModel.timeline.count - 1)
                }
            }
            .padding()
        }
    }

    private func timelineEventRow(_ event: TimelineEvent, isLast: Bool) -> some View {
        HStack(alignment: .top, spacing: 12) {
            // Timeline indicator
            VStack(spacing: 0) {
                Circle()
                    .fill(stateColor(event.state))
                    .frame(width: 12, height: 12)

                if !isLast {
                    Rectangle()
                        .fill(Color.pfBorder)
                        .frame(width: 2)
                        .frame(minHeight: 40)
                }
            }

            // Event content
            VStack(alignment: .leading, spacing: 4) {
                HStack {
                    Text(event.jobName)
                        .font(.subheadline.weight(.medium))
                        .lineLimit(1)
                    Spacer()
                    Text(event.enteredAtUtc, style: .time)
                        .font(.caption2)
                        .foregroundStyle(.tertiary)
                }

                Text(event.state.capitalized)
                    .font(.caption2.weight(.medium))
                    .foregroundStyle(stateColor(event.state))

                Label(event.printerName, systemImage: "printer")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
            .padding(.bottom, 8)
        }
    }

    // MARK: - Helpers

    private func stateColor(_ state: String) -> Color {
        switch state.lowercased() {
        case "completed": return .pfSuccess
        case "printing", "starting": return .pfAccent
        case "queued", "assigned": return .pfSecondaryAccent
        case "failed": return .pfError
        case "cancelled": return .pfTextTertiary
        case "paused": return .pfWarning
        default: return .pfTextSecondary
        }
    }
}
