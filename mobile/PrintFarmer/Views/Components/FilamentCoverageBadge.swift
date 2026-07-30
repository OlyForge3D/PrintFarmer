import SwiftUI

// MARK: - Filament Coverage Badge (F4-M / issue #778)
//
// Renders the four canonical presentations from the frozen contract:
//
//   * `.covers`             → visible "Covers job" badge with the
//                             accessible label "Filament covers this
//                             job".
//   * `.runout` + ETA       → runout-time badge; accessible label
//                             MUST begin "Filament will run out at ".
//   * `.runout` w/o ETA     → "Runout mid-job" presentation;
//                             accessible label "Filament will run
//                             out before the job finishes".
//   * `.unknown`            → nothing rendered; the caller MUST NOT
//                             surface a covers/runout claim.
//
// Feature-disabled scoping is handled by the caller (`ViewModel`);
// this view only visualizes a status that has already been decided.

struct FilamentCoverageBadge: View {
    let status: FilamentCoverageStatus
    let earliestPredictedRunoutAt: Date?

    private static let etaFormatter: DateFormatter = {
        let df = DateFormatter()
        df.dateStyle = .none
        df.timeStyle = .short
        return df
    }()

    var body: some View {
        switch status {
        case .covers:
            HStack(spacing: 4) {
                Image(systemName: "checkmark.seal.fill")
                    .font(.caption2)
                Text("Covers job")
                    .font(.caption2.weight(.semibold))
            }
            .padding(.horizontal, 7)
            .padding(.vertical, 2)
            .foregroundStyle(.white)
            .background(Color.green.opacity(0.85), in: Capsule())
            .accessibilityElement(children: .ignore)
            .accessibilityLabel("Filament covers this job")
            .accessibilityIdentifier("filament-coverage-badge-covers")
        case .runout:
            if let eta = earliestPredictedRunoutAt {
                let etaText = Self.etaFormatter.string(from: eta)
                HStack(spacing: 4) {
                    Image(systemName: "clock.badge.exclamationmark.fill")
                        .font(.caption2)
                    Text("Runout · \(etaText)")
                        .font(.caption2.weight(.semibold))
                        .monospacedDigit()
                }
                .padding(.horizontal, 7)
                .padding(.vertical, 2)
                .foregroundStyle(.white)
                .background(Color.orange.opacity(0.9), in: Capsule())
                .accessibilityElement(children: .ignore)
                .accessibilityLabel("Filament will run out at \(etaText)")
                .accessibilityIdentifier("filament-coverage-badge-runout-eta")
            } else {
                HStack(spacing: 4) {
                    Image(systemName: "exclamationmark.triangle.fill")
                        .font(.caption2)
                    Text("Runout mid-job")
                        .font(.caption2.weight(.semibold))
                }
                .padding(.horizontal, 7)
                .padding(.vertical, 2)
                .foregroundStyle(.white)
                .background(Color.red.opacity(0.85), in: Capsule())
                .accessibilityElement(children: .ignore)
                .accessibilityLabel("Filament will run out before the job finishes")
                .accessibilityIdentifier("filament-coverage-badge-runout-no-eta")
            }
        case .unknown:
            // Frozen-contract rule: never surface any coverage claim.
            EmptyView()
        }
    }
}
