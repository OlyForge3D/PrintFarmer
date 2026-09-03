import SwiftUI

@MainActor @ViewBuilder
func destinationView(for destination: AppDestination) -> some View {
    switch destination {
    case .printerDetail(let id):
        PrinterDetailView(printerId: id)
    case .jobDetail(let id):
        JobDetailView(jobId: id)
    case .locationDetail:
        // LocationListView exists, but location-detail routing intentionally
        // remains unchanged until the navigation epic wires that destination.
        ContentUnavailableView {
            Label("Coming Soon", systemImage: "map")
        } description: {
            Text("Location details will be available in a future update.")
        }
    case .createJob:
        ContentUnavailableView {
            Label("Coming Soon", systemImage: "plus.circle")
        } description: {
            Text("Job creation will be available in a future update.")
        }
    case .createPrinter:
        ContentUnavailableView {
            Label("Coming Soon", systemImage: "printer.fill.and.paper")
        } description: {
            Text("Printer setup will be available in a future update.")
        }
    case .maintenanceAnalytics:
        MaintenanceAnalyticsView()
    case .uptimeReliability:
        UptimeView()
    case .predictiveInsights(let printerId):
        PredictiveInsightsView(printerId: printerId)
    case .jobHistory:
        JobHistoryView()
    case .jobTimeline:
        JobTimelineView()
    case .dispatchDashboard:
        DispatchDashboardView()
    case .advancedPrinterControls(let printerId):
        AdvancedPrinterControlsDestination(printerId: printerId)
    }
}

/// Loads the printer for the given ID and hosts `AdvancedPrinterControlsView`.
///
/// F1 (#706): AppDestination cases must be `Hashable` on a value type, so
/// the printer is not passed directly. This wrapper reuses
/// `PrinterDetailViewModel` to fetch the current printer state before
/// rendering the advanced controls surface.
@MainActor
private struct AdvancedPrinterControlsDestination: View {
    let printerId: UUID
    @Environment(ServiceContainer.self) private var services
    @State private var viewModel: PrinterDetailViewModel
    @State private var activeTasks: [Task<Void, Never>] = []

    init(printerId: UUID) {
        self.printerId = printerId
        _viewModel = State(initialValue: PrinterDetailViewModel(printerId: printerId))
    }

    var body: some View {
        Group {
            if let printer = viewModel.printer {
                AdvancedPrinterControlsView(printer: printer)
            } else if let error = viewModel.errorMessage {
                ContentUnavailableView {
                    Label("Error", systemImage: "exclamationmark.triangle")
                } description: {
                    Text(error)
                } actions: {
                    Button("Retry") {
                        let task = Task { await viewModel.loadPrinter() }
                        activeTasks.append(task)
                    }
                }
            } else {
                ProgressView("Loading printer…")
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
            }
        }
        .task {
            viewModel.isViewActive = true
            viewModel.configure(printerService: services.printerService)
            // F1 (#706) — reviewer fix: without a SignalR subscription
            // the Advanced screen renders a stale snapshot forever.
            // Pending-command state (jog/preheat/home) never clears
            // because `PrinterControlsViewModel.handlePrinterUpdate(_:)`
            // is only invoked when the parent view forwards a fresh
            // `Printer` after `printerupdated` fires. Mirror
            // `PrinterDetailView.task` and wire the same SignalR handle
            // so state transitions and per-command lockouts refresh
            // live while the operator is on Advanced.
            viewModel.configureSignalR(services.signalRService)
            await viewModel.loadPrinter()
        }
        .onDisappear {
            viewModel.isViewActive = false
            activeTasks.forEach { $0.cancel() }
        }
    }
}
