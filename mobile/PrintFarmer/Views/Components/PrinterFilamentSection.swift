import SwiftUI

/// Passive material UI. The host retains sheets, confirmation and command ownership.
struct PrinterFilamentSection: View {
    let presentation: PrinterFilamentPresentation
    let actions: [PrinterFilamentAction]
    let onAction: @MainActor (PrinterFilamentAction) -> Void
    var embedded = false
    var showsAllActions = false
    var spoolDetailsByID: [Int: SpoolmanSpool] = [:]
    var spoolLookupMessage: String?
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
        VStack(alignment: .leading, spacing: showsAllActions ? 8 : 12) {
            if !embedded {
                Text(showsAllActions && presentation.compactRows.contains(where: \.hasAssignment)
                     ? "Assigned spool" : "Filament")
                    .font(showsAllActions ? .subheadline.weight(.semibold) : .headline)
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
            let hasConciseRunoutSummary = showsAllActions
                && presentation.compactRows.contains { conciseCoverageSummaryText(for: $0) != nil }
            if let attention = presentation.attentionText, !hasConciseRunoutSummary {
                Label(attention, systemImage: "exclamationmark.triangle")
                    .font(showsAllActions ? .footnote : .subheadline)
                    .accessibilityIdentifier("printer.filament.attention")
            }
            if !embedded {
                if showsAllActions {
                    Text("Assignment does not verify physical loading.")
                        .font(.caption2)
                        .foregroundStyle(.secondary)
                        .accessibilityIdentifier("printer.filament.assignment-disclaimer")
                }
                if let action = primaryAction {
                    if !showsAllActions { actionButton(action) }
                }
                if showsAllActions {
                    filamentDetailActions
                }
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
        .padding(embedded ? 0 : 16)
        .background(embedded ? Color.clear : Color.pfCard, in: RoundedRectangle(cornerRadius: 12))
        .overlay(RoundedRectangle(cornerRadius: 12).strokeBorder(embedded ? Color.clear : Color.pfBorder, lineWidth: 1))
        .frame(maxWidth: .infinity, alignment: .leading)
    }

    @MainActor
    func select(_ action: PrinterFilamentAction) {
        guard actions.contains(action), presentation.disabledReason(for: action) == nil else { return }
        onAction(action)
    }

    private func compactRow(_ row: PrinterFilamentPresentation.Row) -> some View {
        HStack(alignment: .top, spacing: 12) {
            if embedded {
                Image(systemName: "circle.circle")
                    .font(.system(size: 36, weight: .light))
                    .foregroundStyle(row.swatchHex.map { Color(hex: $0) } ?? Color.pfTextSecondary)
                    .frame(width: 40, height: 40)
                    .accessibilityHidden(true)
            } else if showsAllActions {
                spoolReel(
                    colorHex: row.swatchHex ?? row.spoolID.flatMap { spoolDetailsByID[$0]?.colorHex },
                    size: 64
                )
                    .accessibilityHidden(true)
            } else if let hex = row.swatchHex {
                Circle()
                    .fill(Color(hex: hex))
                    .frame(width: 18, height: 18)
                    .overlay(Circle().strokeBorder(Color.pfBorder, lineWidth: 1))
                    .accessibilityHidden(true)
            }
            VStack(alignment: .leading, spacing: showsAllActions ? 3 : 4) {
                if let title = presentation.compactTitle(for: row) {
                    Text(title).font(.subheadline.weight(.semibold))
                }
                if showsAllActions, row.hasAssignment,
                   let name = row.spoolName ?? row.spoolID.flatMap({ spoolDetailsByID[$0]?.name }) {
                    Text(name)
                        .font(.subheadline.weight(.semibold))
                        .lineLimit(1)
                }
                if showsAllActions && row.hasAssignment {
                    let material = row.material ?? row.spoolID.flatMap { spoolDetailsByID[$0]?.material }
                    let color = row.colorText.flatMap { $0.isEmpty ? nil : $0 }
                    if let material, let color {
                        Text("\(material) · \(color)")
                            .font(.caption)
                            .foregroundStyle(Color.pfTextSecondary)
                    } else if let material {
                        Text(material)
                            .font(.caption)
                            .foregroundStyle(Color.pfTextSecondary)
                    }
                } else {
                    Text(row.materialSummary + (embedded && row.hasAssignment
                         ? row.colorText.flatMap { $0.isEmpty ? nil : " · \($0)" } ?? "" : ""))
                        .font(embedded ? .callout.weight(.semibold) : .subheadline)
                }
                if showsAllActions && row.hasAssignment {
                    coverageSummary(row, useConciseRunoutCopy: true)
                    if let spoolID = row.spoolID, let spoolDetails = spoolDetailsByID[spoolID] {
                        spoolWeightMeter(spoolDetails)
                    } else if let spoolLookupMessage, row.spoolID != nil {
                        Text(spoolLookupMessage)
                            .font(.caption)
                            .foregroundStyle(Color.pfTextSecondary)
                    }
                }
                if embedded, let spool = row.spoolID {
                    Text("Spool #\(spool)" + (row.remainingGrams.flatMap {
                        $0.isFinite && $0 >= 0 ? " · \($0.formatted(.number.precision(.fractionLength(0...1)))) g remaining" : nil
                    } ?? ""))
                    .font(.footnote).foregroundStyle(Color.pfTextSecondary)
                } else if !showsAllActions, let color = row.colorText, !color.isEmpty {
                    Text("Color: \(color)").font(.caption).foregroundStyle(.secondary)
                }
                if row.coverage?.status == .runout, let notice = row.notice {
                    if !showsAllActions {
                        Text(notice).font(.caption)
                    }
                }
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .accessibilityElement(children: .combine)
        .accessibilityIdentifier("printer.filament.row.\(row.id)")
    }

    @ViewBuilder
    private func coverageSummary(
        _ row: PrinterFilamentPresentation.Row,
        useConciseRunoutCopy: Bool = false
    ) -> some View {
        if useConciseRunoutCopy, let text = conciseCoverageSummaryText(for: row) {
            Text(text)
                .font(.footnote.monospacedDigit())
                .foregroundStyle(Color.pfWarning)
                .accessibilityIdentifier("printer.filament.attention")
        } else {
            coverageQuantities(row)
        }
    }

    private func coverageQuantities(_ row: PrinterFilamentPresentation.Row) -> some View {
        let remaining = row.coverage?.remainingGrams
        let demand = row.coverage?.currentJobRequiredGrams
        return HStack(spacing: 4) {
            Text("\(quantityText(remaining)) remaining")
            Text("·").accessibilityHidden(true)
            Text("\(quantityText(demand)) job demand")
        }
        .font(.footnote.monospacedDigit())
        .foregroundStyle(row.coverage?.status == .runout ? Color.pfWarning : Color.pfTextSecondary)
        .accessibilityElement(children: .combine)
        .accessibilityLabel(
            "Spool amount \(quantityText(remaining)); current job required \(quantityText(demand))"
        )
    }

    private func conciseCoverageSummaryText(for row: PrinterFilamentPresentation.Row) -> String? {
        guard row.coverage?.status == .runout,
              let remaining = quantityValue(row.coverage?.remainingGrams),
              let demand = quantityValue(row.coverage?.currentJobRequiredGrams) else {
            return nil
        }
        return "About \(remaining) g left. This job needs about \(demand) g."
    }

    private func quantityText(_ grams: Double?) -> String {
        quantityValue(grams).map { $0 + " g" } ?? "Unknown"
    }

    private func quantityValue(_ grams: Double?) -> String? {
        grams.flatMap { $0.isFinite && $0 >= 0
            ? $0.formatted(.number.precision(.fractionLength(0...1)))
            : nil
        }
    }

    private func spoolWeightMeter(_ spool: SpoolmanSpool) -> some View {
        let meter = SpoolWeightMeter(
            remainingGrams: spool.remainingWeightG,
            initialGrams: spool.initialWeightG
        )
        return VStack(alignment: .leading, spacing: 3) {
            if let fraction = meter.fraction {
                GeometryReader { geometry in
                    ZStack(alignment: .leading) {
                        Capsule().fill(Color.pfBackgroundTertiary)
                        Capsule()
                            .fill(fraction <= 0.12 ? Color.pfError : fraction <= 0.25 ? Color.pfWarning : Color.pfSuccess)
                            .frame(width: geometry.size.width * fraction)
                    }
                }
                .frame(height: 6)
                .accessibilityLabel("Spool remaining")
                .accessibilityValue(meter.label)
            }
            Text(meter.label)
                .font(.caption.monospacedDigit())
                .foregroundStyle(Color.pfTextSecondary)
                .accessibilityIdentifier("printer.filament.remainingMeter.label")
        }
        .accessibilityElement(children: .contain)
        .accessibilityIdentifier("printer.filament.remainingMeter")
    }

    private func spoolReel(colorHex: String?, size: CGFloat) -> some View {
        let materialColor = colorHex.map { Color(hex: $0) } ?? Color.pfTextSecondary
        return ZStack {
            Circle()
                .fill(materialColor.opacity(0.84))
                .overlay(Circle().strokeBorder(Color.pfBorder, lineWidth: 1))
            Circle()
                .fill(Color.pfCard)
                .frame(width: size * 0.28, height: size * 0.28)
            Circle()
                .strokeBorder(Color.pfCard.opacity(0.9), lineWidth: max(2, size * 0.035))
                .frame(width: size * 0.48, height: size * 0.48)
        }
        .frame(width: size, height: size)
    }

    var details: some View {
        VStack(alignment: .leading, spacing: 12) {
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
            if !showsAllActions {
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
    }

    private func actionButton(_ action: PrinterFilamentAction) -> some View {
        Button { select(action) } label: {
            Group {
                if showsAllActions {
                    Text(actionTitle(action))
                        .frame(maxWidth: .infinity, minHeight: 44)
                        .background(Color.pfBackgroundTertiary, in: RoundedRectangle(cornerRadius: 12))
                        .overlay(RoundedRectangle(cornerRadius: 12).strokeBorder(Color.pfBorder, lineWidth: 1))
                } else {
                    Text(actionTitle(action))
                        .frame(minHeight: 44, alignment: .leading)
                }
            }
            .contentShape(Rectangle())
        }

        .buttonStyle(.borderless)
        .disabled(presentation.disabledReason(for: action) != nil)
        .accessibilityLabel("\(actionTitle(action)), \(action.target.label)")
        .accessibilityHint(presentation.disabledReason(for: action) ?? "")
        .accessibilityIdentifier("printer.filament.action.\(action.id)")
    }

    @ViewBuilder
    private var filamentDetailActions: some View {
        let primaryActions = actions.filter { [.set, .change, .scanNFC].contains($0.kind) }
        VStack(spacing: 8) {
            if !primaryActions.isEmpty {
                HStack(spacing: 8) {
                    ForEach(primaryActions) { action in actionButton(action) }
                }
            }
        }
    }

    @ViewBuilder
    var detailUnassignAction: some View {
        if let action = actions.first(where: { $0.kind == .clearAssignment }) {
            actionButton(action)
        }
    }

    private func actionTitle(_ action: PrinterFilamentAction) -> String {
        guard showsAllActions else { return action.kind.title }
        switch action.kind {
        case .change: return "Swap"
        case .scanNFC: return "Scan spool"
        case .clearAssignment: return "Unassign"
        default: return action.kind.title
        }
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
