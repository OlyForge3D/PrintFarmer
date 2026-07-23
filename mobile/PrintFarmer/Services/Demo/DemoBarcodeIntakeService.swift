import Foundation

// MARK: - Demo Barcode Intake Service

final class DemoBarcodeIntakeService: BarcodeIntakeServiceProtocol, @unchecked Sendable {
    private let demoFilament = SpoolmanFilament(
        id: 1,
        name: "Prusament PLA",
        material: "PLA",
        colorHex: "#000000",
        vendor: "Prusa Research",
        density: 1.24,
        diameter: 1.75,
        weight: 1000,
        spoolWeight: 200,
        price: 24.99,
        settingsExtruderTemp: 215,
        settingsBedTemp: 60,
        articleNumber: "DEMO-PLA-BK",
        comment: nil,
        multiColorHexes: nil,
        externalId: nil
    )

    func resolveFilament(barcode: String) async throws -> SpoolmanFilament? {
        barcode == "012345678905" ? demoFilament : nil
    }

    func saveMapping(barcode: String, filamentId: Int) async throws -> SpoolmanFilament {
        demoFilament
    }

    func importSpool(barcode: String, fields: SpoolImportFields) async throws -> SpoolmanSpool {
        SpoolmanSpool(
            id: Int.random(in: 1_000...9_999),
            filamentId: demoFilament.id,
            name: demoFilament.name ?? "Demo Spool",
            material: demoFilament.material ?? "PLA",
            colorHex: demoFilament.colorHex,
            inUse: false,
            filamentName: demoFilament.name,
            vendor: demoFilament.vendor,
            registeredAt: nil,
            firstUsedAt: nil,
            lastUsedAt: nil,
            remainingWeightG: fields.remainingWeight ?? demoFilament.weight,
            initialWeightG: fields.initialWeight ?? demoFilament.weight,
            usedWeightG: 0,
            spoolWeightG: fields.spoolWeight ?? demoFilament.spoolWeight,
            remainingLengthMm: nil,
            usedLengthMm: nil,
            location: fields.location,
            lotNumber: fields.lotNumber,
            archived: false,
            price: fields.price ?? demoFilament.price,
            comment: fields.comment,
            hasNfcTag: false,
            usedPercent: 0,
            remainingPercent: 100
        )
    }
}
