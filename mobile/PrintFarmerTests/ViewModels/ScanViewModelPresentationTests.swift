import XCTest
import Observation
@testable import PrintFarmer
#if canImport(UIKit)
import UIKit
#endif

extension ScanViewModelTests {
    @MainActor
    func testQueuedExternalScanWaitsForBarcodeIntakeDismissal() async {
        let partsService = MockPartsInventoryService()
        partsService.resolveBinError = NetworkError.notFound
        partsService.resolvePartError = NetworkError.notFound
        let barcodeService = MockBarcodeIntakeService()
        barcodeService.filamentToResolve = makeFilament(id: 9, name: "PLA")
        let (viewModel, scanner) = makeControlledSubject(partsService: partsService, barcodeService: barcodeService)
        scanner.scanStarted = expectation(description: "Initial camera")
        viewModel.requestExternalScan(UUID())
        await fulfillment(of: [scanner.scanStarted], timeout: 2)
        viewModel.requestExternalScan(UUID())
        await completeScan(viewModel, scanner, returning: .barcode("012345678905"))

        XCTAssertEqual(viewModel.pendingSpoolBarcode, "012345678905")
        XCTAssertEqual(scanner.callCount, 1)
        viewModel.resultPresentationDidAppear()
        viewModel.pendingSpoolBarcode = nil
        viewModel.requestExternalScan(UUID())
        viewModel.isViewActive = true
        XCTAssertFalse(viewModel.isScanning, "Intake's binding clears before its dismissal transition finishes")

        scanner.scanStarted = expectation(description: "Retry after intake dismissal")
        viewModel.resultPresentationDidDismiss()
        await fulfillment(of: [scanner.scanStarted], timeout: 2)
        await completeScan(viewModel, scanner, returning: .cancelled)
        XCTAssertEqual(scanner.callCount, 2)
        XCTAssertEqual(barcodeService.resolveBarcodes, ["012345678905"])
    }

    @MainActor
    func testUnknownResultDismissedWhileInactiveRetainsQueuedExternalScan() async {
        let (viewModel, scanner) = makeControlledSubject()
        viewModel.pendingOutcome = .unknownCode("UNKNOWN")
        viewModel.resultPresentationDidAppear()
        viewModel.requestExternalScan(UUID())
        XCTAssertFalse(viewModel.isScanning)
        viewModel.isViewActive = false
        viewModel.pendingOutcome = nil
        viewModel.resultPresentationDidDismiss()
        XCTAssertFalse(viewModel.isScanning)
        XCTAssertEqual(scanner.callCount, 0)

        scanner.scanStarted = expectation(description: "Queued request after reactivation")
        viewModel.isViewActive = true
        await fulfillment(of: [scanner.scanStarted], timeout: 2)
        await completeScan(viewModel, scanner, returning: .cancelled)
        XCTAssertEqual(scanner.callCount, 1)
    }

    @MainActor
    func testQueuedExternalScanPreservesResolverErrorUntilAcknowledged() async {
        let partsService = MockPartsInventoryService()
        partsService.resolveBinError = NetworkError.serverError(500)
        let (viewModel, scanner) = makeControlledSubject(partsService: partsService)
        scanner.scanStarted = expectation(description: "Initial camera")
        viewModel.requestExternalScan(UUID())
        await fulfillment(of: [scanner.scanStarted], timeout: 2)
        viewModel.requestExternalScan(UUID())
        await completeScan(viewModel, scanner, returning: .barcode("SERVER-ERROR"))
        let errorMessage = viewModel.errorMessage
        XCTAssertNotNil(errorMessage)
        viewModel.requestExternalScan(UUID())
        viewModel.isViewActive = true
        XCTAssertEqual(viewModel.errorMessage, errorMessage)
        XCTAssertFalse(viewModel.isScanning)
        XCTAssertEqual(scanner.callCount, 1)

        scanner.scanStarted = expectation(description: "Retry after resolver error acknowledgment")
        viewModel.clearError()
        await fulfillment(of: [scanner.scanStarted], timeout: 2)
        await completeScan(viewModel, scanner, returning: .cancelled)
        XCTAssertNil(viewModel.errorMessage)
        XCTAssertEqual(scanner.callCount, 2)
    }

    @MainActor
    func testViewDeactivatedDuringPresentationWaitDoesNotInvokeCameraOrLoseRequest() async {
        let waiting = expectation(description: "Waiting for UIKit transition")
        var continuation: CheckedContinuation<Bool, Never>?
        var shouldWait = true
        let (viewModel, scanner) = makeControlledSubject(waitForScannerPresentation: {
            guard shouldWait else { return true }
            shouldWait = false
            return await withCheckedContinuation {
                continuation = $0
                waiting.fulfill()
            }
        })
        viewModel.requestExternalScan(UUID())
        await fulfillment(of: [waiting], timeout: 2)
        viewModel.isViewActive = false
        let stopped = expectation(description: "Inactive scan stopped")
        withObservationTracking {
            _ = viewModel.isScanning
        } onChange: {
            stopped.fulfill()
        }
        continuation?.resume(returning: true)
        await fulfillment(of: [stopped], timeout: 2)
        XCTAssertEqual(scanner.callCount, 0)

        scanner.scanStarted = expectation(description: "Retained request starts when active")
        viewModel.isViewActive = true
        await fulfillment(of: [scanner.scanStarted], timeout: 2)
        await completeScan(viewModel, scanner, returning: .cancelled)
        XCTAssertEqual(scanner.callCount, 1)
    }

    @MainActor
    func testResultArrivingDuringPresentationWaitFencesCameraUntilDismissal() async {
        let waiting = expectation(description: "Waiting for presentation")
        var continuation: CheckedContinuation<Bool, Never>?
        var shouldWait = true
        let (viewModel, scanner) = makeControlledSubject(waitForScannerPresentation: {
            guard shouldWait else { return true }
            shouldWait = false
            return await withCheckedContinuation {
                continuation = $0
                waiting.fulfill()
            }
        })
        viewModel.requestExternalScan(UUID())
        await fulfillment(of: [waiting], timeout: 2)
        viewModel.pendingOutcome = .unknownCode("DEFERRED")
        viewModel.resultPresentationDidAppear()
        let stopped = expectation(description: "Camera fenced by result")
        withObservationTracking {
            _ = viewModel.isScanning
        } onChange: {
            stopped.fulfill()
        }
        continuation?.resume(returning: true)
        await fulfillment(of: [stopped], timeout: 2)
        XCTAssertEqual(scanner.callCount, 0)

        scanner.scanStarted = expectation(description: "Retained request after result dismissal")
        viewModel.pendingOutcome = nil
        viewModel.resultPresentationDidDismiss()
        await fulfillment(of: [scanner.scanStarted], timeout: 2)
        await completeScan(viewModel, scanner, returning: .cancelled)
        XCTAssertEqual(scanner.callCount, 1)
    }

    #if canImport(UIKit)
    @MainActor
    func testUIKitAlertAcknowledgmentWaitsForActualAnimatedDismissalBeforeRetry() async throws {
        let window = try makePresentationWindow()
        defer { window.isHidden = true }
        let root = try XCTUnwrap(window.rootViewController)
        let alert = UIAlertController(title: "Scan Error", message: "Camera denied", preferredStyle: .alert)
        alert.addAction(UIAlertAction(title: "OK", style: .default))
        await withCheckedContinuation { (continuation: CheckedContinuation<Void, Never>) in
            root.present(alert, animated: true) { continuation.resume() }
        }
        let (viewModel, scanner) = makeControlledSubject()
        viewModel.errorMessage = "Camera denied"
        viewModel.requestExternalScan(UUID())
        let waiting = expectation(description: "Waiting for alert dismissal")
        var dismissalCompleted = false
        let acknowledgment = Task { @MainActor in
            waiting.fulfill()
            let ready = await ScanPresentationReadiness.waitUntilReady(from: root)
            XCTAssertTrue(ready)
            XCTAssertTrue(dismissalCompleted, "Readiness must include the UIKit dismissal transition")
            XCTAssertNil(root.presentedViewController)
            viewModel.clearError()
        }
        await fulfillment(of: [waiting], timeout: 2)
        XCTAssertEqual(scanner.callCount, 0)
        XCTAssertEqual(viewModel.errorMessage, "Camera denied")
        scanner.scanStarted = expectation(description: "Camera after alert dismissal")
        alert.dismiss(animated: true) {
            XCTAssertEqual(viewModel.errorMessage, "Camera denied", "Acknowledgment must not clear a visible alert")
            dismissalCompleted = true
        }
        await acknowledgment.value
        await fulfillment(of: [scanner.scanStarted], timeout: 2)
        await completeScan(viewModel, scanner, returning: .cancelled)
        XCTAssertEqual(scanner.callCount, 1)
    }

    @MainActor
    func testUIKitResultSheetBindingClearDoesNotRetryBeforeAnimatedDismissalCompletes() async throws {
        let window = try makePresentationWindow()
        defer { window.isHidden = true }
        let root = try XCTUnwrap(window.rootViewController)
        let sheet = UIViewController()
        sheet.modalPresentationStyle = .pageSheet
        await withCheckedContinuation { (continuation: CheckedContinuation<Void, Never>) in
            root.present(sheet, animated: true) { continuation.resume() }
        }
        let (viewModel, scanner) = makeControlledSubject(waitForScannerPresentation: {
            await ScanPresentationReadiness.waitUntilReady(from: root)
        })
        viewModel.pendingOutcome = .unknownCode("UNKNOWN")
        viewModel.resultPresentationDidAppear()
        viewModel.requestExternalScan(UUID())
        viewModel.pendingOutcome = nil
        viewModel.isViewActive = true
        XCTAssertFalse(viewModel.isScanning)
        XCTAssertEqual(scanner.callCount, 0)
        scanner.scanStarted = expectation(description: "Camera after result sheet dismissal")
        let dismissed = expectation(description: "UIKit sheet dismissal completed")
        sheet.dismiss(animated: true) {
            XCTAssertFalse(viewModel.isScanning)
            XCTAssertNil(root.presentedViewController)
            viewModel.resultPresentationDidDismiss()
            dismissed.fulfill()
        }
        await fulfillment(of: [dismissed, scanner.scanStarted], timeout: 2)
        await completeScan(viewModel, scanner, returning: .cancelled)
        XCTAssertEqual(scanner.callCount, 1)
    }

    @MainActor
    private func makePresentationWindow() throws -> UIWindow {
        let scene = try XCTUnwrap(UIApplication.shared.connectedScenes.compactMap { $0 as? UIWindowScene }.first)
        let window = UIWindow(windowScene: scene)
        window.rootViewController = UIViewController()
        window.isHidden = false
        window.rootViewController?.view.layoutIfNeeded()
        return window
    }
    #endif
}
