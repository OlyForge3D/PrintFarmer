import CryptoKit
import Foundation
import KeychainSwift
import Security

struct CertificatePinRecord: Codable, Equatable, Sendable {
  enum ConfirmationMethod: String, Codable, Sendable {
    case fingerprint
  }

  let endpoint: String
  let spkiSHA256: String
  let leafCertificateSHA256: String
  let subjectCommonName: String?
  let issuerCommonName: String?
  let notBefore: Date?
  let notAfter: Date?
  let confirmedAt: Date
  let confirmedVia: ConfirmationMethod
}

protocol CertificatePinStoring: Sendable {
  func load(endpoint: String) -> CertificatePinRecord?
  @discardableResult func save(_ record: CertificatePinRecord) -> Bool
  @discardableResult func delete(endpoint: String) -> Bool
}

final class CertificatePinStore: CertificatePinStoring, @unchecked Sendable {
  private let keychain: KeychainSwift
  private let lock = NSLock()
  private let access: KeychainSwiftAccessOptions = .accessibleAfterFirstUnlockThisDeviceOnly

  init(keychain: KeychainSwift = KeychainSwift()) {
    self.keychain = keychain
    self.keychain.synchronizable = false
  }

  func load(endpoint: String) -> CertificatePinRecord? {
    lock.lock()
    defer { lock.unlock() }
    guard let data = keychain.getData(key(endpoint: endpoint)) else { return nil }
    return try? JSONDecoder().decode(CertificatePinRecord.self, from: data)
  }

  @discardableResult
  func save(_ record: CertificatePinRecord) -> Bool {
    lock.lock()
    defer { lock.unlock() }
    guard let data = try? JSONEncoder().encode(record) else { return false }
    return keychain.set(data, forKey: key(endpoint: record.endpoint), withAccess: access)
  }

  @discardableResult
  func delete(endpoint: String) -> Bool {
    lock.lock()
    defer { lock.unlock() }
    let account = key(endpoint: endpoint)
    guard keychain.getData(account) != nil else { return true }
    return keychain.delete(account)
  }

  func key(endpoint: String) -> String {
    let digest = SHA256.hash(data: Data(endpoint.utf8))
    return "pf_cert_pin_\(digest.map { String(format: "%02x", $0) }.joined())"
  }
}

enum CertificateFingerprint {
  enum PublicKeyAlgorithm: Equatable {
    case rsa
    case ec
  }

  static func leafSHA256(_ certificate: SecCertificate) -> String {
    hex(SHA256.hash(data: SecCertificateCopyData(certificate) as Data))
  }

  static func spkiSHA256(_ certificate: SecCertificate) -> String? {
    guard let key = SecCertificateCopyKey(certificate),
      let external = SecKeyCopyExternalRepresentation(key, nil) as Data?,
      let attributes = SecKeyCopyAttributes(key) as? [CFString: Any],
      let type = attributes[kSecAttrKeyType]
    else {
      return nil
    }

    let algorithm: PublicKeyAlgorithm
    if type as! CFString == kSecAttrKeyTypeRSA {
      algorithm = .rsa
    } else if type as! CFString == kSecAttrKeyTypeECSECPrimeRandom {
      algorithm = .ec
    } else {
      return nil
    }
    return spkiSHA256(
      externalRepresentation: external,
      algorithm: algorithm,
      bitSize: attributes[kSecAttrKeySizeInBits] as? Int ?? 0
    )
  }

  static func spkiSHA256(
    externalRepresentation: Data,
    algorithm: PublicKeyAlgorithm,
    bitSize: Int
  ) -> String? {
    let algorithmIdentifier: Data
    if algorithm == .rsa {
      algorithmIdentifier = Data([
        0x30, 0x0d, 0x06, 0x09, 0x2a, 0x86, 0x48, 0x86,
        0xf7, 0x0d, 0x01, 0x01, 0x01, 0x05, 0x00,
      ])
    } else {
      switch bitSize {
      case 256:
        algorithmIdentifier = Data([
          0x30, 0x13, 0x06, 0x07, 0x2a, 0x86, 0x48, 0xce,
          0x3d, 0x02, 0x01, 0x06, 0x08, 0x2a, 0x86, 0x48,
          0xce, 0x3d, 0x03, 0x01, 0x07,
        ])
      case 384:
        algorithmIdentifier = Data([
          0x30, 0x10, 0x06, 0x07, 0x2a, 0x86, 0x48, 0xce,
          0x3d, 0x02, 0x01, 0x06, 0x05, 0x2b, 0x81, 0x04,
          0x00, 0x22,
        ])
      default:
        return nil
      }
    }

    let bitString = der(tag: 0x03, content: Data([0x00]) + externalRepresentation)
    let spki = der(tag: 0x30, content: algorithmIdentifier + bitString)
    return hex(SHA256.hash(data: spki))
  }

  static func display(_ fingerprint: String) -> String {
    stride(from: 0, to: fingerprint.count, by: 4).map { offset in
      let start = fingerprint.index(fingerprint.startIndex, offsetBy: offset)
      let end = fingerprint.index(start, offsetBy: min(4, fingerprint.count - offset))
      return String(fingerprint[start..<end])
    }.joined(separator: " ")
  }

  private static func der(tag: UInt8, content: Data) -> Data {
    Data([tag]) + derLength(content.count) + content
  }

  private static func derLength(_ length: Int) -> Data {
    if length < 128 { return Data([UInt8(length)]) }
    var value = length
    var bytes: [UInt8] = []
    while value > 0 {
      bytes.insert(UInt8(value & 0xff), at: 0)
      value >>= 8
    }
    return Data([0x80 | UInt8(bytes.count)] + bytes)
  }

  private static func hex<D: Sequence>(_ bytes: D) -> String where D.Element == UInt8 {
    bytes.map { String(format: "%02X", $0) }.joined()
  }
}
