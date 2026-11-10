import SwiftUI
import UIKit
import XCTest
@testable import PrintFarmer

@MainActor
final class NFCTagLinkViewSnapshotTests: XCTestCase {
    func testUnknownTagLinkScreenCreatesReviewAttachmentsInLightAndDarkMode() async throws {
        let spool = try TestData.decodeSpoolmanSpool()
        let service = MockSpoolService()
        service.spoolsPageToReturn = SpoolmanPagedResult(items: [spool], totalCount: 1)

        for colorScheme in [ColorScheme.light, .dark] {
            let spoolsLoaded = expectation(description: "Spool list loaded for \(colorScheme)")
            service.onListSpools = { spoolsLoaded.fulfill() }

            let size = CGSize(width: 390, height: 844)
            let rootView = NFCTagLinkView(
                uid: "E0040108662F6FBC",
                scannedData: nil,
                spoolService: service,
                onLinked: { _ in }
            )
            .environment(\.colorScheme, colorScheme)
            let host = UIHostingController(rootView: rootView)
            host.view.frame = CGRect(origin: .zero, size: size)
            host.view.backgroundColor = .systemBackground

            let window = UIWindow(frame: host.view.frame)
            window.rootViewController = host
            window.overrideUserInterfaceStyle = colorScheme == .dark ? .dark : .light
            window.isHidden = false
            host.view.layoutIfNeeded()

            await fulfillment(of: [spoolsLoaded], timeout: 2)
            await Task.yield()
            host.view.layoutIfNeeded()

            let image = UIGraphicsImageRenderer(size: size).image { _ in
                host.view.drawHierarchy(in: host.view.bounds, afterScreenUpdates: true)
            }
            let attachment = XCTAttachment(image: image)
            attachment.name = "review-only-nfc-tag-link-390-\(colorScheme)"
            attachment.lifetime = .keepAlways
            add(attachment)

            await Task.yield()
            window.isHidden = true
        }
    }
}
