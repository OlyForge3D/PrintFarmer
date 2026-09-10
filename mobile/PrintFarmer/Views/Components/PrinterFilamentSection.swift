import SwiftUI

/// Passive material UI. The host retains sheets, confirmation and command ownership.
struct PrinterFilamentSection: View {
    let presentation: PrinterFilamentPresentation
    let actions: [PrinterFilamentAction]
    let onAction: @MainActor (PrinterFilamentAction) -> Void
    var embedded = false
    @State var detailsExpanded = false
    @Environment(\.dynamicTypeSize) private var dynamicTypeSize

    var primaryAction: PrinterFilamentAction? {
        actions.first {
            ($0.kind == .set || $0.kind == .change) && presentation.disabledReason(for: $0) == nil
        }
    }

    var detailActions: [PrinterFilamentAction] {
        actions.filter { $0 != primaryAction }
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            if !embedded {
                Text("Filament")
                    .font(.headline)
                    .accessibilityAddTraits(.isHeader)
                    .accessibilityIdentifier("printer.filament.heading")
            }
            ForEach(presentation.integrityNotices, id: \.self) { notice in
                Label(notice, systemImage: "exclamationmark.triangle")
                    .font(.subheadline)
            }
            if presentation.compactRows.isEmpty {
                Text("Filament status unknown").font(.subheadline)
            }
            if embedded {
                let layout = dynamicTypeSize.isAccessibilitySize
                    ? AnyLayout(VStackLayout(alignment: .leading, spacing: 12))
                    : AnyLayout(HStackLayout(alignment: .center, spacing: 12))
                layout {
                    VStack(alignment: .leading, spacing: 12) {
                        ForEach(presentation.compactRows) { row in compactRow(row) }
                    }
                    .frame(maxWidth: .infinity, alignment: .leading)
                    if let action = primaryAction {
                        ControlActionButton(
                            title: action.kind == .change ? "Change" : "Assign spool",
                            identifier: "printer.filament.action.\(action.id)",
                            accessibilityTitle: "\(action.kind.title), \(action.target.label)",
                            hint: presentation.disabledReason(for: action), compact: true,
                            textSize: 14, tinted: true, textOnly: true
                        ) { select(action) }
                        .fixedSize(horizontal: !dynamicTypeSize.isAccessibilitySize, vertical: false)
                    }
                }
            } else {
                ForEach(presentation.compactRows) { row in compactRow(row) }
            }
            if let attention = presentation.attentionText {
                Label(attention, systemImage: "exclamationmark.triangle")
                    .font(.subheadline)
                    .accessibilityIdentifier("printer.filament.attention")
            }
            if !embedded, let action = primaryAction {
                actionButton(action)
            }
            if !embedded {
                DisclosureGroup(isExpanded: $detailsExpanded) {
                    details
                } label: {
                    Text("Filament details")
                        .font(.subheadline)
                        .frame(minHeight: 44, alignment: .leading)
                        .accessibilityIdentifier("printer.filament.disclosure")
                }
            }
        }
        .fixedSize(horizontal: false, vertical: true)
        .padding(embedded ? 0 : 16)
        .background(embedded ? Color.clear : Color.pfCard, in: RoundedRectangle(cornerRadius: 12))
        .overlay(RoundedRectangle(cornerRadius: 12).strokeBorder(embedded ? Color.clear : Color.pfBorder, lineWidth: 1))
    }

    @MainActor
    func select(_ action: PrinterFilamentAction) {
        guard actions.contains(action), presentation.disabledReason(for: action) == nil else { return }
        onAction(action)
    }

    private func compactRow(_ row: PrinterFilamentPresentation.Row) -> some View {
        HStack(alignment: .top, spacing: 10) {
            if embedded {
                Image(systemName: "circle.circle")
                    .font(.system(size: 36, weight: .light))
                    .foregroundStyle(row.swatchHex.map { Color(hex: $0) } ?? Color.pfTextSecondary)
                    .frame(width: 40, height: 40)
                    .accessibilityHidden(true)
            } else if let hex = row.swatchHex {
                Circle()
                    .fill(Color(hex: hex))
                    .frame(width: 18, height: 18)
                    .overlay(Circle().strokeBorder(Color.pfBorder, lineWidth: 1))
                    .accessibilityHidden(true)
            }
            VStack(alignment: .leading, spacing: 4) {
                if let title = presentation.compactTitle(for: row) {
                    Text(title).font(.subheadline.weight(.semibold))
                }
                Text(row.materialSummary + (embedded && row.hasAssignment
                     ? row.colorText.flatMap { $0.isEmpty ? nil : " · \($0)" } ?? "" : ""))
                    .font(embedded ? .callout.weight(.semibold) : .subheadline)
                if embedded, let spool = row.spoolID {
                    Text("Spool #\(spool)" + (row.remainingGrams.flatMap {
                        $0.isFinite && $0 >= 0 ? " · \($0.formatted(.number.precision(.fractionLength(0...1)))) g remaining" : nil
                    } ?? ""))
                    .font(.footnote).foregroundStyle(Color.pfTextSecondary)
                } else if let color = row.colorText, !color.isEmpty {
                    Text("Color: \(color)").font(.caption).foregroundStyle(.secondary)
                }
                if row.coverage?.status == .runout, let notice = row.notice {
                    Text(notice).font(.caption)
                }
            }
        }
        .accessibilityElement(children: .combine)
        .accessibilityIdentifier("printer.filament.row.\(row.id)")
    }

    var details: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Spool assignments do not confirm physical loading.")
                .font(.caption).foregroundStyle(.secondary)
            if let status = presentation.statusText {
                Text(status).font(.subheadline).foregroundStyle(.secondary)
            }
            if let summary = presentation.summary {
                Text(summary).font(.subheadline)
                    .accessibilityIdentifier("printer.filament.summary")
            }
            if let date = presentation.evaluatedAt {
                Text("\(presentation.isStale ? "Last confirmed" : "Evaluated") \(date.formatted(date: .abbreviated, time: .shortened))")
                    .font(.caption).foregroundStyle(.secondary)
            }
            ForEach(presentation.rows) { row in
                rowDetails(row)
            }
            ForEach(detailActions) { action in
                VStack(alignment: .leading, spacing: 4) {
                    actionButton(action)
                    if let reason = presentation.disabledReason(for: action) {
                        Text(reason).font(.caption).foregroundStyle(.secondary)
                    }
                }
            }
        }
    }

    private func actionButton(_ action: PrinterFilamentAction) -> some View {
        Button { select(action) } label: {
            Text(action.kind.title)
                .frame(minHeight: 44, alignment: .leading)
                .contentShape(Rectangle())
        }
        .buttonStyle(.borderless)
        .disabled(presentation.disabledReason(for: action) != nil)
        .accessibilityLabel("\(action.kind.title), \(action.target.label)")
        .accessibilityHint(presentation.disabledReason(for: action) ?? "")
        .accessibilityIdentifier("printer.filament.action.\(action.id)")
    }

    private func rowDetails(_ row: PrinterFilamentPresentation.Row) -> some View {
        VStack(alignment: .leading, spacing: 6) {
            Divider()
            Text(row.title).font(.subheadline.weight(.semibold))
            if let index = row.index {
                Text("T\(index)").font(.caption).foregroundStyle(.secondary)
            }
            if let nozzleDiameter = row.nozzleDiameter {
                Text("\(nozzleDiameter, specifier: "%.1f") mm nozzle").font(.caption)
            }
            if row.isCoverageOnly {
                Text("Coverage-only slot; current assignment unavailable").font(.caption)
            } else {
                if let spool = row.spoolID {
                    Text("Assigned spool #\(spool)").font(.caption)
                }
                if let name = row.spoolName { Text(name).font(.subheadline) }
            }
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
        .accessibilityIdentifier("printer.filament.details.\(row.id)")
    }

    private func quantity(_ label: String, _ grams: Double?) -> some View {
        Text("\(label): \(grams.flatMap { $0.isFinite && $0 >= 0 ? $0.formatted(.number.precision(.fractionLength(0...1))) + " g" : nil } ?? "Unknown")")
            .font(.caption.monospacedDigit())
    }
}
