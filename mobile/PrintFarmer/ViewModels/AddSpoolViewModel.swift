import Foundation
import os

@MainActor @Observable
final class AddSpoolViewModel {
    // Form fields
    var filamentName = ""
    var selectedMaterial = ""
    var selectedVendor = ""
    var colorHex = "#10b981"
    var totalWeightG: Double = 1000
    var spoolWeightG: Double = 200
    var diameterMm: Double = 1.75
    var extruderTempC: Int?
    var densityGPerCm3 = SpoolmanFilamentRequest.defaultDensityGramsPerCubicCentimeter
    var isCreatingFilament = false
    var selectedFilamentId: Int?

    // Reference data
    var materials: [SpoolmanMaterial] = []
    var vendors: [SpoolmanVendor] = []
    var filaments: [SpoolmanFilament] = []

    // State
    var isLoading = false
    var isSaving = false
    var errorMessage: String?
    var didSave = false
    private(set) var createdSpool: SpoolmanSpool?
    private(set) var createdFilament: SpoolmanFilament?
    private(set) var hasLoadedReferenceData = false
    private(set) var uncertainCreation: String?
    var isViewActive = true

    // Scan pre-fill
    var isPrefilledFromScan = false
    private var scannedFilamentId: Int?
    var isFilamentSelectionLocked: Bool { scannedFilamentId != nil || createdFilament != nil }

    private let logger = Logger(subsystem: "com.printfarmer.ios", category: "AddSpool")
    private var spoolService: (any SpoolServiceProtocol)?

    func configure(spoolService: any SpoolServiceProtocol) {
        self.spoolService = spoolService
    }

    func prefill(from scannedData: ScannedSpoolData) {
        isPrefilledFromScan = true
        if let id = scannedData.filamentId, id > 0 {
            scannedFilamentId = id
            selectedFilamentId = id
        }
        if let material = scannedData.material, !material.isEmpty {
            selectedMaterial = material
        }
        if let hex = scannedData.colorHex, !hex.isEmpty {
            colorHex = hex
        }
        if let vendor = scannedData.vendor, !vendor.isEmpty {
            selectedVendor = vendor
        }
        if let weight = scannedData.weight, weight > 0 {
            totalWeightG = weight
        }
        if let diameter = scannedData.diameter, diameter > 0 {
            diameterMm = diameter
        }
        if let temp = scannedData.temperature, temp > 0 {
            extruderTempC = temp
        }
    }

    var isFormValid: Bool {
        guard uncertainCreation == nil else { return false }
        if createdSpool != nil { return true }
        guard hasLoadedReferenceData, !isLoading,
              totalWeightG.isFinite, totalWeightG > 0,
              spoolWeightG.isFinite, spoolWeightG >= 0 else { return false }
        if isFilamentSelectionLocked { return true }
        if !isCreatingFilament {
            return filaments.contains { $0.id > 0 && $0.id == selectedFilamentId }
        }
        return !filamentName.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
            && !selectedMaterial.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
            && densityGPerCm3.isFinite && densityGPerCm3 > 0
            && diameterMm.isFinite && diameterMm > 0
            && (colorHex.isEmpty || colorHex.range(of: "^#?[0-9a-fA-F]{6}$", options: .regularExpression) != nil)
            && (selectedVendor.isEmpty || selectedVendorId != nil)
    }

    private var selectedVendorId: Int? {
        let matches = vendors.filter { $0.name == selectedVendor && $0.id > 0 }
        return matches.count == 1 ? matches.first?.id : nil
    }

    /// Pre-defined color swatches for quick selection
    static let colorSwatches: [(name: String, hex: String)] = [
        ("Black", "#000000"),
        ("White", "#FFFFFF"),
        ("Red", "#E53E3E"),
        ("Blue", "#3182CE"),
        ("Green", "#38A169"),
        ("Yellow", "#D69E2E"),
        ("Orange", "#DD6B20"),
        ("Purple", "#805AD5"),
        ("Pink", "#D53F8C"),
        ("Gray", "#718096"),
        ("Silver", "#CBD5E0"),
        ("Gold", "#D4A837"),
    ]

    func loadReferenceData() async {
        guard let spoolService, isViewActive, !isSaving, uncertainCreation == nil else {
            if spoolService == nil { errorMessage = "Spool service not available" }
            return
        }
        isLoading = true
        hasLoadedReferenceData = false
        errorMessage = nil
        defer { isLoading = false }

        do {
            async let mats = spoolService.listMaterials()
            async let vends = spoolService.listVendors()
            async let fils = spoolService.listFilaments()
            let m = try await mats
            let v = try await vends
            let f = try await fils
            guard isViewActive else { return }
            materials = m
            vendors = v
            filaments = f
            hasLoadedReferenceData = true
        } catch {
            guard isViewActive else { return }
            logger.warning("Failed to load reference data: \(error.localizedDescription)")
            errorMessage = error.localizedDescription
        }

    }

    func saveSpool(
        afterCreate: ((SpoolmanSpool) async throws -> Void)? = nil
    ) async {
        guard isViewActive, !isSaving, !didSave, uncertainCreation == nil else { return }
        guard let spoolService else {
            errorMessage = "Spool service not available"
            return
        }
        guard isFormValid else {
            errorMessage = "Choose an existing filament or complete the new filament details, "
                + "and enter a positive total weight."
            return
        }

        isSaving = true
        errorMessage = nil
        defer { isSaving = false }
        var pendingWrite: String?

        do {
            if createdSpool == nil {
                var filamentId = scannedFilamentId ?? createdFilament?.id ?? selectedFilamentId
                if isCreatingFilament && !isFilamentSelectionLocked {
                    let request = SpoolmanFilamentRequest.newFilament(
                        name: filamentName.trimmingCharacters(in: .whitespacesAndNewlines),
                        material: selectedMaterial.trimmingCharacters(in: .whitespacesAndNewlines),
                        vendorId: selectedVendorId,
                        colorHex: colorHex.isEmpty ? nil : colorHex,
                        density: densityGPerCm3,
                        diameter: diameterMm,
                        weight: totalWeightG,
                        spoolWeight: spoolWeightG
                    )
                    pendingWrite = "filament"
                    let filament = try await spoolService.createFilament(request)
                    guard filament.id > 0 else { throw NetworkError.invalidResponse }
                    createdFilament = filament
                    filamentId = filament.id
                    pendingWrite = nil
                }
                guard isViewActive, !Task.isCancelled else { return }
                guard let filamentId, filamentId > 0 else {
                    errorMessage = "Choose a valid filament before adding a spool."
                    return
                }
                let request = SpoolmanSpoolRequest(
                    filamentId: filamentId,
                    remainingWeight: totalWeightG,
                    initialWeight: totalWeightG,
                    spoolWeight: spoolWeightG > 0 ? spoolWeightG : nil
                )
                pendingWrite = "spool"
                let spool = try await spoolService.createSpool(request)
                guard spool.id > 0 else { throw NetworkError.invalidResponse }
                createdSpool = spool
                pendingWrite = nil
            }
            if let createdSpool {
                try await afterCreate?(createdSpool)
            }
            guard isViewActive else { return }
            didSave = true
        } catch {
            logger.warning("Failed to create spool: \(error.localizedDescription)")
            if let pendingWrite, !Self.isAuthorizationRejection(error) {
                uncertainCreation = pendingWrite
                errorMessage = "\(error.localizedDescription) The \(pendingWrite) may have been created. "
                    + "Check Filament inventory before starting again; Save is disabled to avoid duplicates."
            } else {
                errorMessage = error.localizedDescription
            }
        }
    }

    // The proxy can return 400 even after an upstream write failed ambiguously.
    // Only authorization rejections are known not to have attempted creation.
    private static func isAuthorizationRejection(_ error: Error) -> Bool {
        guard let error = error as? NetworkError else { return false }
        switch error {
        case .unauthorized, .forbidden: return true
        default: return false
        }
    }
}
