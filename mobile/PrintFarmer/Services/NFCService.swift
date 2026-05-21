#if canImport(UIKit)
import Foundation
@preconcurrency import CoreNFC

// MARK: - NFC Service

/// Reads and writes NFC tags for spool identification.
/// Supports OpenSpool and OpenPrintTag NDEF formats.
final class NFCService: SpoolScannerProtocol, @unchecked Sendable {

    // MARK: - SpoolScannerProtocol

    var isAvailable: Bool {
        NFCNDEFReaderSession.readingAvailable
    }

    func scan() async -> SpoolScanResult {
        guard isAvailable else {
            return .error(.notSupported)
        }

        return await withCheckedContinuation { continuation in
            let delegate = NFCReadDelegate(continuation: continuation)
            let session = NFCNDEFReaderSession(delegate: delegate, queue: nil, invalidateAfterFirstRead: true)
            session.alertMessage = "Hold your iPhone near a filament spool tag."
            // Keep delegate alive for session duration
            objc_setAssociatedObject(session, &NFCReadDelegate.associatedKey, delegate, .OBJC_ASSOCIATION_RETAIN)
            session.begin()
        }
    }

    // MARK: - Tag Writing

    // MARK: - Printer Tag Writing

    /// Writes a printer identification tag with a deep link URI and printer name.
    func writePrinterTag(printerId: UUID, printerName: String) async throws {
        guard isAvailable else {
            throw SpoolScanError.notSupported
        }

        let urlString = "printfarmer://printer/\(printerId.uuidString)"
        guard let uriPayload = NFCNDEFPayload.wellKnownTypeURIPayload(string: urlString) else {
            throw SpoolScanError.invalidPayload("Could not create printer tag URL.")
        }

        let namePayload = NFCNDEFPayload.wellKnownTypeTextPayload(string: printerName, locale: .current)!
        let message = NFCNDEFMessage(records: [uriPayload, namePayload])

        try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, Error>) in
            let delegate = NFCMessageWriteDelegate(message: message, continuation: continuation)
            let session = NFCTagReaderSession(pollingOption: .iso14443, delegate: delegate, queue: nil)
            session?.alertMessage = "Hold your iPhone near the NFC tag to write printer data for \(printerName)."
            objc_setAssociatedObject(session as Any, &NFCMessageWriteDelegate.associatedKey, delegate, .OBJC_ASSOCIATION_RETAIN)
            session?.begin()
        }
    }

    // MARK: - Spool Tag Writing (Dual-Record NDEF)

    /// Writes a spool NFC tag using the specified format.
    /// - `.openSpool`: dual-record — URI deep link + OpenSpool JSON text record
    /// - `.openTag3D`: single `application/opentag3d` media-type record with binary payload
    func writeSpoolTag(spool: SpoolmanSpool, filament: SpoolmanFilament? = nil, format: NFCTagFormat = .openSpool) async throws {
        guard isAvailable else {
            throw SpoolScanError.notSupported
        }

        let message: NFCNDEFMessage
        switch format {
        case .openSpool:
            let urlString = "printfarmer://spool/\(spool.id)"
            guard let uriPayload = NFCNDEFPayload.wellKnownTypeURIPayload(string: urlString) else {
                throw SpoolScanError.invalidPayload("Could not create spool tag URL.")
            }

            guard let jsonData = NFCTagParser.createOpenSpoolPayload(from: spool),
                  let jsonString = String(data: jsonData, encoding: .utf8) else {
                throw SpoolScanError.invalidPayload("Could not create spool tag payload.")
            }

            let textPayload = NFCNDEFPayload.wellKnownTypeTextPayload(string: jsonString, locale: .current)!
            message = NFCNDEFMessage(records: [uriPayload, textPayload])

        case .openPrintTag:
            let urlString = "printfarmer://spool/\(spool.id)"
            guard let uriPayload = NFCNDEFPayload.wellKnownTypeURIPayload(string: urlString) else {
                throw SpoolScanError.invalidPayload("Could not create spool tag URL.")
            }

            guard let jsonData = NFCTagParser.createOpenPrintTagPayload(from: spool, filament: filament),
                  let jsonString = String(data: jsonData, encoding: .utf8) else {
                throw SpoolScanError.invalidPayload("Could not create OpenPrintTag payload.")
            }

            let textPayload = NFCNDEFPayload.wellKnownTypeTextPayload(string: jsonString, locale: .current)!
            message = NFCNDEFMessage(records: [uriPayload, textPayload])

        case .openTag3D:
            guard let binaryData = NFCTagParser.createOpenTag3DPayload(from: spool, filament: filament) else {
                throw SpoolScanError.invalidPayload("Could not create OpenTag3D payload.")
            }

            let typeData = Data("application/opentag3d".utf8)
            let mediaRecord = NFCNDEFPayload(
                format: .media,
                type: typeData,
                identifier: Data(),
                payload: binaryData
            )
            message = NFCNDEFMessage(records: [mediaRecord])
        }

        let spoolName = spool.filamentName ?? spool.name
        try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, Error>) in
            let delegate = NFCMessageWriteDelegate(message: message, continuation: continuation)
            let session = NFCTagReaderSession(pollingOption: .iso14443, delegate: delegate, queue: nil)
            session?.alertMessage = "Hold your iPhone near the NFC tag to write spool data for \(spoolName)."
            objc_setAssociatedObject(session as Any, &NFCMessageWriteDelegate.associatedKey, delegate, .OBJC_ASSOCIATION_RETAIN)
            session?.begin()
        }
    }

    // MARK: - Legacy Spool Tag Writing

    /// Writes OpenSpool-format NDEF data to an NFC tag from spool data (single-record format).
    func writeTag(spool: SpoolmanSpool) async throws {
        guard isAvailable else {
            throw SpoolScanError.notSupported
        }

        guard let payload = NFCTagParser.createOpenSpoolPayload(from: spool) else {
            throw SpoolScanError.invalidPayload("Could not create tag payload from spool data.")
        }

        try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, Error>) in
            let delegate = NFCWriteDelegate(payload: payload, continuation: continuation)
            let session = NFCTagReaderSession(pollingOption: .iso14443, delegate: delegate, queue: nil)
            session?.alertMessage = "Hold your iPhone near the NFC tag to write spool data."
            objc_setAssociatedObject(session as Any, &NFCWriteDelegate.associatedKey, delegate, .OBJC_ASSOCIATION_RETAIN)
            session?.begin()
        }
    }
}

// MARK: - NFC Read Delegate

private final class NFCReadDelegate: NSObject, NFCNDEFReaderSessionDelegate, @unchecked Sendable {
    nonisolated(unsafe) static var associatedKey: UInt8 = 0
    private var continuation: CheckedContinuation<SpoolScanResult, Never>?

    init(continuation: CheckedContinuation<SpoolScanResult, Never>) {
        self.continuation = continuation
    }

    func readerSession(_ session: NFCNDEFReaderSession, didInvalidateWithError error: Error) {
        let nfcError = error as NSError
        // Code 200 = user cancelled
        if nfcError.domain == "NFCError" && nfcError.code == 200 {
            resume(with: .cancelled)
        } else {
            resume(with: .error(.invalidPayload(error.localizedDescription)))
        }
    }

    func readerSession(_ session: NFCNDEFReaderSession, didDetectNDEFs messages: [NFCNDEFMessage]) {
        for message in messages {
            if let result = parseMessage(message) {
                resume(with: result)
                return
            }
        }
        resume(with: .error(.invalidPayload("No recognized spool data on tag.")))
    }

    private func parseMessage(_ message: NFCNDEFMessage) -> SpoolScanResult? {
        for record in message.records {
            let typeString = String(data: record.type, encoding: .utf8) ?? ""

            if typeString == "application/openspool" {
                if let data = parsePayloadData(record),
                   let spoolData = NFCTagParser.parseOpenSpool(data) {
                    if let spoolId = spoolData.spoolmanId {
                        return .spoolId(spoolId)
                    }
                    return .newSpoolData(spoolData)
                }
            }

            if typeString == "application/opentag3d" {
                if let spoolData = NFCTagParser.parseOpenTag3D(record.payload) {
                    return .newSpoolData(spoolData)
                }
            }

            if typeString == "application/openprinttag" {
                if let data = parsePayloadData(record),
                   let spoolData = NFCTagParser.parseOpenPrintTag(data) {
                    if let spoolId = spoolData.spoolmanId {
                        return .spoolId(spoolId)
                    }
                    return .newSpoolData(spoolData)
                }
            }

            // Fallback: try plain text as QR code format
            if record.typeNameFormat == .nfcWellKnown,
               let text = record.wellKnownTypeTextPayload().0,
               let spoolId = QRCodeParser.parse(text) {
                return .spoolId(spoolId)
            }
        }
        return nil
    }

    private func parsePayloadData(_ record: NFCNDEFPayload) -> Data? {
        // Media-type records store the payload directly
        if record.typeNameFormat == .media {
            return record.payload
        }
        // For other formats, the first byte may be a status byte (text records)
        if record.payload.count > 1 {
            return record.payload.advanced(by: 1)
        }
        return record.payload.isEmpty ? nil : record.payload
    }

    private func resume(with result: SpoolScanResult) {
        continuation?.resume(returning: result)
        continuation = nil
    }
}

// MARK: - NFC Message Write Delegate (Printer Tags)

private final class NFCMessageWriteDelegate: NSObject, NFCTagReaderSessionDelegate, @unchecked Sendable {
    nonisolated(unsafe) static var associatedKey: UInt8 = 0
    private let message: NFCNDEFMessage
    private var continuation: CheckedContinuation<Void, Error>?

    init(message: NFCNDEFMessage, continuation: CheckedContinuation<Void, Error>) {
        self.message = message
        self.continuation = continuation
    }

    func tagReaderSessionDidBecomeActive(_ session: NFCTagReaderSession) {}

    func tagReaderSession(_ session: NFCTagReaderSession, didInvalidateWithError error: Error) {
        let nfcError = error as NSError
        if nfcError.domain == "NFCError" && nfcError.code == 200 {
            resume(throwing: SpoolScanError.cancelled)
        } else {
            resume(throwing: SpoolScanError.invalidPayload(error.localizedDescription))
        }
    }

    func tagReaderSession(_ session: NFCTagReaderSession, didDetect tags: [NFCTag]) {
        nonisolated(unsafe) let session = session

        guard let tag = tags.first else {
            session.invalidate(errorMessage: "No tag detected.")
            resume(throwing: SpoolScanError.invalidPayload("No tag detected."))
            return
        }

        session.connect(to: tag) { [weak self] error in
            guard let self else { return }
            if let error {
                session.invalidate(errorMessage: "Connection failed.")
                self.resume(throwing: SpoolScanError.invalidPayload(error.localizedDescription))
                return
            }

            var ndefTag: NFCNDEFTag?
            switch tag {
            case .iso7816(let t): ndefTag = t
            case .miFare(let t): ndefTag = t
            case .iso15693(let t): ndefTag = t
            case .feliCa(let t): ndefTag = t
            @unknown default: break
            }

            guard let ndef = ndefTag else {
                session.invalidate(errorMessage: "Tag does not support NDEF.")
                self.resume(throwing: SpoolScanError.invalidPayload("Tag does not support NDEF."))
                return
            }

            ndef.writeNDEF(self.message) { writeError in
                if let writeError {
                    session.invalidate(errorMessage: "Write failed.")
                    self.resume(throwing: SpoolScanError.invalidPayload(writeError.localizedDescription))
                } else {
                    session.alertMessage = "Tag written successfully!"
                    session.invalidate()
                    self.resume(returning: ())
                }
            }
        }
    }

    private func resume(throwing error: Error) {
        continuation?.resume(throwing: error)
        continuation = nil
    }

    private func resume(returning value: Void) {
        continuation?.resume(returning: value)
        continuation = nil
    }
}

// MARK: - NFC Write Delegate

private final class NFCWriteDelegate: NSObject, NFCTagReaderSessionDelegate, @unchecked Sendable {
    nonisolated(unsafe) static var associatedKey: UInt8 = 0
    private let payload: Data
    private var continuation: CheckedContinuation<Void, Error>?

    init(payload: Data, continuation: CheckedContinuation<Void, Error>) {
        self.payload = payload
        self.continuation = continuation
    }

    func tagReaderSessionDidBecomeActive(_ session: NFCTagReaderSession) {}

    func tagReaderSession(_ session: NFCTagReaderSession, didInvalidateWithError error: Error) {
        let nfcError = error as NSError
        if nfcError.domain == "NFCError" && nfcError.code == 200 {
            resume(throwing: SpoolScanError.cancelled)
        } else {
            resume(throwing: SpoolScanError.invalidPayload(error.localizedDescription))
        }
    }

    func tagReaderSession(_ session: NFCTagReaderSession, didDetect tags: [NFCTag]) {
        // NFCTagReaderSession predates Swift Concurrency and isn't Sendable.
        // Rebind once so all nested @Sendable closures capture the safe binding.
        nonisolated(unsafe) let session = session

        guard let tag = tags.first else {
            session.invalidate(errorMessage: "No tag detected.")
            resume(throwing: SpoolScanError.invalidPayload("No tag detected."))
            return
        }

        session.connect(to: tag) { [weak self] error in
            guard let self else { return }
            if let error {
                session.invalidate(errorMessage: "Connection failed.")
                self.resume(throwing: SpoolScanError.invalidPayload(error.localizedDescription))
                return
            }

            var ndefTag: NFCNDEFTag?
            switch tag {
            case .iso7816(let t): ndefTag = t
            case .miFare(let t): ndefTag = t
            case .iso15693(let t): ndefTag = t
            case .feliCa(let t): ndefTag = t
            @unknown default: break
            }

            guard let ndef = ndefTag else {
                session.invalidate(errorMessage: "Tag does not support NDEF.")
                self.resume(throwing: SpoolScanError.invalidPayload("Tag does not support NDEF."))
                return
            }

            // Create OpenSpool NDEF record
            let typeData = Data("application/openspool".utf8)
            let ndefPayload = NFCNDEFPayload(
                format: .media,
                type: typeData,
                identifier: Data(),
                payload: self.payload
            )
            let ndefMessage = NFCNDEFMessage(records: [ndefPayload])

            ndef.writeNDEF(ndefMessage) { writeError in
                if let writeError {
                    session.invalidate(errorMessage: "Write failed.")
                    self.resume(throwing: SpoolScanError.invalidPayload(writeError.localizedDescription))
                } else {
                    session.alertMessage = "Spool data written successfully!"
                    session.invalidate()
                    self.resume(returning: ())
                }
            }
        }
    }

    private func resume(throwing error: Error) {
        continuation?.resume(throwing: error)
        continuation = nil
    }

    private func resume(returning value: Void) {
        continuation?.resume(returning: value)
        continuation = nil
    }
}
#endif
