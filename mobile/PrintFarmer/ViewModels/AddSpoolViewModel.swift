import Foundation
import os

@MainActor @Observable
final class AddSpoolViewModel {
    // Form fields
    var filamentName = ""
    var selectedMaterial = "" {
        didSet { applyMaterialDensity() }
    }
    var selectedVendor = ""
    var colorHex = "#10b981"
    var totalWeightG: Double = 1000
    var spoolWeightG: Double = 200
    var diameterMm: Double = 1.75
    var extruderTempC: Int?
    var densityGPerCm3 = SpoolmanFilamentRequest.defaultDensityGramsPerCubicCentimeter {
        didSet {
            if !isApplyingMaterialDensity { hasEditedDensity = true }
        }
    }
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
    var filamentInventoryMessage: String? {
        guard hasLoadedReferenceData, !filaments.contains(where: { $0.id > 0 }) else { return nil }
        return "No filaments were returned. Inventory may be empty or unavailable. "
            + "Reload before creating a filament that may already exist."
    }

    private let logger = Logger(subsystem: "com.printfarmer.ios", category: "AddSpool")
    private var spoolService: (any SpoolServiceProtocol)?
    private var hasEditedDensity = false
    private var isApplyingMaterialDensity = false

    private func applyMaterialDensity() {
        guard let density = materials.first(where: { $0.name == selectedMaterial })?.density,
              density.isFinite, density > 0 else { return }
        isApplyingMaterialDensity = true
        densityGPerCm3 = density
        isApplyingMaterialDensity = false
    }

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
            && colorHex.range(of: "^#?[0-9a-fA-F]{6}$", options: .regularExpression) != nil
            && (selectedVendor.isEmpty || selectedVendorId != nil)
    }

    /// Existing filaments that may already be the product being created. Suggestions are
    /// informational only; nothing is selected until `useSuggestedFilament(id:)` is called.
    var filamentSuggestions: [FilamentSuggestion] {
        Array(rankedFilamentSuggestions.prefix(FilamentSuggestionMatcher.limit))
    }

    var hiddenFilamentSuggestionCount: Int {
        max(0, rankedFilamentSuggestions.count - FilamentSuggestionMatcher.limit)
    }

    var hasSameNameSuggestion: Bool {
        rankedFilamentSuggestions.first?.match == .sameName
    }

    private var rankedFilamentSuggestions: [FilamentSuggestion] {
        guard hasLoadedReferenceData, isCreatingFilament, !isFilamentSelectionLocked else { return [] }
        return FilamentSuggestionMatcher.rankedSuggestions(
            in: filaments, material: selectedMaterial, vendor: selectedVendor, name: filamentName
        )
    }

    /// Switches from creating a filament to the explicitly chosen existing one. Typed
    /// new-filament fields are kept so the user can switch back.
    func useSuggestedFilament(id: Int) {
        guard isCreatingFilament, !isFilamentSelectionLocked, !isSaving,
              createdSpool == nil, uncertainCreation == nil,
              filamentSuggestions.contains(where: { $0.id == id }) else { return }
        selectedFilamentId = id
        isCreatingFilament = false
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
            if !hasEditedDensity { applyMaterialDensity() }
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
                        colorHex: colorHex,
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

struct FilamentSuggestion: Identifiable, Equatable {
    enum Match: Equatable {
        /// Same name words, ignoring case, spacing, punctuation and word order.
        case sameName
        /// Shares at least one name word (or word prefix) with the typed name.
        case similarName
        /// Same material, and same vendor when one is chosen; the name does not overlap.
        case materialVendor
    }

    let filament: SpoolmanFilament
    let match: Match
    var id: Int { filament.id }

    static func == (lhs: FilamentSuggestion, rhs: FilamentSuggestion) -> Bool {
        lhs.id == rhs.id && lhs.match == rhs.match
    }
}

enum FilamentSuggestionMatcher {
    static let limit = 5

    /// Ranks existing filaments with the same material (and vendor, when chosen) by how
    /// closely their names match. Filaments with another material or vendor are never
    /// offered, however similar their names are.
    static func rankedSuggestions(
        in filaments: [SpoolmanFilament], material: String, vendor: String, name: String
    ) -> [FilamentSuggestion] {
        let materialKey = comparisonKey(material)
        guard !materialKey.isEmpty else { return [] }
        let vendorKey = comparisonKey(vendor)
        let typed = nameTokens(name)

        typealias Scored = (suggestion: FilamentSuggestion, score: Int, extra: Int, label: String)
        let scored: [Scored] = filaments
            .filter { filament in
                filament.id > 0
                    && comparisonKey(filament.material ?? "") == materialKey
                    && (vendorKey.isEmpty || comparisonKey(filament.vendor ?? "") == vendorKey)
            }
            .map { filament -> Scored in
                let candidate = nameTokens(filament.name ?? "")
                let matched = typed.filter { token in
                    candidate.contains { $0 == token || (token.count >= 2 && $0.hasPrefix(token)) }
                }.count
                let match: FilamentSuggestion.Match
                if !typed.isEmpty && typed == candidate {
                    match = .sameName
                } else if matched > 0 {
                    match = .similarName
                } else {
                    match = .materialVendor
                }
                return (
                    FilamentSuggestion(filament: filament, match: match), matched,
                    abs(candidate.count - typed.count), comparisonKey(filament.name ?? "")
                )
            }

        return scored.sorted { lhs, rhs in
            let lhsRank = rank(lhs.suggestion.match), rhsRank = rank(rhs.suggestion.match)
            if lhsRank != rhsRank { return lhsRank < rhsRank }
            if lhs.score != rhs.score { return lhs.score > rhs.score }
            if lhs.extra != rhs.extra { return lhs.extra < rhs.extra }
            if lhs.label != rhs.label { return lhs.label < rhs.label }
            return lhs.suggestion.id < rhs.suggestion.id
        }
        .map(\.suggestion)
    }

    /// Case-, diacritic- and width-insensitive text with collapsed whitespace. Punctuation is
    /// kept so materials such as "PLA" and "PLA+" stay distinct.
    static func comparisonKey(_ value: String) -> String {
        value.folding(options: [.caseInsensitive, .diacriticInsensitive, .widthInsensitive], locale: nil)
            .split(whereSeparator: \.isWhitespace)
            .joined(separator: " ")
    }

    /// Order-independent name words, ignoring case, diacritics and punctuation.
    static func nameTokens(_ value: String) -> Set<String> {
        Set(
            value.folding(options: [.caseInsensitive, .diacriticInsensitive, .widthInsensitive], locale: nil)
                .split { !$0.isLetter && !$0.isNumber }
                .map(String.init)
        )
    }

    private static func rank(_ match: FilamentSuggestion.Match) -> Int {
        switch match {
        case .sameName: return 0
        case .similarName: return 1
        case .materialVendor: return 2
        }
    }
}
