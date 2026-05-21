import Foundation

extension Date {
    /// Formats the date for display using relative formatting when appropriate.
    var relativeFormatted: String {
        let formatter = RelativeDateTimeFormatter()
        formatter.unitsStyle = .abbreviated
        return formatter.localizedString(for: self, relativeTo: .now)
    }

    /// Formats time remaining from now until this date (e.g., "2h 15m").
    var timeRemainingFormatted: String {
        let remaining = self.timeIntervalSince(Date())
        guard remaining > 0 else { return "0m" }
        let hours = Int(remaining) / 3600
        let minutes = (Int(remaining) % 3600) / 60
        if hours > 0 {
            return "\(hours)h \(minutes)m"
        }
        return "\(minutes)m"
    }

    /// Formats the date as a short time string (e.g., "2:45 PM").
    var shortTimeFormatted: String {
        let formatter = DateFormatter()
        formatter.dateFormat = "h:mm a"
        return formatter.string(from: self)
    }
}

extension Double {
    /// Formats a temperature value with degree symbol (e.g., "215°C").
    var temperatureFormatted: String {
        String(format: "%.0f°C", self)
    }

    /// Formats a progress value as percentage (0.0–1.0 → "75%").
    var percentFormatted: String {
        String(format: "%.0f%%", self * 100)
    }
}

extension TimeInterval {
    /// Formats duration in human-readable form (e.g., "2h 15m").
    var durationFormatted: String {
        let hours = Int(self) / 3600
        let minutes = (Int(self) % 3600) / 60
        if hours > 0 {
            return "\(hours)h \(minutes)m"
        }
        return "\(minutes)m"
    }

    /// Formats ETA as a time (e.g., "2:45 PM" for today, "Tomorrow 10:00 AM" for tomorrow).
    var etaFormatted: String {
        let eta = Date.now.addingTimeInterval(self)
        let calendar = Calendar.current
        if calendar.isDateInToday(eta) {
            let formatter = DateFormatter()
            formatter.timeStyle = .short
            return formatter.string(from: eta)
        } else if calendar.isDateInTomorrow(eta) {
            let formatter = DateFormatter()
            formatter.timeStyle = .short
            return "Tomorrow \(formatter.string(from: eta))"
        } else {
            return eta.relativeFormatted
        }
    }
}

extension String {
    /// Parses a .NET TimeSpan string ("HH:mm:ss" or "d.HH:mm:ss") into seconds.
    var timeSpanSeconds: TimeInterval? {
        // Handles "HH:mm:ss", "HH:mm:ss.fffffff", "d.HH:mm:ss"
        let parts = split(separator: ":")
        guard parts.count >= 2 else { return nil }

        let dayAndHours = String(parts[0]).split(separator: ".")
        let days: Double
        let hours: Double
        if dayAndHours.count == 2 {
            days = Double(dayAndHours[0]) ?? 0
            hours = Double(dayAndHours[1]) ?? 0
        } else {
            days = 0
            hours = Double(parts[0]) ?? 0
        }

        let minutes = Double(parts[1]) ?? 0
        let seconds = parts.count > 2 ? (Double(parts[2].prefix(while: { $0 != "." && $0.isNumber })) ?? 0) : 0

        return (days * 86400) + (hours * 3600) + (minutes * 60) + seconds
    }

    /// Formats a .NET TimeSpan string for display (e.g., "2h 15m").
    var timeSpanFormatted: String {
        guard let secs = timeSpanSeconds else { return self }
        return secs.durationFormatted
    }
}
