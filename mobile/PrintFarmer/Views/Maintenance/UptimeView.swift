import SwiftUI

struct UptimeView: View {
    @Environment(ServiceContainer.self) private var services
    @Environment(\.horizontalSizeClass) private var sizeClass
    @State private var viewModel = UptimeViewModel()
    @State private var retryTask: Task<Void, Never>?

    var body: some View {
        Group {
            if viewModel.isLoading && viewModel.uptimeData.isEmpty {
                ProgressView("Loading uptime data…")
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
                uptimeContent
            }
        }
        .navigationTitle("Uptime & Reliability")
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

    // MARK: - Content

    private var uptimeContent: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 20) {
                fleetOverview
                printerUptimeList
                fleetStatsSection
            }
            .padding()
        }
    }

    // MARK: - Fleet Overview

    private var fleetOverview: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Fleet Overview")
                .font(.title2.bold())

            let columns = sizeClass == .regular
                ? Array(repeating: GridItem(.flexible()), count: 3)
                : Array(repeating: GridItem(.flexible()), count: 3)

            LazyVGrid(columns: columns, spacing: 12) {
                overviewCard(
                    title: "Avg Uptime",
                    value: String(format: "%.1f%%", viewModel.averageUptime),
                    icon: "gauge.with.dots.needle.67percent",
                    color: viewModel.averageUptime >= 95 ? .pfSuccess : .pfWarning
                )

                overviewCard(
                    title: "Total Downtime",
                    value: "\(viewModel.totalDowntimeMinutes)m",
                    icon: "clock.badge.xmark",
                    color: .pfError
                )

                overviewCard(
                    title: "Maintenance",
                    value: "\(viewModel.totalMaintenanceCount)",
                    icon: "wrench",
                    color: .pfWarning
                )
            }
        }
    }

    private func overviewCard(title: String, value: String, icon: String, color: Color) -> some View {
        VStack(spacing: 6) {
            Image(systemName: icon)
                .font(.title3)
                .foregroundStyle(color)

            Text(value)
                .font(.title2.bold().monospacedDigit())

            Text(title)
                .font(.caption)
                .foregroundStyle(.secondary)
        }
        .frame(maxWidth: .infinity)
        .padding(.vertical, 14)
        .background(Color.pfCard, in: RoundedRectangle(cornerRadius: 12))
        .overlay(
            RoundedRectangle(cornerRadius: 12)
                .strokeBorder(Color.pfBorder, lineWidth: 1)
        )
    }

    // MARK: - Per-Printer Uptime

    private var printerUptimeList: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Per-Printer Uptime")
                .font(.title2.bold())

            if viewModel.uptimeData.isEmpty {
                Text("No uptime data available")
                    .font(.subheadline)
                    .foregroundStyle(.secondary)
            } else {
                ForEach(viewModel.uptimeData, id: \.printerId) { uptime in
                    printerUptimeRow(uptime)
                }
            }
        }
    }

    private func printerUptimeRow(_ uptime: PrinterUptime) -> some View {
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
                Label("\(uptime.maintenanceCount) events", systemImage: "wrench")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                Spacer()
                Label("\(uptime.totalDowntimeMinutes)m down", systemImage: "clock.badge.xmark")
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

    // MARK: - Fleet Statistics

    private var fleetStatsSection: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Fleet Statistics")
                .font(.title2.bold())

            if viewModel.fleetStats.isEmpty {
                Text("No fleet statistics available")
                    .font(.subheadline)
                    .foregroundStyle(.secondary)
            } else {
                ForEach(viewModel.fleetStats, id: \.printerId) { stat in
                    fleetStatRow(stat)
                }
            }
        }
    }

    private func fleetStatRow(_ stat: FleetPrinterStatistics) -> some View {
        HStack(spacing: 12) {
            VStack(alignment: .leading, spacing: 2) {
                Text(stat.printerName)
                    .font(.subheadline.weight(.medium))

                HStack(spacing: 12) {
                    Label("\(stat.totalJobsCompleted) jobs", systemImage: "doc.text")
                    Label("\(stat.totalJobsFailed) failed", systemImage: "xmark.circle")
                }
                .font(.caption)
                .foregroundStyle(.secondary)
            }

            Spacer()

            VStack(alignment: .trailing, spacing: 2) {
                let totalJobs = stat.totalJobsCompleted + stat.totalJobsFailed
                let successRate = totalJobs > 0 ? Double(stat.totalJobsCompleted) / Double(totalJobs) * 100.0 : 0.0
                Text(String(format: "%.0f%%", successRate))
                    .font(.subheadline.weight(.bold).monospacedDigit())
                    .foregroundStyle(successRate >= 90 ? Color.pfSuccess : Color.pfWarning)

                Text("success")
                    .font(.caption2)
                    .foregroundStyle(.tertiary)
            }
        }
        .padding(12)
        .background(Color.pfCard, in: RoundedRectangle(cornerRadius: 10))
        .overlay(
            RoundedRectangle(cornerRadius: 10)
                .strokeBorder(Color.pfBorder, lineWidth: 1)
        )
    }
}
