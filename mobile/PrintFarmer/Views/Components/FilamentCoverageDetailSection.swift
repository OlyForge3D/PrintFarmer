import SwiftUI

// MARK: - Filament Coverage Detail Section (F4-M / issue #778)
//
// Renders per-toolhead coverage rows on the printer-detail screen.
// Rows are identified by the stable `ToolheadFilamentCoverage.id`
// (backend-supplied `toolheadId` when present, otherwise a stable
// index-derived string), NEVER by display name. Duplicate names on
// different physical toolheads remain distinct rows.

struct FilamentCoverageDetailSection: View {
    let coverage: PrinterFilamentCoverage

    private static let etaFormatter: DateFormatter = {
        let df = DateFormatter()
        df.dateStyle = .none
        df.timeStyle = .short
        return df
    }()

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack {
                Label("Filament Coverage", systemImage: "gauge.with.dots.needle.50percent")
                    .font(.subheadline.weight(.semibold))
                    // Attach the section marker to the header Label
                    // rather than the outer VStack so the descendant
                    // toolhead rows remain independently reachable
                    // in the accessibility tree.
                    .accessibilityIdentifier("filament-coverage-section")
                Spacer()
                FilamentCoverageBadge(
                    status: coverage.status,
                    earliestPredictedRunoutAt: coverage.earliestPredictedRunoutAt
                )
            }

            VStack(spacing: 6) {
                ForEach(coverage.toolheads) { toolhead in
                    ToolheadCoverageRow(toolhead: toolhead, etaFormatter: Self.etaFormatter)
                }
            }
        }
        .padding()
        .background(Color.pfCard, in: RoundedRectangle(cornerRadius: 12))
        .overlay(
            RoundedRectangle(cornerRadius: 12)
                .strokeBorder(Color.pfBorder, lineWidth: 1)
        )
    }
}

// MARK: - Toolhead row

private struct ToolheadCoverageRow: View {
    let toolhead: ToolheadFilamentCoverage
    let etaFormatter: DateFormatter

    var body: some View {
        HStack(alignment: .top, spacing: 8) {
            VStack(alignment: .leading, spacing: 2) {
                HStack(spacing: 6) {
                    Text(toolhead.toolheadName)
                        .font(.caption.weight(.semibold))
                    Text("T\(toolhead.toolheadIndex)")
                        .font(.caption2)
                        .foregroundStyle(.secondary)
                        .monospacedDigit()
                }
                if let material = toolhead.material {
                    Text(material)
                        .font(.caption2)
                        .foregroundStyle(.tertiary)
                }
            }
            Spacer()
            VStack(alignment: .trailing, spacing: 2) {
                if let grams = toolhead.remainingGrams {
                    Text(String(format: "%.0f g remaining", grams))
                        .font(.caption.monospacedDigit())
                        .foregroundStyle(.secondary)
                } else {
                    Text("Remaining unknown")
                        .font(.caption)
                        .foregroundStyle(.tertiary)
                }
                switch toolhead.status {
                case .covers:
                    FilamentCoverageBadge(status: .covers, earliestPredictedRunoutAt: nil)
                case .runout:
                    FilamentCoverageBadge(
                        status: .runout,
                        earliestPredictedRunoutAt: toolhead.predictedRunoutAt
                    )
                case .unknown:
                    Text("Unknown")
                        .font(.caption2)
                        .foregroundStyle(.tertiary)
                        .accessibilityLabel("Filament coverage unknown for \(toolhead.toolheadName) at index \(toolhead.toolheadIndex)")
                }
            }
        }
        .padding(.vertical, 4)
        // Combine children into a single a11y element for the row
        // so XCUI locates it via the stable id even when the outer
        // container is a plain SwiftUI HStack (accessibilityIdentifier
        // alone doesn't guarantee the container surfaces as its own
        // element). The stable identifier is derived from the model's
        // stable id (backend UUID > index fallback), never the
        // display name.
        .accessibilityElement(children: .combine)
        .accessibilityIdentifier("filament-coverage-toolhead-\(toolhead.id)")
    }
}
