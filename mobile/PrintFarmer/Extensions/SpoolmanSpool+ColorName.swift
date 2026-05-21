import Foundation

extension SpoolmanSpool {
    /// Matches a search query against common color names derived from the spool's hex color.
    func colorNameMatches(_ query: String) -> Bool {
        guard let hex = colorHex?.lowercased().replacingOccurrences(of: "#", with: ""),
              hex.count == 6,
              let r = UInt8(hex.prefix(2), radix: 16),
              let g = UInt8(hex.dropFirst(2).prefix(2), radix: 16),
              let b = UInt8(hex.dropFirst(4).prefix(2), radix: 16) else {
            return false
        }

        let names = Self.approximateColorNames(r: r, g: g, b: b)
        return names.contains { $0.contains(query) }
    }

    private static func approximateColorNames(r: UInt8, g: UInt8, b: UInt8) -> [String] {
        let ri = Int(r), gi = Int(g), bi = Int(b)
        let maxC = max(ri, gi, bi)
        let minC = min(ri, gi, bi)

        // Achromatic
        if maxC - minC < 30 {
            return achromaticNames(maxC: maxC, minC: minC)
        }

        var names = dominantChannelNames(ri: ri, gi: gi, bi: bi)

        // Yellow catch-all
        if ri > 200 && gi > 200 && bi < 100 { names.append("yellow") }
        // Brown
        if ri > 100 && ri < 200 && gi > 50 && gi < 130 && bi < 80 { names.append("brown") }
        // Gold
        if ri > 180 && gi > 150 && bi < 80 { names.append("gold") }

        return names
    }

    private static func achromaticNames(maxC: Int, minC: Int) -> [String] {
        if maxC < 50 {
            return ["black"]
        } else if minC > 200 {
            return ["white"]
        } else {
            return ["gray", "grey", "silver"]
        }
    }

    private static func dominantChannelNames(ri: Int, gi: Int, bi: Int) -> [String] {
        var names: [String] = []
        if ri > gi && ri > bi {
            if gi > 150 && bi < 80 {
                names.append("orange")
            } else if gi < 80 && bi < 80 {
                names.append("red")
            } else if bi > 100 {
                names.append("pink"); names.append("magenta")
            } else {
                names.append("red")
            }
        }
        if gi > ri && gi > bi {
            if ri > 150 {
                names.append("yellow")
            } else if bi > 100 {
                names.append("teal"); names.append("cyan")
            } else {
                names.append("green")
            }
        }
        if bi > ri && bi > gi {
            if ri > 100 && gi < 80 {
                names.append("purple"); names.append("violet")
            } else if gi > 150 {
                names.append("cyan"); names.append("teal")
            } else {
                names.append("blue")
            }
        }
        return names
    }
}
