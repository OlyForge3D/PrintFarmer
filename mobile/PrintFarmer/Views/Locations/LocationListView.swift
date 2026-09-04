import SwiftUI

struct LocationListView: View {
    @Environment(ServiceContainer.self) private var services
    @State private var viewModel = LocationListViewModel()

    var body: some View {
        List {
            if viewModel.isLoading && viewModel.locations.isEmpty {
                ProgressView("Loading locations…")
                    .frame(maxWidth: .infinity)
                    .accessibilityIdentifier("locations.loading")
            } else if let errorMessage = viewModel.errorMessage,
                      viewModel.locations.isEmpty {
                ContentUnavailableView {
                    Label("Couldn't Load Locations", systemImage: "exclamationmark.triangle")
                } description: {
                    Text(errorMessage)
                } actions: {
                    Button("Retry") {
                        Task { await viewModel.load() }
                    }
                }
                .accessibilityIdentifier("locations.error")
            } else if viewModel.locations.isEmpty {
                ContentUnavailableView {
                    Label("No Locations", systemImage: "mappin.and.ellipse")
                } description: {
                    Text("Locations configured on this server will appear here.")
                }
                .accessibilityIdentifier("locations.empty")
            } else {
                ForEach(viewModel.locations) { location in
                    HStack(spacing: 12) {
                        Image(systemName: "mappin.and.ellipse")
                            .foregroundStyle(location.isActive ? Color.pfAccent : .secondary)
                            .accessibilityHidden(true)

                        VStack(alignment: .leading, spacing: 3) {
                            Text(location.name)
                                .font(.body.weight(.medium))
                            if let description = location.description,
                               !description.isEmpty {
                                Text(description)
                                    .font(.subheadline)
                                    .foregroundStyle(.secondary)
                            }
                        }

                        Spacer(minLength: 12)

                        Text("\(location.printerCount) printer\(location.printerCount == 1 ? "" : "s")")
                            .font(.caption)
                            .foregroundStyle(.secondary)
                    }
                    .frame(minHeight: 44)
                    .accessibilityElement(children: .combine)
                    .accessibilityLabel(
                        "\(location.name), \(location.printerCount) "
                            + "printer\(location.printerCount == 1 ? "" : "s")"
                            + (location.isActive ? "" : ", inactive")
                    )
                    .accessibilityIdentifier("locations.row.\(location.id.uuidString)")
                }
            }
        }
        .navigationTitle("Locations")
        .refreshable {
            await viewModel.load()
        }
        .task(id: services.activeServerGeneration) {
            viewModel.activate(locationService: services.locationService)
            await viewModel.load()
        }
        .onDisappear {
            viewModel.deactivate()
        }
    }
}

@MainActor @Observable
private final class LocationListViewModel {
    private(set) var locations: [Location] = []
    private(set) var isLoading = false
    private(set) var errorMessage: String?

    @ObservationIgnored private var locationService: (any LocationServiceProtocol)?
    @ObservationIgnored private var isActive = false
    @ObservationIgnored private var loadGeneration: UInt64 = 0

    func activate(locationService: any LocationServiceProtocol) {
        self.locationService = locationService
        isActive = true
        loadGeneration &+= 1
        locations = []
        errorMessage = nil
        isLoading = true
    }

    func deactivate() {
        isActive = false
        loadGeneration &+= 1
    }

    func load() async {
        guard let locationService, isActive else { return }

        loadGeneration &+= 1
        let generation = loadGeneration
        isLoading = locations.isEmpty
        errorMessage = nil
        defer {
            if isActive, generation == loadGeneration {
                isLoading = false
            }
        }

        do {
            let loaded = try await locationService.list()
            guard isActive,
                  generation == loadGeneration,
                  !Task.isCancelled else { return }
            locations = loaded.sorted {
                $0.name.localizedCaseInsensitiveCompare($1.name) == .orderedAscending
            }
        } catch is CancellationError {
            return
        } catch {
            guard isActive, generation == loadGeneration else { return }
            errorMessage = error.localizedDescription
        }
    }
}
