import Foundation

// MARK: - Farm Snapshot Envelope (F10-C1a, #816)
//
// The at-rest record for the per-server/user farm snapshot cache. Every type in
// this file is a structural allow-list projection: it can only carry non-secret
// fields that the existing iPhone/iPad Farm cards render. Credentials, tokens,
// cookies, headers, passwords, ports, camera/transport URLs, coordinates, and
// raw telemetry have no property here, so persisting a secret is impossible by
// schema rather than by runtime filtering.
//
// The published protocol/envelope is the stable contract consumed unchanged by
// the C1b UI child (#817). Do not add credential-bearing fields.

/// Strict identity namespace for a snapshot record. A snapshot is addressed only
/// by the stable server UUID plus the authenticated user UUID — never by URL,
/// token, display name, or ordering. The pairing is meaningful: a `userID` is
/// only ever valid under the exact `serverID` it was verified against.
struct FarmSnapshotNamespace: Codable, Sendable, Equatable, Hashable {
    let serverID: UUID
    let userID: UUID

    init(serverID: UUID, userID: UUID) {
        self.serverID = serverID
        self.userID = userID
    }
}

/// Non-secret spool display fields (subset of `PrinterSpoolInfo`).
struct FarmSnapshotSpool: Codable, Sendable, Equatable {
    let hasActiveSpool: Bool
    let activeSpoolId: Int?
    let spoolName: String?
    let material: String?
    let colorHex: String?
    let filamentName: String?
    let vendor: String?
    let remainingWeightG: Double?
    let spoolInUse: Bool?

    init(_ info: PrinterSpoolInfo) {
        self.hasActiveSpool = info.hasActiveSpool
        self.activeSpoolId = info.activeSpoolId
        self.spoolName = info.spoolName
        self.material = info.material
        self.colorHex = info.colorHex
        self.filamentName = info.filamentName
        self.vendor = info.vendor
        self.remainingWeightG = info.remainingWeightG
        self.spoolInUse = info.spoolInUse
    }
}

/// Non-secret location display fields (subset of `LocationSummary`).
struct FarmSnapshotLocation: Codable, Sendable, Equatable {
    let id: UUID
    let name: String
    let description: String?

    init(_ summary: LocationSummary) {
        self.id = summary.id
        self.name = summary.name
        self.description = summary.description
    }
}

/// Allow-list projection of a `Printer` carrying every non-secret field the
/// iPhone (`PrinterCardView`) and iPad (`iPadPrinterCardView`) Farm cards render.
///
/// Structurally absent (no property exists): `apiKey`, `originalServerUrl`,
/// `backendUrl`, `frontendUrl`, `backendPort`, `frontendPort`, `thumbnailUrl`,
/// `cameraStreamUrl`, `cameraSnapshotUrl`, camera access/format/strategy, `x`,
/// `y`, `z`, `homedAxes`, `notes`, `backend`, `manufacturerId`, `modelId`,
/// `motionType`, and any token/cookie/password/header.
struct FarmSnapshotPrinter: Codable, Sendable, Equatable, Identifiable {
    let id: UUID
    let name: String
    let location: FarmSnapshotLocation?
    let modelName: String?
    let manufacturerName: String?
    let isOnline: Bool
    let isEnabled: Bool
    let inMaintenance: Bool
    let state: String?
    let progress: Double?
    let jobName: String?
    let fileName: String?
    let hotendTemp: Double?
    let hotendTarget: Double?
    let bedTemp: Double?
    let bedTarget: Double?
    let spool: FarmSnapshotSpool?
    let obicoEnabled: Bool

    init(_ printer: Printer) {
        self.id = printer.id
        self.name = printer.name
        self.location = printer.location.map(FarmSnapshotLocation.init)
        self.modelName = printer.modelName
        self.manufacturerName = printer.manufacturerName
        self.isOnline = printer.isOnline
        self.isEnabled = printer.isEnabled
        self.inMaintenance = printer.inMaintenance
        self.state = printer.state
        self.progress = printer.progress
        self.jobName = printer.jobName
        self.fileName = printer.fileName
        self.hotendTemp = printer.hotendTemp
        self.hotendTarget = printer.hotendTarget
        self.bedTemp = printer.bedTemp
        self.bedTarget = printer.bedTarget
        self.spool = printer.spoolInfo.map(FarmSnapshotSpool.init)
        self.obicoEnabled = printer.obicoEnabled
    }
}

/// Versioned, self-describing at-rest record. `lastUpdatedAtMillis` is the
/// immutable UTC instant of the successful canonical response, encoded as an
/// `Int64` epoch-millisecond so sub-second ordering survives store/process
/// recreation with exact integer comparison.
struct FarmSnapshotEnvelope: Codable, Sendable, Equatable {
    /// Bump only when the on-disk layout changes incompatibly. A record whose
    /// `schemaVersion` differs from `currentSchemaVersion` is treated as
    /// unreadable/quarantinable — never silently coerced.
    static let currentSchemaVersion = 1

    let schemaVersion: Int
    let namespace: FarmSnapshotNamespace
    let payload: [FarmSnapshotPrinter]
    let lastUpdatedAtMillis: Int64

    init(
        schemaVersion: Int = FarmSnapshotEnvelope.currentSchemaVersion,
        namespace: FarmSnapshotNamespace,
        payload: [FarmSnapshotPrinter],
        lastUpdatedAtMillis: Int64
    ) {
        self.schemaVersion = schemaVersion
        self.namespace = namespace
        self.payload = payload
        self.lastUpdatedAtMillis = lastUpdatedAtMillis
    }

    /// Convenience projection from live `Printer` values.
    init(
        namespace: FarmSnapshotNamespace,
        printers: [Printer],
        lastUpdatedAtMillis: Int64
    ) {
        self.init(
            namespace: namespace,
            payload: printers.map(FarmSnapshotPrinter.init),
            lastUpdatedAtMillis: lastUpdatedAtMillis
        )
    }

    var isSupportedSchema: Bool {
        schemaVersion == FarmSnapshotEnvelope.currentSchemaVersion
    }
}

extension FarmSnapshotEnvelope {
    /// Deterministic, key-sorted encoder so byte-level scans and equality are stable.
    static func makeEncoder() -> JSONEncoder {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.sortedKeys]
        return encoder
    }

    static func makeDecoder() -> JSONDecoder {
        JSONDecoder()
    }
}
