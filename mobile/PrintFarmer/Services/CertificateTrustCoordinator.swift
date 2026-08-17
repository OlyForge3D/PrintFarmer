import Darwin
import Foundation
import Observation
import Security

struct CertificateTrustRequest: Identifiable, Equatable, Sendable {
  let id: UUID
  let endpoint: String
  let fingerprint: String
  let subjectCommonName: String?
  let issuerCommonName: String?
  let notBefore: Date?
  let notAfter: Date?
  let subjectAlternativeNames: [String]
  let warning: String?
}

@Observable
final class CertificateTrustPresentation: @unchecked Sendable {
  static let shared = CertificateTrustPresentation()

  private(set) var request: CertificateTrustRequest?
  @ObservationIgnored private var continuation: CheckedContinuation<Bool, Never>?
  @ObservationIgnored private var deadlineTask: Task<Void, Never>?

  @MainActor
  func confirm(_ request: CertificateTrustRequest, deadline: Duration = .seconds(120)) async -> Bool
  {
    await withCheckedContinuation { continuation in
      self.request = request
      self.continuation = continuation
      deadlineTask?.cancel()
      deadlineTask = Task { [weak self] in
        try? await Task.sleep(for: deadline)
        guard !Task.isCancelled else { return }
        self?.respond(accepted: false)
      }
    }
  }

  @MainActor
  func respond(accepted: Bool) {
    guard let continuation else { return }
    self.continuation = nil
    request = nil
    deadlineTask?.cancel()
    deadlineTask = nil
    continuation.resume(returning: accepted)
  }
}

protocol CertificateConfirmationRequesting: Sendable {
  func confirm(_ request: CertificateTrustRequest) async -> Bool
  func cancelPending() async
}

extension CertificateConfirmationRequesting {
  func cancelPending() async {}
}

extension CertificateTrustPresentation: CertificateConfirmationRequesting {
  @MainActor
  func confirm(_ request: CertificateTrustRequest) async -> Bool {
    await confirm(request, deadline: .seconds(120))
  }

  @MainActor
  func cancelPending() async {
    respond(accepted: false)
  }
}

enum CertificateTrustOutcome: Equatable, Sendable {
  case useSystemTrust
  case usePinnedLeaf
  case certificateNotTrusted(String)
  case certificateChanged(String)
}

struct CertificateTrustCandidate: Sendable {
  let endpoint: String
  let spkiSHA256: String
  let leafCertificateSHA256: String
  let subjectCommonName: String?
  let issuerCommonName: String?
  let notBefore: Date?
  let notAfter: Date?
  let subjectAlternativeNames: [String]
  let warning: String?
}

actor CertificateTrustCoordinator {
  static let shared = CertificateTrustCoordinator()

  private struct PendingConfirmation {
    let fingerprint: String
    let task: Task<Bool, Never>
  }

  private struct CertificateMetadata {
    let issuerCommonName: String?
    let notBefore: Date?
    let notAfter: Date?
    let subjectAlternativeNames: [String]

    private struct Node {
      let tag: UInt8
      let content: Range<Int>
    }

    static func parse(_ certificate: SecCertificate) -> CertificateMetadata {
      let data = SecCertificateCopyData(certificate) as Data
      guard let root = node(in: data, at: 0),
        let tbs = children(of: root, in: data).first
      else {
        return CertificateMetadata(
          issuerCommonName: nil,
          notBefore: nil,
          notAfter: nil,
          subjectAlternativeNames: []
        )
      }
      let fields = children(of: tbs, in: data)
      let offset = fields.first?.tag == 0xa0 ? 1 : 0
      guard fields.count > offset + 5 else {
        return CertificateMetadata(
          issuerCommonName: nil,
          notBefore: nil,
          notAfter: nil,
          subjectAlternativeNames: []
        )
      }
      let issuer = fields[offset + 2]
      let validity = fields[offset + 3]
      let dates = children(of: validity, in: data).compactMap { date(from: $0, in: data) }
      let sans =
        fields.first(where: { $0.tag == 0xa3 })
        .map { subjectAlternativeNames(from: $0, in: data) } ?? []
      return CertificateMetadata(
        issuerCommonName: commonName(from: issuer, in: data),
        notBefore: dates.first,
        notAfter: dates.dropFirst().first,
        subjectAlternativeNames: sans
      )
    }

    private static func commonName(from issuer: Node, in data: Data) -> String? {
      let commonNameOID = Data([0x55, 0x04, 0x03])
      for set in children(of: issuer, in: data) {
        for sequence in children(of: set, in: data) {
          let values = children(of: sequence, in: data)
          guard values.count >= 2,
            values[0].tag == 0x06,
            data.subdata(in: values[0].content) == commonNameOID
          else {
            continue
          }
          return String(data: data.subdata(in: values[1].content), encoding: .utf8)
        }
      }
      return nil
    }

    private static func date(from node: Node, in data: Data) -> Date? {
      guard node.tag == 0x17 || node.tag == 0x18,
        let value = String(data: data.subdata(in: node.content), encoding: .ascii)
      else {
        return nil
      }
      let formatter = DateFormatter()
      formatter.locale = Locale(identifier: "en_US_POSIX")
      formatter.timeZone = TimeZone(secondsFromGMT: 0)
      formatter.dateFormat = node.tag == 0x17 ? "yyMMddHHmmss'Z'" : "yyyyMMddHHmmss'Z'"
      return formatter.date(from: value)
    }

    private static func subjectAlternativeNames(from extensions: Node, in data: Data) -> [String] {
      let subjectAltNameOID = Data([0x55, 0x1d, 0x11])
      guard let extensionsSequence = children(of: extensions, in: data).first else { return [] }
      for ext in children(of: extensionsSequence, in: data) {
        let fields = children(of: ext, in: data)
        guard let oid = fields.first,
          oid.tag == 0x06,
          data.subdata(in: oid.content) == subjectAltNameOID,
          let octet = fields.last,
          octet.tag == 0x04,
          let namesSequence = node(in: data, at: octet.content.lowerBound)
        else {
          continue
        }
        return children(of: namesSequence, in: data).compactMap { name in
          let bytes = data.subdata(in: name.content)
          switch name.tag {
          case 0x82:
            return String(data: bytes, encoding: .ascii)
          case 0x87:
            return ipAddress(bytes)
          default:
            return nil
          }
        }
      }
      return []
    }

    private static func ipAddress(_ data: Data) -> String? {
      let family: Int32
      let length: Int32
      switch data.count {
      case 4:
        family = AF_INET
        length = INET_ADDRSTRLEN
      case 16:
        family = AF_INET6
        length = INET6_ADDRSTRLEN
      default:
        return nil
      }
      var bytes = [UInt8](data)
      var output = [CChar](repeating: 0, count: Int(length))
      return bytes.withUnsafeMutableBytes { address in
        guard inet_ntop(family, address.baseAddress, &output, socklen_t(output.count)) != nil else {
          return nil
        }
        return String(
          decoding: output.prefix { $0 != 0 }.map { UInt8(bitPattern: $0) },
          as: UTF8.self
        )
      }
    }

    private static func children(of parent: Node, in data: Data) -> [Node] {
      var result: [Node] = []
      var offset = parent.content.lowerBound
      while offset < parent.content.upperBound, let child = node(in: data, at: offset) {
        guard child.content.upperBound <= parent.content.upperBound else { return [] }
        result.append(child)
        offset = child.content.upperBound
      }
      return offset == parent.content.upperBound ? result : []
    }

    private static func node(in data: Data, at offset: Int) -> Node? {
      guard offset >= 0, offset + 2 <= data.count else { return nil }
      let tag = data[offset]
      let firstLength = Int(data[offset + 1])
      let headerLength: Int
      let contentLength: Int
      if firstLength & 0x80 == 0 {
        headerLength = 2
        contentLength = firstLength
      } else {
        let byteCount = firstLength & 0x7f
        guard (1...4).contains(byteCount), offset + 2 + byteCount <= data.count else { return nil }
        var value = 0
        for index in 0..<byteCount {
          value = (value << 8) | Int(data[offset + 2 + index])
        }
        headerLength = 2 + byteCount
        contentLength = value
      }
      let start = offset + headerLength
      guard contentLength >= 0, start <= data.count, contentLength <= data.count - start else {
        return nil
      }
      return Node(tag: tag, content: start..<(start + contentLength))
    }
  }

  private let pinStore: any CertificatePinStoring
  private let confirmationRequester: any CertificateConfirmationRequesting
  private var pending: [String: PendingConfirmation] = [:]
  private var promptTail: Task<Void, Never>?
  private var promptSequence = 0

  init(
    pinStore: any CertificatePinStoring = CertificatePinStore(),
    confirmationRequester: any CertificateConfirmationRequesting = CertificateTrustPresentation
      .shared
  ) {
    self.pinStore = pinStore
    self.confirmationRequester = confirmationRequester
  }

  func evaluate(serverTrust: SecTrust, host: String, port: Int) async -> CertificateTrustOutcome {
    guard NetworkHostClassifier.classify(host) == .local,
      let endpoint = NetworkHostClassifier.endpointKey(host: host, port: port)
    else {
      return .certificateNotTrusted(host)
    }

    let policy = SecPolicyCreateSSL(true, host as CFString)
    SecTrustSetPolicies(serverTrust, policy)
    if SecTrustEvaluateWithError(serverTrust, nil) {
      return .useSystemTrust
    }

    let certificateCount = SecTrustGetCertificateCount(serverTrust)
    guard certificateCount == 1,
      let leaf = (SecTrustCopyCertificateChain(serverTrust) as? [SecCertificate])?.first,
      let spki = CertificateFingerprint.spkiSHA256(leaf)
    else {
      return .certificateNotTrusted(endpoint)
    }

    SecTrustSetPolicies(serverTrust, policy)
    SecTrustSetAnchorCertificates(serverTrust, [leaf] as CFArray)
    SecTrustSetAnchorCertificatesOnly(serverTrust, true)
    guard SecTrustEvaluateWithError(serverTrust, nil) else {
      return .certificateNotTrusted(endpoint)
    }

    let metadata = CertificateMetadata.parse(leaf)
    let candidate = CertificateTrustCandidate(
      endpoint: endpoint,
      spkiSHA256: spki,
      leafCertificateSHA256: CertificateFingerprint.leafSHA256(leaf),
      subjectCommonName: commonName(leaf),
      issuerCommonName: metadata.issuerCommonName,
      notBefore: metadata.notBefore,
      notAfter: metadata.notAfter,
      subjectAlternativeNames: metadata.subjectAlternativeNames,
      warning: TLSCertificateProfile.warningSummary(for: leaf)
    )
    return await resolve(candidate)
  }

  func resolve(_ candidate: CertificateTrustCandidate) async -> CertificateTrustOutcome {
    if let existing = pinStore.load(endpoint: candidate.endpoint) {
      return existing.spkiSHA256 == candidate.spkiSHA256
        ? .usePinnedLeaf
        : .certificateChanged(candidate.endpoint)
    }

    let accepted: Bool
    if let existingPending = pending[candidate.endpoint] {
      guard existingPending.fingerprint == candidate.spkiSHA256 else {
        return .certificateNotTrusted(candidate.endpoint)
      }
      accepted = await existingPending.task.value
    } else {
      let request = CertificateTrustRequest(
        id: UUID(),
        endpoint: candidate.endpoint.replacingOccurrences(of: "https://", with: ""),
        fingerprint: candidate.spkiSHA256,
        subjectCommonName: candidate.subjectCommonName,
        issuerCommonName: candidate.issuerCommonName,
        notBefore: candidate.notBefore,
        notAfter: candidate.notAfter,
        subjectAlternativeNames: candidate.subjectAlternativeNames,
        warning: candidate.warning
      )
      let prior = promptTail
      let requester = confirmationRequester
      promptSequence += 1
      let sequence = promptSequence
      let task = Task {
        _ = await prior?.value
        guard !Task.isCancelled else { return false }
        return await requester.confirm(request)
      }
      promptTail = Task { _ = await task.value }
      pending[candidate.endpoint] = PendingConfirmation(
        fingerprint: candidate.spkiSHA256,
        task: task
      )
      accepted = await task.value
      if sequence == promptSequence {
        promptTail = nil
      }
      if pending[candidate.endpoint]?.fingerprint == candidate.spkiSHA256 {
        pending[candidate.endpoint] = nil
      }
    }

    guard accepted else {
      return .certificateNotTrusted(candidate.endpoint)
    }
    let record = CertificatePinRecord(
      endpoint: candidate.endpoint,
      spkiSHA256: candidate.spkiSHA256,
      leafCertificateSHA256: candidate.leafCertificateSHA256,
      subjectCommonName: candidate.subjectCommonName,
      issuerCommonName: candidate.issuerCommonName,
      notBefore: candidate.notBefore,
      notAfter: candidate.notAfter,
      confirmedAt: Date(),
      confirmedVia: .fingerprint
    )
    guard pinStore.save(record) else {
      return .certificateNotTrusted(candidate.endpoint)
    }
    return .usePinnedLeaf
  }

  func cancelPendingConfirmations() async {
    let tasks = pending.values.map(\.task)
    pending.removeAll()
    promptSequence += 1
    promptTail?.cancel()
    promptTail = nil
    tasks.forEach { $0.cancel() }
    await confirmationRequester.cancelPending()
  }

  private func commonName(_ certificate: SecCertificate) -> String? {
    var name: CFString?
    guard SecCertificateCopyCommonName(certificate, &name) == errSecSuccess else {
      return nil
    }
    return name as String?
  }

}
