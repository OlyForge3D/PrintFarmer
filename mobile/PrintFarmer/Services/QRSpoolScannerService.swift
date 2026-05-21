#if canImport(UIKit)
import Foundation
@preconcurrency import VisionKit
import AVFoundation

// MARK: - QR Spool Scanner Service

/// Scans QR codes using VisionKit DataScannerViewController to identify spools.
final class QRSpoolScannerService: SpoolScannerProtocol, @unchecked Sendable {

    var isAvailable: Bool {
        // DataScannerViewController.isSupported is a compile-time check, safe to call
        true // Actual availability checked in scan()
    }

    func scan() async -> SpoolScanResult {
        // Check device support on MainActor where DataScannerViewController properties are isolated
        let supported = await MainActor.run { DataScannerViewController.isSupported }
        guard supported else {
            return .error(.notSupported)
        }

        // Check camera permission
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

        // Bridge DataScannerViewController delegate to async
        return await withCheckedContinuation { continuation in
            Task { @MainActor in
                let coordinator = ScanCoordinator(continuation: continuation)
                let scanner = DataScannerViewController(
                    recognizedDataTypes: [.barcode(symbologies: [.qr])],
                    qualityLevel: .balanced,
                    isHighlightingEnabled: true
                )
                scanner.delegate = coordinator

                // Present the scanner
                guard let windowScene = UIApplication.shared.connectedScenes
                    .compactMap({ $0 as? UIWindowScene }).first,
                      let rootVC = windowScene.windows.first?.rootViewController else {
                    continuation.resume(returning: .error(.notSupported))
                    return
                }

                // Keep coordinator alive while scanner is presented
                objc_setAssociatedObject(scanner, &ScanCoordinator.associatedKey, coordinator, .OBJC_ASSOCIATION_RETAIN)

                let topVC = Self.topViewController(from: rootVC)
                topVC.present(scanner, animated: true) {
                    try? scanner.startScanning()
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

// MARK: - Scan Coordinator

@MainActor
private final class ScanCoordinator: NSObject, DataScannerViewControllerDelegate {
    nonisolated(unsafe) static var associatedKey: UInt8 = 0
    private var continuation: CheckedContinuation<SpoolScanResult, Never>?
    private var hasResumed = false

    init(continuation: CheckedContinuation<SpoolScanResult, Never>) {
        self.continuation = continuation
    }

    func dataScanner(_ dataScanner: DataScannerViewController, didAdd addedItems: [RecognizedItem], allItems: [RecognizedItem]) {
        guard !hasResumed else { return }
        for item in addedItems {
            if case .barcode(let barcode) = item,
               let payload = barcode.payloadStringValue,
               let spoolId = QRCodeParser.parse(payload) {
                hasResumed = true
                dataScanner.stopScanning()
                dataScanner.dismiss(animated: true) { [weak self] in
                    self?.continuation?.resume(returning: .spoolId(spoolId))
                    self?.continuation = nil
                }
                return
            }
        }
    }

    func dataScannerDidCancel(_ dataScanner: DataScannerViewController) {
        guard !hasResumed else { return }
        hasResumed = true
        dataScanner.dismiss(animated: true) { [weak self] in
            self?.continuation?.resume(returning: .cancelled)
            self?.continuation = nil
        }
    }
}
#endif
