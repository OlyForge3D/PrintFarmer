import SwiftUI

struct MaintenanceView: View {
    @Environment(AppRouter.self) private var router
    @Environment(ServiceContainer.self) private var services
    @Environment(\.horizontalSizeClass) private var sizeClass
    @State private var viewModel = MaintenanceViewModel()
    @State private var currentPage = 0
    @State private var retryTask: Task<Void, Never>?

    var body: some View {
        @Bindable var router = router

        NavigationStack(path: $router.maintenancePath) {
            Group {
                if viewModel.isLoading && viewModel.alerts.isEmpty {
                    ProgressView("Loading maintenance…")
                        .frame(maxWidth: .infinity, maxHeight: .infinity)
                } else if let error = viewModel.error, viewModel.alerts.isEmpty && viewModel.upcomingTasks.isEmpty {
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
                    // iPhone: swipeable pages
                    if sizeClass == .compact {
                        VStack(spacing: 0) {
                            TabView(selection: $currentPage) {
                                AlertsPage()
                                    .tag(0)
                                TasksPage()
                                    .tag(1)
                            }
                            .tabViewStyle(.page(indexDisplayMode: .never))
                            
                            PageIndicator(currentPage: $currentPage, pageCount: 2, labels: ["Alerts", "Tasks"])
                                .padding(.bottom, 8)
                        }
                    } else {
                        // iPad: keep existing ScrollView layout
                        mainContent
                    }
                }
            }
            .navigationTitle("Maintenance")
            .refreshable {
                await viewModel.loadData()
            }
            .navigationDestination(for: AppDestination.self) { destination in
                destinationView(for: destination)
            }
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
    
    // MARK: - iPhone Pages
    
    @ViewBuilder
    private func AlertsPage() -> some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 20) {
                if !viewModel.activeAlerts.isEmpty {
                    alertsSection
                }
                
                analyticsLink
                
                uptimeLink
                
                if viewModel.activeAlerts.isEmpty {
                    EmptyStateView(
                        icon: "checkmark.seal",
                        title: "No Active Alerts",
                        message: "All clear — no maintenance alerts at this time."
                    )
                }
            }
            .padding()
        }
        .refreshable {
            await viewModel.loadData()
        }
    }
    
    @ViewBuilder
    private func TasksPage() -> some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 20) {
                if !viewModel.sortedUpcomingTasks.isEmpty {
                    upcomingSection
                } else {
                    EmptyStateView(
                        icon: "checkmark.seal",
                        title: "No Upcoming Tasks",
                        message: "No scheduled maintenance tasks at this time."
                    )
                }
            }
            .padding()
        }
        .refreshable {
            await viewModel.loadData()
        }
    }

    // MARK: - Main Content

    private var mainContent: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 20) {
                if !viewModel.activeAlerts.isEmpty {
                    alertsSection
                }

                if !viewModel.sortedUpcomingTasks.isEmpty {
                    upcomingSection
                }

                analyticsLink

                uptimeLink

                if viewModel.activeAlerts.isEmpty && viewModel.sortedUpcomingTasks.isEmpty {
                    EmptyStateView(
                        icon: "checkmark.seal",
                        title: "All Clear",
                        message: "No active alerts or upcoming maintenance tasks."
                    )
                }
            }
            .padding()
        }
    }

    // MARK: - Active Alerts

    private var alertsSection: some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack {
                Text("Active Alerts")
                    .font(.title2.bold())

                Text("\(viewModel.activeAlerts.count)")
                    .font(.caption.weight(.semibold).monospacedDigit())
                    .padding(.horizontal, 6)
                    .padding(.vertical, 2)
                    .background(Color.pfError.opacity(0.15), in: Capsule())
                    .foregroundStyle(Color.pfError)

                Spacer()
            }

            ForEach(viewModel.activeAlerts, id: \.id) { alert in
                MaintenanceAlertRow(alert: alert) {
                    retryTask = Task { await viewModel.acknowledgeAlert(alert) }
                } onDismiss: {
                    retryTask = Task { await viewModel.dismissAlert(alert) }
                }
            }
        }
    }

    // MARK: - Upcoming Tasks

    private var upcomingSection: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Upcoming Tasks")
                .font(.title2.bold())

            ForEach(viewModel.sortedUpcomingTasks, id: \.id) { task in
                upcomingTaskRow(task)
            }
        }
    }

    private func upcomingTaskRow(_ task: UpcomingMaintenanceTask) -> some View {
        HStack(spacing: 12) {
            Image(systemName: task.isOverdue ? "exclamationmark.circle.fill" : "clock")
                .font(.title3)
                .foregroundStyle(task.isOverdue ? Color.pfError : Color.pfWarning)

            VStack(alignment: .leading, spacing: 2) {
                Text(task.taskName)
                    .font(.subheadline.weight(.medium))

                Text(task.printerName)
                    .font(.caption)
                    .foregroundStyle(.secondary)

                if let dueDate = task.dueDate {
                    Text(dueDate, style: .relative)
                        .font(.caption)
                        .foregroundStyle(task.isOverdue ? Color.pfError : .secondary)
                }
            }

            Spacer()

            if task.isOverdue {
                Text("Overdue")
                    .font(.caption2.weight(.semibold))
                    .padding(.horizontal, 6)
                    .padding(.vertical, 2)
                    .background(Color.pfError.opacity(0.15), in: Capsule())
                    .foregroundStyle(Color.pfError)
            }
        }
        .padding(12)
        .background(Color.pfCard, in: RoundedRectangle(cornerRadius: 10))
        .overlay(
            RoundedRectangle(cornerRadius: 10)
                .strokeBorder(Color.pfBorder, lineWidth: 1)
        )
    }

    // MARK: - Navigation Links

    private var analyticsLink: some View {
        NavigationLink(value: AppDestination.maintenanceAnalytics) {
            HStack {
                Label("Analytics", systemImage: "chart.bar")
                    .font(.headline)
                Spacer()
                Image(systemName: "chevron.right")
                    .font(.caption)
                    .foregroundStyle(.tertiary)
            }
            .padding()
            .background(Color.pfCard, in: RoundedRectangle(cornerRadius: 12))
            .overlay(
                RoundedRectangle(cornerRadius: 12)
                    .strokeBorder(Color.pfBorder, lineWidth: 1)
            )
        }
        .buttonStyle(.plain)
    }

    private var uptimeLink: some View {
        NavigationLink(value: AppDestination.uptimeReliability) {
            HStack {
                Label("Uptime & Reliability", systemImage: "gauge.with.dots.needle.33percent")
                    .font(.headline)
                Spacer()
                Image(systemName: "chevron.right")
                    .font(.caption)
                    .foregroundStyle(.tertiary)
            }
            .padding()
            .background(Color.pfCard, in: RoundedRectangle(cornerRadius: 12))
            .overlay(
                RoundedRectangle(cornerRadius: 12)
                    .strokeBorder(Color.pfBorder, lineWidth: 1)
            )
        }
        .buttonStyle(.plain)
    }
}
