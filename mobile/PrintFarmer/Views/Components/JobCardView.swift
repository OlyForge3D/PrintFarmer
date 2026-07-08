import Foundation
import SwiftUI

/// Compact card for a print job in list views.
struct JobCardView: View {
    let job: PrintJob

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            // Header
            HStack {
                VStack(alignment: .leading, spacing: 2) {
                    Text(job.name)
                        .font(.headline)
                        .lineLimit(1)

                    if let printerName = job.assignedPrinterName {
                        Label(printerName, systemImage: "printer")
                            .font(.caption)
                            .foregroundStyle(.secondary)
                            .lineLimit(1)
                    }
                }

                Spacer()

                StatusBadge(jobStatus: job.status)
            }

            // Progress (if active)
            if job.status == .printing || job.status == .starting {
                if let eta = job.estimatedPrintTime?.timeSpanSeconds,
                   let started = job.actualStartTime, eta > 0 {
                    let elapsed = Date.now.timeIntervalSince(started)
                    PrintProgressBar(progress: min(1.0, elapsed / eta), height: 6)
                }
            }

            // Metadata row
            HStack(spacing: 12) {
                if job.isMultiCopy {
                    Label("\(job.completedCopies)/\(job.copies)", systemImage: "doc.on.doc")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }

                if let eta = job.estimatedPrintTime {
                    Label(eta.timeSpanFormatted, systemImage: "clock")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }

                Spacer()

                Text((job.createdAt ?? Date()).relativeFormatted)
                    .font(.caption2)
                    .foregroundStyle(.tertiary)
            }

            if hasMaterialDetails(job) {
                HStack(spacing: 12) {
                    if let material = materialSummary(job) {
                        Label(material, systemImage: "cube.box")
                            .font(.caption)
                            .foregroundStyle(.secondary)
                            .lineLimit(1)
                    }

                    if let filamentUsage = filamentUsageSummary(job) {
                        Label(filamentUsage, systemImage: "scalemass")
                            .font(.caption)
                            .foregroundStyle(.secondary)
                            .lineLimit(1)
                    }

                    if let cost = costSummary(job) {
                        Label(cost, systemImage: "dollarsign.circle")
                            .font(.caption)
                            .foregroundStyle(.secondary)
                            .lineLimit(1)
                    }

                    Spacer()
                }
            }
        }
        .padding(.vertical, 4)
    }

    private func hasMaterialDetails(_ job: PrintJob) -> Bool {
        materialSummary(job) != nil || filamentUsageSummary(job) != nil || costSummary(job) != nil
    }

    private func materialSummary(_ job: PrintJob) -> String? {
        var parts: [String] = []
        if let material = job.requiredMaterialType?.trimmingCharacters(in: .whitespacesAndNewlines), !material.isEmpty {
            parts.append(material)
        }
        if let filament = job.filamentName?.trimmingCharacters(in: .whitespacesAndNewlines), !filament.isEmpty {
            parts.append(filament)
        }
        return parts.isEmpty ? nil : parts.joined(separator: " · ")
    }

    private func filamentUsageSummary(_ job: PrintJob) -> String? {
        if let actual = job.actualFilamentUsage, actual > 0 {
            return String(format: "%.1fg", actual)
        }
        if let estimated = job.estimatedFilamentUsage, estimated > 0 {
            return String(format: "%.1fg est.", estimated)
        }
        return nil
    }

    private func costSummary(_ job: PrintJob) -> String? {
        if let actual = job.actualCost, actual > 0 {
            return currencyString(actual)
        }
        if let estimated = job.estimatedCost, estimated > 0 {
            return "\(currencyString(estimated)) est."
        }
        return nil
    }

    private func currencyString(_ amount: Decimal) -> String {
        String(format: "$%.2f", NSDecimalNumber(decimal: amount).doubleValue)
    }
}
