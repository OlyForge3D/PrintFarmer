import SwiftUI

struct MaintenanceAnalyticsView: View {
    @Environment(ServiceContainer.self) private var services
    @Environment(\.horizontalSizeClass) private var sizeClass
    @State private var viewModel = MaintenanceViewModel()
    @State private var retryTask: Task<Void, Never>?

    var body: some View {
        Group {
            if viewModel.isLoading && viewModel.uptimeData.isEmpty {
                ProgressView("Loading analytics…")
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
            } else if let error = viewModel.error, viewModel.uptimeData.isEmpty {
                ContentUnavailableView {
                    Label("Error", systemImage: "exclamationmark.triangle")
                } description: {
                    Text(error)
                } actions: {
                    Button("Retry") {
                        retryTask = Task { await viewModel.loadData() }
                    }
                }
            } else {
                analyticsContent
            }
        }
        .navigationTitle("Maintenance Analytics")
        #if os(iOS)
        .navigationBarTitleDisplayMode(.large)
        #endif
        .refreshable {
            await viewModel.loadData()
        }
        .task {
            viewModel.configure(maintenanceService: services.maintenanceService)
            await viewModel.loadData()
        }
        .onDisappear {
            viewModel.isViewActive = false
            retryTask?.cancel()
        }
    }

    // MARK: - Analytics Content

    private var analyticsContent: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 20) {
                uptimeSection
                costSection
            }
            .padding()
        }
    }

    // MARK: - Uptime Section

    private var uptimeSection: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Uptime by Printer")
                .font(.title2.bold())

            if viewModel.uptimeData.isEmpty {
                Text("No uptime data available")
                    .font(.subheadline)
                    .foregroundStyle(.secondary)
            } else {
                ForEach(viewModel.uptimeData, id: \.printerId) { uptime in
                    uptimeRow(uptime)
                }
            }
        }
    }

    private func uptimeRow(_ uptime: PrinterUptime) -> some View {
        VStack(alignment: .leading, spacing: 6) {
            HStack {
                Text(uptime.printerName)
                    .font(.subheadline.weight(.medium))
                Spacer()
                Text(String(format: "%.1f%%", uptime.uptimePercent))
                    .font(.subheadline.weight(.bold).monospacedDigit())
                    .foregroundStyle(uptimeColor(uptime.uptimePercent))
            }

            GeometryReader { geo in
                ZStack(alignment: .leading) {
                    RoundedRectangle(cornerRadius: 4)
                        .fill(Color.pfBackgroundTertiary)
                        .frame(height: 8)

                    RoundedRectangle(cornerRadius: 4)
                        .fill(uptimeColor(uptime.uptimePercent))
                        .frame(width: geo.size.width * (uptime.uptimePercent / 100.0), height: 8)
                }
            }
            .frame(height: 8)

            HStack {
                Label("\(uptime.maintenanceCount) maintenance", systemImage: "wrench")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                Spacer()
                Label("\(uptime.totalDowntimeMinutes)m downtime", systemImage: "clock.badge.xmark")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
        }
        .padding(12)
        .background(Color.pfCard, in: RoundedRectangle(cornerRadius: 10))
        .overlay(
            RoundedRectangle(cornerRadius: 10)
                .strokeBorder(Color.pfBorder, lineWidth: 1)
        )
    }

    private func uptimeColor(_ percentage: Double) -> Color {
        switch percentage {
        case 95...: return .pfSuccess
        case 80...: return .pfWarning
        default: return .pfError
        }
    }

    // MARK: - Cost Section

    private var costSection: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Cost Breakdown")
                .font(.title2.bold())

            if viewModel.costData.isEmpty {
                Text("No cost data available")
                    .font(.subheadline)
                    .foregroundStyle(.secondary)
            } else {
                let columns = sizeClass == .regular
                    ? [GridItem(.flexible()), GridItem(.flexible()), GridItem(.flexible())]
                    : [GridItem(.flexible()), GridItem(.flexible())]

                LazyVGrid(columns: columns, spacing: 12) {
                    ForEach(viewModel.costData, id: \.month) { cost in
                        costCard(cost)
                    }
                }
            }
        }
    }

    private func costCard(_ cost: MaintenanceCost) -> some View {
        VStack(spacing: 6) {
            Text(cost.month)
                .font(.caption.weight(.medium))
                .foregroundStyle(.secondary)

            Text(String(format: "$%.0f", NSDecimalNumber(decimal: cost.totalCost).doubleValue))
                .font(.title3.bold().monospacedDigit())
        }
        .frame(maxWidth: .infinity)
        .padding(.vertical, 14)
        .background(Color.pfCard, in: RoundedRectangle(cornerRadius: 12))
        .overlay(
            RoundedRectangle(cornerRadius: 12)
                .strokeBorder(Color.pfBorder, lineWidth: 1)
        )
    }
}
