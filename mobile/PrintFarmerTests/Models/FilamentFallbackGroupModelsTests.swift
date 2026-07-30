import XCTest
@testable import PrintFarmer

/// Wire-contract tests for the iOS fallback-group DTOs added for issue
/// #711 (F6). Locks the shape emitted by `FilamentFallbackGroupsController`
/// in PR #752: camelCase keys, `JsonStringEnumConverter` string enums, and
/// stable member ordering for UI callers.
final class FilamentFallbackGroupModelsTests: XCTestCase {

    private let decoder: JSONDecoder = {
        // Match APIClient's runtime decoder: default key strategy + ISO-8601 dates.
        let d = JSONDecoder()
        d.dateDecodingStrategy = .iso8601
        return d
    }()

    // MARK: - FilamentFallbackGroup

    func testGroup_decodesHappyPathFromMergedContract() throws {
        let json = """
        {
          "id": "550e8400-e29b-41d4-a716-446655440000",
          "printerId": "660e8400-e29b-41d4-a716-446655440001",
          "name": "PLA fallback",
          "materialType": "PLA",
          "displayOrder": 2,
          "createdAt": "2025-01-01T12:00:00Z",
          "updatedAt": "2025-01-02T13:00:00Z",
          "members": [
            {
              "id": "770e8400-e29b-41d4-a716-446655440002",
              "toolheadId": "880e8400-e29b-41d4-a716-446655440003",
              "position": 0,
              "toolheadName": "T0",
              "toolheadIndex": 0,
              "currentMaterial": "PLA",
              "currentSpoolId": 42,
              "materialMatches": true
            }
          ]
        }
        """
        let group = try decoder.decode(FilamentFallbackGroup.self, from: Data(json.utf8))
        XCTAssertEqual(group.name, "PLA fallback")
        XCTAssertEqual(group.materialType, "PLA")
        XCTAssertEqual(group.displayOrder, 2)
        XCTAssertEqual(group.members.count, 1)
        XCTAssertEqual(group.members.first?.currentSpoolId, 42)
        XCTAssertEqual(group.members.first?.materialMatches, true)
    }

    func testGroup_sortsMembersByPositionAtDecode() throws {
        let json = """
        {
          "id": "550e8400-e29b-41d4-a716-446655440000",
          "printerId": "660e8400-e29b-41d4-a716-446655440001",
          "name": "chain",
          "materialType": "PETG",
          "displayOrder": 0,
          "createdAt": "2025-01-01T12:00:00Z",
          "updatedAt": "2025-01-01T12:00:00Z",
          "members": [
            {"id":"11111111-1111-1111-1111-111111111111","toolheadId":"22222222-2222-2222-2222-222222222222","position":2,"toolheadIndex":2,"materialMatches":false},
            {"id":"33333333-3333-3333-3333-333333333333","toolheadId":"44444444-4444-4444-4444-444444444444","position":0,"toolheadIndex":0,"materialMatches":true},
            {"id":"55555555-5555-5555-5555-555555555555","toolheadId":"66666666-6666-6666-6666-666666666666","position":1,"toolheadIndex":1,"materialMatches":false}
          ]
        }
        """
        let group = try decoder.decode(FilamentFallbackGroup.self, from: Data(json.utf8))
        XCTAssertEqual(group.members.map { $0.position }, [0, 1, 2])
    }

    func testGroup_missingMembersDecodesAsEmpty() throws {
        // Backend guarantees `members` is always emitted, but Swift decoder
        // stays defensive so a legacy or partial payload does not fail.
        let json = """
        {
          "id": "550e8400-e29b-41d4-a716-446655440000",
          "printerId": "660e8400-e29b-41d4-a716-446655440001",
          "name": "empty",
          "materialType": "ABS",
          "displayOrder": 0,
          "createdAt": "2025-01-01T12:00:00Z",
          "updatedAt": "2025-01-01T12:00:00Z"
        }
        """
        let group = try decoder.decode(FilamentFallbackGroup.self, from: Data(json.utf8))
        XCTAssertTrue(group.members.isEmpty)
    }

    // MARK: - FilamentFallbackGroupMember optional fields

    func testMember_toolheadNameAndSpoolCanBeMissing() throws {
        let json = """
        {
          "id": "11111111-1111-1111-1111-111111111111",
          "toolheadId": "22222222-2222-2222-2222-222222222222",
          "position": 0,
          "toolheadIndex": 0,
          "materialMatches": false
        }
        """
        let member = try decoder.decode(FilamentFallbackGroupMember.self, from: Data(json.utf8))
        XCTAssertNil(member.toolheadName)
        XCTAssertNil(member.currentMaterial)
        XCTAssertNil(member.currentSpoolId)
        XCTAssertFalse(member.materialMatches)
    }

    // MARK: - AvailableFallbackMember

    func testAvailableFallbackMember_decodesEvidenceFields() throws {
        let json = """
        {
          "groupId": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
          "memberId": "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
          "toolheadId": "cccccccc-cccc-cccc-cccc-cccccccccccc",
          "position": 1,
          "loadedMaterial": "PETG",
          "loadedSpoolId": 99
        }
        """
        let a = try decoder.decode(AvailableFallbackMember.self, from: Data(json.utf8))
        XCTAssertEqual(a.loadedMaterial, "PETG")
        XCTAssertEqual(a.loadedSpoolId, 99)
        XCTAssertEqual(a.position, 1)
    }

    func testAvailableFallbackMember_loadedSpoolIdOptional() throws {
        let json = """
        {
          "groupId": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
          "memberId": "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
          "toolheadId": "cccccccc-cccc-cccc-cccc-cccccccccccc",
          "position": 0,
          "loadedMaterial": "PLA"
        }
        """
        let a = try decoder.decode(AvailableFallbackMember.self, from: Data(json.utf8))
        XCTAssertNil(a.loadedSpoolId)
    }

    // MARK: - Request encoding

    func testCreateRequest_encodesCamelCaseKeys() throws {
        let request = CreateFilamentFallbackGroupRequest(
            name: "primary",
            materialType: "PLA",
            displayOrder: 3,
            toolheadIds: [TestData.testUUID, TestData.testUUID2]
        )
        let data = try JSONEncoder().encode(request)
        let obj = try JSONSerialization.jsonObject(with: data) as? [String: Any]
        XCTAssertEqual(obj?["name"] as? String, "primary")
        XCTAssertEqual(obj?["materialType"] as? String, "PLA")
        XCTAssertEqual(obj?["displayOrder"] as? Int, 3)
        let ids = obj?["toolheadIds"] as? [String]
        XCTAssertEqual(ids?.count, 2)
    }

    func testCreateRequest_displayOrderNilIsOmittedOrEncodedAsNull() throws {
        // The backend accepts either an explicit null or a missing key here;
        // Swift's default encoding drops nils. Both are contract-compatible.
        let request = CreateFilamentFallbackGroupRequest(
            name: "auto",
            materialType: "ABS",
            displayOrder: nil,
            toolheadIds: [TestData.testUUID]
        )
        let data = try JSONEncoder().encode(request)
        let text = String(data: data, encoding: .utf8) ?? ""
        // Whichever route the encoder picks, the server must be able to
        // parse it: it's either absent or explicit null.
        XCTAssertTrue(text.contains("\"displayOrder\":null") || !text.contains("displayOrder"))
    }

    func testUpdateRequest_encodesCamelCaseKeys() throws {
        let request = UpdateFilamentFallbackGroupRequest(
            name: "renamed",
            materialType: "PETG",
            displayOrder: 0,
            toolheadIds: [TestData.testUUID]
        )
        let data = try JSONEncoder().encode(request)
        let obj = try JSONSerialization.jsonObject(with: data) as? [String: Any]
        XCTAssertEqual(obj?["name"] as? String, "renamed")
        XCTAssertEqual(obj?["materialType"] as? String, "PETG")
        XCTAssertEqual(obj?["displayOrder"] as? Int, 0)
    }

    // MARK: - FallbackGroupsUpdatedEvent

    func testFallbackGroupsUpdatedEvent_decodesPrinterId() throws {
        let printerId = UUID(uuidString: "aaaaaaaa-1111-2222-3333-cccccccccccc")!
        let json = """
        {"printerId":"\(printerId.uuidString)"}
        """
        let event = try decoder.decode(FallbackGroupsUpdatedEvent.self, from: Data(json.utf8))
        XCTAssertEqual(event.printerId, printerId)
    }
}
