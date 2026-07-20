import Foundation

enum SignalRFrameParserError: Error, Equatable, Sendable {
    case frameTooLarge(maximumBytes: Int)
}

struct SignalRFrameParser {
    static let recordSeparator: UInt8 = 0x1E
    static let maximumFrameBytes = 1_048_576

    private var buffer = Data()

    mutating func append(_ chunk: Data) throws -> [Data] {
        buffer.append(chunk)
        var frames: [Data] = []

        while let separator = buffer.firstIndex(of: Self.recordSeparator) {
            let frameLength = buffer.distance(
                from: buffer.startIndex,
                to: separator
            )
            guard frameLength <= Self.maximumFrameBytes else {
                reset()
                throw SignalRFrameParserError.frameTooLarge(
                    maximumBytes: Self.maximumFrameBytes
                )
            }
            let frame = Data(buffer[..<separator])
            buffer.removeSubrange(...separator)
            if !frame.isEmpty {
                frames.append(frame)
            }
        }

        guard buffer.count <= Self.maximumFrameBytes else {
            reset()
            throw SignalRFrameParserError.frameTooLarge(
                maximumBytes: Self.maximumFrameBytes
            )
        }
        return frames
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
