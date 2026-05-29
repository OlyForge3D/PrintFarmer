## Swift Codable: Omit Nil Fields from JSON Encoding

**When to use:** You have a Swift request model where some fields are optional and you want the
JSON body to omit keys entirely (not emit `null`) when those fields are nil.

**Why it matters:** Many backends treat a missing key differently from `null`. Swift's default
`Codable` synthesis emits `null` for optional properties when `encodeIfPresent` is not used.

### Pattern: Custom `encode(to:)` with conditional encoding

```swift
private struct SetTemperaturesRequest: Encodable, Sendable {
    let hotend: Double?
    let bed: Double?

    func encode(to encoder: Encoder) throws {
        var container = encoder.container(keyedBy: CodingKeys.self)
        if let hotend { try container.encode(hotend, forKey: .hotend) }
        if let bed    { try container.encode(bed,    forKey: .bed)    }
    }

    enum CodingKeys: String, CodingKey {
        case hotend, bed
    }
}
```

`container.encode(_:forKey:)` only runs when the value is non-nil, so missing fields are
completely absent from the JSON output.

### Alternative: `[String: Value]` dictionary

When the field set is dynamic (e.g., setting one of N axes), a dictionary is cleaner:

```swift
var body: [String: Double] = ["f": Double(feedrateMmMin)]
body[axis.lowercased()] = distanceMm
// Result: {"x": 10.0, "f": 3000.0} — y and z are absent
```

Dictionary keys that are never set are never emitted.

### When to choose which

| Situation | Prefer |
|---|---|
| Fixed set of optional fields (2–5), type-safe | Custom `Encodable` struct |
| Dynamic key selection (e.g., "one of X/Y/Z") | `[String: Value]` dictionary |
| Many optional fields with heterogeneous types | Custom `Encodable` struct |

### Usage in PFarm-Ios

- `SetTemperaturesRequest` (PrinterService.swift) — struct pattern for hotend/bed
- `move(axis:)` body (PrinterService.swift) — dictionary pattern for axis dispatch
