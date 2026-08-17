import XCTest

@testable import PrintFarmer

final class TransportSecurityConfigurationTests: XCTestCase {
  func testATSContainsOnlyLocalNetworkingException() throws {
    let ats = try XCTUnwrap(
      Bundle.main.object(forInfoDictionaryKey: "NSAppTransportSecurity") as? [String: Any])

    XCTAssertEqual(Set(ats.keys), ["NSAllowsLocalNetworking"])
    XCTAssertEqual(ats["NSAllowsLocalNetworking"] as? Bool, true)
  }

  func testPublicSelfSignedDestinationsCannotEnterPrivateTrustPath() {
    ["8.8.8.8", "1.1.1.1", "172.32.0.1", "example.com"].forEach {
      XCTAssertFalse(PrivateNetworkSessionDelegate.isPrivateHost($0), $0)
    }
  }

  func testURLNormalizationRejectsPublicCleartextAndKeepsLANHTTP() {
    XCTAssertNil(APIClient.normalizedServerURLString("http://example.com"))
    XCTAssertNil(APIClient.normalizedServerURLString("http://8.8.8.8"))
    XCTAssertEqual(
      APIClient.normalizedServerURLString("http://192.168.1.10"),
      "http://192.168.1.10"
    )
    XCTAssertEqual(
      APIClient.normalizedServerURLString("http://printfarmer.local"),
      "http://printfarmer.local"
    )
    XCTAssertEqual(
      APIClient.normalizedServerURLString("http://localhost:5245"),
      "http://localhost:5245"
    )
  }

  func testPersistedPublicCleartextIsUpgraded() {
    let suite = "TransportSecurityConfigurationTests.\(UUID().uuidString)"
    let defaults = UserDefaults(suiteName: suite)!
    defer { defaults.removePersistentDomain(forName: suite) }
    defaults.set("http://example.com", forKey: APIClient.serverURLKey)

    XCTAssertEqual(APIClient.savedServerURLString(userDefaults: defaults), "https://example.com")
    XCTAssertEqual(defaults.string(forKey: APIClient.serverURLKey), "https://example.com")
  }

  func testPersistedIPCleartextIsUpgradedButLocalNamesRemainHTTP() {
    let suite = "TransportSecurityConfigurationTests.\(UUID().uuidString)"
    let defaults = UserDefaults(suiteName: suite)!
    defer { defaults.removePersistentDomain(forName: suite) }

    defaults.set("http://192.168.1.10", forKey: APIClient.serverURLKey)
    XCTAssertEqual(APIClient.savedServerURLString(userDefaults: defaults), "https://192.168.1.10")

    defaults.set("http://printfarmer.local", forKey: APIClient.serverURLKey)
    XCTAssertEqual(
      APIClient.savedServerURLString(userDefaults: defaults), "http://printfarmer.local")
  }
}
