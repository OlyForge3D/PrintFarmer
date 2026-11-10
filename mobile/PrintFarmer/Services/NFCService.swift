#if canImport(UIKit)
import Foundation
@preconcurrency import CoreNFC

// MARK: - NFC Service

/// Reads NFC UIDs and legacy NDEF data, and writes spool identification records.
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
            let session = NFCTagReaderSession(
                pollingOption: [.iso14443, .iso15693],
                delegate: delegate,
                queue: nil
            )
            guard let session else {
                continuation.resume(returning: .error(.notSupported))
                return
            }
            session.alertMessage = "Hold your iPhone near a printer or filament spool tag."
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

        _ = try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<String, Error>) in
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
    func writeSpoolTag(
        spool: SpoolmanSpool,
        filament: SpoolmanFilament? = nil,
        format: NFCTagFormat = .openSpool,
        beforeWrite: @escaping @Sendable (String) async throws -> Void,
        afterWrite: @escaping @Sendable (String) async throws -> Void
    ) async throws -> String {
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
            guard let openPrintTagData = NFCTagParser.createOpenPrintTagPayload(from: spool, filament: filament) else {
                throw SpoolScanError.invalidPayload("Could not create OpenPrintTag payload.")
            }

            let mediaPayload = NFCNDEFPayload(
                format: .media,
                type: Data(NFCTagParser.openPrintTagMimeType.utf8),
                identifier: Data(),
                payload: openPrintTagData
            )
            message = NFCNDEFMessage(records: [mediaPayload])

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
        return try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<String, Error>) in
            let delegate = NFCMessageWriteDelegate(
                message: message,
                continuation: continuation,
                requiresISO15693: format == .openPrintTag,
                beforeWrite: beforeWrite,
                afterWrite: afterWrite
            )
            let session = NFCTagReaderSession(
                pollingOption: [.iso14443, .iso15693],
                delegate: delegate,
                queue: nil
            )
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

private final class NFCReadDelegate: NSObject, NFCTagReaderSessionDelegate, @unchecked Sendable {
    nonisolated(unsafe) static var associatedKey: UInt8 = 0
    private var continuation: CheckedContinuation<SpoolScanResult, Never>?

    init(continuation: CheckedContinuation<SpoolScanResult, Never>) {
        self.continuation = continuation
    }

    func tagReaderSessionDidBecomeActive(_ session: NFCTagReaderSession) {}

    func tagReaderSession(_ session: NFCTagReaderSession, didInvalidateWithError error: Error) {
        let nfcError = error as NSError
        if nfcError.domain == NFCReaderError.errorDomain,
           nfcError.code == NFCReaderError.readerSessionInvalidationErrorUserCanceled.rawValue {
            resume(with: .cancelled)
        } else {
            resume(with: .error(.invalidPayload(error.localizedDescription)))
        }
    }

    func tagReaderSession(_ session: NFCTagReaderSession, didDetect tags: [NFCTag]) {
        nonisolated(unsafe) let session = session
        guard let tag = tags.first else {
            session.invalidate(errorMessage: "No tag detected.")
            resume(with: .error(.invalidPayload("No tag detected.")))
            return
        }
        nonisolated(unsafe) let connectedTag = tag

        session.connect(to: connectedTag) { [weak self] error in
            guard let self else { return }
            if let error {
                session.invalidate(errorMessage: "Could not read tag.")
                self.resume(with: .error(.invalidPayload(error.localizedDescription)))
                return
            }

            guard let (uid, technology) = Self.canonicalIdentifier(for: connectedTag) else {
                session.invalidate(errorMessage: "Unsupported NFC tag.")
                self.resume(with: .error(.notSupported))
                return
            }

            guard let ndef = Self.ndefTag(for: connectedTag) else {
                session.alertMessage = "Tag UID read successfully."
                session.invalidate()
                self.resume(with: .nfcTag(ScannedNFCTag(
                    uid: uid,
                    technology: technology,
                    openPrintTagPayload: nil,
                    legacyContent: nil
                )))
                return
            }

            ndef.readNDEF { message, readError in
                if readError != nil {
                    session.alertMessage = "Tag UID read successfully; NDEF data was unavailable."
                    session.invalidate()
                    self.resume(with: .nfcTag(ScannedNFCTag(
                        uid: uid,
                        technology: technology,
                        openPrintTagPayload: nil,
                        legacyContent: nil
                    )))
                    return
                }

                let parsed = message.map(self.parseMessage)
                session.alertMessage = "Tag read successfully."
                session.invalidate()
                self.resume(with: .nfcTag(ScannedNFCTag(
                    uid: uid,
                    technology: technology,
                    openPrintTagPayload: parsed?.openPrintTagPayload,
                    legacyContent: parsed?.legacyContent
                )))
            }
        }
    }

    private func parseMessage(_ message: NFCNDEFMessage) -> (openPrintTagPayload: Data?, legacyContent: LegacyNFCTagContent?) {
        var openPrintTagPayload: Data?
        var fallback: LegacyNFCTagContent?
        for record in message.records {
            if let url = record.wellKnownTypeURIPayload(),
               let result = NFCTagParser.parseNavigationURL(url) {
                fallback = fallback ?? legacyContent(from: result)
            }
            let typeString = String(data: record.type, encoding: .utf8) ?? ""

            if record.typeNameFormat == .media,
               typeString.caseInsensitiveCompare(NFCTagParser.openPrintTagMimeType) == .orderedSame {
                openPrintTagPayload = record.payload
                if let spoolData = NFCTagParser.parseOpenPrintTag(record.payload) {
                    fallback = fallback ?? .newSpoolData(spoolData)
                }
            }

            if typeString == "application/openspool" {
                if let data = parsePayloadData(record),
                   let spoolData = NFCTagParser.parseOpenSpool(data) {
                    fallback = fallback ?? legacyContent(from: spoolData)
                }
            }

            if typeString == "application/opentag3d" {
                if let spoolData = NFCTagParser.parseOpenTag3D(record.payload) {
                    fallback = fallback ?? .newSpoolData(spoolData)
                }
            }

            if typeString == "application/openprinttag" {
                if let data = parsePayloadData(record),
                   let spoolData = NFCTagParser.parseLegacyOpenPrintTag(data) {
                    fallback = fallback ?? .newSpoolData(spoolData)
                }
            }

            if record.typeNameFormat == .nfcWellKnown,
               let text = record.wellKnownTypeTextPayload().0 {
                if let url = URL(string: text),
                   let result = NFCTagParser.parseNavigationURL(url) {
                    fallback = fallback ?? legacyContent(from: result)
                } else if let spoolId = QRCodeParser.parse(text) {
                    fallback = fallback ?? .spoolId(spoolId)
                }
            }
        }
        return (openPrintTagPayload, fallback)
    }

    private func legacyContent(from result: SpoolScanResult) -> LegacyNFCTagContent? {
        switch result {
        case .spoolId(let id): .spoolId(id)
        case .printerId(let id): .printerId(id)
        case .newSpoolData(let data): .newSpoolData(data)
        case .nfcTag, .cancelled, .error: nil
        }
    }

    private func legacyContent(from data: ScannedSpoolData) -> LegacyNFCTagContent {
        if let spoolId = data.spoolmanId {
            return .spoolId(spoolId)
        }
        return .newSpoolData(data)
    }

    fileprivate static func canonicalIdentifier(for tag: NFCTag) -> (String, NFCTagTechnology)? {
        let identifier: Data
        let technology: NFCTagTechnology
        switch tag {
        case .iso15693(let value):
            identifier = value.identifier
            technology = .iso15693
        case .miFare(let value):
            identifier = value.identifier
            technology = .iso14443
        case .iso7816(let value):
            identifier = value.identifier
            technology = .iso14443
        case .feliCa:
            return nil
        @unknown default:
            return nil
        }
        guard let uid = NFCTagParser.canonicalUID(from: identifier) else { return nil }
        return (uid, technology)
    }

    fileprivate static func ndefTag(for tag: NFCTag) -> NFCNDEFTag? {
        switch tag {
        case .iso7816(let value): value
        case .miFare(let value): value
        case .iso15693(let value): value
        case .feliCa(let value): value
        @unknown default: nil
        }
    }

    private func parsePayloadData(_ record: NFCNDEFPayload) -> Data? {
        if record.typeNameFormat == .media {
            return record.payload
        }
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

// MARK: - NFC Message Write Delegate

private final class NFCMessageWriteDelegate: NSObject, NFCTagReaderSessionDelegate, @unchecked Sendable {
    nonisolated(unsafe) static var associatedKey: UInt8 = 0
    private let message: NFCNDEFMessage
    private let requiresISO15693: Bool
    private let beforeWrite: (@Sendable (String) async throws -> Void)?
    private let afterWrite: (@Sendable (String) async throws -> Void)?
    // CoreNFC invalidation callbacks can race with the async UID registration hooks.
    private let continuationLock = NSLock()
    private var continuation: CheckedContinuation<String, Error>?
    private var sessionInvalidated = false
    private var physicalWriteVerified = false

    init(
        message: NFCNDEFMessage,
        continuation: CheckedContinuation<String, Error>,
        requiresISO15693: Bool = false,
        beforeWrite: (@Sendable (String) async throws -> Void)? = nil,
        afterWrite: (@Sendable (String) async throws -> Void)? = nil
    ) {
        self.message = message
        self.continuation = continuation
        self.requiresISO15693 = requiresISO15693
        self.beforeWrite = beforeWrite
        self.afterWrite = afterWrite
    }

    func tagReaderSessionDidBecomeActive(_ session: NFCTagReaderSession) {}

    func tagReaderSession(_ session: NFCTagReaderSession, didInvalidateWithError error: Error) {
        let nfcError = error as NSError
        guard shouldResumeAfterInvalidation() else {
            return
        }
        if nfcError.domain == NFCReaderError.errorDomain,
           nfcError.code == NFCReaderError.readerSessionInvalidationErrorUserCanceled.rawValue {
            resume(throwing: SpoolScanError.cancelled)
            return
        }
        resume(throwing: SpoolScanError.invalidPayload(error.localizedDescription))
    }

    func tagReaderSession(_ session: NFCTagReaderSession, didDetect tags: [NFCTag]) {
        nonisolated(unsafe) let session = session
        guard let tag = tags.first else {
            resume(throwing: SpoolScanError.invalidPayload("No tag detected."))
            session.invalidate(errorMessage: "No tag detected.")
            return
        }
        nonisolated(unsafe) let connectedTag = tag

        session.connect(to: connectedTag) { [weak self] error in
            guard let self else { return }
            if let error {
                self.resume(throwing: SpoolScanError.invalidPayload(error.localizedDescription))
                session.invalidate(errorMessage: "Connection failed.")
                return
            }
            guard let (uid, technology) = Self.canonicalIdentifier(for: connectedTag),
                  let ndefTag = Self.ndefTag(for: connectedTag) else {
                self.resume(throwing: SpoolScanError.tagNotNDEFSupported)
                session.invalidate(errorMessage: "Tag does not support NDEF or has no supported UID.")
                return
            }
            guard !self.requiresISO15693 || technology == .iso15693 else {
                self.resume(throwing: SpoolScanError.tagRequiresISO15693)
                session.invalidate(errorMessage: "OpenPrintTag writing requires an NFC-V (ISO 15693) tag.")
                return
            }
            nonisolated(unsafe) let ndef = ndefTag

            ndef.queryNDEFStatus { status, capacity, statusError in
                if let statusError {
                    self.resume(throwing: SpoolScanError.invalidPayload(statusError.localizedDescription))
                    session.invalidate(errorMessage: "Could not check tag.")
                    return
                }
                switch status {
                case .notSupported:
                    self.resume(throwing: SpoolScanError.tagNotNDEFSupported)
                    session.invalidate(errorMessage: "Tag does not support NDEF.")
                case .readOnly:
                    self.resume(throwing: SpoolScanError.tagReadOnly)
                    session.invalidate(errorMessage: "Tag is read-only.")
                case .readWrite:
                    guard self.message.length <= capacity else {
                        self.resume(throwing: SpoolScanError.tagCapacityExceeded(capacity))
                        session.invalidate(errorMessage: "Tag capacity is too small.")
                        return
                    }
                    Task {
                        do {
                            try await self.beforeWrite?(uid)
                            DispatchQueue.main.async {
                                guard self.canStartPhysicalWrite() else { return }
                                ndef.writeNDEF(self.message) { writeError in
                                    if let writeError {
                                        self.resume(throwing: SpoolScanError.invalidPayload(writeError.localizedDescription))
                                        session.invalidate(errorMessage: "Write failed.")
                                        return
                                    }
                                    ndef.readNDEF { writtenMessage, readError in
                                        guard readError == nil,
                                              let writtenMessage,
                                              Self.messagesEqual(self.message, writtenMessage) else {
                                            self.resume(throwing: SpoolScanError.invalidPayload(
                                                readError?.localizedDescription ?? "Tag contents did not match the written message."
                                            ))
                                            session.invalidate(errorMessage: "Read-back verification failed.")
                                            return
                                        }
                                        self.markPhysicalWriteVerified()
                                        Task {
                                            do {
                                                try await self.afterWrite?(uid)
                                                self.resume(returning: uid)
                                                DispatchQueue.main.async {
                                                    session.alertMessage = "Tag written and verified."
                                                    session.invalidate()
                                                }
                                            } catch {
                                                self.resume(throwing: SpoolScanError.tagWrittenButLinkFailed(error.localizedDescription))
                                                DispatchQueue.main.async {
                                                    session.invalidate(errorMessage: "Tag written, but its UID could not be linked.")
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        } catch {
                            self.resume(throwing: error)
                            DispatchQueue.main.async {
                                session.invalidate(errorMessage: "Could not link tag UID.")
                            }
                        }
                    }
                    @unknown default:
                        self.resume(throwing: SpoolScanError.tagNotNDEFSupported)
                        session.invalidate(errorMessage: "Tag NDEF status is unsupported.")
                    }
                }
            }
        }

    private static func canonicalIdentifier(for tag: NFCTag) -> (String, NFCTagTechnology)? {
        NFCReadDelegate.canonicalIdentifier(for: tag)
    }

    private static func ndefTag(for tag: NFCTag) -> NFCNDEFTag? {
        NFCReadDelegate.ndefTag(for: tag)
    }

    private static func messagesEqual(_ left: NFCNDEFMessage, _ right: NFCNDEFMessage) -> Bool {
        left.records.count == right.records.count
            && zip(left.records, right.records).allSatisfy {
                $0.typeNameFormat == $1.typeNameFormat
                    && $0.type == $1.type
                    && $0.identifier == $1.identifier
                    && $0.payload == $1.payload
            }
    }

    private func resume(throwing error: Error) {
        takeContinuation()?.resume(throwing: error)
    }

    private func resume(returning value: String) {
        takeContinuation()?.resume(returning: value)
    }

    private func takeContinuation() -> CheckedContinuation<String, Error>? {
        continuationLock.lock()
        defer { continuationLock.unlock() }
        let current = continuation
        continuation = nil
        return current
    }

    private func canStartPhysicalWrite() -> Bool {
        continuationLock.lock()
        defer { continuationLock.unlock() }
        return !sessionInvalidated
    }

    private func markPhysicalWriteVerified() {
        continuationLock.lock()
        defer { continuationLock.unlock() }
        physicalWriteVerified = true
    }

    private func shouldResumeAfterInvalidation() -> Bool {
        continuationLock.lock()
        defer { continuationLock.unlock() }
        sessionInvalidated = true
        return !physicalWriteVerified
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

        guard let detectedTag = tags.first else {
            session.invalidate(errorMessage: "No tag detected.")
            resume(throwing: SpoolScanError.invalidPayload("No tag detected."))
            return
        }
        // NFCTag is not Sendable but CoreNFC drives its callbacks on its
        // own serial dispatch queue, so this tag is only touched from there.
        // Rebind with nonisolated(unsafe) so the @Sendable connect
        // completion handler can capture it under Swift 6 strict concurrency.
        nonisolated(unsafe) let tag = detectedTag

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
