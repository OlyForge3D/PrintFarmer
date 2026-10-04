import SwiftUI
import OSLog
import ImageIO

struct PrinterCardRequestID: Equatable {
    let printerID: UUID
    let path: String?
    let jobName: String?
    let state: String?
    let serviceID: ObjectIdentifier?
}

@MainActor @Observable
final class PrinterCardMediaModel {
    private static let logger = Logger(subsystem: "com.printfarmer.ios", category: "PrinterCard")
    private(set) var request: PrinterCardRequestID?
    private(set) var image: UIImage?
    private(set) var printTimeLeftSeconds: Double?
    private var revision = UUID()

    func cancel() {
        revision = UUID()
        request = nil
        image = nil
        printTimeLeftSeconds = nil
    }

    func load(request: PrinterCardRequestID, service: (any PrinterServiceProtocol)?) async {
        cancel()
        let revision = self.revision
        self.request = request
        guard let service else { return }
        async let thumbnail: Void = loadThumbnail(request: request, service: service, revision: revision)
        async let status: Void = loadETA(request: request, service: service, revision: revision)
        _ = await (thumbnail, status)
    }

    private func loadThumbnail(
        request: PrinterCardRequestID, service: any PrinterServiceProtocol, revision: UUID
    ) async {
        guard let path = request.path,
              ["printing", "paused"].contains(request.state?.lowercased() ?? "") else { return }
        do {
            let data = try await service.getCurrentJobThumbnail(id: request.printerID, path: path)
            guard self.revision == revision, !Task.isCancelled else { return }
            guard let source = CGImageSourceCreateWithData(data as CFData, nil),
                  let thumbnail = CGImageSourceCreateThumbnailAtIndex(source, 0, [
                    kCGImageSourceCreateThumbnailFromImageAlways: true,
                    kCGImageSourceCreateThumbnailWithTransform: true,
                    kCGImageSourceThumbnailMaxPixelSize: 180
                  ] as CFDictionary) else {
                Self.logger.notice("Current-job thumbnail is not a decodable image")
                return
            }
            self.image = UIImage(cgImage: thumbnail)
        } catch {
            guard self.revision == revision, !AttentionFeedViewModel.isCancellation(error) else { return }
            // Never log the URL, response body or token for optional job media.
            Self.logger.notice("Current-job thumbnail unavailable")
        }
    }

    private func loadETA(
        request: PrinterCardRequestID, service: any PrinterServiceProtocol, revision: UUID
    ) async {
        guard ["printing", "paused"].contains(request.state?.lowercased() ?? "") else { return }
        repeat {
            do {
                let status = try await service.getStatus(id: request.printerID)
                guard self.revision == revision, !Task.isCancelled else { return }
                guard status.isOnline,
                      status.state?.lowercased() == request.state?.lowercased(),
                      status.jobName == request.jobName else {
                    printTimeLeftSeconds = nil
                    return
                }
                printTimeLeftSeconds = status.printTimeLeftSeconds
                try await Task.sleep(for: .seconds(30))
            } catch {
                guard self.revision == revision, !AttentionFeedViewModel.isCancellation(error) else { return }
                printTimeLeftSeconds = nil
                Self.logger.notice("Current-job ETA unavailable")
                return
            }
        } while self.revision == revision && !Task.isCancelled
    }
}
