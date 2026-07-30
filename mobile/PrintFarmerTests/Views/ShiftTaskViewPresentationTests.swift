import Foundation
import XCTest
@testable import PrintFarmer

final class ShiftTaskViewPresentationTests: XCTestCase {
    func testP8MutationAccessibilityLabelsAndHintsAreUniqueAndExact() {
        let task = makeShiftTask(title: "Harvest completed plate")
        let accessibility = ShiftTaskMutationErrorAccessibility(
            task: task,
            operation: .complete
        )

        XCTAssertEqual(accessibility.retryLabel, "Retry")
        XCTAssertEqual(
            accessibility.retryHint,
            "Retries Complete for Harvest completed plate."
        )
        XCTAssertEqual(accessibility.dismissLabel, "Dismiss")
        XCTAssertEqual(
            accessibility.dismissHint,
            "Dismisses this error without changing the task."
        )
        XCTAssertEqual(
            Set([accessibility.retryLabel, accessibility.dismissLabel]).count,
            2
        )
    }

    func testP9VisualAndVoiceOverTimeMatchInNonDefaultLocaleAndZone() {
        var calendar = Calendar(identifier: .gregorian)
        let timeZone = TimeZone(identifier: "Europe/Paris")!
        calendar.timeZone = timeZone
        let formatter = ShiftTaskTimeFormatter(
            locale: Locale(identifier: "fr_FR"),
            calendar: calendar,
            timeZone: timeZone
        )
        let task = makeShiftTask(
            anchorKind: .window,
            windowStartUtc: isoDate("2026-03-08T09:30:00Z"),
            windowEndUtc: isoDate("2026-03-08T10:30:00Z")
        )

        let presentation = ShiftTaskRowPresentation(
            task: task,
            formatter: formatter
        )

        XCTAssertEqual(presentation.timeText, formatter.string(for: task))
        XCTAssertTrue(
            presentation.accessibilityLabel.contains(presentation.timeText)
        )
        XCTAssertEqual(presentation.timeText, "10:30 to 11:30")
    }

    func testP9SpringDaylightBoundaryUsesOneFormatterForVisualAndVoiceOver() {
        let formatter = losAngelesFormatter()
        let task = makeShiftTask(
            anchorKind: .window,
            windowStartUtc: isoDate("2026-03-08T09:30:00Z"),
            windowEndUtc: isoDate("2026-03-08T10:30:00Z")
        )

        let presentation = ShiftTaskRowPresentation(
            task: task,
            formatter: formatter
        )

        let expected = "1:30\u{202F}AM to 3:30\u{202F}AM"
        XCTAssertEqual(presentation.timeText, expected)
        XCTAssertTrue(
            presentation.accessibilityLabel.contains(expected)
        )
    }

    func testP9FallDaylightBoundaryPreservesIdenticalWallClockSemantics() {
        let formatter = losAngelesFormatter()
        let task = makeShiftTask(
            anchorKind: .window,
            windowStartUtc: isoDate("2026-11-01T08:30:00Z"),
            windowEndUtc: isoDate("2026-11-01T09:30:00Z")
        )

        let presentation = ShiftTaskRowPresentation(
            task: task,
            formatter: formatter
        )

        let expected = "1:30\u{202F}AM to 1:30\u{202F}AM"
        XCTAssertEqual(presentation.timeText, expected)
        XCTAssertTrue(
            presentation.accessibilityLabel.contains(expected)
        )
    }

    private func losAngelesFormatter() -> ShiftTaskTimeFormatter {
        var calendar = Calendar(identifier: .gregorian)
        let timeZone = TimeZone(identifier: "America/Los_Angeles")!
        calendar.timeZone = timeZone
        return ShiftTaskTimeFormatter(
            locale: Locale(identifier: "en_US_POSIX"),
            calendar: calendar,
            timeZone: timeZone
        )
    }

    private func isoDate(_ value: String) -> Date {
        ISO8601DateFormatter().date(from: value)!
    }
}
