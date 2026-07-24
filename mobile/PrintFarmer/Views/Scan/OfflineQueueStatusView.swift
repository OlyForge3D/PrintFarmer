import SwiftUI

// MARK: - Offline Queue Status View (F10-Q1, #787)
//
// The minimal operator-facing status surface for the durable offline outbox.
// Lists the active namespace's queued part-adjustment / harvest writes with
// their disposition and the explicit controls each state requires:
//   * pending / replaying — visible, "Sync now" drives a replay pass;
//   * conflict / expired  — Discard, or Review-and-retry-as-new (two-step,
//     reads current server state before minting a NEW intent);
//   * paused              — retained while the operator gate is off.
//
// Reachable from the affected part/harvest flows. It never fabricates a replay:
// every network effect goes through the coordinator's single owner.
struct OfflineQueueStatusView: View {
    @Environment(ServiceContainer.self) private var services
    @State private var viewModel: OfflineQueueStatusViewModel?

    var body: some View {
        List {
            if let viewModel {
                content(viewModel)
            } else {
                ProgressView().accessibilityIdentifier("offlineQueue.loading")
            }
        }
        .navigationTitle("Offline Queue")
        .task {
            if viewModel == nil {
                viewModel = services.makeOfflineQueueStatusViewModel()
            }
            await services.syncOfflineWriteQueue()
            await viewModel?.refresh()
        }
        .refreshable {
            await viewModel?.retryReplay()
        }
        .sheet(item: reviewBinding) { review in
            reviewSheet(review)
        }
    }

    @ViewBuilder
    private func content(_ viewModel: OfflineQueueStatusViewModel) -> some View {
        if let errorMessage = viewModel.errorMessage {
            Section {
                Label(errorMessage, systemImage: "exclamationmark.triangle.fill")
                    .foregroundStyle(Color.pfError)
                    .accessibilityIdentifier("offlineQueue.error")
            }
        }

        if viewModel.entries.isEmpty {
            Section {
                Text("No queued writes.")
                    .foregroundStyle(Color.pfTextSecondary)
                    .accessibilityIdentifier("offlineQueue.empty")
            }
        } else {
            Section {
                Button {
                    Task { await viewModel.retryReplay() }
                } label: {
                    Label("Sync now", systemImage: "arrow.clockwise")
                }
                .accessibilityIdentifier("offlineQueue.syncNow")
            }

            Section("Queued Writes") {
                ForEach(viewModel.entries) { entry in
                    row(entry, viewModel: viewModel)
                }
            }
        }
    }

    @ViewBuilder
    private func row(_ entry: OfflineWriteQueueEntry, viewModel: OfflineQueueStatusViewModel) -> some View {
        VStack(alignment: .leading, spacing: 6) {
            HStack {
                Text(title(for: entry.item))
                    .font(.body.weight(.medium))
                Spacer()
                statusBadge(entry)
            }
            Text(entry.item.route.path)
                .font(.caption.monospaced())
                .foregroundStyle(Color.pfTextSecondary)
                .lineLimit(1)
                .truncationMode(.middle)

            if let detail = conflictDetail(entry.item) {
                Text(detail)
                    .font(.caption)
                    .foregroundStyle(Color.pfWarning)
            }

            if needsReview(entry.item) {
                HStack(spacing: 12) {
                    Button(role: .destructive) {
                        Task { await viewModel.discard(entry.id) }
                    } label: {
                        Text("Discard")
                    }
                    .accessibilityIdentifier("offlineQueue.discard")

                    Button {
                        Task { await viewModel.beginReview(entry) }
                    } label: {
                        Text("Review & retry")
                    }
                    .accessibilityIdentifier("offlineQueue.review")
                }
                .buttonStyle(.borderless)
                .font(.caption)
            }
        }
        .padding(.vertical, 2)
        .accessibilityIdentifier("offlineQueue.row")
    }

    @ViewBuilder
    private func statusBadge(_ entry: OfflineWriteQueueEntry) -> some View {
        let (label, color) = badge(entry)
        Text(label)
            .font(.caption2.weight(.semibold))
            .padding(.horizontal, 8)
            .padding(.vertical, 3)
            .background(color.opacity(0.15), in: Capsule())
            .foregroundStyle(color)
            .accessibilityIdentifier("offlineQueue.status.\(rawStatus(entry))")
    }

    // MARK: Review sheet

    private var reviewBinding: Binding<OfflineWriteReview?> {
        Binding(
            get: { viewModel?.review },
            set: { newValue in
                if newValue == nil { viewModel?.cancelReview() }
            }
        )
    }

    @ViewBuilder
    private func reviewSheet(_ review: OfflineWriteReview) -> some View {
        NavigationStack {
            Form {
                Section("Original Intent") {
                    LabeledContent("Action", value: title(for: review.entry.item))
                    LabeledContent("Route", value: review.entry.item.route.path)
                }
                Section("Current Server State") {
                    if review.currentOnHandBySku.isEmpty {
                        Text("No current stock information for the affected SKUs.")
                            .foregroundStyle(Color.pfTextSecondary)
                    } else {
                        ForEach(review.currentOnHandBySku.sorted(by: { $0.key < $1.key }), id: \.key) { sku, onHand in
                            LabeledContent(sku, value: "On hand: \(onHand)")
                        }
                    }
                }
                Section {
                    Text("Retrying creates a NEW request with a fresh idempotency key — it is not a silent replay of the original.")
                        .font(.caption)
                        .foregroundStyle(Color.pfTextSecondary)
                }
            }
            .navigationTitle("Review & Retry")
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button("Cancel") { viewModel?.cancelReview() }
                        .accessibilityIdentifier("offlineQueue.review.cancel")
                }
                ToolbarItem(placement: .confirmationAction) {
                    Button("Retry as New") {
                        Task { await viewModel?.confirmRetryAsNew() }
                    }
                    .accessibilityIdentifier("offlineQueue.review.confirm")
                }
            }
        }
    }

    // MARK: Presentation helpers

    private func title(for item: OfflineWriteItem) -> String {
        switch item.operation {
        case .partAdjustment(let sku, _): return "Adjust \(sku)"
        case .harvest: return "Harvest job"
        }
    }

    private func conflictDetail(_ item: OfflineWriteItem) -> String? {
        if case .conflict(let conflict) = item.status { return conflict.message }
        return nil
    }

    private func needsReview(_ item: OfflineWriteItem) -> Bool {
        item.status.isTerminalNeedsReview
    }

    private func badge(_ entry: OfflineWriteQueueEntry) -> (String, Color) {
        if entry.isReplaying { return ("Replaying", Color.pfAccent) }
        switch entry.item.status {
        case .pending: return ("Pending", Color.pfTextSecondary)
        case .conflict: return ("Conflict", Color.pfError)
        case .expiredNeedsReview: return ("Expired", Color.pfWarning)
        case .paused: return ("Paused", Color.pfTextSecondary)
        }
    }

    private func rawStatus(_ entry: OfflineWriteQueueEntry) -> String {
        if entry.isReplaying { return "replaying" }
        switch entry.item.status {
        case .pending: return "pending"
        case .conflict: return "conflict"
        case .expiredNeedsReview: return "expired"
        case .paused: return "paused"
        }
    }
}
