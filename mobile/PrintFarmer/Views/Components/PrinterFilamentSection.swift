import SwiftUI

/// Passive material UI. The host retains sheets, confirmation and command ownership.
struct PrinterFilamentSection: View {
    let presentation: PrinterFilamentPresentation
    let actions: [PrinterFilamentAction]
    let onAction: @MainActor (PrinterFilamentAction) -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Filament")
                .font(.headline)
                .accessibilityAddTraits(.isHeader)
                .accessibilityIdentifier("printer.filament.heading")
            ForEach(presentation.integrityNotices, id: \.self) { notice in
                Label(notice, systemImage: "exclamationmark.triangle")
                    .font(.subheadline)
            }
            if let status = presentation.statusText {
                Text(status).font(.subheadline).foregroundStyle(.secondary)
            }
            if let summary = presentation.summary {
                Text(summary)
                    .font(.subheadline)
                    .accessibilityIdentifier("printer.filament.summary")
            }
            if let date = presentation.evaluatedAt {
                Text("\(presentation.isStale ? "Last confirmed" : "Evaluated") \(date.formatted(date: .abbreviated, time: .shortened))")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
            if presentation.rows.isEmpty {
                Text("Material information unavailable").font(.subheadline)
            }
            ForEach(presentation.rows) { row in
                rowContent(row)
            }
            ForEach(actions) { action in
                VStack(alignment: .leading, spacing: 4) {
                    Button { select(action) } label: {
                        Text(action.kind.title)
                            .frame(maxWidth: .infinity, minHeight: 44, alignment: .leading)
                            .contentShape(Rectangle())
                    }
                    .buttonStyle(.bordered)
                    .disabled(presentation.disabledReason(for: action) != nil)
                    .accessibilityLabel("\(action.kind.title), \(action.target.label)")
                    .accessibilityHint(presentation.disabledReason(for: action) ?? "")
                    .accessibilityIdentifier("printer.filament.action.\(action.id)")
                    if let reason = presentation.disabledReason(for: action) {
                        Text(reason).font(.caption).foregroundStyle(.secondary)
                    }
                }
            }
        }
        .fixedSize(horizontal: false, vertical: true)
        .padding()
        .background(Color.pfCard, in: RoundedRectangle(cornerRadius: 12))
        .overlay(RoundedRectangle(cornerRadius: 12).strokeBorder(Color.pfBorder, lineWidth: 1))
    }

    @MainActor
    func select(_ action: PrinterFilamentAction) {
        guard actions.contains(action), presentation.disabledReason(for: action) == nil else { return }
        onAction(action)
    }

    private func rowContent(_ row: PrinterFilamentPresentation.Row) -> some View {
        VStack(alignment: .leading, spacing: 6) {
            Divider()
            Text(row.title).font(.subheadline.weight(.semibold))
            if let index = row.index {
                Text("T\(index)").font(.caption).foregroundStyle(.secondary)
            }
            // Retained toolhead detail (issue #2522, Hicks review finding
            // 21): the pre-#2522 `toolheadSlotRow` this section replaced
            // showed nozzle diameter per slot; carried through
            // `PrinterFilamentPresentation.Row.nozzleDiameter` so it isn't
            // lost.
            if let nozzleDiameter = row.nozzleDiameter {
                Text("\(nozzleDiameter, specifier: "%.1f") mm nozzle")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
            if row.isCoverageOnly {
                Text("Coverage-only slot; current assignment unavailable").font(.caption)
            } else {
                Text(row.material ?? "Material unknown").font(.subheadline)
                if let spool = row.spoolID {
                    Text("Assigned spool #\(spool)").font(.caption)
                }
            }
            if let name = row.spoolName { Text(name).font(.subheadline) }
            if row.index == nil {
                quantity("Remaining", row.remainingGrams)
            }
            if let coverage = row.coverage {
                if row.isLastConfirmed { Text("Last-confirmed coverage").font(.caption) }
                if let spool = coverage.spoolId {
                    Text("Coverage for spool #\(spool)").font(.caption)
                }
                Text("Coverage material: \(coverage.material ?? "Unknown")").font(.caption)
                quantity("Remaining at evaluation", coverage.remainingGrams)
                quantity("Current job remaining demand", coverage.currentJobRemainingGrams)
                quantity("Assigned queued demand", coverage.queuedRequiredGrams)
                quantity("Total demand", coverage.totalDemandGrams)
                if let notice = row.notice {
                    Label(notice, systemImage: coverage.status == .runout
                          ? "exclamationmark.triangle" : "questionmark.circle")
                        .font(.subheadline)
                }
                if coverage.status == .runout, let eta = coverage.predictedRunoutAt {
                    Text("Predicted runout \(eta.formatted(date: .abbreviated, time: .shortened))")
                        .font(.caption)
                }
            } else if row.index != nil {
                Text("Coverage unavailable").font(.caption).foregroundStyle(.secondary)
            }
        }
        .accessibilityElement(children: .combine)
        .accessibilityIdentifier("printer.filament.row.\(row.id)")
    }

    private func quantity(_ label: String, _ grams: Double?) -> some View {
        Text("\(label): \(grams.flatMap { $0.isFinite && $0 >= 0 ? $0.formatted(.number.precision(.fractionLength(0...1))) + " g" : nil } ?? "Unknown")")
            .font(.caption.monospacedDigit())
    }
}
