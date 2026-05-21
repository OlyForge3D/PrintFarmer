import Foundation

// MARK: - Spoolman Spool (matches SpoolmanSpoolDto)

struct SpoolmanSpool: Codable, Identifiable, Sendable {
    let id: Int
    let filamentId: Int?
    let name: String
    let material: String
    let colorHex: String?
    let inUse: Bool?
    let filamentName: String?
    let vendor: String?
    let registeredAt: String?
    let firstUsedAt: String?
    let lastUsedAt: String?

    // Weight & length
    let remainingWeightG: Double?
    let initialWeightG: Double?
    let usedWeightG: Double?
    let spoolWeightG: Double?
    let remainingLengthMm: Double?
    let usedLengthMm: Double?

    // Metadata
    let location: String?
    let lotNumber: String?
    let archived: Bool?
    let price: Double?
    let comment: String?

    // NFC
    let hasNfcTag: Bool?

    // Computed by backend
    let usedPercent: Double?
    let remainingPercent: Double?

    init(
        id: Int,
        filamentId: Int? = nil,
        name: String,
        material: String,
        colorHex: String?,
        inUse: Bool?,
        filamentName: String?,
        vendor: String?,
        registeredAt: String?,
        firstUsedAt: String?,
        lastUsedAt: String?,
        remainingWeightG: Double?,
        initialWeightG: Double?,
        usedWeightG: Double?,
        spoolWeightG: Double?,
        remainingLengthMm: Double?,
        usedLengthMm: Double?,
        location: String?,
        lotNumber: String?,
        archived: Bool?,
        price: Double?,
        comment: String?,
        hasNfcTag: Bool?,
        usedPercent: Double?,
        remainingPercent: Double?
    ) {
        self.id = id
        self.filamentId = filamentId
        self.name = name
        self.material = material
        self.colorHex = colorHex
        self.inUse = inUse
        self.filamentName = filamentName
        self.vendor = vendor
        self.registeredAt = registeredAt
        self.firstUsedAt = firstUsedAt
        self.lastUsedAt = lastUsedAt
        self.remainingWeightG = remainingWeightG
        self.initialWeightG = initialWeightG
        self.usedWeightG = usedWeightG
        self.spoolWeightG = spoolWeightG
        self.remainingLengthMm = remainingLengthMm
        self.usedLengthMm = usedLengthMm
        self.location = location
        self.lotNumber = lotNumber
        self.archived = archived
        self.price = price
        self.comment = comment
        self.hasNfcTag = hasNfcTag
        self.usedPercent = usedPercent
        self.remainingPercent = remainingPercent
    }
}

// MARK: - Spoolman Filament (matches SpoolmanFilamentDto)

struct SpoolmanFilament: Codable, Identifiable, Sendable {
    let id: Int
    let name: String?
    let material: String?
    let colorHex: String?
    let vendor: String?
    let density: Double?
    let diameter: Double?
    let weight: Double?
    let spoolWeight: Double?
    let price: Double?
    let settingsExtruderTemp: Int?
    let settingsBedTemp: Int?
    let articleNumber: String?
    let comment: String?
    let multiColorHexes: String?
    let externalId: String?
}

// MARK: - Spoolman Vendor (matches SpoolmanVendorDto)

struct SpoolmanVendor: Codable, Identifiable, Sendable {
    let id: Int
    let name: String
    let externalId: String?
}

// MARK: - Spoolman Material (matches SpoolmanMaterialDto)

struct SpoolmanMaterial: Codable, Identifiable, Sendable {
    let id: Int
    let name: String
    let density: Double?
    let colorHex: String?
}

// MARK: - Paged Result (matches SpoolmanPagedResult<T>)

struct SpoolmanPagedResult<T: Codable & Sendable>: Codable, Sendable {
    let items: [T]
    let totalCount: Int
}

// MARK: - Spool Request (matches SpoolmanSpoolRequest — create/update)

struct SpoolmanSpoolRequest: Codable, Sendable {
    var filamentId: Int?
    var remainingWeight: Double?
    var initialWeight: Double?
    var spoolWeight: Double?
    var location: String?
    var lotNumber: String?
    var price: Double?
    var comment: String?
    var archived: Bool?
    var hasNfcTag: Bool?
}

// MARK: - Set Active Spool Request (matches SetActiveSpoolRequest)

struct SetActiveSpoolRequest: Codable, Sendable {
    let spoolId: Int?
}
