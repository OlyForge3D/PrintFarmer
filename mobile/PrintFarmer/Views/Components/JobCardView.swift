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
        }
        .padding(.vertical, 4)
    }
}
