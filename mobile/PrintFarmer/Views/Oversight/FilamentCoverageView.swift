import SwiftUI

struct FilamentCoverageView: View {
    @Environment(ServiceContainer.self) private var services
    @Environment(\.dismiss) private var dismiss
    @State private var viewModel = FarmFilamentCoverageViewModel()
    @State private var isLoading = true
    @State private var retryTask: Task<Void, Never>?

    private var filamentCoverageEnabled: Bool {
        services.capabilitiesService.resolved.filamentCoverageEnabled
    }

    private var coverage: [PrinterFilamentCoverage] {
        viewModel.coverageByPrinter.values.sorted {
            $0.printerName.localizedCaseInsensitiveCompare($1.printerName) == .orderedAscending
        }
    }

    var body: some View {
        VStack(spacing: 0) {
            if viewModel.isStaleCacheReportable {
                ConnectionStatusBar(
                    status: .offline,
                    lastConfirmedAt: viewModel.cacheLastUpdatedAt,
                    hasCache: true
                )
            }

            Group {
                if isLoading && coverage.isEmpty {
                    ProgressView("Loading filament coverage…")
                        .frame(maxWidth: .infinity, maxHeight: .infinity)
                        .accessibilityIdentifier("oversight.filamentCoverage.loading")
                } else if let error = viewModel.lastLoadError, coverage.isEmpty {
                    errorState(error)
                } else if coverage.isEmpty {
                    EmptyStateView(
                        icon: "gauge.with.dots.needle.50percent",
                        title: "No Coverage Data",
                        message: "No printers currently report filament coverage."
                    )
                    .accessibilityIdentifier("oversight.filamentCoverage.empty")
                } else {
                    coverageList
                }
            }
        }
        .navigationTitle("Filament Coverage")
        .navigationBarTitleDisplayMode(.inline)
        .refreshable {
            await reload()
        }
        .task(id: filamentCoverageEnabled) {
            guard filamentCoverageEnabled else {
                viewModel.disableForCapabilityGate()
                isLoading = false
                dismiss()
                return
            }

            isLoading = true
            viewModel.configure(coverageService: services.filamentCoverageService)
            viewModel.configureSignalR(services.signalRService)
            viewModel.configureCache(services.filamentCoverageReadCache)
            await viewModel.bootstrap(startupPrefetchStore: services.startupPrefetchStore)
            isLoading = false
        }
        .onDisappear {
            retryTask?.cancel()
            viewModel.tearDownSignalR()
        }
        .onReceive(
            NotificationCenter.default.publisher(
                for: UIApplication.willEnterForegroundNotification
            )
        ) { _ in
            retryTask = Task { await reload() }
        }
        .accessibilityIdentifier("oversight.filamentCoverage")
    }

    private var coverageList: some View {
        List(coverage) { printerCoverage in
            NavigationLink(
                value: AppDestination.printerDetail(id: printerCoverage.printerId)
            ) {
                FilamentCoveragePrinterRow(coverage: printerCoverage)
            }
            .accessibilityLabel(printerCoverage.printerName)
            .accessibilityValue(accessibilityValue(for: printerCoverage))
            .accessibilityHint("Opens \(printerCoverage.printerName) printer details.")
            .accessibilityIdentifier(
                "oversight.filamentCoverage.printer.\(printerCoverage.printerId.uuidString)"
            )
        }
        .listStyle(.insetGrouped)
    }

    private func errorState(_ message: String) -> some View {
        VStack(spacing: 16) {
            Image(systemName: "exclamationmark.triangle")
                .font(.largeTitle)
                .foregroundStyle(.secondary)
                .accessibilityHidden(true)
            Text("Unable to Load Coverage")
                .font(.headline)
            Text(message)
                .font(.body)
                .foregroundStyle(.secondary)
                .multilineTextAlignment(.center)
            Button("Retry") {
                retryTask?.cancel()
                retryTask = Task { await reload() }
            }
            .buttonStyle(.borderedProminent)
            .frame(minHeight: OversightHubView.minimumRowHeight)
            .accessibilityHint("Attempts to load filament coverage again.")
            .accessibilityIdentifier("oversight.filamentCoverage.retry")
        }
        .padding()
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .accessibilityIdentifier("oversight.filamentCoverage.error")
    }

    private func reload() async {
        isLoading = coverage.isEmpty
        await viewModel.load()
        isLoading = false
    }

    private func accessibilityValue(
        for printerCoverage: PrinterFilamentCoverage
    ) -> String {
        let status: String = switch printerCoverage.status {
        case .covers:
            "Filament covers assigned work"
        case .runout:
            "Filament may run out before assigned work finishes"
        case .unknown:
            "Filament coverage unknown"
        }
        let activeJob = printerCoverage.activeJobName.map { "Active job \($0)" }
        let details = [
            status,
            activeJob,
            FilamentCoveragePrinterRow.summary(for: printerCoverage)
        ].compactMap { $0 }
        return details.joined(separator: ", ")
    }
}

private struct FilamentCoveragePrinterRow: View {
    let coverage: PrinterFilamentCoverage

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack(alignment: .firstTextBaseline, spacing: 8) {
                Text(coverage.printerName)
                    .font(.headline)
                Spacer()
                statusView
            }

            if let activeJobName = coverage.activeJobName {
                Label(activeJobName, systemImage: "printer.fill")
                    .font(.subheadline)
                    .foregroundStyle(.secondary)
            }

            Text(toolheadSummary)
                .font(.subheadline)
                .foregroundStyle(.secondary)
        }
        .padding(.vertical, 6)
        .frame(minHeight: OversightHubView.minimumRowHeight)
        .fixedSize(horizontal: false, vertical: true)
    }

    @ViewBuilder
    private var statusView: some View {
        switch coverage.status {
        case .covers, .runout:
            FilamentCoverageStatusLabel(
                status: coverage.status,
                earliestPredictedRunoutAt: coverage.earliestPredictedRunoutAt
            )
        case .unknown:
            Text("Unknown")
                .font(.caption.weight(.semibold))
                .foregroundStyle(.secondary)
        }
    }

    private var toolheadSummary: String {
        Self.summary(for: coverage)
    }

    static func summary(for coverage: PrinterFilamentCoverage) -> String {
        let toolheadCount = coverage.toolheads.count
        let queuedCount = coverage.assignedQueuedJobCount
        let toolheadText = toolheadCount == 1 ? "1 toolhead" : "\(toolheadCount) toolheads"
        let queuedText = queuedCount == 1 ? "1 queued job" : "\(queuedCount) queued jobs"
        return "\(toolheadText) · \(queuedText)"
    }
}

private struct FilamentCoverageStatusLabel: View {
    let status: FilamentCoverageStatus
    let earliestPredictedRunoutAt: Date?

    private static let timeFormatter: DateFormatter = {
        let formatter = DateFormatter()
        formatter.dateStyle = .none
        formatter.timeStyle = .short
        return formatter
    }()

    var body: some View {
        Label(title, systemImage: systemImage)
            .font(.caption.weight(.semibold))
            .foregroundStyle(.primary)
            .padding(.horizontal, 8)
            .padding(.vertical, 4)
            .background(tint.opacity(0.16), in: Capsule())
            .overlay {
                Capsule()
                    .strokeBorder(tint, lineWidth: 1)
            }
            .accessibilityLabel(accessibilityLabel)
    }

    private var title: String {
        switch status {
        case .covers:
            "Covers work"
        case .runout:
            earliestPredictedRunoutAt.map {
                "Runout · \(Self.timeFormatter.string(from: $0))"
            } ?? "Runout risk"
        case .unknown:
            "Unknown"
        }
    }

    private var systemImage: String {
        switch status {
        case .covers:
            "checkmark.seal.fill"
        case .runout:
            earliestPredictedRunoutAt == nil
                ? "exclamationmark.triangle.fill"
                : "clock.badge.exclamationmark.fill"
        case .unknown:
            "questionmark.circle"
        }
    }

    private var tint: Color {
        switch status {
        case .covers:
            Color.pfSuccess
        case .runout:
            earliestPredictedRunoutAt == nil ? Color.pfError : Color.pfWarning
        case .unknown:
            Color.secondary
        }
    }

    private var accessibilityLabel: String {
        switch status {
        case .covers:
            "Filament covers assigned work"
        case .runout:
            earliestPredictedRunoutAt.map {
                "Filament will run out at \(Self.timeFormatter.string(from: $0))"
            } ?? "Filament may run out before assigned work finishes"
        case .unknown:
            "Filament coverage unknown"
        }
    }
}
