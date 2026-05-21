import SwiftUI

/// Home subgroup of the printer Controls section. Three buttons in fixed order:
/// Home All (prominent, full-width), Home XY, Home Z (2-up standard row).
///
/// Spec: `mobile/docs/design/printer-controls-section.md` §2.3 Home.
/// View model: `PrinterControlsViewModel`.
struct HomeSubgroup: View {

    @ObservedObject var viewModel: PrinterControlsViewModel

    /// Returns true when the entire subgroup must be removed from layout
    /// (capability gating per spec §3.5). The Controls section reflows the
    /// surrounding subgroups when this returns true.
    static func shouldHide(capabilities: PrinterBackendCapabilities?) -> Bool {
        guard let caps = capabilities else { return true }
        return !caps.supportsMovement || !caps.supportsHoming
    }

    private var isAllPending: Bool {
        if case .home(let axes) = viewModel.pendingCommand?.kind, axes == ["X", "Y", "Z"] { return true }
        return false
    }

    private var isXYPending: Bool {
        if case .home(let axes) = viewModel.pendingCommand?.kind, axes == ["X", "Y"] { return true }
        return false
    }

    private var isZPending: Bool {
        if case .home(let axes) = viewModel.pendingCommand?.kind, axes == ["Z"] { return true }
        return false
    }

    private var anyPending: Bool {
        if case .home = viewModel.pendingCommand?.kind { return true }
        return false
    }

    private var isDisabled: Bool {
        !viewModel.canControl
    }

    var body: some View {
        if Self.shouldHide(capabilities: viewModel.capabilities) {
            EmptyView()
        } else {
            VStack(alignment: .leading, spacing: 8) {
                Text("Home")
                    .font(.headline)
                    .foregroundStyle(Color.pfTextPrimary)

                Button {
                    Task { await viewModel.homeAll() }
                } label: {
                    homeAllLabel
                }
                .buttonStyle(ActionButtonStyle(size: .prominent))
                .frame(maxWidth: .infinity)
                .background(Color.pfButtonPrimary)
                .foregroundStyle(Color.pfButtonPrimaryText)
                .clipShape(RoundedRectangle(cornerRadius: 10, style: .continuous))
                .overlay(
                    RoundedRectangle(cornerRadius: 10, style: .continuous)
                        .strokeBorder(isAllPending ? Color.pfAssigned : Color.clear, lineWidth: 1.5)
                )
                .disabled(isDisabled || (anyPending && !isAllPending))
                .opacity((isDisabled && !isAllPending) ? 0.5 : 1.0)
                .accessibilityLabel(isAllPending ? "Homing all axes, in progress" : "Home all axes")
                .accessibilityAddTraits(.isButton)

                HStack(spacing: 8) {
                    homeAxisButton(
                        label: "Home XY",
                        symbol: "move.3d",
                        isPending: isXYPending,
                        a11y: isXYPending ? "Homing X and Y, in progress" : "Home X and Y axes"
                    ) {
                        Task { await viewModel.homeXY() }
                    }
                    .disabled(isDisabled || (anyPending && !isXYPending))

                    homeAxisButton(
                        label: "Home Z",
                        symbol: "arrow.up.and.down",
                        isPending: isZPending,
                        a11y: isZPending ? "Homing Z, in progress" : "Home Z axis"
                    ) {
                        Task { await viewModel.homeZ() }
                    }
                    .disabled(isDisabled || (anyPending && !isZPending))
                }
            }
        }
    }

    @ViewBuilder
    private var homeAllLabel: some View {
        if isAllPending {
            ProgressView()
                .progressViewStyle(.circular)
                .tint(Color.pfButtonPrimaryText)
                .frame(maxWidth: .infinity)
        } else {
            Label("Home All", systemImage: "house.fill")
                .font(.subheadline.weight(.medium))
                .frame(maxWidth: .infinity)
        }
    }

    @ViewBuilder
    private func homeAxisButton(
        label: String,
        symbol: String,
        isPending: Bool,
        a11y: String,
        action: @escaping () -> Void
    ) -> some View {
        Button(action: action) {
            ZStack {
                if isPending {
                    ProgressView()
                        .progressViewStyle(.circular)
                        .tint(Color.pfTextPrimary)
                } else {
                    Label(label, systemImage: symbol)
                        .font(.subheadline.weight(.medium))
                }
            }
            .frame(maxWidth: .infinity)
        }
        .buttonStyle(ActionButtonStyle(size: .standard))
        .background(Color.pfBackgroundTertiary)
        .foregroundStyle(Color.pfTextPrimary)
        .clipShape(RoundedRectangle(cornerRadius: 10, style: .continuous))
        .overlay(
            RoundedRectangle(cornerRadius: 10, style: .continuous)
                .strokeBorder(isPending ? Color.pfAssigned : Color.pfBorder, lineWidth: isPending ? 1.5 : 1)
        )
        .opacity(isDisabled && !isPending ? 0.5 : 1.0)
        .accessibilityLabel(a11y)
        .accessibilityAddTraits(.isButton)
    }
}
