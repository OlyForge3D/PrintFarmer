import SwiftUI

/// The read-only context the offer card needs to write honest, threshold-aware
/// copy per the approved A′ concept (`docs/design/ia-concepts.html#c-aa`,
/// mockup `p-offer`).
///
/// The card enumerates whichever signals are currently at or above their
/// threshold: multiple accounts, multiple bays, and shift planning being on.
/// When ``farmShape`` is absent, the card falls back to honest unknown-shape
/// copy that mirrors ``NavigationShellDerivation/automatic(farmShape:shiftPlanEnabled:isFarmAdmin:)``.
struct OversightUpgradeOfferContext: Equatable, Sendable {
    let farmShape: FarmShape?
    let shiftPlanEnabled: Bool
}

struct OversightUpgradeOfferCard: View {
    static let minimumTapTargetHeight: CGFloat = 44

    let context: OversightUpgradeOfferContext
    let turnOn: () -> Void
    let notNow: () -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Your farm grew")
                .font(.headline)
            Text(Self.subheadline(for: context))
                .font(.subheadline)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
                .accessibilityIdentifier("oversight.upgradeOffer.subheadline")

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

    // MARK: - Copy

    /// Assembles the subheadline paragraph per the approved concept:
    /// a signals sentence that names the reason the offer appeared, the
    /// preview sentence promising five destinations, and the reassurance
    /// that nothing moves out of reach either way.
    ///
    /// When the shape is unknown the signals sentence is replaced with an
    /// honest fallback that mirrors ``NavigationShellDerivation/automatic``.
    static func subheadline(for context: OversightUpgradeOfferContext) -> String {
        let opening = signalsSentence(for: context) ?? unknownShapeSentence
        return [
            opening,
            previewSentence,
            reassuranceSentence
        ].joined(separator: " ")
    }

    static let previewSentence =
        "Oversight can become its own mode with five destinations instead of this one tab."

    static let reassuranceSentence =
        "Nothing moves out of reach either way."

    static let unknownShapeSentence =
        "This server hasn't reported its size yet, so we can't name what changed."

    /// Enumerates whichever concrete signals currently qualify as reasons for
    /// the offer. Returns `nil` when the shape is unknown, so the caller can
    /// substitute the unknown-shape fallback.
    static func signalsSentence(for context: OversightUpgradeOfferContext) -> String? {
        guard let shape = context.farmShape else { return nil }

        var clauses: [String] = []
        if shape.accountCount >= 2 {
            clauses.append("there are \(shape.accountCount) accounts on this server now")
        }
        if shape.locationCount >= 2 {
            clauses.append(bayClause(for: shape.locationCount))
        }
        if context.shiftPlanEnabled {
            clauses.append("shift planning is on")
        }

        guard !clauses.isEmpty else {
            // Every signal is below threshold. The offer should not normally
            // reach this branch — `observeOversightUpgradeOffer` gates on a
            // threshold crossing — but if it does, be honest rather than
            // fabricate a reason.
            return "Your farm has grown enough to justify a second mode."
        }

        return capitalizeFirst(joinWithOxfordComma(clauses)) + "."
    }

    /// Renders the bay count as either "a second bay" (the concept's copy for
    /// the first crossing) or "N bays" for larger farms. The threshold to
    /// trigger this signal is `locationCount >= 2`.
    private static func bayClause(for locationCount: Int) -> String {
        locationCount == 2 ? "a second bay" : "\(locationCount) bays"
    }

    private static func joinWithOxfordComma(_ clauses: [String]) -> String {
        switch clauses.count {
        case 0: return ""
        case 1: return clauses[0]
        case 2: return "\(clauses[0]) and \(clauses[1])"
        default:
            let head = clauses.dropLast().joined(separator: ", ")
            return "\(head), and \(clauses.last!)"
        }
    }

    private static func capitalizeFirst(_ text: String) -> String {
        guard let first = text.first else { return text }
        return String(first).uppercased() + text.dropFirst()
    }
}
