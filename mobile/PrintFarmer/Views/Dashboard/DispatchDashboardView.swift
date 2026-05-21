import SwiftUI

struct DispatchDashboardView: View {
    @Environment(ServiceContainer.self) private var services
    @Environment(\.horizontalSizeClass) private var sizeClass
    @State private var viewModel = DispatchViewModel()
    @State private var retryTask: Task<Void, Never>?

    var body: some View {
        Group {
            if viewModel.isLoading && viewModel.queueStatus == nil {
                ProgressView("Loading dispatch…")
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
            } else if let error = viewModel.error, viewModel.queueStatus == nil {
                ContentUnavailableView {
                    Label("Error", systemImage: "exclamationmark.triangle")
                } description: {
                    Text(error)
                } actions: {
                    Button("Retry") {
                        retryTask = Task {
                            await viewModel.loadQueueStatus()
                            await viewModel.loadHistory()
                        }
                    }
                }
            } else {
                dispatchContent
            }
        }
        .navigationTitle("Dispatch")
        #if os(iOS)
        .navigationBarTitleDisplayMode(.large)
        #endif
        .refreshable {
            await viewModel.loadQueueStatus()
            await viewModel.loadHistory()
        }
        .task {
            viewModel.configure(dispatchService: services.dispatchService)
            await viewModel.loadQueueStatus()
            await viewModel.loadHistory()
        }
        .onDisappear {
            retryTask?.cancel()
            viewModel.isViewActive = false
        }
    }

    // MARK: - Content

    private var dispatchContent: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 20) {
                statusCards
                historySection
            }
            .padding()
        }
    }

    // MARK: - Status Cards

    private var statusCards: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Queue Status")
                .font(.title2.bold())

            let twoColumns = Array(repeating: GridItem(.flexible()), count: 2)
            let threeColumns = sizeClass == .regular
                ? Array(repeating: GridItem(.flexible()), count: 3)
                : Array(repeating: GridItem(.flexible()), count: 3)

            // Row 1 — Queue Overview
            LazyVGrid(columns: twoColumns, spacing: 12) {
                dispatchCard(
                    title: "Queued Jobs",
                    value: "\(viewModel.totalQueuedJobs)",
                    subtitle: "Unassigned: \(viewModel.pendingJobCount)",
                    icon: "tray.full",
                    color: .pfSecondaryAccent
                )

                dispatchCard(
                    title: "Printers",
                    value: "\(viewModel.idlePrinterCount + viewModel.busyPrinterCount)",
                    subtitle: "\(viewModel.idlePrinterCount) Idle · \(viewModel.busyPrinterCount) Busy",
                    icon: "printer",
                    color: .pfAccent
                )
            }

            // Row 2 — Dispatch Stats (24h)
            Text("Last 24 Hours")
                .font(.subheadline.weight(.medium))
                .foregroundStyle(.secondary)
                .padding(.top, 4)

            LazyVGrid(columns: threeColumns, spacing: 12) {
                dispatchCard(
                    title: "Dispatched",
                    value: "\(viewModel.dispatchedLast24h)",
                    icon: "checkmark.circle",
                    color: .pfSuccess
                )

                dispatchCard(
                    title: "Auto",
                    value: "\(viewModel.autoDispatchedLast24h)",
                    icon: "bolt.circle",
                    color: .pfSecondaryAccent
                )

                dispatchCard(
                    title: "Failed",
                    value: "\(viewModel.failedLast24h)",
                    icon: "xmark.circle",
                    color: viewModel.failedLast24h > 0 ? .pfWarning : .secondary
                )
            }

            // Average Score
            if viewModel.averageScoreLast24h > 0 {
                averageScoreCard
            }

            // Printer Queue Depths
            if let depths = viewModel.queueStatus?.printerQueueDepths, !depths.isEmpty {
                printerQueueDepthsSection(depths)
            }
        }
    }

    private var averageScoreCard: some View {
        HStack(spacing: 12) {
            Image(systemName: "gauge.medium")
                .font(.title3)
                .foregroundStyle(Color.pfAccent)

            VStack(alignment: .leading, spacing: 2) {
                Text("Avg Dispatch Score (24h)")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                Text(String(format: "%.1f", viewModel.averageScoreLast24h))
                    .font(.title3.bold().monospacedDigit())
            }

            Spacer()

            ProgressView(value: min(viewModel.averageScoreLast24h / 100.0, 1.0))
                .tint(.pfAccent)
                .frame(width: 80)
        }
        .padding(12)
        .background(Color.pfCard, in: RoundedRectangle(cornerRadius: 12))
        .overlay(
            RoundedRectangle(cornerRadius: 12)
                .strokeBorder(Color.pfBorder, lineWidth: 1)
        )
    }

    private func printerQueueDepthsSection(_ depths: [PrinterQueueDepth]) -> some View {
        VStack(alignment: .leading, spacing: 8) {
            Text("Printer Queues")
                .font(.subheadline.weight(.medium))
                .foregroundStyle(.secondary)
                .padding(.top, 4)

            ForEach(depths, id: \.printerId) { printer in
                HStack(spacing: 10) {
                    Image(systemName: printer.isPrinting ? "printer.fill" : "printer")
                        .foregroundStyle(printer.isAvailable ? Color.pfSuccess : .secondary)

                    Text(printer.printerName)
                        .font(.subheadline)
                        .lineLimit(1)

                    Spacer()

                    Text("\(printer.queueDepth) queued")
                        .font(.caption.monospacedDigit())
                        .foregroundStyle(.secondary)

                    Circle()
                        .fill(printer.isPrinting ? Color.pfAccent : (printer.isAvailable ? .pfSuccess : .secondary))
                        .frame(width: 8, height: 8)
                }
                .padding(10)
                .background(Color.pfCard, in: RoundedRectangle(cornerRadius: 8))
                .overlay(
                    RoundedRectangle(cornerRadius: 8)
                        .strokeBorder(Color.pfBorder, lineWidth: 1)
                )
            }
        }
    }

    private func dispatchCard(
        title: String,
        value: String,
        subtitle: String? = nil,
        icon: String,
        color: Color
    ) -> some View {
        VStack(spacing: 6) {
            Image(systemName: icon)
                .font(.title3)
                .foregroundStyle(color)

            Text(value)
                .font(.title2.bold().monospacedDigit())

            Text(title)
                .font(.caption)
                .foregroundStyle(.secondary)
                .multilineTextAlignment(.center)

            if let subtitle {
                Text(subtitle)
                    .font(.caption2)
                    .foregroundStyle(.tertiary)
                    .multilineTextAlignment(.center)
            }
        }
        .frame(maxWidth: .infinity)
        .padding(.vertical, 14)
        .background(Color.pfCard, in: RoundedRectangle(cornerRadius: 12))
        .overlay(
            RoundedRectangle(cornerRadius: 12)
                .strokeBorder(Color.pfBorder, lineWidth: 1)
        )
    }

    // MARK: - History Section

    private var historySection: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Recent Dispatches")
                .font(.title2.bold())

            if viewModel.history.isEmpty {
                Text("No recent dispatch history")
                    .font(.subheadline)
                    .foregroundStyle(.secondary)
                    .frame(maxWidth: .infinity)
                    .padding(.vertical, 20)
            } else {
                ForEach(viewModel.history.prefix(20), id: \.id) { entry in
                    historyRow(entry)
                }
            }
        }
    }

    private func historyRow(_ entry: DispatchHistoryEntry) -> some View {
        HStack(spacing: 10) {
            Image(systemName: "arrow.right.circle.fill")
                .foregroundStyle(Color.pfAccent)

            VStack(alignment: .leading, spacing: 2) {
                Text(entry.jobName ?? "Job \(entry.printJobId.uuidString.prefix(8))")
                    .font(.subheadline.weight(.medium))
                    .lineLimit(1)

                HStack(spacing: 8) {
                    if let printer = entry.printerName {
                        Label(printer, systemImage: "printer")
                            .font(.caption)
                            .foregroundStyle(.secondary)
                    }
                    Text(entry.action)
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }
            }

            Spacer()

            Text(entry.createdAtUtc, style: .relative)
                .font(.caption2)
                .foregroundStyle(.tertiary)
        }
        .padding(10)
        .background(Color.pfCard, in: RoundedRectangle(cornerRadius: 8))
        .overlay(
            RoundedRectangle(cornerRadius: 8)
                .strokeBorder(Color.pfBorder, lineWidth: 1)
        )
    }
}
