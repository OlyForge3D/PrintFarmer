import SwiftUI

struct PredictiveInsightsView: View {
    let printerId: UUID
    @Environment(ServiceContainer.self) private var services
    @State private var viewModel = PredictiveViewModel()

    var body: some View {
        Group {
            if viewModel.isLoading && viewModel.prediction == nil {
                ProgressView("Analyzing…")
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
            } else {
                insightsContent
            }
        }
        .navigationTitle("Predictive Insights")
        #if os(iOS)
        .navigationBarTitleDisplayMode(.large)
        #endif
        .task {
            viewModel.configure(predictiveService: services.predictiveService)
            await viewModel.predictFailure(
                printerId: printerId,
                material: nil,
                duration: nil
            )
            await viewModel.loadAlerts(printerId: printerId)
            await viewModel.loadForecasts(printerId: printerId)
        }
        .onDisappear {
            viewModel.isViewActive = false
        }
    }

    // MARK: - Content

    private var insightsContent: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 20) {
                riskGauge
                riskFactorsSection
                alertsSection
                forecastsSection
            }
            .padding()
        }
    }

    // MARK: - Risk Gauge

    private var riskGauge: some View {
        VStack(spacing: 12) {
            if viewModel.prediction != nil {
                ZStack {
                    Circle()
                        .stroke(Color.pfBackgroundTertiary, lineWidth: 12)
                        .frame(width: 120, height: 120)

                    Circle()
                        .trim(from: 0, to: CGFloat(viewModel.riskPercentage) / 100.0)
                        .stroke(riskColor, style: StrokeStyle(lineWidth: 12, lineCap: .round))
                        .rotationEffect(.degrees(-90))
                        .frame(width: 120, height: 120)

                    VStack(spacing: 2) {
                        Text("\(viewModel.riskPercentage)%")
                            .font(.title.bold().monospacedDigit())

                        Text(viewModel.riskLevel)
                            .font(.caption.weight(.medium))
                            .foregroundStyle(riskColor)
                    }
                }

                Text("Failure Risk")
                    .font(.headline)
            } else {
                Image(systemName: "chart.line.downtrend.xyaxis")
                    .font(.largeTitle)
                    .foregroundStyle(.secondary)
                Text("No predictions available")
                    .font(.headline)
                Text("Predictions will appear once enough print history is collected.")
                    .font(.subheadline)
                    .foregroundStyle(.secondary)
                    .multilineTextAlignment(.center)
            }
        }
        .frame(maxWidth: .infinity)
        .padding()
        .background(Color.pfCard, in: RoundedRectangle(cornerRadius: 12))
        .overlay(
            RoundedRectangle(cornerRadius: 12)
                .strokeBorder(Color.pfBorder, lineWidth: 1)
        )
    }

    // MARK: - Risk Factors

    private var riskFactorsSection: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Risk Factors")
                .font(.title2.bold())

            if let prediction = viewModel.prediction, !prediction.factors.isEmpty {
                ForEach(prediction.factors, id: \.name) { factor in
                    HStack(spacing: 10) {
                        Image(systemName: "exclamationmark.circle")
                            .foregroundStyle(Color.pfWarning)

                        VStack(alignment: .leading, spacing: 2) {
                            Text(factor.name)
                                .font(.subheadline.weight(.medium))
                            Text(String(format: "Weight: %.2f", factor.weight))
                                .font(.caption)
                                .foregroundStyle(.secondary)
                        }

                        Spacer()

                        Text(String(format: "%.0f%%", factor.value))
                            .font(.caption.weight(.bold).monospacedDigit())
                            .foregroundStyle(Color.pfWarning)
                    }
                    .padding(10)
                    .background(Color.pfCard, in: RoundedRectangle(cornerRadius: 8))
                    .overlay(
                        RoundedRectangle(cornerRadius: 8)
                            .strokeBorder(Color.pfBorder, lineWidth: 1)
                    )
                }
            } else {
                Text("No risk factors identified")
                    .font(.subheadline)
                    .foregroundStyle(.secondary)
            }
        }
    }

    // MARK: - Recommended Actions

    private var alertsSection: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Active Alerts")
                .font(.title2.bold())

            if viewModel.alerts.isEmpty {
                Text("No active predictive alerts")
                    .font(.subheadline)
                    .foregroundStyle(.secondary)
            } else {
                ForEach(Array(viewModel.alerts.enumerated()), id: \.offset) { _, alert in
                    HStack(spacing: 10) {
                        Image(systemName: "bell.badge.fill")
                            .foregroundStyle(Color.pfWarning)

                        VStack(alignment: .leading, spacing: 2) {
                            Text(alert.alertType)
                                .font(.subheadline.weight(.medium))
                            Text(alert.message)
                                .font(.caption)
                                .foregroundStyle(.secondary)
                                .lineLimit(2)
                        }

                        Spacer()
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
    }

    // MARK: - Forecasts

    private var forecastsSection: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Maintenance Forecast")
                .font(.title2.bold())

            if viewModel.forecasts.isEmpty {
                Text("No upcoming maintenance forecasted")
                    .font(.subheadline)
                    .foregroundStyle(.secondary)
            } else {
                ForEach(Array(viewModel.forecasts.enumerated()), id: \.offset) { _, forecast in
                    HStack(spacing: 10) {
                        Image(systemName: "calendar.badge.clock")
                            .foregroundStyle(Color.pfAccent)

                        VStack(alignment: .leading, spacing: 2) {
                            Text(forecast.printerName)
                                .font(.subheadline.weight(.medium))
                            if let firstTask = forecast.upcomingTasks.first {
                                Text("\(firstTask.taskName) in ~\(firstTask.estimatedDaysUntilDue) days")
                                    .font(.caption)
                                    .foregroundStyle(.secondary)
                            }
                        }

                        Spacer()

                        Text("\(forecast.upcomingTasks.count) tasks")
                            .font(.caption2.weight(.medium))
                            .foregroundStyle(Color.pfAccent)
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
    }

    // MARK: - Helpers

    private var riskColor: Color {
        switch viewModel.riskPercentage {
        case 0..<25: return .pfSuccess
        case 25..<50: return .pfWarning
        case 50..<75: return .orange
        default: return .pfError
        }
    }
}
