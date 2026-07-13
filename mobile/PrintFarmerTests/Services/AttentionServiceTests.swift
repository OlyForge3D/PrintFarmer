import XCTest
@testable import PrintFarmer

/// Wire-shape and error-path coverage for the AttentionService actor.
/// Uses the same `MockAPIClient` + `MockURLProtocol` infrastructure the
/// other service tests rely on so we hit the real `APIClient` code path
/// (URL building, JSON encoding, error mapping) instead of stubbing it out.
final class AttentionServiceTests: XCTestCase {

    private var apiClient: APIClient!
    private var service: AttentionService!

    override func setUp() {
        super.setUp()
        MockURLProtocol.reset()
        apiClient = MockAPIClient.makeAPIClient()
        service = AttentionService(apiClient: apiClient)
    }

    override func tearDown() {
        MockURLProtocol.reset()
        apiClient = nil
        service = nil
        super.tearDown()
    }

    // MARK: - GET /api/attention

    func testGetFeedIssuesGetAgainstAttentionEndpointWithoutQuery() async throws {
        let json = """
        { "items": [], "nextCursor": null, "healthyPrinterCount": 0 }
        """
        MockAPIClient.stubResponse(json: json)

        _ = try await service.getFeed()

        let request = try XCTUnwrap(MockURLProtocol.capturedRequests.first)
        XCTAssertEqual(request.httpMethod, "GET")
        XCTAssertEqual(request.url?.path, "/api/attention")
        XCTAssertNil(request.url?.query,
            "No cursor / limit → no query string. Empty query attracts caches oddly.")
    }

    func testGetFeedEncodesCursorAndLimitAsQueryParameters() async throws {
        let json = """
        { "items": [], "nextCursor": null, "healthyPrinterCount": 0 }
        """
        MockAPIClient.stubResponse(json: json)

        _ = try await service.getFeed(cursor: "eyJvIjozLCJpIjoiZm9vIn0", limit: 25)

        let request = try XCTUnwrap(MockURLProtocol.capturedRequests.first)
        let components = URLComponents(url: request.url!, resolvingAgainstBaseURL: false)
        let items = components?.queryItems ?? []
        XCTAssertEqual(items.first(where: { $0.name == "cursor" })?.value,
                       "eyJvIjozLCJpIjoiZm9vIn0")
        XCTAssertEqual(items.first(where: { $0.name == "limit" })?.value, "25")
    }

    func testGetFeedPropagatesFeatureDisabled() async {
        // Backend PR #728 gates the feature with a structured 404 whose
        // body carries `code:"featureDisabled"`. APIClient auto-maps that
        // to NetworkError.featureDisabled — this test locks the contract
        // so callers can pick the safe fallback without parsing text.
        let json = """
        {
          "type": "https://printfarmer/errors/feature-disabled",
          "title": "Feature Disabled",
          "status": 404,
          "detail": "The attention feature is disabled on this server.",
          "code": "featureDisabled"
        }
        """
        MockAPIClient.stubResponse(json: json, statusCode: 404)

        do {
            _ = try await service.getFeed()
            XCTFail("Expected NetworkError.featureDisabled")
        } catch let error as NetworkError {
            guard case .featureDisabled(let apiError) = error else {
                XCTFail("Expected .featureDisabled, got \(error)")
                return
            }
            XCTAssertEqual(apiError.code, "featureDisabled")
            XCTAssertEqual(apiError.status, 404)
        } catch {
            XCTFail("Unexpected error type: \(error)")
        }
    }

    // MARK: - POST /api/attention/{id}/snooze

    func testSnoozePostsCamelCaseBodyToUrlEncodedIdSegment() async throws {
        let json = """
        {
          "snoozedUntilUtc": "2026-06-02T12:00:00Z",
          "attentionItemAnchorAtUtc": "2026-06-01T12:00:00Z"
        }
        """
        MockAPIClient.stubResponse(json: json)

        let until = Date(timeIntervalSince1970: 1_800_000_000)
        _ = try await service.snooze(
            itemId: "failure:11111111-1111-1111-1111-111111111111",
            snoozedUntilUtc: until
        )

        let request = try XCTUnwrap(MockURLProtocol.capturedRequests.first)
        XCTAssertEqual(request.httpMethod, "POST")
        // The `:` in the attention id MUST be percent-encoded (`%3A`) or
        // ASP.NET Core route matching would split the id at the colon and
        // route to the wrong action. `.path` decodes the URL back so we
        // inspect `.absoluteString` to see the wire form.
        let absoluteURL = try XCTUnwrap(request.url?.absoluteString)
        XCTAssertTrue(absoluteURL.contains("failure%3A11111111-1111-1111-1111-111111111111"),
            "Attention id path segment must percent-encode `:`; got url=\(absoluteURL)")
        XCTAssertTrue(absoluteURL.hasSuffix("/snooze"))

        let body = try XCTUnwrap(request.capturedHTTPBody())
        let bodyStr = try XCTUnwrap(String(data: body, encoding: .utf8))
        XCTAssertTrue(bodyStr.contains("\"snoozedUntilUtc\""),
            "Body must use camelCase key expected by the backend contract; got: \(bodyStr)")
        XCTAssertFalse(bodyStr.contains("attentionItemAnchorAtUtc"),
            "Client must never send an anchor field: the encoder is lossy on fractional seconds; got: \(bodyStr)")
    }

    /// End-to-end regression for the anchor-truncation bug that got the
    /// first #707 candidate rejected.
    ///
    /// The failure mode was: the mobile decoder preserves fractional seconds
    /// on incoming `occurredAt`, but the shared JSON encoder serialises
    /// `Date` with `.iso8601` (no fractions). A client that decoded an
    /// AttentionItem and then round-tripped its `occurredAt` back to the
    /// server as `attentionItemAnchorAtUtc` would silently send a
    /// fraction-truncated timestamp; the server's strict
    /// `item.OccurredAt > anchor` bypass check would then match the very
    /// same item on the very next fetch and defeat the snooze.
    ///
    /// This test wires up the real `APIClient` code path (decoder → snooze
    /// request → encoder → captured body) with a fixture whose `occurredAt`
    /// carries non-zero fractional seconds, and asserts that the body sent
    /// on the wire contains no anchor field at all.
    func testSnoozeOnItemWithFractionalOccurredAtDoesNotSendClientAnchor() async throws {
        // Feed page containing one item whose occurredAt has real
        // fractional seconds (…:59.123456Z). This is the shape ASP.NET Core
        // emits when the underlying `DateTime` retains sub-second precision.
        let feedJSON = """
        {
          "items": [
            {
              "id": "failure:22222222-2222-2222-2222-222222222222",
              "kind": "failure",
              "severity": "warning",
              "printerId": "33333333-3333-3333-3333-333333333333",
              "printerName": "Bay 3",
              "title": "Layer shift detected",
              "detail": "Motion axis reported a step loss on layer 42.",
              "occurredAt": "2026-06-01T11:59:59.123456Z",
              "actions": []
            }
          ],
          "nextCursor": null,
          "healthyPrinterCount": 3
        }
        """
        MockAPIClient.stubResponse(json: feedJSON)

        let feed = try await service.getFeed()
        let item = try XCTUnwrap(feed.items.first,
            "Fixture must decode into exactly one item so we exercise the fractional path.")
        // Sanity: the decoder did preserve sub-second precision. The mobile
        // decoder rounds fractional ISO8601 to millisecond resolution, so
        // we check whole-second alignment rather than exact micros — the
        // bug reviewers flagged is that `.iso8601` encoding then drops
        // ALL fractional seconds, so any sub-second detail is enough to
        // demonstrate the lossy round-trip.
        let occurredSeconds = item.occurredAt.timeIntervalSince1970
        XCTAssertNotEqual(occurredSeconds.rounded(.towardZero), occurredSeconds,
            "Test fixture must not decode to a whole-second timestamp; got \(occurredSeconds).")

        // Reset so the next captured request is the snooze POST alone.
        MockURLProtocol.reset()
        MockAPIClient.stubResponse(json: """
        { "snoozedUntilUtc": "2026-06-02T12:00:00Z", "attentionItemAnchorAtUtc": "2026-06-01T11:59:59.123456Z" }
        """)

        let until = Date(timeIntervalSince1970: 1_800_000_000)
        _ = try await service.snooze(
            itemId: item.id,
            snoozedUntilUtc: until
        )

        let request = try XCTUnwrap(MockURLProtocol.capturedRequests.first,
            "Snooze POST must be observable on the wire.")
        let body = try XCTUnwrap(request.capturedHTTPBody())
        let bodyStr = try XCTUnwrap(String(data: body, encoding: .utf8))

        XCTAssertTrue(bodyStr.contains("\"snoozedUntilUtc\""),
            "Snooze deadline field must still be on the wire; got: \(bodyStr)")
        XCTAssertFalse(bodyStr.contains("attentionItemAnchorAtUtc"),
            "Regression: mobile client must NOT round-trip a decoded fractional occurredAt as the snooze anchor. Body was: \(bodyStr)")
    }

    // MARK: - DELETE /api/attention/{id}/snooze

    func testClearSnoozeIssuesDelete() async throws {
        MockAPIClient.stubEmptySuccess()

        try await service.clearSnooze(itemId: "runout:aa")

        let request = try XCTUnwrap(MockURLProtocol.capturedRequests.first)
        XCTAssertEqual(request.httpMethod, "DELETE")
        XCTAssertTrue(request.url?.path.hasSuffix("/snooze") == true)
    }

    // MARK: - POST /api/attention/{id}/actions/{actionKind}

    func testExecuteActionPostsToTypedActionRoute() async throws {
        let json = "{ \"outcome\": \"Ok\" }"
        MockAPIClient.stubResponse(json: json)

        let result = try await service.executeAction(
            itemId: "harvest:bb",
            actionKind: .harvest
        )

        XCTAssertEqual(result.outcome, "Ok")
        let request = try XCTUnwrap(MockURLProtocol.capturedRequests.first)
        XCTAssertEqual(request.httpMethod, "POST")
        XCTAssertTrue(request.url?.path.hasSuffix("/actions/harvest") == true,
            "actionKind must be dispatched via the typed route segment.")
    }

    func testExecuteActionRefusesUnknownActionKindWithoutHittingNetwork() async {
        // A future backend enum value we don't know about → we must NOT
        // silently POST to `/actions/unknown` because the client cannot
        // describe the outcome to the user.
        MockAPIClient.stubResponse(json: "{\"outcome\":\"Ok\"}")

        do {
            _ = try await service.executeAction(itemId: "x:1", actionKind: .unknown)
            XCTFail("Executing an unknown action kind must throw.")
        } catch let error as NetworkError {
            guard case .clientError(let status, let apiError) = error else {
                XCTFail("Expected .clientError, got \(error)")
                return
            }
            XCTAssertEqual(status, 400)
            XCTAssertEqual(apiError?.code, "clientUnknownAction")
        } catch {
            XCTFail("Unexpected error type: \(error)")
        }
        XCTAssertTrue(MockURLProtocol.capturedRequests.isEmpty,
            "Guard must fire before any network call is made.")
    }
}
