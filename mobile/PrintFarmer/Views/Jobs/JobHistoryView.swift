import Foundation
import SwiftUI

struct JobHistoryView: View {
    private struct ActiveTask {
        let activationToken: UUID
        let task: Task<Void, Never>
    }

    @Environment(ServiceContainer.self) private var services
    @State private var viewModel = JobHistoryViewModel()
    @State private var showDateFilter = false
    @State private var activeTasks: [ActiveTask] = []
    @State private var activationToken: UUID?

    var body: some View {
        Group {
            if viewModel.isLoading && viewModel.historyItems.isEmpty {
                ProgressView("Loading history…")
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
            } else if let error = viewModel.error, viewModel.historyItems.isEmpty {
                ContentUnavailableView {
                    Label("Error", systemImage: "exclamationmark.triangle")
                } description: {
                    Text(error)
                } actions: {
                    Button("Retry", action: startHistoryLoad)
                }
            } else if viewModel.historyItems.isEmpty {
                EmptyStateView(
                    icon: "clock.arrow.circlepath",
                    title: "No History",
                    message: "Job history will appear here as jobs complete."
                )
            } else {
                historyList
            }
        }
        .navigationTitle("Job History")
        #if os(iOS)
        .navigationBarTitleDisplayMode(.large)
        #endif
        .toolbar {
            ToolbarItem(placement: .primaryAction) {
                Button {
                    showDateFilter.toggle()
                } label: {
                    Image(systemName: "line.3.horizontal.decrease.circle")
                }
            }

            ToolbarItem(placement: .primaryAction) {
                NavigationLink(value: AppDestination.jobTimeline) {
                    Image(systemName: "chart.line.text.clipboard")
                }
            }
        }
        .sheet(isPresented: $showDateFilter) {
            dateFilterSheet
        }
        .refreshable {
            guard let activationToken else { return }
            await viewModel.loadHistory(activationToken: activationToken)
        }
        .task {
            await JobAnalyticsMountLifecycle.runHistory(
                viewModel: viewModel,
                service: services.jobAnalyticsService,
                onAcquire: { token in
                    activationToken = token
                },
                onRelease: { token in
                    releaseMount(activationToken: token)
                }
            )
        }
    }

    // MARK: - History List

    private var historyList: some View {
        List {
            ForEach(viewModel.historyItems, id: \.id) { item in
                historyRow(item)
            }

            if viewModel.canLoadMore {
                HStack {
                    Spacer()
                    if viewModel.isLoadingMore {
                        ProgressView()
                    } else {
                        Button("Load More", action: startLoadMore)
                    }
                    Spacer()
                }
                .listRowSeparator(.hidden)
            }
        }
        .listStyle(.plain)
    }

    private func historyRow(_ item: QueueHistoryEntry) -> some View {
        VStack(alignment: .leading, spacing: 4) {
            HStack {
                Text(item.jobName)
                    .font(.subheadline.weight(.medium))
                    .lineLimit(1)
                Spacer()
                Text(item.statusBadgeText)
                    .font(.caption2.weight(.semibold))
                    .padding(.horizontal, 6)
                    .padding(.vertical, 2)
                    .background(statusColor(item.status).opacity(0.15), in: Capsule())
                    .foregroundStyle(statusColor(item.status))
            }

            HStack {
                if let printer = item.printerName {
                    Label(printer, systemImage: "printer")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }
                Spacer()
                if let completedAt = item.completedAt {
                    Text(completedAt, style: .relative)
                        .font(.caption2)
                        .foregroundStyle(.tertiary)
                }
            }

            if let durationMinutes = item.durationSeconds.map({ $0 / 60 }) {
                Label("\(durationMinutes)m print time", systemImage: "clock")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }

            if let failureReason = item.failureReason, item.status.lowercased() == "failed" {
                Text(failureReason)
                    .font(.caption)
                    .foregroundStyle(Color.pfError)
                    .lineLimit(2)
            }

            historyDetails(item)
        }
        .padding(.vertical, 4)
    }

    @ViewBuilder
    private func historyDetails(_ item: QueueHistoryEntry) -> some View {
        let material = materialSummary(item)
        let filamentUsage = filamentUsageSummary(item)
        let cost = costSummary(item)
        let toolheads = toolheadUsageSummary(item)

        if material != nil || filamentUsage != nil || cost != nil || toolheads != nil {
            VStack(alignment: .leading, spacing: 4) {
                HStack(spacing: 12) {
                    if let material {
                        Label(material, systemImage: "cube.box")
                            .font(.caption)
                            .foregroundStyle(.secondary)
                            .lineLimit(1)
                    }

                    if let filamentUsage {
                        Label(filamentUsage, systemImage: "scalemass")
                            .font(.caption)
                            .foregroundStyle(.secondary)
                            .lineLimit(1)
                    }

                    if let cost {
                        Label(cost, systemImage: "dollarsign.circle")
                            .font(.caption)
                            .foregroundStyle(.secondary)
                            .lineLimit(1)
                    }
                }

                if let toolheads {
                    Text(toolheads)
                        .font(.caption2)
                        .foregroundStyle(.tertiary)
                        .lineLimit(2)
                }
            }
        }
    }

    // MARK: - Date Filter Sheet

    private var dateFilterSheet: some View {
        NavigationStack {
            Form {
                Section("Date Range") {
                    DatePicker(
                        "From",
                        selection: Binding(
                            get: { viewModel.dateFrom ?? Calendar.current.date(byAdding: .month, value: -1, to: .now)! },
                            set: { viewModel.dateFrom = $0 }
                        ),
                        displayedComponents: .date
                    )

                    DatePicker(
                        "To",
                        selection: Binding(
                            get: { viewModel.dateTo ?? .now },
                            set: { viewModel.dateTo = $0 }
                        ),
                        displayedComponents: .date
                    )
                }

                Section {
                    Button("Apply") {
                        showDateFilter = false
                        startHistoryLoad()
                    }

                    Button("Clear Dates") {
                        viewModel.dateFrom = nil
                        viewModel.dateTo = nil
                        showDateFilter = false
                        startHistoryLoad()
                    }
                }
            }
            .navigationTitle("Filter")
            #if os(iOS)
            .navigationBarTitleDisplayMode(.inline)
            #endif
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button("Cancel") { showDateFilter = false }
                }
            }
        }
        .presentationDetents([.medium])
    }

    private func startHistoryLoad() {
        guard let activationToken else { return }
        let task = Task {
            await viewModel.loadHistory(activationToken: activationToken)
        }
        activeTasks.append(ActiveTask(activationToken: activationToken, task: task))
    }

    private func startLoadMore() {
        guard let activationToken else { return }
        let task = Task {
            await viewModel.loadMore(activationToken: activationToken)
        }
        activeTasks.append(ActiveTask(activationToken: activationToken, task: task))
    }

    private func releaseMount(activationToken releasedToken: UUID) {
        activeTasks
            .filter { $0.activationToken == releasedToken }
            .forEach { $0.task.cancel() }
        activeTasks.removeAll { $0.activationToken == releasedToken }
        if activationToken == releasedToken {
            activationToken = nil
        }
    }

    // MARK: - Helpers

    private func materialSummary(_ item: QueueHistoryEntry) -> String? {
        var parts: [String] = []
        if let materialType = item.materialType?.trimmingCharacters(in: .whitespacesAndNewlines), !materialType.isEmpty {
            parts.append(materialType)
        }
        if let filamentName = item.filamentName?.trimmingCharacters(in: .whitespacesAndNewlines), !filamentName.isEmpty {
            parts.append(filamentName)
        }
        return parts.isEmpty ? nil : parts.joined(separator: " · ")
    }

    private func filamentUsageSummary(_ item: QueueHistoryEntry) -> String? {
        guard let grams = item.displayFilamentUsageGrams else { return nil }
        let suffix = item.displayFilamentUsageIsEstimated ? " est." : ""
        return String(format: "%.1fg%@", grams, suffix)
    }

    private func costSummary(_ item: QueueHistoryEntry) -> String? {
        guard let cost = item.displayMaterialCostUsd else { return nil }
        let suffix = item.costIsEstimated == true ? " est." : ""
        let materialCostText = "\(currencyString(cost))\(suffix)"

        if let totalCost = item.totalCostUsd, costDiffers(totalCost, from: cost) {
            return "\(materialCostText) · \(currencyString(totalCost)) total"
        }

        return materialCostText
    }

    private func costDiffers(_ lhs: Decimal, from rhs: Decimal) -> Bool {
        let left = NSDecimalNumber(decimal: lhs).doubleValue
        let right = NSDecimalNumber(decimal: rhs).doubleValue
        return abs(left - right) > 0.005
    }

    private func toolheadUsageSummary(_ item: QueueHistoryEntry) -> String? {
        guard let usages = item.toolheadUsages, !usages.isEmpty else { return nil }
        let summaries = usages.map { usage in
            let label = "T\(usage.toolheadIndex ?? 0)"
            let filament = usage.filamentName?.trimmingCharacters(in: .whitespacesAndNewlines)
            let grams: String
            if let actualGrams = usage.filamentUsageGrams, actualGrams > 0 {
                grams = String(format: "%.1fg", actualGrams)
            } else if let estimateGrams = usage.slicerEstimateGrams, estimateGrams > 0 {
                grams = String(format: "%.1fg est.", estimateGrams)
            } else {
                grams = "—"
            }

            if let filament, !filament.isEmpty {
                return "\(label): \(filament) \(grams)"
            }
            return "\(label): \(grams)"
        }
        return summaries.joined(separator: " · ")
    }

    private func currencyString(_ amount: Decimal) -> String {
        String(format: "$%.2f", NSDecimalNumber(decimal: amount).doubleValue)
    }

    private func statusColor(_ status: String) -> Color {
        switch status.lowercased() {
        case "completed": return .pfSuccess
        case "failed": return .pfError
        case "cancelled": return .pfTextTertiary
        default: return .pfTextSecondary
        }
    }
}
