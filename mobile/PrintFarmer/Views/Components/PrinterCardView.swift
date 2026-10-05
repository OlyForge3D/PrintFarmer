import SwiftUI

/// Shared floor card for the phone list, tablet grid and read-only cached farm.
struct PrinterCardView: View {
    let printer: Printer
    var isPendingReady = false
    var isReadOnly = false
    var attentionCount: Int? = nil
    var failureReason: String? = nil
    var printerService: (any PrinterServiceProtocol)? = nil
    @Environment(\.dynamicTypeSize) private var dynamicTypeSize
    @State private var media = PrinterCardMediaModel()

    private var presentation: PrinterCardPresentation {
        PrinterCardPresentation(
            printer: printer, isPendingReady: isPendingReady,
            attentionCount: attentionCount, failureReason: failureReason,
            printTimeLeftSeconds: media.printTimeLeftSeconds
        )
    }

    private var requestID: PrinterCardRequestID {
        PrinterCardRequestID(
            printerID: printer.id,
            path: isReadOnly ? nil : printer.currentJobThumbnailUrl,
            jobName: printer.jobName ?? printer.fileName,
            state: printer.state,
            isFailureSuspected: failureReason != nil,
            serviceID: printerService.map { ObjectIdentifier($0 as AnyObject) }
        )
    }

    var body: some View {
        let layout = dynamicTypeSize.isAccessibilitySize
            ? AnyLayout(VStackLayout(alignment: .leading, spacing: 10))
            : AnyLayout(HStackLayout(alignment: .top, spacing: 10))
        layout {
            thumbnail
            VStack(alignment: .leading, spacing: 6) {
                ViewThatFits(in: .horizontal) {
                    HStack(alignment: .top, spacing: 6) {
                        name
                        Spacer(minLength: 0)
                        statePill
                    }
                    VStack(alignment: .leading, spacing: 4) {
                        name
                        statePill
                    }
                }
                Text(presentation.jobLabel)
                    .font(.caption)
                    .foregroundStyle(Color.pfTextSecondary)
                    .lineLimit(dynamicTypeSize.isAccessibilitySize ? nil : 2)
                if presentation.canShowJobETA {
                    if presentation.showsJobProgress, let progress = printer.progress, progress.isFinite {
                        PrintProgressBar(
                            progress: progress, showLabel: false,
                            height: 4, color: presentation.accent
                        )
                        ViewThatFits(in: .horizontal) {
                            HStack {
                                progressLabel
                                Spacer(minLength: 4)
                                etaLabel
                            }
                            VStack(alignment: .leading, spacing: 4) {
                                progressLabel
                                etaLabel
                            }
                        }
                    } else {
                        Text("Progress unavailable")
                            .font(.caption2)
                            .foregroundStyle(Color.pfTextSecondary)
                    }
                }
                ViewThatFits(in: .horizontal) {
                    HStack(spacing: 10) {
                        filamentAndTemperatures
                        Spacer(minLength: 0)
                        attentionBadge
                    }
                    VStack(alignment: .leading, spacing: 6) {
                        filamentAndTemperatures
                        attentionBadge
                    }
                }
            }
        }
        .padding(10)
        .foregroundStyle(Color.pfTextPrimary)
        .background(Color.pfCard, in: RoundedRectangle(cornerRadius: 14))
        .overlay {
            RoundedRectangle(cornerRadius: 14)
                .strokeBorder(Color.pfBorder, lineWidth: 1)
        }
        .accessibilityElement(children: .ignore)
        .accessibilityLabel(presentation.accessibilityLabel)
        .accessibilityRepresentation {
            Color.clear
                .accessibilityElement(children: .ignore)
                .accessibilityLabel(presentation.accessibilityLabel)
        }
        .accessibilityHint(isReadOnly
            ? "Read-only cached status. Reconnect to control this printer."
            : "Opens \(printer.name) printer details.")
        .task(id: requestID) {
            await media.load(request: requestID, service: isReadOnly ? nil : printerService)
        }
        .onDisappear { media.cancel() }
    }

    private var thumbnail: some View {
        ZStack {
            RoundedRectangle(cornerRadius: 10).fill(Color.pfBackgroundTertiary)
            if media.request == requestID, let image = media.image {
                Image(uiImage: image).resizable().scaledToFit()
            } else {
                Image(systemName: "printer")
                    .font(.system(size: 24))
                    .foregroundStyle(Color.pfTextTertiary)
            }
        }
        .frame(width: 60, height: 60)
        .clipShape(RoundedRectangle(cornerRadius: 10))
        .overlay {
            RoundedRectangle(cornerRadius: 10).strokeBorder(Color.pfBorder, lineWidth: 1)
        }
    }

    private var name: some View {
        Text(printer.name)
            .font(.subheadline.weight(.semibold))
            .fixedSize(horizontal: false, vertical: true)
    }

    private var statePill: some View {
        Text(presentation.stateLabel)
            .font(.caption2.weight(.semibold))
            .foregroundStyle(presentation.accent)
            .padding(.horizontal, 8)
            .padding(.vertical, 3)
            .background(presentation.accent.opacity(0.14), in: Capsule())
            .fixedSize(horizontal: true, vertical: false)
    }

    private var progressLabel: some View {
        Text(presentation.progressLabel)
            .font(.caption.weight(.semibold).monospacedDigit())
    }

    private var etaLabel: some View {
        Text(presentation.etaLabel)
            .font(.caption2.monospacedDigit())
            .foregroundStyle(Color.pfTextSecondary)
            .fixedSize(horizontal: false, vertical: true)
    }

    private var filamentAndTemperatures: some View {
        HStack(spacing: 4) {
            Circle()
                .fill(printer.spoolInfo?.colorHex.map { Color(hex: $0) } ?? .pfTextTertiary)
                .frame(width: 10, height: 10)
                .overlay(Circle().strokeBorder(Color.pfBorderLight, lineWidth: 1))
            Text(temperatureSummary)
                .font(.caption2.monospacedDigit())
                .foregroundStyle(Color.pfTextSecondary)
                .lineLimit(1)
                .minimumScaleFactor(0.85)
        }
    }

    private var temperatureSummary: String {
        let hotend = printer.hotendTemp.map(\.temperatureFormatted) ?? "--"
        let bed = printer.bedTemp.map(\.temperatureFormatted) ?? "--"
        return "\(hotend) / \(bed)"
    }

    @ViewBuilder private var attentionBadge: some View {
        if failureReason != nil {
            Label("Check print", systemImage: "exclamationmark.triangle.fill")
                .font(.caption2.weight(.semibold))
                .foregroundStyle(Color.pfError)
                .padding(.horizontal, 6)
                .padding(.vertical, 3)
                .background(Color.pfError.opacity(0.14), in: Capsule())
                .fixedSize(horizontal: true, vertical: false)
        } else if let attentionCount, attentionCount > 0 {
            Label(
                "\(attentionCount)",
                systemImage: "exclamationmark.triangle.fill"
            )
                .font(.caption2.weight(.semibold))
                .foregroundStyle(Color.pfWarning)
                .padding(.horizontal, 6)
                .padding(.vertical, 3)
                .background(Color.pfWarning.opacity(0.14), in: Capsule())
                .fixedSize(horizontal: true, vertical: false)
        }
    }
}

struct PrinterCardPresentation {
    let printer: Printer
    let isPendingReady: Bool
    let attentionCount: Int?
    let failureReason: String?
    let printTimeLeftSeconds: Double?

    var isActiveJob: Bool {
        printer.isOnline && ["printing", "paused"].contains(printer.state?.lowercased() ?? "")
    }

    var hasFailureJobContext: Bool {
        guard failureReason != nil,
              printer.isOnline,
              printer.state?.lowercased() == "error",
              let jobName = printer.jobName ?? printer.fileName,
              !jobName.isEmpty else {
            return false
        }
        return true
    }

    var canShowJobETA: Bool {
        !isPendingReady && (isActiveJob || hasFailureJobContext)
    }

    var showsJobProgress: Bool {
        guard canShowJobETA,
              let progress = printer.progress,
              progress.isFinite,
              printer.jobName != nil || printer.fileName != nil else {
            return false
        }
        return true
    }

    var stateLabel: String {
        if failureReason != nil { return "Failure suspected" }
        if isPendingReady { return "Bed clear" }
        if !printer.isOnline { return "Offline" }
        if printer.inMaintenance { return "Maintenance" }
        switch printer.state?.lowercased() {
        case "printing": return "Printing"
        case "paused": return "Paused"
        case "error": return "Error"
        case "ready": return "Ready"
        case nil, "idle": return "Idle"
        default: return printer.state?.capitalized ?? "Idle"
        }
    }

    var accent: Color {
        if failureReason != nil { return .pfError }
        if isPendingReady { return .pfAssigned }
        if !printer.isOnline { return .pfTextSecondary }
        if printer.inMaintenance { return .pfMaintenance }
        switch printer.state?.lowercased() {
        case "printing": return .pfSuccess
        case "paused": return .pfWarning
        case "error": return .pfError
        default: return .pfTextSecondary
        }
    }

    var jobLabel: String {
        if isPendingReady { return "Finished · Clear bed to continue" }
        guard isActiveJob || hasFailureJobContext else { return "No active job" }
        return printer.jobName ?? printer.fileName ?? "Job name unavailable"
    }

    var progressLabel: String {
        guard let progress = printer.progress, progress.isFinite else { return "--%" }
        return min(max(progress, 0), 1).percentFormatted
    }

    var etaLabel: String {
        guard canShowJobETA, let seconds = printTimeLeftSeconds,
              seconds.isFinite, seconds >= 0, seconds < Double(Int.max) else {
            return "ETA unavailable"
        }
        if printer.state?.lowercased() == "paused" {
            return "\(seconds.durationFormatted) left · Paused"
        }
        return "\(seconds.durationFormatted) left · Done \(seconds.etaFormatted)"
    }

    var accessibilityLabel: String {
        var parts = [printer.name, stateLabel, jobLabel]
        if canShowJobETA {
            if showsJobProgress {
                parts.append(isActiveJob ? progressLabel + " complete" : progressLabel + " progress at failure")
            }
            parts.append(etaLabel)
        }
        parts.append("Nozzle \(printer.hotendTemp.map(\.temperatureFormatted) ?? "unavailable")")
        parts.append("Bed \(printer.bedTemp.map(\.temperatureFormatted) ?? "unavailable")")
        if let spool = printer.spoolInfo, spool.hasActiveSpool {
            parts.append("Filament \(spool.material ?? "loaded")")
        } else {
            parts.append("No spool loaded")
        }
        if let attentionCount { parts.append("\(attentionCount) attention items") }
        if let failureReason { parts.append("Failure suspected: \(failureReason)") }
        return parts.joined(separator: ", ")
    }
}
