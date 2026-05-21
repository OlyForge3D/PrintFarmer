import SwiftUI

// MARK: - Icon Type

enum TemperatureIcon {
    case hotend
    case bed

    @ViewBuilder var view: some View {
        switch self {
        case .hotend:
            NozzleIcon()
                .fill(.primary)
        case .bed:
            RadiatorIcon()
                .fill(.primary)
        }
    }
}

// MARK: - Nozzle Icon (mdiPrinter3dNozzle)

/// 3D printer nozzle icon matching the web UI's MDI icon.
struct NozzleIcon: Shape {
    func path(in rect: CGRect) -> Path {
        let scaleX = rect.width / 24
        let scaleY = rect.height / 24
        var path = Path()

        // M7,2 H17 V8 H19 V13 H16.5 L13,17 H11 L7.5,13 H5 V8 H7 V2
        path.move(to: CGPoint(x: 7, y: 2))
        path.addLine(to: CGPoint(x: 17, y: 2))
        path.addLine(to: CGPoint(x: 17, y: 8))
        path.addLine(to: CGPoint(x: 19, y: 8))
        path.addLine(to: CGPoint(x: 19, y: 13))
        path.addLine(to: CGPoint(x: 16.5, y: 13))
        path.addLine(to: CGPoint(x: 13, y: 17))
        path.addLine(to: CGPoint(x: 11, y: 17))
        path.addLine(to: CGPoint(x: 7.5, y: 13))
        path.addLine(to: CGPoint(x: 5, y: 13))
        path.addLine(to: CGPoint(x: 5, y: 8))
        path.addLine(to: CGPoint(x: 7, y: 8))
        path.closeSubpath()

        // M10,22 H2 V20 H10 A1,1 0 0,0 11,19 V18 H13 V19 A3,3 0 0,1 10,22 Z
        path.move(to: CGPoint(x: 10, y: 22))
        path.addLine(to: CGPoint(x: 2, y: 22))
        path.addLine(to: CGPoint(x: 2, y: 20))
        path.addLine(to: CGPoint(x: 10, y: 20))
        path.addArc(
            center: CGPoint(x: 10, y: 19),
            radius: 1,
            startAngle: .degrees(90),
            endAngle: .degrees(0),
            clockwise: true
        )
        path.addLine(to: CGPoint(x: 11, y: 18))
        path.addLine(to: CGPoint(x: 13, y: 18))
        path.addLine(to: CGPoint(x: 13, y: 19))
        path.addArc(
            center: CGPoint(x: 10, y: 19),
            radius: 3,
            startAngle: .degrees(0),
            endAngle: .degrees(90),
            clockwise: false
        )
        path.closeSubpath()

        return path.applying(CGAffineTransform(scaleX: scaleX, y: scaleY))
    }
}

// MARK: - Radiator Icon (mdiRadiator)

/// Radiator / heated bed icon matching the web UI's MDI icon.
struct RadiatorIcon: Shape {
    func path(in rect: CGRect) -> Path {
        let scaleX = rect.width / 24
        let scaleY = rect.height / 24
        var path = Path()

        // Steam wave 1: M7.95,3 L6.53,5.19 L7.95,7.4 H7.94 L5.95,10.5 L4.22,9.6 L5.64,7.39 L4.22,5.19 L6.22,2.09 L7.95,3
        path.move(to: CGPoint(x: 7.95, y: 3))
        path.addLine(to: CGPoint(x: 6.53, y: 5.19))
        path.addLine(to: CGPoint(x: 7.95, y: 7.4))
        path.addLine(to: CGPoint(x: 7.94, y: 7.4))
        path.addLine(to: CGPoint(x: 5.95, y: 10.5))
        path.addLine(to: CGPoint(x: 4.22, y: 9.6))
        path.addLine(to: CGPoint(x: 5.64, y: 7.39))
        path.addLine(to: CGPoint(x: 4.22, y: 5.19))
        path.addLine(to: CGPoint(x: 6.22, y: 2.09))
        path.closeSubpath()

        // Steam wave 2: M13.95,2.89 L12.53,5.1 L13.95,7.3 L13.94,7.31 L11.95,10.4 L10.22,9.5 L11.64,7.3 L10.22,5.1 L12.22,2 L13.95,2.89
        path.move(to: CGPoint(x: 13.95, y: 2.89))
        path.addLine(to: CGPoint(x: 12.53, y: 5.1))
        path.addLine(to: CGPoint(x: 13.95, y: 7.3))
        path.addLine(to: CGPoint(x: 13.94, y: 7.31))
        path.addLine(to: CGPoint(x: 11.95, y: 10.4))
        path.addLine(to: CGPoint(x: 10.22, y: 9.5))
        path.addLine(to: CGPoint(x: 11.64, y: 7.3))
        path.addLine(to: CGPoint(x: 10.22, y: 5.1))
        path.addLine(to: CGPoint(x: 12.22, y: 2))
        path.closeSubpath()

        // Steam wave 3: M20,2.89 L18.56,5.1 L20,7.3 V7.31 L18,10.4 L16.25,9.5 L17.67,7.3 L16.25,5.1 L18.25,2 L20,2.89
        path.move(to: CGPoint(x: 20, y: 2.89))
        path.addLine(to: CGPoint(x: 18.56, y: 5.1))
        path.addLine(to: CGPoint(x: 20, y: 7.3))
        path.addLine(to: CGPoint(x: 20, y: 7.31))
        path.addLine(to: CGPoint(x: 18, y: 10.4))
        path.addLine(to: CGPoint(x: 16.25, y: 9.5))
        path.addLine(to: CGPoint(x: 17.67, y: 7.3))
        path.addLine(to: CGPoint(x: 16.25, y: 5.1))
        path.addLine(to: CGPoint(x: 18.25, y: 2))
        path.closeSubpath()

        // Radiator body: M2,22 V14 A2,2 0 0,1 4,12 H20 A2,2 0 0,1 22,14 V22 H20 V20 H4 V22 H2
        path.move(to: CGPoint(x: 2, y: 22))
        path.addLine(to: CGPoint(x: 2, y: 14))
        path.addArc(
            center: CGPoint(x: 4, y: 14),
            radius: 2,
            startAngle: .degrees(180),
            endAngle: .degrees(270),
            clockwise: false
        )
        path.addLine(to: CGPoint(x: 20, y: 12))
        path.addArc(
            center: CGPoint(x: 20, y: 14),
            radius: 2,
            startAngle: .degrees(270),
            endAngle: .degrees(360),
            clockwise: false
        )
        path.addLine(to: CGPoint(x: 22, y: 22))
        path.addLine(to: CGPoint(x: 20, y: 22))
        path.addLine(to: CGPoint(x: 20, y: 20))
        path.addLine(to: CGPoint(x: 4, y: 20))
        path.addLine(to: CGPoint(x: 4, y: 22))
        path.closeSubpath()

        // Fin 1: M6,14 A1,1 0 0,0 5,15 V17 A1,1 0 0,0 6,18 A1,1 0 0,0 7,17 V15 A1,1 0 0,0 6,14
        addFin(to: &path, cx: 6, topY: 14, bottomY: 18)

        // Fin 2
        addFin(to: &path, cx: 10, topY: 14, bottomY: 18)

        // Fin 3
        addFin(to: &path, cx: 14, topY: 14, bottomY: 18)

        // Fin 4
        addFin(to: &path, cx: 18, topY: 14, bottomY: 18)

        return path.applying(CGAffineTransform(scaleX: scaleX, y: scaleY))
    }

    /// Draws a rounded radiator fin (pill shape).
    private func addFin(to path: inout Path, cx: CGFloat, topY: CGFloat, bottomY: CGFloat) {
        let midTop = topY + 1 // center of top arc
        let midBot = bottomY - 1 // center of bottom arc
        path.move(to: CGPoint(x: cx, y: topY))
        path.addArc(
            center: CGPoint(x: cx, y: midTop),
            radius: 1,
            startAngle: .degrees(270),
            endAngle: .degrees(180),
            clockwise: true
        )
        path.addLine(to: CGPoint(x: cx - 1, y: midBot))
        path.addArc(
            center: CGPoint(x: cx, y: midBot),
            radius: 1,
            startAngle: .degrees(180),
            endAngle: .degrees(90),
            clockwise: true
        )
        path.addArc(
            center: CGPoint(x: cx, y: midBot),
            radius: 1,
            startAngle: .degrees(90),
            endAngle: .degrees(0),
            clockwise: true
        )
        path.addLine(to: CGPoint(x: cx + 1, y: midTop))
        path.addArc(
            center: CGPoint(x: cx, y: midTop),
            radius: 1,
            startAngle: .degrees(0),
            endAngle: .degrees(270),
            clockwise: true
        )
        path.closeSubpath()
    }
}
