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

    func testSpoolLoadFailureCreatesReviewAttachment() async throws {
        let service = MockSpoolService()
        service.errorToThrow = NetworkError.serverError(503)
        let spoolsLoaded = expectation(description: "Failed spool lookup completed")
        service.onListSpools = { spoolsLoaded.fulfill() }

        let size = CGSize(width: 390, height: 844)
        let rootView = NFCTagLinkView(
            uid: "E0040108662F6FBC",
            scannedData: nil,
            spoolService: service,
            onLinked: { _ in }
        )
        .environment(\.colorScheme, .light)
        let host = UIHostingController(rootView: rootView)
        host.view.frame = CGRect(origin: .zero, size: size)
        host.view.backgroundColor = .systemBackground

        let window = UIWindow(frame: host.view.frame)
        window.rootViewController = host
        window.overrideUserInterfaceStyle = .light
        window.isHidden = false
        host.view.layoutIfNeeded()

        await fulfillment(of: [spoolsLoaded], timeout: 2)
        await Task.yield()
        host.view.layoutIfNeeded()

        let image = UIGraphicsImageRenderer(size: size).image { _ in
            host.view.drawHierarchy(in: host.view.bounds, afterScreenUpdates: true)
        }
        let attachment = XCTAttachment(image: image)
        attachment.name = "review-only-nfc-tag-link-spool-load-error-390-light"
        attachment.lifetime = .keepAlways
        add(attachment)

        await Task.yield()
        window.isHidden = true
    }

    func testStaleSpoolRelinkScreenCreatesReviewAttachmentsInLightAndDarkMode() async throws {
        let spool = try TestData.decodeSpoolmanSpool()
        let service = MockSpoolService()
        service.spoolsPageToReturn = SpoolmanPagedResult(items: [spool], totalCount: 1)

        for colorScheme in [ColorScheme.light, .dark] {
            let spoolsLoaded = expectation(description: "Spool list loaded for stale \(colorScheme)")
            service.onListSpools = { spoolsLoaded.fulfill() }
            let rootView = NFCTagLinkView(
                uid: "E0040108662F6FBC",
                scannedData: nil,
                spoolService: service,
                staleSpoolID: 77,
                onLinked: { _ in }
            )
            try await attachRender(
                rootView, colorScheme: colorScheme,
                name: "review-only-nfc-tag-relink-stale-390-\(colorScheme)",
                waitingFor: spoolsLoaded
            )
        }
    }

    /// Renders the Create Spool sheet that Link NFC Tag presents; the created
    /// spool is linked to the tag by NFCTagLinkView's onSpoolCreated handler.
    func testCreateSpoolSheetCreatesReviewAttachmentsInLightAndDarkMode() async throws {
        for colorScheme in [ColorScheme.light, .dark] {
            let rootView = AddSpoolView(scannedData: nil) { _ in }
                .environment(ServiceContainer())
            try await attachRender(
                rootView, colorScheme: colorScheme,
                name: "review-only-nfc-tag-create-spool-390-\(colorScheme)",
                waitingFor: nil
            )
        }
    }

    private func attachRender<V: View>(
        _ view: V, colorScheme: ColorScheme, name: String, waitingFor loaded: XCTestExpectation?
    ) async throws {
        let size = CGSize(width: 390, height: 844)
        let host = UIHostingController(rootView: view.environment(\.colorScheme, colorScheme))
        host.view.frame = CGRect(origin: .zero, size: size)
        host.view.backgroundColor = .systemBackground
        let window = UIWindow(frame: host.view.frame)
        window.rootViewController = host
        window.overrideUserInterfaceStyle = colorScheme == .dark ? .dark : .light
        window.isHidden = false
        host.view.layoutIfNeeded()
        if let loaded {
            await fulfillment(of: [loaded], timeout: 2)
        } else {
            try await Task.sleep(for: .milliseconds(300))
        }
        await Task.yield()
        host.view.layoutIfNeeded()
        let image = UIGraphicsImageRenderer(size: size).image { _ in
            host.view.drawHierarchy(in: host.view.bounds, afterScreenUpdates: true)
        }
        let attachment = XCTAttachment(image: image)
        attachment.name = name
        attachment.lifetime = .keepAlways
        add(attachment)
        await Task.yield()
        window.isHidden = true
    }
}
