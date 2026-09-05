import AppIntents
import SwiftUI
import WidgetKit

private let scanURL = URL(string: "printfarmer://scan")!

@main
struct PrintFarmerScanWidgets: WidgetBundle {
    var body: some Widget {
        ScannerLockScreenWidget()

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

private struct ScannerLockScreenWidget: Widget {
    private let kind = "com.olyforge3d.printfarmer.scan-lock-screen"

    var body: some WidgetConfiguration {
        StaticConfiguration(kind: kind, provider: ScannerTimelineProvider()) { _ in
            Link(destination: scanURL) {
                Image(systemName: "barcode.viewfinder")
                    .widgetAccentable()
                    .accessibilityLabel("Open PrintFarmer scanner")
            }
        }
        .configurationDisplayName("Scan")
        .description("Open PrintFarmer directly to the scanner.")
        .supportedFamilies([.accessoryCircular])
    }
}

@available(iOSApplicationExtension 18.0, *)
private struct ScannerControlWidget: ControlWidget {
    private let kind = "com.olyforge3d.printfarmer.scan-control"

    var body: some ControlWidgetConfiguration {
        StaticControlConfiguration(kind: kind) {
            ControlWidgetButton(action: OpenURLIntent(scanURL)) {
                Label("Scan", systemImage: "barcode.viewfinder")
            }
        }
        .displayName("Scan")
        .description("Open PrintFarmer directly to the scanner.")
    }
}
