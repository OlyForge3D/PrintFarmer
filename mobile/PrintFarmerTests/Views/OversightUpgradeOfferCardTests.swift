import XCTest
@testable import PrintFarmer

@MainActor
final class OversightUpgradeOfferCardTests: XCTestCase {

    // MARK: - Approved concept copy (docs/design/ia-concepts.html#c-aa, mockup p-offer)

    func test_subheadline_matchesApprovedConcept_whenAllThreeSignalsActive() {
        let context = OversightUpgradeOfferContext(
            farmShape: FarmShape(accountCount: 3, locationCount: 2, printerCount: 21),
            shiftPlanEnabled: true
        )

        XCTAssertEqual(
            OversightUpgradeOfferCard.subheadline(for: context),
            "There are 3 accounts on this server now, a second bay, and shift planning is on. "
            + "Oversight can become its own mode with five destinations instead of this one tab. "
            + "Nothing moves out of reach either way."
        )
    }

    // MARK: - Signals sentence permutations

    func test_signalsSentence_isNilForUnknownShape() {
        let context = OversightUpgradeOfferContext(farmShape: nil, shiftPlanEnabled: true)

        XCTAssertNil(OversightUpgradeOfferCard.signalsSentence(for: context))
    }

    func test_signalsSentence_accountsOnly() {
        let context = OversightUpgradeOfferContext(
            farmShape: FarmShape(accountCount: 3, locationCount: 1, printerCount: 1),
            shiftPlanEnabled: false
        )

        XCTAssertEqual(
            OversightUpgradeOfferCard.signalsSentence(for: context),
            "There are 3 accounts on this server now."
        )
    }

    func test_signalsSentence_twoBaysReadsAsSecondBay() {
        let context = OversightUpgradeOfferContext(
            farmShape: FarmShape(accountCount: 1, locationCount: 2, printerCount: 1),
            shiftPlanEnabled: false
        )

        XCTAssertEqual(
            OversightUpgradeOfferCard.signalsSentence(for: context),
            "A second bay."
        )
    }

    func test_signalsSentence_moreThanTwoBaysReadsAsCount() {
        let context = OversightUpgradeOfferContext(
            farmShape: FarmShape(accountCount: 1, locationCount: 4, printerCount: 1),
            shiftPlanEnabled: false
        )

        XCTAssertEqual(
            OversightUpgradeOfferCard.signalsSentence(for: context),
            "4 bays."
        )
    }

    func test_signalsSentence_shiftPlanOnly() {
        let context = OversightUpgradeOfferContext(
            farmShape: FarmShape(accountCount: 1, locationCount: 1, printerCount: 1),
            shiftPlanEnabled: true
        )

        XCTAssertEqual(
            OversightUpgradeOfferCard.signalsSentence(for: context),
            "Shift planning is on."
        )
    }

    func test_signalsSentence_twoSignalsJoinsWithAnd() {
        let context = OversightUpgradeOfferContext(
            farmShape: FarmShape(accountCount: 2, locationCount: 1, printerCount: 1),
            shiftPlanEnabled: true
        )

        XCTAssertEqual(
            OversightUpgradeOfferCard.signalsSentence(for: context),
            "There are 2 accounts on this server now and shift planning is on."
        )
    }

    func test_signalsSentence_ignoresPrinterCountEntirely() {
        // Printer count never drives the offer shape — only accounts, bays,
        // and shift planning. Guard against a future regression that reads it.
        let smallFleet = OversightUpgradeOfferContext(
            farmShape: FarmShape(accountCount: 3, locationCount: 2, printerCount: 1),
            shiftPlanEnabled: true
        )
        let largeFleet = OversightUpgradeOfferContext(
            farmShape: FarmShape(accountCount: 3, locationCount: 2, printerCount: 500),
            shiftPlanEnabled: true
        )

        XCTAssertEqual(
            OversightUpgradeOfferCard.signalsSentence(for: smallFleet),
            OversightUpgradeOfferCard.signalsSentence(for: largeFleet)
        )
    }

    // MARK: - Unknown-shape fallback (mirrors NavigationShellDerivation.automatic)

    func test_subheadline_unknownShape_isHonest_andStillPromisesReassurance() {
        let context = OversightUpgradeOfferContext(farmShape: nil, shiftPlanEnabled: false)

        let subheadline = OversightUpgradeOfferCard.subheadline(for: context)

        XCTAssertTrue(
            subheadline.contains("This server hasn't reported its size yet"),
            "Unknown-shape copy should mirror NavigationShellDerivation.automatic's honest phrasing, got: \(subheadline)"
        )
        XCTAssertTrue(
            subheadline.contains("Oversight can become its own mode with five destinations instead of this one tab."),
            "Preview sentence must still appear in unknown-shape copy, got: \(subheadline)"
        )
        XCTAssertTrue(
            subheadline.contains("Nothing moves out of reach either way."),
            "Reassurance must still appear in unknown-shape copy, got: \(subheadline)"
        )
    }

    // MARK: - Constant sentences

    func test_previewSentence_namesFiveDestinationsAndOneTab() {
        // Locks the exact phrasing required by acceptance criterion:
        // "Body line previews 'five destinations instead of this one tab'".
        XCTAssertEqual(
            OversightUpgradeOfferCard.previewSentence,
            "Oversight can become its own mode with five destinations instead of this one tab."
        )
    }

    func test_reassuranceSentence_isExactApprovedConceptWording() {
        XCTAssertEqual(
            OversightUpgradeOfferCard.reassuranceSentence,
            "Nothing moves out of reach either way."
        )
    }

    // MARK: - Smoke: view renders without crashing

    func test_view_rendersWithKnownShape() {
        let card = OversightUpgradeOfferCard(
            context: OversightUpgradeOfferContext(
                farmShape: FarmShape(accountCount: 3, locationCount: 2, printerCount: 21),
                shiftPlanEnabled: true
            ),
            turnOn: {},
            notNow: {}
        )
        _ = card.body
    }

    func test_view_rendersWithUnknownShape() {
        let card = OversightUpgradeOfferCard(
            context: OversightUpgradeOfferContext(farmShape: nil, shiftPlanEnabled: false),
            turnOn: {},
            notNow: {}
        )
        _ = card.body
    }
}
