#if canImport(UIKit)
import Foundation
@preconcurrency import VisionKit
import AVFoundation
import UIKit

// MARK: - Barcode Scanner Service

final class BarcodeScannerService: BarcodeScannerProtocol, @unchecked Sendable {

    var isAvailable: Bool {
        true
    }

    func scanBarcode() async -> BarcodeScanResult {
        let available = await MainActor.run {
            DataScannerViewController.isSupported && DataScannerViewController.isAvailable
        }
        guard available else {
            return .error(.notSupported)
        }

        let status = AVCaptureDevice.authorizationStatus(for: .video)
        switch status {
        case .denied, .restricted:
            return .error(.permissionDenied)
        case .notDetermined:
            let granted = await AVCaptureDevice.requestAccess(for: .video)
            if !granted { return .error(.permissionDenied) }
        case .authorized:
            break
        @unknown default:
            break
        }

        return await withCheckedContinuation { continuation in
            Task { @MainActor in
                let coordinator = BarcodeScanCoordinator(continuation: continuation)
                let scanner = DataScannerViewController(
                    recognizedDataTypes: [.barcode(symbologies: [.ean13, .ean8, .upce, .code128, .code39, .qr])],
                    qualityLevel: .balanced,
                    isHighlightingEnabled: true
                )
                scanner.delegate = coordinator
                scanner.presentationController?.delegate = coordinator

                guard let windowScene = UIApplication.shared.connectedScenes
                    .compactMap({ $0 as? UIWindowScene }).first,
                      let rootVC = windowScene.windows.first?.rootViewController else {
                    coordinator.resume(returning: .error(.notSupported))
                    return
                }

                objc_setAssociatedObject(scanner, &BarcodeScanCoordinator.associatedKey, coordinator, .OBJC_ASSOCIATION_RETAIN)

                let topVC = Self.topViewController(from: rootVC)
                topVC.present(scanner, animated: true) {
                    do {
                        try scanner.startScanning()
                    } catch {
                        scanner.dismiss(animated: true) {
                            coordinator.resume(returning: .error(.notSupported))
                        }
                    }
                }
            }
        }
    }

    @MainActor
    private static func topViewController(from root: UIViewController) -> UIViewController {
        if let presented = root.presentedViewController {
            return topViewController(from: presented)
        }
        if let nav = root as? UINavigationController, let visible = nav.visibleViewController {
            return topViewController(from: visible)
        }
        if let tab = root as? UITabBarController, let selected = tab.selectedViewController {
            return topViewController(from: selected)
        }
        return root
    }
}

// MARK: - Barcode Scan Coordinator

@MainActor
private final class BarcodeScanCoordinator: NSObject, DataScannerViewControllerDelegate, UIAdaptivePresentationControllerDelegate {
    nonisolated(unsafe) static var associatedKey: UInt8 = 0
    private var continuation: CheckedContinuation<BarcodeScanResult, Never>?
    private var hasResumed = false

    init(continuation: CheckedContinuation<BarcodeScanResult, Never>) {
        self.continuation = continuation
    }

    deinit {
        guard !hasResumed else { return }
        continuation?.resume(returning: .cancelled)
    }

    func dataScanner(_ dataScanner: DataScannerViewController, didAdd addedItems: [RecognizedItem], allItems: [RecognizedItem]) {
        guard !hasResumed else { return }
        for item in addedItems {
            if case .barcode(let barcode) = item,
               let payload = barcode.payloadStringValue?.trimmingCharacters(in: .whitespacesAndNewlines),
               !payload.isEmpty {
                dataScanner.stopScanning()
                dataScanner.dismiss(animated: true) { [weak self] in
                    self?.resume(returning: .barcode(payload))
                }
                return
            }
        }
    }

    func dataScannerDidCancel(_ dataScanner: DataScannerViewController) {
        dataScanner.dismiss(animated: true) { [weak self] in
            self?.resume(returning: .cancelled)
        }
    }

    func presentationControllerDidDismiss(_ presentationController: UIPresentationController) {
        resume(returning: .cancelled)
    }

    func resume(returning result: BarcodeScanResult) {
        guard !hasResumed else { return }
        hasResumed = true
        continuation?.resume(returning: result)
        continuation = nil
    }
}
#endif
