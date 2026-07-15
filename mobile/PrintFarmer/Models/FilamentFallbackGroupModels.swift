import Foundation

// MARK: - Fallback Groups (issue #711, F6)
//
// Ordered same-material chains over the existing per-printer toolhead IDs.
// Wire contract mirrors `Farm.Infrastructure.Dtos.FilamentFallbackGroupDto`
// exposed at `GET/POST/PUT/DELETE /api/printers/{printerId}/fallback-groups`
// (`FilamentFallbackGroupsController.cs`). Every field the backend emits with
// `[JsonPropertyName]` is decoded exactly by camelCase name — the shared
// `APIClient` decoder uses the default key strategy, so Swift property names
// must match the wire keys.

/// A per-printer fallback group. Members are already in `position` order as
/// returned by the API but are re-sorted defensively at decode-time so UI
/// callers never rely on server ordering.
struct FilamentFallbackGroup: Codable, Identifiable, Sendable, Equatable {
    let id: UUID
    let printerId: UUID
    let name: String
    let materialType: String
    let displayOrder: Int
    let createdAt: Date
    let updatedAt: Date
    let members: [FilamentFallbackGroupMember]

    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        id = try c.decode(UUID.self, forKey: .id)
        printerId = try c.decode(UUID.self, forKey: .printerId)
        name = try c.decode(String.self, forKey: .name)
        materialType = try c.decode(String.self, forKey: .materialType)
        displayOrder = try c.decodeIfPresent(Int.self, forKey: .displayOrder) ?? 0
        createdAt = try c.decode(Date.self, forKey: .createdAt)
        updatedAt = try c.decode(Date.self, forKey: .updatedAt)
        let decoded = try c.decodeIfPresent([FilamentFallbackGroupMember].self, forKey: .members) ?? []
        members = decoded.sorted { $0.position < $1.position }
    }

    init(
        id: UUID,
        printerId: UUID,
        name: String,
        materialType: String,
        displayOrder: Int,
        createdAt: Date,
        updatedAt: Date,
        members: [FilamentFallbackGroupMember]
    ) {
        self.id = id
        self.printerId = printerId
        self.name = name
        self.materialType = materialType
        self.displayOrder = displayOrder
        self.createdAt = createdAt
        self.updatedAt = updatedAt
        self.members = members.sorted { $0.position < $1.position }
    }

    private enum CodingKeys: String, CodingKey {
        case id, printerId, name, materialType, displayOrder, createdAt, updatedAt, members
    }
}

/// A single member of a fallback chain, referencing a toolhead already
/// configured on the same printer as the group.
///
/// `materialMatches` indicates whether the toolhead's currently loaded
/// spool matches `FilamentFallbackGroup.materialType`; UI callers use it
/// to render a "ready" vs "wrong-material" state without re-computing.
struct FilamentFallbackGroupMember: Codable, Identifiable, Sendable, Equatable {
    let id: UUID
    let toolheadId: UUID
    let position: Int
    let toolheadName: String?
    let toolheadIndex: Int
    let currentMaterial: String?
    let currentSpoolId: Int?
    let materialMatches: Bool
}

/// Read-only evidence returned by `GET .../fallback-groups/available` for
/// runout-attention downgrade logic. `nil`/204 = no available backup.
struct AvailableFallbackMember: Codable, Sendable, Equatable {
    let groupId: UUID
    let memberId: UUID
    let toolheadId: UUID
    let position: Int
    let loadedMaterial: String
    let loadedSpoolId: Int?
}

/// Request body for `POST .../fallback-groups`. Backend wire keys are
/// camelCase; `displayOrder == nil` lets the server append to the end.
struct CreateFilamentFallbackGroupRequest: Codable, Sendable, Equatable {
    let name: String
    let materialType: String
    let displayOrder: Int?
    let toolheadIds: [UUID]
}

/// Request body for `PUT .../fallback-groups/{groupId}`.
struct UpdateFilamentFallbackGroupRequest: Codable, Sendable, Equatable {
    let name: String
    let materialType: String
    let displayOrder: Int?
    let toolheadIds: [UUID]
}

// MARK: - SignalR invalidation event

/// Payload of the lowercase `fallbackgroupsupdated` SignalR event emitted by
/// `FilamentFallbackGroupsController` after any mutation. Following the same
/// pattern as `attentionchanged` (issue #707), this is an invalidation hint —
/// callers must refetch `GET .../fallback-groups` and never persist any field
/// of the payload as canonical state.
struct FallbackGroupsUpdatedEvent: Codable, Sendable, Equatable {
    let printerId: UUID
}
