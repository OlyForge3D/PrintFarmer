import Darwin
import Foundation

enum HostClassification: Equatable, Sendable {
  case local
  case `public`
  case invalid
}

struct CanonicalNetworkHost: Equatable, Sendable {
  let value: String
  let classification: HostClassification
  let isIPLiteral: Bool
  let isIPv6: Bool
}

enum NetworkHostClassifier {
  static func classify(_ host: String) -> HostClassification {
    canonicalize(host)?.classification ?? .invalid
  }

  static func canonicalize(_ host: String) -> CanonicalNetworkHost? {
    guard var prepared = prepare(host) else { return nil }

    if let octets = strictIPv4(prepared) {
      return CanonicalNetworkHost(
        value: octets.map(String.init).joined(separator: "."),
        classification: classifyIPv4(octets),
        isIPLiteral: true,
        isIPv6: false
      )
    }

    if prepared.allSatisfy({ $0.isASCII && ($0.isNumber || $0 == ".") }) {
      return nil
    }

    if let bytes = strictIPv6(prepared) {
      if bytes[0..<12] == Array(repeating: 0, count: 10) + [0xff, 0xff] {
        let octets = Array(bytes[12..<16])
        return CanonicalNetworkHost(
          value: octets.map(String.init).joined(separator: "."),
          classification: classifyIPv4(octets),
          isIPLiteral: true,
          isIPv6: false
        )
      }

      let classification: HostClassification
      if bytes == Array(repeating: 0, count: 15) + [1]
        || bytes[0] & 0xfe == 0xfc
        || (bytes[0] == 0xfe && bytes[1] & 0xc0 == 0x80)
      {
        classification = .local
      } else if bytes[0..<12].allSatisfy({ $0 == 0 }) || bytes[0] == 0xff {
        classification = .invalid
      } else {
        classification = .public
      }

      guard let canonical = canonicalIPv6(bytes) else { return nil }
      return CanonicalNetworkHost(
        value: canonical,
        classification: classification,
        isIPLiteral: true,
        isIPv6: true
      )
    }

    if prepared.unicodeScalars.contains(where: { $0.value >= 128 }) {
      let labels = prepared.split(separator: ".", omittingEmptySubsequences: false)
      let encoded = labels.map { label -> String? in
        label.unicodeScalars.allSatisfy { $0.value < 128 } ? String(label) : punycode(String(label))
      }
      guard encoded.allSatisfy({ $0 != nil }) else {
        return nil
      }
      prepared = encoded.compactMap { $0 }.joined(separator: ".")
    }

    guard isValidHostname(prepared) else { return nil }
    let labels = prepared.split(separator: ".", omittingEmptySubsequences: false)
    let isLocal =
      prepared == "localhost"
      || labels.count == 1
      || (labels.count > 1 && prepared.hasSuffix(".local"))
    return CanonicalNetworkHost(
      value: prepared,
      classification: isLocal ? .local : .public,
      isIPLiteral: false,
      isIPv6: false
    )
  }

  static func endpointKey(host: String, port: Int?) -> String? {
    guard let canonical = canonicalize(host), canonical.classification != .invalid else {
      return nil
    }
    let renderedHost = canonical.isIPv6 ? "[\(canonical.value)]" : canonical.value
    return "https://\(renderedHost):\(port ?? 443)"
  }

  private static func prepare(_ host: String) -> String? {
    var value = host.trimmingCharacters(in: .whitespacesAndNewlines)
    if value.hasPrefix("["), value.hasSuffix("]") {
      value.removeFirst()
      value.removeLast()
    }
    if let zoneIndex = value.firstIndex(of: "%") {
      value = String(value[..<zoneIndex])
    }
    value = value.lowercased()
    if value.hasSuffix(".") {
      value.removeLast()
    }
    return value.isEmpty ? nil : value
  }

  private static func strictIPv4(_ host: String) -> [UInt8]? {
    let fields = host.split(separator: ".", omittingEmptySubsequences: false)
    guard fields.count == 4 else { return nil }
    var octets: [UInt8] = []
    for field in fields {
      guard (1...3).contains(field.count),
        field.allSatisfy({ $0.isASCII && $0.isNumber }),
        !(field.count > 1 && field.first == "0"),
        let value = UInt8(field)
      else {
        return nil
      }
      octets.append(value)
    }
    return octets
  }

  private static func classifyIPv4(_ octets: [UInt8]) -> HostClassification {
    let first = octets[0]
    let second = octets[1]
    if first == 10
      || first == 127
      || (first == 172 && (16...31).contains(second))
      || (first == 192 && second == 168)
      || (first == 169 && second == 254)
      || (first == 100 && (64...127).contains(second))
    {
      return .local
    }
    if first == 0 || first >= 224 {
      return .invalid
    }
    return .public
  }

  private static func strictIPv6(_ host: String) -> [UInt8]? {
    var address = in6_addr()
    guard host.withCString({ inet_pton(AF_INET6, $0, &address) }) == 1 else {
      return nil
    }
    return withUnsafeBytes(of: &address) { Array($0) }
  }

  private static func canonicalIPv6(_ bytes: [UInt8]) -> String? {
    var address = in6_addr()
    withUnsafeMutableBytes(of: &address) { destination in
      destination.copyBytes(from: bytes)
    }
    var buffer = [CChar](repeating: 0, count: Int(INET6_ADDRSTRLEN))
    guard inet_ntop(AF_INET6, &address, &buffer, socklen_t(buffer.count)) != nil else {
      return nil
    }
    return String(
      decoding: buffer.prefix { $0 != 0 }.map { UInt8(bitPattern: $0) },
      as: UTF8.self
    )
  }

  private static func isValidHostname(_ host: String) -> Bool {
    guard host.count <= 253 else { return false }
    let labels = host.split(separator: ".", omittingEmptySubsequences: false)
    return !labels.isEmpty
      && labels.allSatisfy { label in
        guard (1...63).contains(label.count),
          label.first != "-",
          label.last != "-"
        else {
          return false
        }
        return label.allSatisfy {
          $0.isASCII && ($0.isLetter || $0.isNumber || $0 == "-")
        }
      }
  }

  static func punycode(_ label: String) -> String? {
    let input = label.unicodeScalars.map { Int64($0.value) }
    guard !input.isEmpty else { return nil }
    var output = input.filter { $0 < 0x80 }.compactMap { UnicodeScalar(Int($0)).map(String.init) }
      .joined()
    let basicCount = output.utf8.count
    var handled = basicCount
    if basicCount > 0 { output.append("-") }
    var n: Int64 = 128
    var delta: Int64 = 0
    var bias: Int64 = 72

    while handled < input.count {
      guard let next = input.filter({ $0 >= n }).min() else { return nil }
      let step = (next - n) * Int64(handled + 1)
      guard step <= Int64.max - delta else { return nil }
      delta += step
      n = next
      for scalar in input {
        if scalar < n {
          guard delta < Int64.max else { return nil }
          delta += 1
        } else if scalar == n {
          var quotient = delta
          var k: Int64 = 36
          while true {
            let threshold = k <= bias ? 1 : (k >= bias + 26 ? 26 : k - bias)
            if quotient < threshold { break }
            let digit = threshold + (quotient - threshold) % (36 - threshold)
            output.append(base36(digit))
            quotient = (quotient - threshold) / (36 - threshold)
            k += 36
          }
          output.append(base36(quotient))
          bias = adapt(delta: delta, points: Int64(handled + 1), first: handled == basicCount)
          delta = 0
          handled += 1
        }
      }
      guard delta < Int64.max, n < Int64.max else { return nil }
      delta += 1
      n += 1
    }
    return "xn--\(output)"
  }

  private static func adapt(delta: Int64, points: Int64, first: Bool) -> Int64 {
    var delta = first ? delta / 700 : delta / 2
    delta += delta / points
    var k: Int64 = 0
    while delta > 455 {
      delta /= 35
      k += 36
    }
    return k + (36 * delta) / (delta + 38)
  }

  private static func base36(_ value: Int64) -> Character {
    Character(String(UnicodeScalar(Int(value < 26 ? value + 97 : value - 26 + 48))!))
  }
}
