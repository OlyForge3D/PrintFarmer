import Foundation
import XCTest

@testable import PrintFarmer

final class CertificateTrustCoordinatorTests: XCTestCase {
  func testFirstContactRequiresExplicitAcceptanceBeforePinIsSaved() async {
    let store = TestPinStore()
    let requester = TestConfirmationRequester(decisions: [true])
    let coordinator = CertificateTrustCoordinator(pinStore: store, confirmationRequester: requester)

    let outcome = await coordinator.resolve(
      candidate(endpoint: "https://192.168.1.10:443", spki: "A"))
    let requestCount = await requester.requestCount

    XCTAssertEqual(outcome, .usePinnedLeaf)
    XCTAssertEqual(store.load(endpoint: "https://192.168.1.10:443")?.spkiSHA256, "A")
    XCTAssertEqual(requestCount, 1)
  }

  func testCancelFailsClosedWithoutSavingPin() async {
    let store = TestPinStore()
    let requester = TestConfirmationRequester(decisions: [false])
    let coordinator = CertificateTrustCoordinator(pinStore: store, confirmationRequester: requester)

    let outcome = await coordinator.resolve(
      candidate(endpoint: "https://192.168.1.10:443", spki: "A"))

    XCTAssertEqual(outcome, .certificateNotTrusted("https://192.168.1.10:443"))
    XCTAssertNil(store.load(endpoint: "https://192.168.1.10:443"))
  }

  func testMatchingPinDoesNotPrompt() async {
    let store = TestPinStore()
    _ = store.save(record(endpoint: "https://192.168.1.10:443", spki: "A"))
    let requester = TestConfirmationRequester(decisions: [])
    let coordinator = CertificateTrustCoordinator(pinStore: store, confirmationRequester: requester)

    let outcome = await coordinator.resolve(
      candidate(endpoint: "https://192.168.1.10:443", spki: "A"))
    let requestCount = await requester.requestCount

    XCTAssertEqual(outcome, .usePinnedLeaf)
    XCTAssertEqual(requestCount, 0)
  }

  func testChangedCertificateFailsWithoutPromptOrReplacement() async {
    let store = TestPinStore()
    _ = store.save(record(endpoint: "https://192.168.1.10:443", spki: "A"))
    let requester = TestConfirmationRequester(decisions: [true])
    let coordinator = CertificateTrustCoordinator(pinStore: store, confirmationRequester: requester)

    let outcome = await coordinator.resolve(
      candidate(endpoint: "https://192.168.1.10:443", spki: "B"))
    let requestCount = await requester.requestCount

    XCTAssertEqual(outcome, .certificateChanged("https://192.168.1.10:443"))
    XCTAssertEqual(store.load(endpoint: "https://192.168.1.10:443")?.spkiSHA256, "A")
    XCTAssertEqual(requestCount, 0)
  }

  func testPinsAreIsolatedByEndpointAndPort() async {
    let store = TestPinStore()
    _ = store.save(record(endpoint: "https://192.168.1.10:443", spki: "A"))
    let requester = TestConfirmationRequester(decisions: [true, true])
    let coordinator = CertificateTrustCoordinator(pinStore: store, confirmationRequester: requester)

    let otherHost = await coordinator.resolve(
      candidate(endpoint: "https://192.168.1.11:443", spki: "B"))
    let otherPort = await coordinator.resolve(
      candidate(endpoint: "https://192.168.1.10:8443", spki: "C"))

    XCTAssertEqual(otherHost, .usePinnedLeaf)
    XCTAssertEqual(otherPort, .usePinnedLeaf)
    XCTAssertEqual(store.load(endpoint: "https://192.168.1.10:443")?.spkiSHA256, "A")
    XCTAssertEqual(store.load(endpoint: "https://192.168.1.11:443")?.spkiSHA256, "B")
    XCTAssertEqual(store.load(endpoint: "https://192.168.1.10:8443")?.spkiSHA256, "C")
  }

  func testConcurrentMatchingCandidatesCoalesceToOnePrompt() async {
    let store = TestPinStore()
    let requester = SuspendingConfirmationRequester()
    let coordinator = CertificateTrustCoordinator(pinStore: store, confirmationRequester: requester)
    let candidate = candidate(endpoint: "https://192.168.1.10:443", spki: "A")

    let tasks = (0..<8).map { _ in
      Task { await coordinator.resolve(candidate) }
    }
    await requester.waitUntilRequested()
    await requester.resolve(true)
    var outcomes: [CertificateTrustOutcome] = []
    for task in tasks {
      outcomes.append(await task.value)
    }
    let requestCount = await requester.requestCount

    XCTAssertEqual(requestCount, 1)
    XCTAssertEqual(outcomes, Array(repeating: .usePinnedLeaf, count: 8))
  }

  func testConcurrentDifferentCandidateIsDeniedWithoutSecondPrompt() async {
    let store = TestPinStore()
    let requester = SuspendingConfirmationRequester()
    let coordinator = CertificateTrustCoordinator(pinStore: store, confirmationRequester: requester)
    let initialCandidate = candidate(endpoint: "https://192.168.1.10:443", spki: "A")
    let first = Task {
      await coordinator.resolve(initialCandidate)
    }
    await requester.waitUntilRequested()

    let conflicting = await coordinator.resolve(
      candidate(endpoint: "https://192.168.1.10:443", spki: "B")
    )
    await requester.resolve(true)
    let accepted = await first.value
    let requestCount = await requester.requestCount

    XCTAssertEqual(conflicting, .certificateNotTrusted("https://192.168.1.10:443"))
    XCTAssertEqual(accepted, .usePinnedLeaf)
    XCTAssertEqual(requestCount, 1)
  }

  func testActiveServerSwitchCancellationDeniesPendingConfirmation() async {
    let store = TestPinStore()
    let requester = SuspendingConfirmationRequester()
    let coordinator = CertificateTrustCoordinator(pinStore: store, confirmationRequester: requester)
    let initialCandidate = candidate(endpoint: "https://192.168.1.10:443", spki: "A")
    let resolution = Task {
      await coordinator.resolve(initialCandidate)
    }
    await requester.waitUntilRequested()

    await coordinator.cancelPendingConfirmations()
    let outcome = await resolution.value

    XCTAssertEqual(outcome, .certificateNotTrusted("https://192.168.1.10:443"))
    XCTAssertNil(store.load(endpoint: "https://192.168.1.10:443"))
  }

  func testSPKIFingerprintsMatchIndependentGoldenValues() {
    let cases: [(Data, CertificateFingerprint.PublicKeyAlgorithm, Int, String)] = [
      (
        Data(repeating: 1, count: 32), .rsa, 2048,
        "21BE684D3F16C540B3A069CFF25965016C72FB9A6C5DFC2BE13329582E3714F9"
      ),
      (
        Data(repeating: 2, count: 48), .rsa, 3072,
        "DFBACD9732D84BE9D8EFB49E6AD452FF8BC37D65AA2428314CF470511FC52832"
      ),
      (
        Data(repeating: 3, count: 64), .rsa, 4096,
        "55AAFD8CD260DF579D2A6581198062404258AD3085DD85AA057D5FBEFE66E3B6"
      ),
      (
        Data([4]) + Data(repeating: 17, count: 64), .ec, 256,
        "8DCF31A3AE6172B1DF2B27702790897F2533ECD95C5DAC56C39149FC2DD4CC7F"
      ),
      (
        Data([4]) + Data(repeating: 34, count: 96), .ec, 384,
        "EFB9875A969882C9E6CDA16A9DEECACF31EC824712939FFFBF07F4B3EAD46F03"
      ),
    ]

    for (representation, algorithm, bitSize, expected) in cases {
      XCTAssertEqual(
        CertificateFingerprint.spkiSHA256(
          externalRepresentation: representation,
          algorithm: algorithm,
          bitSize: bitSize
        ),
        expected
      )
    }
  }

  private func candidate(endpoint: String, spki: String) -> CertificateTrustCandidate {
    CertificateTrustCandidate(
      endpoint: endpoint,
      spkiSHA256: spki,
      leafCertificateSHA256: "LEAF-\(spki)",
      subjectCommonName: "PrintFarmer",
      issuerCommonName: "PrintFarmer",
      notBefore: nil,
      notAfter: nil,
      subjectAlternativeNames: ["192.168.1.10"],
      warning: nil
    )
  }

  private func record(endpoint: String, spki: String) -> CertificatePinRecord {
    CertificatePinRecord(
      endpoint: endpoint,
      spkiSHA256: spki,
      leafCertificateSHA256: "LEAF-\(spki)",
      subjectCommonName: nil,
      issuerCommonName: nil,
      notBefore: nil,
      notAfter: nil,
      confirmedAt: Date(timeIntervalSince1970: 1),
      confirmedVia: .fingerprint
    )
  }
}

private final class TestPinStore: CertificatePinStoring, @unchecked Sendable {
  private let lock = NSLock()
  private var records: [String: CertificatePinRecord] = [:]

  func load(endpoint: String) -> CertificatePinRecord? {
    lock.withLock { records[endpoint] }
  }

  func save(_ record: CertificatePinRecord) -> Bool {
    lock.withLock { records[record.endpoint] = record }
    return true
  }

  func delete(endpoint: String) -> Bool {
    lock.withLock { records[endpoint] = nil }
    return true
  }
}

private actor TestConfirmationRequester: CertificateConfirmationRequesting {
  private var decisions: [Bool]
  private(set) var requestCount = 0

  init(decisions: [Bool]) {
    self.decisions = decisions
  }

  func confirm(_ request: CertificateTrustRequest) async -> Bool {
    requestCount += 1
    return decisions.isEmpty ? false : decisions.removeFirst()
  }
}

private actor SuspendingConfirmationRequester: CertificateConfirmationRequesting {
  private var continuation: CheckedContinuation<Bool, Never>?
  private(set) var requestCount = 0

  func confirm(_ request: CertificateTrustRequest) async -> Bool {
    requestCount += 1
    return await withCheckedContinuation { continuation in
      self.continuation = continuation
    }
  }

  func cancelPending() async {
    resolve(false)
  }

  func resolve(_ accepted: Bool) {
    guard let continuation else { return }
    self.continuation = nil
    continuation.resume(returning: accepted)
  }

  func waitUntilRequested() async {
    while requestCount == 0 {
      await Task.yield()
    }
  }
}
