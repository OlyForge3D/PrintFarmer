import Foundation

enum SignalRFrameParserError: Error, Equatable, Sendable {
    case frameTooLarge(maximumBytes: Int)
}

struct SignalRFrameParser {
    struct AppendDecision: Equatable, Sendable {
        let bufferedBytes: Int
        let incomingBytes: Int
        let maximumBytes: Int
        let canAppend: Bool
    }

#if DEBUG
    struct DebugAppendObservation: Equatable, Sendable {
        let decision: AppendDecision
        let bufferedBytesBeforeMutation: Int
    }
#endif

    static let recordSeparator: UInt8 = 0x1E
    static let maximumFrameBytes = 1_048_576

    private var buffer = Data()
#if DEBUG
    private(set) var debugAppendObservations: [DebugAppendObservation] = []
    var debugBufferedBytes: Int { buffer.count }
#endif

    static func appendDecision(
        bufferedBytes: Int,
        incomingBytes: Int,
        maximumBytes: Int = maximumFrameBytes
    ) -> AppendDecision {
        let canAppend: Bool
        if bufferedBytes < 0
            || incomingBytes < 0
            || maximumBytes < 0
            || bufferedBytes > maximumBytes {
            canAppend = false
        } else {
            canAppend = incomingBytes <= maximumBytes - bufferedBytes
        }
        return AppendDecision(
            bufferedBytes: bufferedBytes,
            incomingBytes: incomingBytes,
            maximumBytes: maximumBytes,
            canAppend: canAppend
        )
    }

    mutating func append(_ chunk: Data) throws -> [Data] {
        var frames: [Data] = []
        var segmentStart = chunk.startIndex

        while let separator = chunk[segmentStart...].firstIndex(of: Self.recordSeparator) {
            try appendBounded(chunk[segmentStart..<separator])
            if !buffer.isEmpty {
                frames.append(buffer)
                buffer = Data()
            }
            segmentStart = chunk.index(after: separator)
        }

        try appendBounded(chunk[segmentStart...])
        return frames
    }

    private mutating func appendBounded(_ segment: Data.SubSequence) throws {
        let decision = Self.appendDecision(
            bufferedBytes: buffer.count,
            incomingBytes: segment.count
        )
#if DEBUG
        debugAppendObservations.append(
            DebugAppendObservation(
                decision: decision,
                bufferedBytesBeforeMutation: buffer.count
            )
        )
#endif
        guard decision.canAppend else {
            reset()
            throw SignalRFrameParserError.frameTooLarge(
                maximumBytes: Self.maximumFrameBytes
            )
        }
        buffer.append(contentsOf: segment)
    }

    mutating func reset() {
        buffer.removeAll(keepingCapacity: false)
    }
}

enum SignalRProtocolMessage: Equatable, Sendable {
    case invocation(target: String, firstArgument: Data?)
    case ping
    case close(error: String?)
    case unsupported(type: Int)
    case malformed

    static func decode(_ data: Data) -> SignalRProtocolMessage {
        guard let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let type = json["type"] as? Int else {
            return .malformed
        }

        switch type {
        case 1:
            guard let target = json["target"] as? String,
                  let arguments = json["arguments"] as? [Any] else {
                return .malformed
            }
            let argumentData: Data?
            if let firstArgument = arguments.first {
                guard let data = try? JSONSerialization.data(
                    withJSONObject: firstArgument,
                    options: .fragmentsAllowed
                ) else {
                    return .malformed
                }
                argumentData = data
            } else {
                argumentData = nil
            }
            return .invocation(target: target, firstArgument: argumentData)
        case 6:
            return .ping
        case 7:
            return .close(error: json["error"] as? String)
        default:
            return .unsupported(type: type)
        }
    }
}
