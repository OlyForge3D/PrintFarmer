import AppIntents
import SwiftUI
import WidgetKit

private let scanURL = URL(string: "printfarmer://scan")!

@main
struct PrintFarmerScanWidgets: WidgetBundle {
    var body: some Widget {
        ScannerWidget()

        if #available(iOSApplicationExtension 18.0, *) {
            ScannerControlWidget()
        }
    }
}

private struct ScannerTimelineEntry: TimelineEntry {
    let date: Date
}

private struct ScannerTimelineProvider: TimelineProvider {
    func placeholder(in context: Context) -> ScannerTimelineEntry {
        ScannerTimelineEntry(date: .now)
    }

    func getSnapshot(
        in context: Context,
        completion: @escaping (ScannerTimelineEntry) -> Void
    ) {
        completion(ScannerTimelineEntry(date: .now))
    }

    func getTimeline(
        in context: Context,
        completion: @escaping (Timeline<ScannerTimelineEntry>) -> Void
    ) {
        completion(Timeline(entries: [ScannerTimelineEntry(date: .now)], policy: .never))
    }
}

private struct ScannerWidgetView: View {
    @Environment(\.widgetFamily) private var family

    var body: some View {
        Group {
            if family == .systemSmall {
                VStack(alignment: .leading, spacing: 4) {
                    Image(systemName: "barcode.viewfinder")
                        .font(.system(size: 32, weight: .semibold))
                        .widgetAccentable()

                    Spacer()

                    Text("Scan")
                        .font(.headline)
                    Text("Open PrintFarmer")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }
                .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .leading)
            } else {
                Image(systemName: "barcode.viewfinder")
                    .widgetAccentable()
            }
        }
        .accessibilityElement(children: .ignore)
        .accessibilityLabel("Open PrintFarmer scanner")
        .widgetURL(scanURL)
        .containerBackground(.clear, for: .widget)
    }
}

private struct ScannerWidget: Widget {
    private let kind = "com.olyforge3d.printfarmer.scan-lock-screen"

    var body: some WidgetConfiguration {
        StaticConfiguration(kind: kind, provider: ScannerTimelineProvider()) { _ in
            ScannerWidgetView()
        }
        .configurationDisplayName("Scan")
        .description("Open PrintFarmer directly to the scanner.")
        .supportedFamilies([.accessoryCircular, .systemSmall])
    }
}

@available(iOSApplicationExtension 18.0, *)
private struct ScannerControlWidget: ControlWidget {
    private let kind = "com.olyforge3d.printfarmer.scan-control"

    var body: some ControlWidgetConfiguration {
        StaticControlConfiguration(kind: kind) {
            ControlWidgetButton(action: OpenScannerIntent()) {
                Label("Scan", systemImage: "barcode.viewfinder")
            }
        }
        .displayName("Scan")
        .description("Open PrintFarmer directly to the scanner.")
    }
}
