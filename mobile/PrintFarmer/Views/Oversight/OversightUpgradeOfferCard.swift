import SwiftUI

struct OversightUpgradeOfferCard: View {
    static let minimumTapTargetHeight: CGFloat = 44

    let turnOn: () -> Void
    let notNow: () -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Your farm grew")
                .font(.headline)
            Text("Oversight can become its own mode, with its own tabs.")
                .font(.subheadline)
                .foregroundStyle(.secondary)

            ViewThatFits(in: .horizontal) {
                HStack(spacing: 12) {
                    turnOnButton
                    notNowButton
                }
                VStack(alignment: .leading, spacing: 8) {
                    turnOnButton
                    notNowButton
                }
            }
        }
        .padding(.vertical, 4)
        .accessibilityElement(children: .contain)
        .accessibilityIdentifier("oversight.upgradeOffer")
    }

    private var turnOnButton: some View {
        Button("Turn on Oversight mode", action: turnOn)
            .frame(minHeight: Self.minimumTapTargetHeight)
            .buttonStyle(.borderless)
            .accessibilityHint("Separates Floor and Oversight into their own tabs.")
            .accessibilityIdentifier("oversight.upgradeOffer.turnOn")
    }

    private var notNowButton: some View {
        Button("Not now", action: notNow)
            .frame(minHeight: Self.minimumTapTargetHeight)
            .buttonStyle(.borderless)
            .accessibilityHint("Dismisses this offer until the farm reaches a new threshold.")
            .accessibilityIdentifier("oversight.upgradeOffer.notNow")
    }
}
