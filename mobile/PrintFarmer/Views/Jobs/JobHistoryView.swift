import SwiftUI

struct JobHistoryView: View {
    @Environment(ServiceContainer.self) private var services
    @State private var viewModel = JobHistoryViewModel()
    @State private var showDateFilter = false
    @State private var activeTasks: [Task<Void, Never>] = []

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
                    Button("Retry") {
                        let task = Task { await viewModel.loadHistory() }
                        activeTasks.append(task)
                    }
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
            await viewModel.loadHistory()
        }
        .task {
            viewModel.configure(jobAnalyticsService: services.jobAnalyticsService)
            await viewModel.loadHistory()
        }
        .onDisappear {
            activeTasks.forEach { $0.cancel() }
            activeTasks.removeAll()
            viewModel.isViewActive = false
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
                        Button("Load More") {
                            let task = Task { await viewModel.loadMore() }
                            activeTasks.append(task)
                        }
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
                Text(item.status.capitalized)
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
        }
        .padding(.vertical, 4)
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
                        let task = Task { await viewModel.loadHistory() }
                        activeTasks.append(task)
                    }

                    Button("Clear Dates") {
                        viewModel.dateFrom = nil
                        viewModel.dateTo = nil
                        showDateFilter = false
                        let task = Task { await viewModel.loadHistory() }
                        activeTasks.append(task)
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

    // MARK: - Helpers

    private func statusColor(_ status: String) -> Color {
        switch status.lowercased() {
        case "completed": return .pfSuccess
        case "failed": return .pfError
        case "cancelled": return .pfTextTertiary
        default: return .pfTextSecondary
        }
    }
}
