import XCTest

@testable import PrintFarmer

final class NetworkHostClassifierTests: XCTestCase {
  func testIPv4PrivateAndPublicBoundaries() {
    let local = [
      "10.0.0.5", "172.16.0.1", "172.31.255.255",
      "192.168.0.0", "192.168.255.255", "127.0.0.1",
      "169.254.1.1", "100.64.0.1", "100.127.255.255",
      "100.119.81.25",
    ]
    let publicHosts = [
      "172.15.255.255", "172.32.0.1", "100.63.255.255",
      "100.128.0.0", "8.8.8.8", "1.1.1.1", "9.255.255.255",
      "11.0.0.0",
    ]

    local.forEach { XCTAssertEqual(NetworkHostClassifier.classify($0), .local, $0) }
    publicHosts.forEach { XCTAssertEqual(NetworkHostClassifier.classify($0), .public, $0) }
  }

  func testInvalidIPv4LikeInputsFailClosed() {
    let invalid = [
      "0.0.0.0", "0.1.2.3", "224.0.0.1", "239.255.255.255",
      "240.0.0.1", "255.255.255.255", "999.1.1.1", "0177.0.0.1",
      "192.168.1.010", "2130706433", "1.2.3", "1.2.3.4.5",
    ]

    invalid.forEach { XCTAssertEqual(NetworkHostClassifier.classify($0), .invalid, $0) }
  }

  func testHostnameRules() {
    XCTAssertEqual(NetworkHostClassifier.punycode("bücher"), "xn--bcher-kva")
    XCTAssertEqual(
      NetworkHostClassifier.canonicalize("xn--bcher-kva.example")?.value, "xn--bcher-kva.example")
    [
      "printfarmer.local", "PrintFarmer.LOCAL", "printfarmer.local.", "localhost", "LOCALHOST",
      "printfarm",
    ]
    .forEach { XCTAssertEqual(NetworkHostClassifier.classify($0), .local, $0) }
    [
      "example.com", "printfarm.example.com", "printfarm.lan", "printfarm.home.arpa",
      "printfarm.internal", "evil.local.example.com",
    ]
    .forEach { XCTAssertEqual(NetworkHostClassifier.classify($0), .public, $0) }
    ["-bad", "bad-", String(repeating: "a", count: 64)]
      .forEach { XCTAssertEqual(NetworkHostClassifier.classify($0), .invalid, $0) }
    XCTAssertEqual(
      NetworkHostClassifier.canonicalize("BÜCHER.example.")?.value,
      "xn--bcher-kva.example"
    )
  }

  func testIPv6RulesAndCanonicalization() {
    ["::1", "[::1]", "fd00::1", "fc00::1", "fe80::1", "fe80::1%en0", "::ffff:192.168.1.10"]
      .forEach { XCTAssertEqual(NetworkHostClassifier.classify($0), .local, $0) }
    ["2001:4860:4860::8888", "::ffff:8.8.8.8", "fb00::1", "fe00::1", "fec0::1"]
      .forEach { XCTAssertEqual(NetworkHostClassifier.classify($0), .public, $0) }
    ["ff02::1", "::"]
      .forEach { XCTAssertEqual(NetworkHostClassifier.classify($0), .invalid, $0) }
    XCTAssertEqual(NetworkHostClassifier.classify("febf::1"), .local)
    XCTAssertEqual(
      NetworkHostClassifier.endpointKey(host: "FD00:0:0:0:0:0:0:1", port: 8443),
      "https://[fd00::1]:8443"
    )
  }

  func testEquivalentEndpointFormsConverge() {
    let expected = "https://192.168.1.10:443"
    XCTAssertEqual(NetworkHostClassifier.endpointKey(host: "192.168.1.10", port: nil), expected)
    XCTAssertEqual(NetworkHostClassifier.endpointKey(host: "192.168.1.10", port: 443), expected)
    XCTAssertEqual(NetworkHostClassifier.endpointKey(host: " 192.168.1.10 ", port: 443), expected)
  }
}
