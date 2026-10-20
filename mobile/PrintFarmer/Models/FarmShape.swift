import Foundation

/// Server-observed farm counts returned by `GET /api/system/farm-shape`.
///
/// This is an **authenticated** endpoint, deliberately separate from
/// `GET /api/system/capabilities` (which stays `[AllowAnonymous]`).
/// Response payload uses camelCase and `Cache-Control: no-store`. An absent
/// response (401 / 404 / timeout / older server) is treated as shape unknown;
/// an absent field on a present response is a decode failure, not "unknown".
///
/// See ``FarmShapeService`` for fetch/persistence and #2410 / #2411 for
/// the original endpoint design rationale. Counts no longer choose mobile navigation.
struct FarmShape: Codable, Sendable, Equatable {
    let accountCount: Int
    let locationCount: Int
    let printerCount: Int
}

// MARK: - Printer sensors and fans (backend-neutral contract, docs/API.md)

/// Sensor kind; unknown wire values decode to `.unknown`.
enum SensorKind: String, Codable, Sendable, Equatable {
    case other = "Other", nozzle = "Nozzle", bed = "Bed", chamber = "Chamber", mcu = "Mcu"
    case host = "Host", ambient = "Ambient", filamentRunout = "FilamentRunout", fan = "Fan"
    case unknown

    init(from decoder: Decoder) throws {
        let raw = try decoder.singleValueContainer().decode(String.self)
        self = SensorKind(rawValue: raw) ?? .unknown
    }
}

enum SensorFanRole: String, Codable, Sendable, Equatable {
    case other = "Other", partCooling = "PartCooling", hotend = "Hotend"
    case controller = "Controller", chamber = "Chamber", auxiliary = "Auxiliary"
    case unknown

    init(from decoder: Decoder) throws {
        let raw = try decoder.singleValueContainer().decode(String.self)
        self = SensorFanRole(rawValue: raw) ?? .unknown
    }
}

enum SensorQuantity: String, Codable, Sendable, Equatable {
    case other = "Other", temperature = "Temperature", targetTemperature = "TargetTemperature"
    case humidity = "Humidity", pressure = "Pressure", gas = "Gas", fanSpeed = "FanSpeed"
    case fanRpm = "FanRpm", power = "Power", filamentDetected = "FilamentDetected"
    case unknown

    init(from decoder: Decoder) throws {
        let raw = try decoder.singleValueContainer().decode(String.self)
        self = SensorQuantity(rawValue: raw) ?? .unknown
    }
}

enum SensorUnit: String, Codable, Sendable, Equatable {
    case none = "None", celsius = "Celsius", percent = "Percent", hectoPascal = "HectoPascal"
    case ohm = "Ohm", rpm = "Rpm", watt = "Watt"
    case unknown

    init(from decoder: Decoder) throws {
        let raw = try decoder.singleValueContainer().decode(String.self)
        self = SensorUnit(rawValue: raw) ?? .unknown
    }

    /// Compact suffix used in visible text.
    var symbol: String {
        switch self {
        case .celsius: return "°C"
        case .percent: return "%"
        case .hectoPascal: return " hPa"
        case .ohm: return " Ω"
        case .rpm: return " RPM"
        case .watt: return " W"
        case .none, .unknown: return ""
        }
    }

    /// Spoken unit for VoiceOver.
    var spoken: String {
        switch self {
        case .celsius: return "degrees Celsius"
        case .percent: return "percent"
        case .hectoPascal: return "hectopascals"
        case .ohm: return "ohms"
        case .rpm: return "revolutions per minute"
        case .watt: return "watts"
        case .none, .unknown: return ""
        }
    }
}

/// Wire `value` is a bare JSON number or boolean.
enum SensorValue: Codable, Sendable, Equatable, Hashable {
    case number(Double)
    case bool(Bool)

    init(from decoder: Decoder) throws {
        let c = try decoder.singleValueContainer()
        if let b = try? c.decode(Bool.self) { self = .bool(b); return }
        self = .number(try c.decode(Double.self))
    }

    func encode(to encoder: Encoder) throws {
        var c = encoder.singleValueContainer()
        switch self {
        case .number(let v): try c.encode(v)
        case .bool(let v): try c.encode(v)
        }
    }

    var number: Double? {
        if case .number(let v) = self, v.isFinite { return v }
        return nil
    }
}

struct SensorReading: Codable, Sendable, Equatable, Hashable {
    let quantity: SensorQuantity
    let value: SensorValue
    let unit: SensorUnit
}

struct SensorWritable: Codable, Sendable, Equatable, Hashable {
    let quantity: SensorQuantity
    let min: Double
    let max: Double
    let step: Double?
    let unit: SensorUnit
}

struct PrinterSensor: Codable, Sendable, Equatable, Identifiable, Hashable {
    let id: String
    let displayName: String
    let kind: SensorKind
    let fanRole: SensorFanRole?
    let readings: [SensorReading]
    let available: Bool
    let lastUpdated: Date?
    let writable: [SensorWritable]

    private enum CodingKeys: String, CodingKey {
        case id, displayName, kind, fanRole, readings, available, lastUpdated, writable
    }

    init(
        id: String, displayName: String, kind: SensorKind, fanRole: SensorFanRole? = nil,
        readings: [SensorReading] = [], available: Bool = true, lastUpdated: Date? = nil,
        writable: [SensorWritable] = []
    ) {
        self.id = id; self.displayName = displayName; self.kind = kind; self.fanRole = fanRole
        self.readings = readings; self.available = available; self.lastUpdated = lastUpdated
        self.writable = writable
    }

    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        id = try c.decode(String.self, forKey: .id)
        displayName = try c.decodeIfPresent(String.self, forKey: .displayName) ?? id
        kind = try c.decodeIfPresent(SensorKind.self, forKey: .kind) ?? .other
        fanRole = try c.decodeIfPresent(SensorFanRole.self, forKey: .fanRole)
        readings = try c.decodeIfPresent([SensorReading].self, forKey: .readings) ?? []
        available = try c.decodeIfPresent(Bool.self, forKey: .available) ?? true
        lastUpdated = try? c.decodeIfPresent(Date.self, forKey: .lastUpdated)
        writable = try c.decodeIfPresent([SensorWritable].self, forKey: .writable) ?? []
    }

    func descriptor(for quantity: SensorQuantity) -> SensorWritable? {
        writable.first { $0.quantity == quantity }
    }

    func reading(_ quantity: SensorQuantity) -> SensorReading? {
        readings.first { $0.quantity == quantity }
    }
}

enum PrinterSensorGroup: Int, CaseIterable, Hashable, Sendable {
    case temperatures, fans, other

    var title: String {
        switch self {
        case .temperatures: return "Temperatures"
        case .fans: return "Fans"
        case .other: return "Other sensors"
        }
    }

    static func group(for sensor: PrinterSensor) -> PrinterSensorGroup {
        if sensor.readings.contains(where: { [.humidity, .pressure, .gas].contains($0.quantity) }) {
            return .temperatures
        }
        switch sensor.kind {
        case .fan: return .fans
        case .nozzle, .bed, .chamber, .mcu, .host, .ambient: return .temperatures
        case .filamentRunout, .other, .unknown: return .other
        }
    }
}

/// Pure display helpers shared by Status and Control rows.
enum PrinterSensorFormatting {
    private static let gridEpsilon = 1e-9

    static func displayName(_ name: String) -> String {
        name.replacingOccurrences(of: "_", with: " ")
            .components(separatedBy: " ")
            .map { part in
                guard let first = part.first else { return part }
                return String(first).uppercased() + String(part.dropFirst())
            }
            .joined(separator: " ")
    }

    static func isHidden(_ sensor: PrinterSensor) -> Bool {
        let shortName = sensor.id.split(whereSeparator: \.isWhitespace).dropFirst().joined(separator: " ")
        let name = shortName.isEmpty ? sensor.id : shortName
        return name.hasPrefix("_")
    }

    /// Next on-grid (min + n*step) value at least one step from `current` in the
    /// pressed direction, clamped to the largest on-grid value at or below max.
    static func steppedTarget(current: Double, increasing: Bool, descriptor: SensorWritable) -> Double {
        let step = (descriptor.step ?? 1) > 0 ? (descriptor.step ?? 1) : 1
        let maxIndex = ((descriptor.max - descriptor.min) / step + gridEpsilon).rounded(.down)
        let position = (current - descriptor.min) / step
        let index = increasing
            ? (position + gridEpsilon).rounded(.down) + 1
            : (position - gridEpsilon).rounded(.up) - 1
        return descriptor.min + Swift.min(Swift.max(index, 0), Swift.max(maxIndex, 0)) * step
    }

    static func isOnGrid(_ value: Double, descriptor: SensorWritable) -> Bool {
        guard let step = descriptor.step, step > 0 else { return true }
        let n = (value - descriptor.min) / step
        return abs(n - n.rounded()) <= 1e-6
    }

    static func valueText(_ reading: SensorReading) -> String {
        switch reading.value {
        case .bool(let v):
            return reading.quantity == .filamentDetected ? (v ? "Detected" : "Not detected") : (v ? "Yes" : "No")
        case .number(let v):
            guard v.isFinite else { return "—" }
            return v.formatted(.number.precision(.fractionLength(0...1))) + reading.unit.symbol
        }
    }

    static func spokenValue(_ reading: SensorReading) -> String {
        switch reading.value {
        case .bool: return valueText(reading)
        case .number(let v):
            guard v.isFinite else { return "unknown" }
            let unit = reading.unit.spoken
            return v.formatted(.number.precision(.fractionLength(0...1))) + (unit.isEmpty ? "" : " " + unit)
        }
    }

    static func quantityTitle(_ quantity: SensorQuantity) -> String {
        switch quantity {
        case .temperature: return "Temperature"
        case .targetTemperature: return "Target"
        case .humidity: return "Humidity"
        case .pressure: return "Pressure"
        case .gas: return "Gas"
        case .fanSpeed: return "Speed"
        case .fanRpm: return "RPM"
        case .power: return "Power"
        case .filamentDetected: return "Filament"
        case .other, .unknown: return "Value"
        }
    }

    /// One-line summary of all readings, e.g. "Temperature 24.5°C · Humidity 41%".
    static func summary(_ sensor: PrinterSensor) -> String {
        guard sensor.available else { return "Unavailable" }
        guard !sensor.readings.isEmpty else { return "No reading" }
        return sensor.readings.map { "\(quantityTitle($0.quantity)) \(valueText($0))" }.joined(separator: " · ")
    }

    static func accessibilityLabel(_ sensor: PrinterSensor) -> String {
        let name = displayName(sensor.displayName)
        guard sensor.available else { return "\(name), unavailable" }
        guard !sensor.readings.isEmpty else { return "\(name), no reading" }
        let parts = sensor.readings.map { "\(quantityTitle($0.quantity)) \(spokenValue($0))" }
        return "\(name), " + parts.joined(separator: ", ")
    }

    /// A reported list replaces the cache; an omitted list keeps it.
    static func merge(cached: [PrinterSensor], update: [PrinterSensor]?) -> [PrinterSensor] {
        guard let update else { return cached }
        return update.sorted { $0.id < $1.id }
    }
}
