import Foundation
import SwiftUI

// Minimal, dependency-free repro for the iOS 26 AttributeGraph async-layout
// livelock of a regular-width NavigationSplitView (PrintFarmer #3067, #3035).
// Not part of the PrintFarmer app or its tests; built by build.sh with swiftc.
//
// Launch environment (pass with SIMCTL_CHILD_<NAME>):
//   REPRO_STARVE_SECONDS  CPU starvation window in seconds (default 8; 0 disables)
//   REPRO_STARVE_DELAY    seconds after launch before starvation starts (default 0)
//   REPRO_STARVE_QOS      utility | default | userInitiated | background (default utility)
//   REPRO_SPINNERS        number of spinning blocks (default 2 x active cores)
//   REPRO_SPLASH_SECONDS  splash time before the shell mounts (default 0.5)
//   REPRO_SHELL           split | stack (default split)
//   REPRO_VARIANT         comma list: nobinding, nostyle, nodetailstack, plaindetail, plainsidebar
//   REPRO_TOGGLE_AT       seconds after launch to change selection and mount a new detail type
//   AG_ASYNC_LAYOUTS      undocumented AttributeGraph switch; 0 = synchronous layouts

@main
struct ReproApp: App {
    init() { Probe.start() }
    var body: some Scene { WindowGroup { RootView() } }
}

enum Probe {
    static let env = ProcessInfo.processInfo.environment
    private static let origin = DispatchTime.now().uptimeNanoseconds

    static func number(_ key: String, default fallback: Double) -> Double {
        env[key].flatMap(Double.init) ?? fallback
    }

    static func elapsed() -> Double {
        let base = origin
        return Double(DispatchTime.now().uptimeNanoseconds - base) / 1e9
    }

    static func log(_ message: String) {
        print(String(format: "REPRO t=%.3f ", elapsed()) + message)
        fflush(stdout)
    }

    static func start() {
        let starve = number("REPRO_STARVE_SECONDS", default: 8)
        let delay = number("REPRO_STARVE_DELAY", default: 0)
        log("start AG_ASYNC_LAYOUTS=\(env["AG_ASYNC_LAYOUTS"] ?? "<unset>") starve=\(starve) "
            + "shell=\(env["REPRO_SHELL"] ?? "split") variant=\(env["REPRO_VARIANT"] ?? "")")
        startWatchdog(duration: max(starve, 0) + delay + 6)
        guard starve > 0 else { return }
        DispatchQueue.global(qos: .userInteractive).asyncAfter(deadline: .now() + delay) {
            starveCPU(seconds: starve)
        }
    }

    private static func starveCPU(seconds: Double) {
        let cores = ProcessInfo.processInfo.activeProcessorCount
        let spinners = env["REPRO_SPINNERS"].flatMap(Int.init) ?? cores * 2
        let qos: DispatchQoS.QoSClass = switch env["REPRO_STARVE_QOS"] ?? "utility" {
        case "userInitiated": .userInitiated
        case "default": .default
        case "background": .background
        default: .utility
        }
        let end = DispatchTime.now().uptimeNanoseconds + UInt64(seconds * 1e9)
        for _ in 0..<spinners {
            DispatchQueue.global(qos: qos).async {
                while DispatchTime.now().uptimeNanoseconds < end {}
            }
        }
        log("starving \(qos) pool with \(spinners) spinners for \(seconds)s (cores=\(cores))")
    }

    /// Posts a block to the main queue every 20 ms and reports how late it ran.
    private static func startWatchdog(duration: Double) {
        let thread = Thread {
            var worst = 0.0
            var worstAt = 0.0
            var stalledTotal = 0.0
            let stopAt = elapsed() + duration
            while elapsed() < stopAt {
                let posted = DispatchTime.now().uptimeNanoseconds
                let ran = DispatchSemaphore(value: 0)
                var latency = 0.0
                DispatchQueue.main.async {
                    latency = Double(DispatchTime.now().uptimeNanoseconds - posted) / 1e9
                    ran.signal()
                }
                ran.wait()
                if latency > 0.25 {
                    log(String(format: "main stall %.3fs ending", latency))
                    stalledTotal += latency
                }
                if latency > worst {
                    worst = latency
                    worstAt = elapsed()
                }
                Thread.sleep(forTimeInterval: 0.02)
            }
            log(String(
                format: "RESULT worst_main_latency=%.3fs at t=%.3f stalls_over_250ms_total=%.3fs",
                worst, worstAt, stalledTotal
            ))
        }
        thread.qualityOfService = .userInteractive
        thread.start()
    }
}

@Observable
final class ShellModel {
    var selection: Destination? = .attention
    var visibility: NavigationSplitViewVisibility = .automatic
    var badge = BadgeState(unread: 3, ready: 1)
    var showAlternateDetail = false
}

struct BadgeState: Equatable {
    var unread: Int
    var ready: Int
}

enum Destination: String, CaseIterable, Hashable, Identifiable {
    case attention, farm, tasks, inventory, oversight

    var id: String { rawValue }

    var symbol: String {
        switch self {
        case .attention: "bell"
        case .farm: "square.grid.2x2"
        case .tasks: "checklist"
        case .inventory: "shippingbox"
        case .oversight: "chart.bar"
        }
    }
}

struct RootView: View {
    @State private var showShell = false
    @State private var model = ShellModel()

    var body: some View {
        Group {
            if showShell {
                if Probe.env["REPRO_SHELL"] == "stack" {
                    StackShell(model: model)
                } else {
                    SplitShell(model: model)
                }
            } else {
                ProgressView("Loading…")
            }
        }
        .task {
            let splash = Probe.number("REPRO_SPLASH_SECONDS", default: 0.5)
            try? await Task.sleep(for: .seconds(splash))
            Probe.log("mounting shell")
            showShell = true
            DispatchQueue.main.async { Probe.log("first main-queue turn after shell mount") }
            guard let toggleAt = Probe.env["REPRO_TOGGLE_AT"].flatMap(Double.init) else { return }
            try? await Task.sleep(for: .seconds(max(toggleAt - splash, 0)))
            Probe.log("toggling selection + new detail type")
            model.selection = .farm
            model.badge.unread += 1
            model.showAlternateDetail = true
            DispatchQueue.main.async { Probe.log("first main-queue turn after toggle") }
        }
    }
}

struct SplitShell: View {
    @Bindable var model: ShellModel
    private static let variant = Set(
        (Probe.env["REPRO_VARIANT"] ?? "").split(separator: ",").map(String.init)
    )

    private func has(_ option: String) -> Bool { Self.variant.contains(option) }

    @ViewBuilder private var sidebar: some View {
        if has("plainsidebar") {
            Text("Sidebar")
        } else {
            List(Destination.allCases, selection: $model.selection) { destination in
                Label(destination.rawValue.capitalized, systemImage: destination.symbol)
                    .tag(destination)
            }
            .navigationTitle("Repro")
        }
    }

    @ViewBuilder private var detail: some View {
        if has("plaindetail") {
            Text("Detail")
        } else if has("nodetailstack") {
            DetailView(destination: model.selection ?? .attention, badge: model.badge)
        } else if model.showAlternateDetail {
            NavigationStack { AlternateDetailView(count: model.badge.unread) }
        } else {
            NavigationStack {
                DetailView(destination: model.selection ?? .attention, badge: model.badge)
            }
        }
    }

    @ViewBuilder private var split: some View {
        if has("nobinding") {
            NavigationSplitView { sidebar } detail: { detail }
        } else {
            NavigationSplitView(columnVisibility: $model.visibility) { sidebar } detail: { detail }
        }
    }

    var body: some View {
        if has("nostyle") {
            split
        } else {
            split.navigationSplitViewStyle(.balanced)
        }
    }
}

struct StackShell: View {
    @Bindable var model: ShellModel

    var body: some View {
        NavigationStack {
            DetailView(destination: model.selection ?? .attention, badge: model.badge)
        }
    }
}

struct DetailView: View {
    let destination: Destination
    let badge: BadgeState

    var body: some View {
        List {
            Section("Summary") {
                LabeledContent("Unread", value: "\(badge.unread)")
                LabeledContent("Ready", value: "\(badge.ready)")
            }
            ForEach(0..<20, id: \.self) { row in
                Text("\(destination.rawValue) row \(row)")
            }
        }
        .navigationTitle(destination.rawValue.capitalized)
        .toolbar {
            ToolbarItem(placement: .primaryAction) { Button("Refresh") {} }
        }
    }
}

struct AlternateDetailView: View {
    let count: Int
    @State private var name = ""

    var body: some View {
        Form {
            Section("Alternate") {
                TextField("Name", text: $name)
                Stepper("Count \(count)", value: .constant(count))
                Toggle("Enabled", isOn: .constant(true))
            }
        }
        .navigationTitle("Alternate")
    }
}
