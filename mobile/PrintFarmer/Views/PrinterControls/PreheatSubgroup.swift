import SwiftUI

/// Dimensions from the owner-selected Essential prototype, in native points.
enum EssentialControlsStyle {
    static let groupSpacing: CGFloat = 14
    static let groupPadding: CGFloat = 18
    static let columnSpacing: CGFloat = 24

    static func inputHeight(fontSize: CGFloat) -> CGFloat {
        max(45, ceil(UIFont.systemFont(ofSize: fontSize).lineHeight) + 16)
    }
}

struct EssentialControlHeading<Trailing: View>: View {
    let title: String
    var detail: String? = nil
    @ViewBuilder var trailing: Trailing
    @Environment(\.dynamicTypeSize) private var dynamicTypeSize

    var body: some View {
        let layout = dynamicTypeSize.isAccessibilitySize
            ? AnyLayout(VStackLayout(alignment: .leading, spacing: 4))
            : AnyLayout(HStackLayout(alignment: .firstTextBaseline, spacing: 12))
        layout {
            Text(title).font(.headline).foregroundStyle(Color.pfTextPrimary)
                .frame(maxWidth: .infinity, alignment: .leading)
                .accessibilityAddTraits(.isHeader)
            if let detail {
                Text(detail).font(.caption).foregroundStyle(Color.pfTextSecondary)
                    .fixedSize(horizontal: false, vertical: true)
            }
            trailing
        }
        .frame(minHeight: 22)
    }
}

extension EssentialControlHeading where Trailing == EmptyView {
    init(title: String, detail: String? = nil) {
        self.init(title: title, detail: detail) { EmptyView() }
    }
}

struct EssentialControlSeparator: View {
    var body: some View {
        Rectangle().fill(Color.pfBorder).frame(height: 1)
            .padding(.top, 16).padding(.bottom, 14)
            .accessibilityHidden(true)
    }
}

/// Rounded SwiftUI fields keep a 34-point UIKit editor even inside a taller
/// frame. Size the native editor itself so both touch and VoiceOver get 44pt.
struct ControlNumberField: UIViewRepresentable {
    let placeholder: String
    @Binding var text: String
    let label: String
    let identifier: String
    var hint: String?
    /// Defaults to the punctuation-capable keyboard so signed/jog fields that
    /// need "-" keep full access. Temperature fields pass `.decimalPad` so
    /// iOS shows the compact numeric keypad instead of the full keyboard.
    var keyboardType: UIKeyboardType = .numbersAndPunctuation
    /// Heat target values read as centred primary monospaced text (weight 500)
    /// on a filled, rounded chip so the value looks tappable (#3547).
    var stepperValueStyle = false
    /// Incrementing this asks the field to become first responder (Heat "Custom…").
    var focusRequest = 0
    /// When set, the keypad toolbar offers Cancel and Set instead of Done. A
    /// typed value applies only through Set (return `false` to keep editing);
    /// any other end of editing calls `onCancel` so the draft reverts.
    var onSubmit: (() -> Bool)?
    var onCancel: (() -> Void)?
    /// Called when the field gains focus, before any typing.
    var onBeginEditing: (() -> Void)?
    /// Replaces the spoken value (e.g. "Applying" while a heat row is in
    /// flight). Stepper fields otherwise speak their visible value.
    var accessibilityValueOverride: String?
    /// Right-aligns the value with a fixed trailing inset, so a wide field keeps
    /// the same gap to its unit whatever the value's length.
    var trailingAligned = false
    @ScaledMetric(relativeTo: .body) private var fontSize: CGFloat = 16
    @Environment(\.isEnabled) private var isEnabled

    func makeUIView(context: Context) -> UITextField {
        let field = UITextField()
        field.borderStyle = .roundedRect
        field.keyboardType = keyboardType
        field.returnKeyType = .done
        field.autocorrectionType = .no
        field.delegate = context.coordinator
        field.addTarget(context.coordinator, action: #selector(Coordinator.changed(_:)), for: .editingChanged)
        // Numeric-pad keyboards (.decimalPad, .numberPad, etc.) have no
        // Return key, so textFieldShouldReturn never fires. Add a "Done"
        // toolbar so the keyboard can still be dismissed.
        if onSubmit != nil {
            let toolbar = UIToolbar(frame: CGRect(x: 0, y: 0, width: 320, height: 44))
            let cancel = UIBarButtonItem(
                title: String(localized: "Cancel", comment: "Heat target keypad: discard the typed value"),
                style: .plain, target: context.coordinator, action: #selector(Coordinator.cancelTapped)
            )
            cancel.accessibilityIdentifier = "\(identifier).cancel"
            let set = UIBarButtonItem(
                title: String(localized: "Set", comment: "Heat target keypad: apply the typed value"),
                style: .done, target: context.coordinator, action: #selector(Coordinator.setTapped)
            )
            set.accessibilityIdentifier = "\(identifier).set"
            toolbar.items = [cancel, UIBarButtonItem(systemItem: .flexibleSpace), set]
            toolbar.sizeToFit()
            field.inputAccessoryView = toolbar
            context.coordinator.field = field
        } else if Self.padKeyboardTypesWithoutReturnKey.contains(keyboardType) {
            let toolbar = UIToolbar(frame: CGRect(x: 0, y: 0, width: 320, height: 44))
            let done = UIBarButtonItem(
                barButtonSystemItem: .done,
                target: context.coordinator,
                action: #selector(Coordinator.doneTapped)
            )
            toolbar.items = [UIBarButtonItem(systemItem: .flexibleSpace), done]
            toolbar.sizeToFit()
            field.inputAccessoryView = toolbar
            context.coordinator.field = field
        }
        return field
    }

    static let trailingInset: CGFloat = 6

    private static let padKeyboardTypesWithoutReturnKey: Set<UIKeyboardType> = [
        .numberPad, .decimalPad, .phonePad, .asciiCapableNumberPad
    ]

    func updateUIView(_ field: UITextField, context: Context) {
        context.coordinator.text = $text
        context.coordinator.onSubmit = onSubmit
        context.coordinator.onCancel = onCancel
        context.coordinator.onBeginEditing = onBeginEditing
        if field.text != text { field.text = text }
        // A stepper's placeholder is the live target, so it reads as the value.
        let valueFont = stepperValueStyle
            ? UIFont.monospacedSystemFont(ofSize: fontSize, weight: .medium)
            : UIFont.systemFont(ofSize: fontSize)
        field.attributedPlaceholder = NSAttributedString(
            string: placeholder,
            attributes: [
                .foregroundColor: UIColor(stepperValueStyle ? Color.pfTextPrimary : Color.pfTextSecondary),
                .font: valueFont
            ]
        )
        field.font = valueFont
        field.isEnabled = isEnabled
        field.accessibilityLabel = label
        field.accessibilityIdentifier = identifier
        field.accessibilityHint = hint
        field.accessibilityValue = accessibilityValueOverride
            ?? (stepperValueStyle ? (text.isEmpty ? placeholder : text) : nil)
        field.textColor = UIColor(Color.pfTextPrimary)
        field.textAlignment = trailingAligned ? .right : (stepperValueStyle ? .center : .natural)
        if trailingAligned {
            if field.rightView == nil {
                field.rightView = UIView(frame: CGRect(x: 0, y: 0, width: Self.trailingInset, height: 1))
                field.rightViewMode = .always
            }
        } else {
            field.rightView = nil
        }
        field.borderStyle = stepperValueStyle ? .none : .roundedRect
        field.backgroundColor = UIColor(Color.pfBackgroundTertiary)
        field.layer.cornerRadius = stepperValueStyle ? 8 : 0
        field.layer.cornerCurve = .continuous
        field.clipsToBounds = stepperValueStyle
        if focusRequest != context.coordinator.focusRequest {
            context.coordinator.focusRequest = focusRequest
            DispatchQueue.main.async { if field.isEnabled { field.becomeFirstResponder() } }
        }
    }

    func sizeThatFits(_ proposal: ProposedViewSize, uiView: UITextField, context: Context) -> CGSize? {
        CGSize(width: proposal.width ?? 200, height: EssentialControlsStyle.inputHeight(fontSize: fontSize))
    }

    func makeCoordinator() -> Coordinator { Coordinator(text: $text, focusRequest: focusRequest) }

    final class Coordinator: NSObject, UITextFieldDelegate {
        var text: Binding<String>
        var focusRequest: Int
        weak var field: UITextField?
        var onSubmit: (() -> Bool)?
        var onCancel: (() -> Void)?
        var onBeginEditing: (() -> Void)?
        private var submitted = false
        init(text: Binding<String>, focusRequest: Int = 0) {
            self.text = text
            self.focusRequest = focusRequest
        }
        @objc func changed(_ field: UITextField) { text.wrappedValue = field.text ?? "" }
        func textFieldShouldReturn(_ textField: UITextField) -> Bool {
            if onSubmit != nil {
                setTapped()
            } else {
                textField.resignFirstResponder()
            }
            return true
        }
        func textFieldDidBeginEditing(_ textField: UITextField) {
            submitted = false
            onBeginEditing?()
        }
        func textFieldDidEndEditing(_ textField: UITextField) {
            if !submitted { onCancel?() }
            submitted = false
        }
        @objc func doneTapped() {
            field?.resignFirstResponder()
        }
        @objc func setTapped() {
            guard let onSubmit, onSubmit() else { return }
            submitted = true
            field?.resignFirstResponder()
        }
        @objc func cancelTapped() {
            onCancel?()
            // Already reverted; keep the end-of-editing callback from repeating it.
            submitted = true
            field?.resignFirstResponder()
        }
    }
}

// MARK: - Heat target rows

private struct HeatTargetDebounceKey: EnvironmentKey {
    static let defaultValue: Duration = .milliseconds(700)
}

extension EnvironmentValues {
    /// Quiet period after the last ± tap before a heat row sends its target.
    var heatTargetDebounce: Duration {
        get { self[HeatTargetDebounceKey.self] }
        set { self[HeatTargetDebounceKey.self] = newValue }
    }
}

/// One settable heat row shared by Nozzle, Bed, Chamber, Heater Box and any
/// other writable target (#3547): name and measured value on the left, a
/// tap-to-edit target with − and + on the right. A row applies only its own
/// target: ± bursts collapse into one request after `heatTargetDebounce`, and
/// a typed value applies only on Set (Cancel reverts). The view model owns the
/// queued value, so in-flight state is a small spinner, never caption text,
/// and a draft being edited is never replaced by a settling send.
struct HeatTargetRow: View {
    let title: String
    let measured: Double?
    let identifier: String
    let fieldIdentifier: String
    let fieldLabel: String
    let fieldHint: String
    let stepLabel: (_ increasing: Bool) -> String
    let unit: SensorUnit
    let currentTarget: Double?
    /// Whether 0 reads as "Off" (heaters and sensor target temperatures that allow it).
    var allowsOff = true
    var isPending = false
    /// Target this row has queued in the view model and not yet settled.
    let queued: Double?
    /// Next target one step from `base` (queued or reported target), or nil.
    let next: (_ base: Double?, _ increasing: Bool) -> Double?
    /// Parses typed text; nil means blank (no change). Throws a user-facing message.
    let parse: (String) throws -> Double?
    /// Queues a value in the view model, sent after the delay (nil: now).
    let schedule: @MainActor (_ value: Double, _ delay: Duration?) -> Void
    var focusRequest = 0

    @State private var draft = ""
    @State private var isEditing = false
    @State private var inputError: String?
    @State private var rowWidth: CGFloat = .infinity
    @Environment(\.heatTargetDebounce) private var debounce
    @Environment(\.dynamicTypeSize) private var dynamicTypeSize

    static func draftText(_ value: Double) -> String {
        value.formatted(.number.locale(Locale(identifier: "en_US_POSIX")).grouping(.never))
    }

    static func placeholder(currentTarget: Double?, allowsOff: Bool = true) -> String {
        guard let currentTarget, currentTarget.isFinite else { return PrinterDetailViewModel.missingValuePlaceholder }
        if currentTarget == 0, allowsOff { return "Off" }
        return currentTarget.formatted(.number.precision(.fractionLength(0...1)))
    }

    /// Queued value first, then the reported target.
    private var displayedTarget: Double? { queued ?? currentTarget }

    private var showsOff: Bool {
        !isEditing && allowsOff && displayedTarget == 0
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            let isAccessibilitySize = dynamicTypeSize.isAccessibilitySize
            let layout = isAccessibilitySize
                ? AnyLayout(VStackLayout(alignment: .leading, spacing: 6))
                : AnyLayout(HStackLayout(spacing: 8))
            layout {
                VStack(alignment: .leading, spacing: 2) {
                    Text(title)
                        .font(.subheadline.weight(.medium))
                        .foregroundStyle(Color.pfTextPrimary)
                        .lineLimit(isAccessibilitySize ? nil : 1)
                        .minimumScaleFactor(isAccessibilitySize ? 1 : 0.8)
                        .fixedSize(horizontal: false, vertical: isAccessibilitySize)
                    // One line at every width so all rows share one height.
                    Text(measuredText)
                        .font(.caption.monospacedDigit())
                        .foregroundStyle(Color.pfTextSecondary)
                        .lineLimit(1)
                        .minimumScaleFactor(0.7)
                        .accessibilityIdentifier("\(identifier).measured")
                        .accessibilityLabel(measuredAccessibilityLabel)
                }
                .frame(maxWidth: .infinity, alignment: .leading)
                editor(fillsWidth: isAccessibilitySize, narrow: !isAccessibilitySize && rowWidth < Self.narrowWidth)
            }
            .frame(minHeight: 48)
            // Every row in a panel shares this width, so all rows switch together.
            .onGeometryChange(for: CGFloat.self) { $0.size.width } action: { rowWidth = $0 }
            if let inputError {
                Text(inputError).font(.footnote).foregroundStyle(Color.pfError)
                    .fixedSize(horizontal: false, vertical: true)
                    .accessibilityIdentifier("\(identifier).error")
            }
        }
    }

    /// Below this row width (for example a 256 pt column) the editor drops the
    /// separate unit slot and narrows the field so names stay readable.
    static let narrowWidth: CGFloat = 300

    private func editor(fillsWidth: Bool, narrow: Bool) -> some View {
        HStack(spacing: 4) {
            stepButton(increasing: false)
            ControlNumberField(
                placeholder: Self.placeholder(currentTarget: displayedTarget, allowsOff: allowsOff),
                text: Binding(get: { isEditing ? draft : "" }, set: { draft = $0; isEditing = true }),
                label: fieldLabel, identifier: fieldIdentifier, hint: fieldHint,
                keyboardType: .decimalPad, stepperValueStyle: true, focusRequest: focusRequest,
                onSubmit: submit, onCancel: cancelEditing, onBeginEditing: beginEditing,
                accessibilityValueOverride: isPending ? "Applying" : nil,
                trailingAligned: fillsWidth
            )
            // Accessibility sizes stack the row, so the field takes the free width.
            .frame(width: fillsWidth ? nil : (narrow ? 52 : 72))
            .frame(maxWidth: fillsWidth ? .infinity : nil)
            .overlay(alignment: .topTrailing) {
                if isPending {
                    ProgressView().controlSize(.mini)
                        .tint(Color.pfTextPrimary)
                        .padding(3)
                        .accessibilityHidden(true)
                }
            }
            // "Off" carries no unit; the slot keeps its width so columns align.
            if !narrow {
                Text(unit.symbol).font(.footnote).foregroundStyle(Color.pfTextSecondary)
                    .fixedSize()
                    .opacity(showsOff ? 0 : 1)
                    .accessibilityHidden(true)
            }
            stepButton(increasing: true)
        }
    }

    private func stepButton(increasing: Bool) -> some View {
        let target = next(displayedTarget, increasing)
        return ControlActionButton(
            title: "",
            identifier: "\(identifier).\(increasing ? "increase" : "decrease")",
            accessibilityTitle: stepLabel(increasing),
            hint: "Applies this target after a short pause.",
            compact: true,
            systemImage: increasing ? "plus" : "minus",
            minimumHeight: 44
        ) {
            guard let target = next(displayedTarget, increasing) else { return }
            inputError = nil
            schedule(target, debounce)
        }
        .frame(minWidth: 44, minHeight: 44)
        .fixedSize()
        .disabled(target == nil)
    }

    private var measuredText: String {
        guard let measured, measured.isFinite else { return "now \(PrinterDetailViewModel.missingValuePlaceholder)" }
        return "now \(measured.formatted(.number.precision(.fractionLength(0...1))))\(unit == .celsius ? "°" : unit.symbol)"
    }

    private var measuredAccessibilityLabel: String {
        guard let measured, measured.isFinite else { return "Current \(title) value not reported" }
        let spoken = unit == .celsius ? "degrees" : unit.spoken
        return "Current \(measured.formatted(.number.precision(.fractionLength(0...1))))\(spoken.isEmpty ? "" : " " + spoken)"
    }

    private func beginEditing() {
        draft = queued.map(Self.draftText) ?? ""
        isEditing = true
    }

    private func submit() -> Bool {
        do {
            if let value = try parse(draft) {
                schedule(value, nil)
            }
            inputError = nil
            isEditing = false
            draft = ""
            return true
        } catch {
            inputError = error.localizedDescription
            return false
        }
    }

    private func cancelEditing() {
        inputError = nil
        isEditing = false
        draft = ""
    }
}

// MARK: - Control tab environment overrides

private struct ControlActionCornerRadiusKey: EnvironmentKey {
    static let defaultValue: CGFloat? = nil
}

private struct PreheatPresetsFillRowKey: EnvironmentKey {
    static let defaultValue = false
}

private struct ControlActionSelectedFillKey: EnvironmentKey {
    static let defaultValue: Color? = nil
}

extension EnvironmentValues {
    /// Overrides `ControlActionButton`'s corner radius for surfaces that follow
    /// the DESIGN.md 8 pt ceiling without restyling every existing control host.
    var controlActionCornerRadius: CGFloat? {
        get { self[ControlActionCornerRadiusKey.self] }
        set { self[ControlActionCornerRadiusKey.self] = newValue }
    }

    /// Lays detail-mode preheat presets out as one row of equal-width buttons.
    var preheatPresetsFillRow: Bool {
        get { self[PreheatPresetsFillRowKey.self] }
        set { self[PreheatPresetsFillRowKey.self] = newValue }
    }

    /// Overrides the selected fill of segmented `ControlActionButton`s.
    var controlActionSelectedFill: Color? {
        get { self[ControlActionSelectedFillKey.self] }
        set { self[ControlActionSelectedFillKey.self] = newValue }
    }
}

struct ControlActionButton: UIViewRepresentable {
    let title: String
    var identifier = ""
    var accessibilityTitle: String?
    var hint: String?
    var selected = false
    var isDestructive = false
    var compact = false
    var prominent = false
    var systemImage: String?
    var value: String?
    var menu: UIMenu?
    var textSize: CGFloat = 16
    var segmented = false
    var tinted = false
    var minimumHeight: CGFloat = 45
    var matchesInputHeight = false
    var textOnly = false
    var isPending = false
    let action: () -> Void
    @ScaledMetric(relativeTo: .body) private var fontScale: CGFloat = 16
    @Environment(\.isEnabled) private var isEnabled
    @Environment(\.controlActionCornerRadius) private var cornerRadiusOverride
    @Environment(\.controlActionSelectedFill) private var selectedFillOverride

    func makeUIView(context: Context) -> UIButton {
        let button = NativeControlButton(type: .system)
        button.addTarget(context.coordinator, action: #selector(Coordinator.activate), for: .touchUpInside)
        return button
    }

    func updateUIView(_ button: UIButton, context: Context) {
        context.coordinator.action = action
        let fontSize = fontScale / 16 * textSize
        var configuration = UIButton.Configuration.plain()
        // An empty title still reserves label space and pushes a lone symbol off-centre.
        configuration.title = title.isEmpty ? nil : title
        configuration.titleAlignment = menu == nil ? .center : .leading
        configuration.image = menu == nil ? systemImage.flatMap { UIImage(systemName: $0) } : nil
        // Pending shows an overlay spinner over the hidden content rather than
        // `showsActivityIndicator`, so the content layout is unchanged when it settles.
        configuration.preferredSymbolConfigurationForImage = .init(pointSize: fontScale, weight: .regular)
        configuration.imagePlacement = menu == nil ? .leading : .trailing
        configuration.imagePadding = menu == nil ? 0 : 8
        configuration.buttonSize = .large
        if compact {
            configuration.contentInsets = .init(top: 8, leading: 8, bottom: 8, trailing: 8)
        }
        if menu != nil { configuration.contentInsets.trailing = 30 }
        configuration.background.cornerRadius = cornerRadiusOverride ?? (segmented ? 8 : 10)
        configuration.baseForegroundColor = UIColor(
            isPending ? Color.clear :
            !isEnabled ? Color.pfTextTertiary :
                isDestructive ? Color.pfError : prominent ? Color.pfButtonPrimaryText :
                tinted ? Color.pfButtonPrimary : Color.pfTextPrimary
        )
        configuration.background.backgroundColor = UIColor(
            textOnly ? Color.clear : !isEnabled ? Color.pfBackgroundTertiary : prominent ? Color.pfButtonPrimary :
                segmented && !selected ? Color.clear :
                segmented ? (selectedFillOverride ?? Color.pfBackground) : Color.pfBackground
        )
        configuration.background.strokeColor = UIColor(Color.pfBorder)
        configuration.background.strokeWidth = segmented || prominent || textOnly ? 0 : 1
        let font = UIFont.systemFont(ofSize: fontSize, weight: prominent || selected || textOnly ? .semibold : .regular)
        configuration.titleTextAttributesTransformer = UIConfigurationTextAttributesTransformer {
            var attributes = $0
            attributes.font = font
            return attributes
        }
        button.configuration = configuration
        button.menu = menu
        if let button = button as? NativeControlButton {
            button.pendingIndicator.color = UIColor(Color.pfTextSecondary)
            if isPending {
                button.pendingIndicator.startAnimating()
            } else {
                button.pendingIndicator.stopAnimating()
            }
            button.menuChevron.image = menu == nil ? nil : UIImage(
                systemName: "chevron.down", withConfiguration: UIImage.SymbolConfiguration(pointSize: fontScale * 0.75)
            )
            button.menuChevron.tintColor = UIColor(isEnabled ? Color.pfTextPrimary : Color.pfTextTertiary)
        }
        button.showsMenuAsPrimaryAction = menu != nil
        button.contentHorizontalAlignment = menu == nil ? .center : .leading
        button.titleLabel?.numberOfLines = 0
        button.isSelected = selected
        button.isEnabled = isEnabled
        button.accessibilityLabel = accessibilityTitle ?? title
        button.accessibilityHint = hint
        button.accessibilityValue = value
        button.accessibilityIdentifier = identifier
    }

    func sizeThatFits(_ proposal: ProposedViewSize, uiView: UIButton, context: Context) -> CGSize? {
        let size = uiView.sizeThatFits(CGSize(
            width: proposal.width ?? .greatestFiniteMagnitude, height: .greatestFiniteMagnitude
        ))
        // A fractional parent origin can round a nominal 44pt child below 44.
        let height = matchesInputHeight
            ? EssentialControlsStyle.inputHeight(fontSize: fontScale)
            : max(minimumHeight, ceil(size.height))
        return CGSize(width: max(44, proposal.width ?? size.width), height: height)
    }

    func makeCoordinator() -> Coordinator { Coordinator(action: action) }

    final class Coordinator: NSObject {
        var action: () -> Void
        init(action: @escaping () -> Void) { self.action = action }
        @objc func activate() { action() }
    }
}

private final class NativeControlButton: UIButton {
    let menuChevron = UIImageView()
    let pendingIndicator = UIActivityIndicatorView(style: .medium)

    override init(frame: CGRect) {
        super.init(frame: frame)
        pendingIndicator.hidesWhenStopped = true
        pendingIndicator.isUserInteractionEnabled = false
        pendingIndicator.isAccessibilityElement = false
        pendingIndicator.translatesAutoresizingMaskIntoConstraints = false
        addSubview(pendingIndicator)
        menuChevron.isAccessibilityElement = false
        menuChevron.contentMode = .scaleAspectFit
        menuChevron.translatesAutoresizingMaskIntoConstraints = false
        addSubview(menuChevron)
        NSLayoutConstraint.activate([
            menuChevron.trailingAnchor.constraint(equalTo: trailingAnchor, constant: -10),
            menuChevron.centerYAnchor.constraint(equalTo: centerYAnchor),
            menuChevron.widthAnchor.constraint(equalToConstant: 16),
            menuChevron.heightAnchor.constraint(equalToConstant: 16),
            pendingIndicator.centerXAnchor.constraint(equalTo: centerXAnchor),
            pendingIndicator.centerYAnchor.constraint(equalTo: centerYAnchor)
        ])
    }

    required init?(coder: NSCoder) { return nil }

    override func layoutSubviews() {
        super.layoutSubviews()
        recentreContent()
    }

    /// UIKit can lay a configuration's content out at its size-to-fit origin after SwiftUI
    /// re-measures a flexible representable, leaving centred titles stuck at the leading
    /// inset (#3496 N09). Re-centre the content block rather than rebuilding the button,
    /// so the tapped control keeps VoiceOver focus (R3514-H07).
    private func recentreContent() {
        guard contentHorizontalAlignment == .center, let content = contentFrame() else { return }
        let dx = (bounds.midX - content.midX).rounded()
        let dy = (bounds.midY - content.midY).rounded()
        guard abs(dx) >= 1 || abs(dy) >= 1 else { return }
        for view in [titleLabel, imageView].compactMap({ $0 }) where !view.isHidden {
            view.frame = view.frame.offsetBy(dx: dx, dy: dy)
        }
    }

    private func contentFrame() -> CGRect? {
        [titleLabel, imageView].compactMap { $0 }
            .filter { !$0.isHidden && $0.bounds.width > 0 && $0.superview != nil }
            .map { $0.convert($0.bounds, to: self) }
            .reduce(nil) { $0?.union($1) ?? $1 }
    }
}

/// Essential Heat group (#2593): paired target inputs, one guarded setter,
/// compact presets and Cool down.
struct PreheatSubgroup: View {

    @ObservedObject var viewModel: PrinterControlsViewModel
    var compactForDetail = false

    /// Transient caption shown when the user taps a button while controls are
    /// disabled (offline / mid-print). Cleared after a few seconds. Phone
    /// equivalent of the `.help()` tooltip that appears on iPad/Mac.
    @State private var disabledTapMessage: String?
    /// Bumped by the More menu's "Custom…" to focus the Nozzle target field.
    @State private var nozzleFocusRequest = 0

    @Environment(\.horizontalSizeClass) private var horizontalSizeClass
    @Environment(\.dynamicTypeSize) private var dynamicTypeSize
    @Environment(\.preheatPresetsFillRow) private var presetsFillRow
    @Environment(\.controlActionCornerRadius) private var cornerRadiusOverride

    /// Cool down is an icon-only snowflake in every layout; VoiceOver still
    /// speaks "Cool down" via `accessibilityLabel(preset:isPending:)`.
    static func detailPresetSymbol(_ preset: PreheatPreset) -> String? {
        preset == .coolDown ? "snowflake" : nil
    }

    /// Fixed display order matching the UX spec.
    static let presets: [PreheatPreset] = PreheatPreset.rowMaterials + [.coolDown]

    /// Cool down is always offered: heater shutdown when supported, else 0/0 targets while idle.
    private var visiblePresets: [PreheatPreset] { Self.presets }

    /// Presets require explicit hotend-control evidence, including Cool Down.
    static func isVisible(capabilities: PrinterBackendCapabilities?) -> Bool {
        capabilities?.supportsTemperatureControl == true
    }

    var body: some View {
        content
    }

    private var content: some View {
        VStack(alignment: .leading, spacing: 0) {
            // Presets sit above the heat rows in every layout (mockup v3).
            if compactForDetail {
                // The Heat segment already names this panel, so detail mode shows only presets.
                Group {
                    if presetsFillRow {
                        filledPresetRow
                    } else {
                        ViewThatFits(in: .horizontal) {
                            detailPresets
                            grid
                        }
                    }
                }
                .frame(maxWidth: .infinity, alignment: .leading)
                .padding(.bottom, 8)
            } else {
                heatHeading.padding(.bottom, 14)
                grid.padding(.bottom, 14)
            }
            PrinterControlCommandFeedback(viewModel: viewModel, section: .heat)

            IndividualHeaterControls(viewModel: viewModel, nozzleFocusRequest: nozzleFocusRequest)

            if !compactForDetail {
                Text("Hotend max \(viewModel.maximum(for: .hotend).map { $0.formatted() + "°C" } ?? "unknown") · Bed max \(viewModel.maximum(for: .bed).map { $0.formatted() + "°C" } ?? "unknown").")
                    .font(.footnote.monospacedDigit()).foregroundStyle(Color.pfTextSecondary)
                    .padding(.top, 10)
            }
            if let message = blockedReasonMessage {
                Text(message)
                    .font(.footnote)
                    .foregroundStyle(Color.pfTextSecondary)
                    .transition(.opacity)
                    .accessibilityAddTraits(.isStaticText)
            }
        }
    }

    private var heatHeading: some View {
        Text("Heat")
            .font(.headline)
            .foregroundStyle(Color.pfTextPrimary)
            .accessibilityAddTraits(.isHeader)
    }

    private var detailPresets: some View {
        HStack(spacing: 4) {
            ForEach(visiblePresets, id: \.self) { preset in
                // Equal-width slots, so the snowflake matches the materials.
                button(for: preset, compactForDetail: true)
                    .frame(maxWidth: .infinity)
            }
        }
    }

    enum PresetSlot: Hashable {
        case preset(PreheatPreset)
        case more
    }

    /// Mockup v3: PLA · PETG · ABS · More · Cool down in one row; from
    /// xxLarge (including every accessibility size) the materials wrap above
    /// More + Cool down, so More stays reachable at every text size.
    static func presetRows(dynamicTypeSize: DynamicTypeSize, wrapped: Bool = false) -> [[PresetSlot]] {
        let materials = PreheatPreset.rowMaterials.map(PresetSlot.preset)
        let trailing: [PresetSlot] = [.more, .preset(.coolDown)]
        return wrapped || dynamicTypeSize >= .xxLarge ? [materials, trailing] : [materials + trailing]
    }

    /// One row when every slot fits at its natural width; otherwise (e.g. a
    /// 256pt column) two rows, so a temperature pair is never squeezed or split.
    private var filledPresetRow: some View {
        ViewThatFits(in: .horizontal) {
            presetRowsView(Self.presetRows(dynamicTypeSize: dynamicTypeSize))
            presetRowsView(Self.presetRows(dynamicTypeSize: dynamicTypeSize, wrapped: true))
        }
    }

    private func presetRowsView(_ rows: [[PresetSlot]]) -> some View {
        VStack(spacing: 8) {
            ForEach(rows, id: \.self) { row in
                // Every slot stretches to the tallest one, so More matches
                // Cool down when accessibility sizes grow the row.
                HStack(spacing: 8) {
                    ForEach(row, id: \.self) { slot in
                        switch slot {
                        case .preset(let preset):
                            button(for: preset, compactForDetail: true)
                                .frame(maxWidth: .infinity, maxHeight: .infinity)
                        case .more:
                            moreMenu.frame(maxWidth: .infinity, maxHeight: .infinity)
                        }
                    }
                }
                .fixedSize(horizontal: false, vertical: true)
            }
        }
    }

    static func menuItemSingleLineText(_ material: String, temperatures: String) -> String {
        "\(material)  \(temperatures)"
    }

    static func menuItemTemperatureText(_ preset: PreheatPreset, hasBed: Bool) -> String {
        hasBed ? "\(Int(preset.hotend))° · \(Int(preset.bed))°" : "\(Int(preset.hotend))°"
    }

    /// Native menu of extra materials, then "Custom…" which focuses the
    /// existing Nozzle target field rather than opening a new editor.
    private var customMenuItem: some View {
        Button {
            nozzleFocusRequest += 1
        } label: {
            Label("Custom…", systemImage: "slider.horizontal.3")
        }
        .accessibilityIdentifier("printer.controls.preheat.custom")
    }

    private var moreMenu: some View {
        let canControl = viewModel.canRunPreheat(.pla)
        let disabledReason = viewModel.preheatMenuBlockedReason
        let isEnabled = disabledReason == nil
        // The native menu doesn't scroll at accessibility sizes, so Custom…
        // moves to the top there to stay reachable.
        let customFirst = dynamicTypeSize.isAccessibilitySize
        return Menu {
            if customFirst {
                customMenuItem
                Divider()
            }
            Section {
                ForEach(PreheatPreset.menuMaterials, id: \.self) { preset in
                    Button {
                        handleTap(preset: preset, canControl: canControl)
                    } label: {
                        let temperatures = Self.menuItemTemperatureText(preset, hasBed: viewModel.supports(.bed))
                        // One line per item keeps more of the menu on screen.
                        if dynamicTypeSize.isAccessibilitySize {
                            Text(Self.menuItemSingleLineText(preset.displayLabel, temperatures: temperatures))
                        } else {
                            Text(preset.displayLabel)
                            Text(temperatures)
                        }
                    }
                    .disabled(viewModel.preheatBlockedReason(preset) != nil)
                    .accessibilityIdentifier("printer.controls.preheat.\(preset.rawValue)")
                }
            }
            if !customFirst {
                Divider()
                customMenuItem
            }
        } label: {
            HStack(spacing: 3) {
                Text("More")
                    .font(.caption.weight(.semibold))
                    .lineLimit(1)
                    .minimumScaleFactor(0.8)
                Image(systemName: "chevron.down")
                    .font(.caption2.weight(.semibold))
                    .accessibilityHidden(true)
            }
            .foregroundStyle(isEnabled ? Color.accentColor : Color.pfTextTertiary)
            .frame(maxWidth: .infinity, minHeight: 48, maxHeight: .infinity)
            .padding(.horizontal, 4)
            .background(
                isEnabled ? Color.pfBackground : Color.pfBackgroundTertiary,
                in: RoundedRectangle(cornerRadius: cornerRadiusOverride ?? 10, style: .continuous)
            )
            .overlay {
                RoundedRectangle(cornerRadius: cornerRadiusOverride ?? 10, style: .continuous)
                    .strokeBorder(Color.pfBorder)
            }
            .contentShape(Rectangle())
        }
        .disabled(!isEnabled)
        .accessibilityLabel(String(localized: "More materials", comment: "VoiceOver: Heat More menu button"))
        .accessibilityHint(isEnabled
            ? String(localized: "Shows more preheat materials and a custom nozzle target.", comment: "VoiceOver: Heat More menu hint")
            : disabledReason ?? "")
        .accessibilityIdentifier("printer.controls.preheat.more")
    }

    @ViewBuilder
    private var grid: some View {
        let columns = gridColumns
        // Cool down sits in the same row as ABS (spec update): all four
        // presets share one grid so they render at identical dimensions.
        LazyVGrid(columns: columns, alignment: .leading, spacing: 8) {
            ForEach(visiblePresets, id: \.self) { preset in
                button(for: preset)
            }
        }
    }

    private var gridColumns: [GridItem] {
        let count: Int
        if dynamicTypeSize.isAccessibilitySize {
            count = 1
        } else if dynamicTypeSize >= .xxLarge {
            count = 3
        } else {
            count = 4
        }
        return Array(repeating: GridItem(.flexible(), spacing: 8), count: count)
    }

    @ViewBuilder
    private func button(for preset: PreheatPreset, compactForDetail: Bool = false) -> some View {
        let isPending: Bool = {
            if case let .preheat(pendingPreset, _, _) = viewModel.pendingCommand?.kind {
                return pendingPreset == preset
            }
            return false
        }()
        let canControl = viewModel.canRunPreheat(preset)
        let limitsReason = viewModel.preheatBlockedReason(preset)
        // Per spec §3.1 single-flight queue: if any preheat command is in
        // flight, *all* preheat siblings disable so the user can't stack
        // burst commands like "PLA then ABS".
        let isAnyPreheatInProgress = viewModel.isExecuting
        let isInteractive = canControl && !isAnyPreheatInProgress && limitsReason == nil

        let hasError = isErrored(preset: preset)
        Button {
            handleTap(preset: preset, canControl: canControl)
        } label: {
            buttonLabel(preset: preset, isPending: isPending, compactForDetail: compactForDetail)
        }
        .buttonStyle(PreheatButtonStyle(
            preset: preset, isEnabled: isInteractive, isPending: isPending,
            cornerRadius: cornerRadiusOverride ?? 10
        ))
        .disabled(isAnyPreheatInProgress || limitsReason != nil || isBlockedWithoutTapReveal(canControl: canControl))
        // On compact layouts we keep blocked buttons tappable so the user can
        // reveal the disabled reason; regular width shows the reason inline.
        .disabledControlStyle(isDisabled: !isInteractive && !isPending, cornerRadius: 8)
        .errorBorderHighlight(isActive: hasError, cornerRadius: 8)
        .accessibilityLabel(accessibilityLabel(preset: preset, isPending: isPending))
        .accessibilityHint(limitsReason ?? accessibilityHint(preset: preset, canControl: canControl, hasError: hasError))
        .accessibilityValue(accessibilityValue(isPending: isPending, hasError: hasError))
        .accessibilityAddTraits(isPending ? .updatesFrequently : [])
        .help(limitsReason ?? viewModel.preheatAccessReason(preset) ?? "")
    }

    /// Nozzle and Bed rows. Each heater applies on its own: ± taps send after
    /// a short debounce and typed values send only on Set (#3547).
    struct IndividualHeaterControls: View {
        @ObservedObject var viewModel: PrinterControlsViewModel
        var nozzleFocusRequest = 0

        var body: some View {
            VStack(alignment: .leading, spacing: 8) {
                HeaterTargetEditor(viewModel: viewModel, heater: .hotend, focusRequest: nozzleFocusRequest)
                HeaterTargetEditor(viewModel: viewModel, heater: .bed)
            }
            // Another row's send never disables these rows: disabling a
            // focused field would resign it and discard the draft.
            .disabled(!viewModel.canControl)
        }
    }

    struct HeaterTargetEditor: View {
        @ObservedObject var viewModel: PrinterControlsViewModel
        let heater: Heater
        var focusRequest = 0

        private var currentTarget: Double? {
            heater == .hotend ? viewModel.printer.hotendTarget : viewModel.printer.bedTarget
        }

        private var title: String { heater == .hotend ? "Nozzle" : "Bed" }

        static func steppedTarget(draft: String, current: Double?, maximum: Double?, delta: Double) -> Double? {
            let value = draft.isEmpty ? current : Double(draft)
            guard let value, value.isFinite, value >= 0, delta.isFinite else { return nil }
            let upper = maximum.flatMap { $0.isFinite && $0 > 0 ? $0 : nil } ?? Double(Int32.max)
            guard value <= upper else { return nil }
            let next = min(max(value + delta, 0), upper)
            return next == value ? nil : next
        }

        private var isPending: Bool {
            switch viewModel.pendingCommand?.kind {
            case .heater(let pending, _): pending == heater
            default: false
            }
        }

        var body: some View {
            let measured = heater == .hotend ? viewModel.printer.hotendTemp : viewModel.printer.bedTemp
            let maximum = viewModel.maximum(for: heater).map(Double.init)
            HeatTargetRow(
                title: title,
                measured: measured,
                identifier: "printer.controls.\(heater.rawValue)",
                fieldIdentifier: "printer.controls.\(heater.rawValue).target",
                fieldLabel: "\(heater.title) target in degrees Celsius",
                fieldHint: targetHint,
                stepLabel: { "\($0 ? "Increase" : "Decrease") \(heater.title) target by 5 degrees" },
                unit: .celsius,
                currentTarget: viewModel.supports(heater) ? currentTarget : nil,
                isPending: isPending,
                queued: viewModel.queuedHeatTargets[heater.rawValue],
                next: { base, increasing in
                    Self.steppedTarget(draft: "", current: base, maximum: maximum, delta: increasing ? 5 : -5)
                },
                parse: { text in
                    guard let value = try ControlNumberInput.heaterTarget(text) else { return nil }
                    if let message = viewModel.heaterTargetError(heater, target: value) {
                        throw PrinterControlError.invalidRequest(message)
                    }
                    return value
                },
                schedule: { value, delay in
                    viewModel.scheduleHeatTarget(key: heater.rawValue, title: title, value: value, after: delay) {
                        await $0.setHeaterTarget(heater, target: value)
                    }
                },
                focusRequest: focusRequest
            )
            .disabled(!viewModel.supports(heater))
        }

        private var targetHint: String {
            guard viewModel.supports(heater) else { return "\(heater.title) control is unavailable." }
            let limit = viewModel.maximum(for: heater).map { "Maximum \($0) degrees. " }
                ?? "Reported maximum unavailable; firmware limits apply. "
            return "Double tap to enter a target, then choose Set. " + limit
                + ControlNumberInput.heaterPrecisionMessage + " Zero switches it off."
        }
    }

    /// Genuine blocked reasons only; an in-flight request just disables the rows (#3547).
    private var blockedReasonMessage: String? {
        if horizontalSizeClass == .regular {
            return viewModel.heatRowBlockedReason
        }
        return disabledTapMessage
    }

    private var shouldRevealDisabledTooltipOnTap: Bool {
        horizontalSizeClass != .regular
    }

    private func isBlockedWithoutTapReveal(canControl: Bool) -> Bool {
        !canControl && !shouldRevealDisabledTooltipOnTap
    }

    /// One shared tile in every layout (#3555): the name over a mono
    /// "200/60" caption that never wraps, or an icon-only snowflake.
    private func buttonLabel(
        preset: PreheatPreset,
        isPending: Bool,
        compactForDetail: Bool = false
    ) -> some View {
        VStack(spacing: 3) {
            if let symbol = Self.detailPresetSymbol(preset) {
                Image(systemName: symbol)
                    .font(.body.weight(.semibold))
                    .accessibilityHidden(true)
            } else {
                Text(preset.displayLabel)
                    .font(.caption.weight(.semibold))
                    .foregroundStyle(isPending ? Color.pfPresetHeatingName : Color.primary)
                    .lineLimit(1)
                    .minimumScaleFactor(0.8)
                Text(Self.compactTargetsText(preset, hasBed: viewModel.supports(.bed)))
                    .font(.system(.caption2, design: .monospaced))
                    .foregroundStyle(Color.pfTextSecondary)
                    .lineLimit(1)
                    .minimumScaleFactor(0.6)
                    .accessibilityHidden(true)
            }
        }
        // Height comes from the type metrics alone, never the rendered label, so
        // every preset matches Cool down; accessibility sizes let the row grow.
        .frame(height: dynamicTypeSize.isAccessibilitySize ? nil : 48)
        .frame(minHeight: 48)
        .frame(maxWidth: .infinity, maxHeight: compactForDetail && presetsFillRow ? .infinity : nil)
        // The preset name stays visible during a send; a small corner
        // spinner in the heating accent marks it in flight.
        .overlay(alignment: .topTrailing) {
            if isPending {
                ProgressView()
                    .controlSize(.mini)
                    .tint(Color.pfPresetHeatingName)
                    .padding(4)
                    .accessibilityHidden(true)
            }
        }
    }

    /// Preset caption, e.g. "200/60" (nozzle only without a heated bed), at
    /// every text size; it scales down rather than wrapping.
    static func compactTargetsText(_ preset: PreheatPreset, hasBed: Bool) -> String {
        return hasBed ? "\(Int(preset.hotend))/\(Int(preset.bed))" : "\(Int(preset.hotend))"
    }

    private func handleTap(preset: PreheatPreset, canControl: Bool) {
        guard viewModel.preheatBlockedReason(preset) == nil else { return }
        guard canControl else {
            // Disabled tap: surface the blocked reason as a transient caption
            // (phone) and let `.help()` cover iPad/Mac hover.
            guard let message = viewModel.heatRowBlockedReason else { return }
            withAnimation(.easeInOut(duration: 0.15)) {
                disabledTapMessage = message
            }
            // Auto-clear after a moment so it doesn't linger.
            Task { @MainActor in
                try? await Task.sleep(nanoseconds: 3_000_000_000)
                if disabledTapMessage == message {
                    withAnimation(.easeInOut(duration: 0.15)) {
                        disabledTapMessage = nil
                    }
                }
            }
            return
        }
        disabledTapMessage = nil
        Task { await viewModel.preheat(preset) }
    }

    // MARK: - Accessibility strings

    // Exposed internal (not private) so accessibility tests can validate
    // that label and hint strings match spec §4.1 exactly.
    func accessibilityLabel(preset: PreheatPreset, isPending: Bool) -> String {
        if isPending {
            return preset == .coolDown
                ? String(localized: "Cooling down, in progress", comment: "VoiceOver: Cool Down while in flight")
                : String(localized: "Preheat for \(preset.spokenName), in progress", comment: "VoiceOver: preheat button while in flight")
        }
        if preset == .coolDown {
            return String(localized: "Cool down", comment: "VoiceOver: Cool Down idle label per spec §4.1")
        }
        return String(localized: "Preheat for \(preset.spokenName)", comment: "VoiceOver: preheat button idle label per spec §4.1")
    }

    func accessibilityHint(preset: PreheatPreset, canControl: Bool, hasError: Bool) -> String {
        if hasError, let message = viewModel.lastError?.message {
            return String(localized: "Failed: \(message). Double tap to retry.", comment: "VoiceOver hint when last preheat command failed")
        }
        if !canControl {
            return String(localized: "Disabled while printing.", comment: "VoiceOver disabled hint per spec §4.1")
        }
        return preset.a11yHint(hasBed: viewModel.supports(.bed), turnsOffHeaters: viewModel.supportsHeaterShutdown)
    }

    func accessibilityValue(isPending: Bool, hasError: Bool) -> String {
        if isPending { return String(localized: "Pending", comment: "VoiceOver value while a control command is in flight per spec §4.1") }
        if hasError { return String(localized: "Failed", comment: "VoiceOver value when last command failed") }
        return ""
    }

    private func isErrored(preset: PreheatPreset) -> Bool {
        guard let last = viewModel.lastError else { return false }
        if case let .preheat(errPreset, _, _) = last.command.kind, errPreset == preset { return true }
        return false
    }
}

// MARK: - Preset display helpers

private extension PreheatPreset {
    var displayLabel: String { definition.name }

    var spokenName: String { self == .coolDown ? "cool down" : definition.name }
}

extension PreheatPreset {
    /// VoiceOver idle hint per spec §4.1. Omits bed segment when printer has no heated bed.
    /// Cool down describes the path it takes: heater shutdown, or the 0° target fallback.
    func a11yHint(hasBed: Bool, turnsOffHeaters: Bool) -> String {
        switch self {
        case .coolDown:
            if turnsOffHeaters {
                return String(localized: "Turns off all heaters.", comment: "VoiceOver: Cool Down hint when the backend supports heater shutdown")
            }
            return hasBed
                ? String(localized: "Sets hotend and bed to 0 degrees.", comment: "VoiceOver: Cool Down idle hint")
                : String(localized: "Sets hotend to 0 degrees.", comment: "VoiceOver: Cool Down hotend-only hint")
        case .pla, .petg, .abs, .asa, .tpu, .pc, .nylon:
            if hasBed {
                return String(localized: "Sets hotend to \(Int(hotend)) degrees, bed to \(Int(bed)) degrees.", comment: "VoiceOver: preheat idle hint with bed")
            } else {
                return String(localized: "Sets hotend to \(Int(hotend)) degrees.", comment: "VoiceOver: preheat idle hint, hotend-only")
            }
        }
    }
}

// MARK: - Button style

extension Color {
    /// In-flight preset tile (mockup v3 `.pbtn.heating`): a subtle accent tint
    /// under an accent border. The name and the pfTextSecondary caption both
    /// stay at or above 4.5:1 on this fill in light and dark.
    static let pfPresetHeatingFill = adaptive(light: "#ecfdf5", dark: "#0b2a2a")
    static let pfPresetHeatingName = adaptive(light: "#047857", dark: "#34d399")
}

/// Preheat-specific button style with per-state visual treatment per spec §2.4.
private struct PreheatButtonStyle: ButtonStyle {
    let preset: PreheatPreset
    let isEnabled: Bool
    let isPending: Bool
    let cornerRadius: CGFloat

    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .padding(.horizontal, 4)
            .background(background)
            .foregroundStyle(foreground)
            .overlay(border)
            .clipShape(RoundedRectangle(cornerRadius: cornerRadius, style: .continuous))
            .opacity(configuration.isPressed && isEnabled ? 0.7 : 1.0)
            .contentShape(Rectangle())
    }

    private var background: some View {
        Group {
            if isPending {
                Color.pfPresetHeatingFill
            } else if !isEnabled {
                Color.pfBackgroundTertiary
            } else {
                Color.pfBackground
            }
        }
    }

    private var foreground: Color {
        if !isEnabled && !isPending { return Color.pfTextTertiary }
        return Color.pfTextPrimary
    }

    @ViewBuilder
    private var border: some View {
        RoundedRectangle(cornerRadius: cornerRadius, style: .continuous)
            .strokeBorder(
                isPending ? Color.pfAccent : Color.pfBorder,
                lineWidth: isPending ? 1.5 : 1
            )
    }
}

// MARK: - Previews

#Preview("Default — full caps, idle") {
    PreheatSubgroupPreviewHost(
        capabilities: .init(
            supportsMovement: true,
            supportsTemperatureControl: true,
            supportsBedTemperature: true,
            supportsFanControl: true,
            supportsHoming: true,
            supportedAxes: ["X", "Y", "Z"]
        ),
        printerState: "ready",
        isOnline: true,
        startPendingPreset: nil
    )
    .padding(16)
    .background(Color.pfCard)
    .preferredColorScheme(.dark)
}

#Preview("Pending — PLA in flight") {
    PreheatSubgroupPreviewHost(
        capabilities: .init(
            supportsMovement: true,
            supportsTemperatureControl: true,
            supportsBedTemperature: true,
            supportsFanControl: true,
            supportsHoming: true,
            supportedAxes: ["X", "Y", "Z"]
        ),
        printerState: "ready",
        isOnline: true,
        startPendingPreset: .pla
    )
    .padding(16)
    .background(Color.pfCard)
    .preferredColorScheme(.dark)
}

#Preview("Disabled — printing") {
    PreheatSubgroupPreviewHost(
        capabilities: .init(
            supportsMovement: true,
            supportsTemperatureControl: true,
            supportsBedTemperature: true,
            supportsFanControl: true,
            supportsHoming: true,
            supportedAxes: ["X", "Y", "Z"]
        ),
        printerState: "printing",
        isOnline: true,
        startPendingPreset: nil
    )
    .padding(16)
    .background(Color.pfCard)
    .preferredColorScheme(.dark)
}

#Preview("Hotend only — FlashForge") {
    PreheatSubgroupPreviewHost(
        capabilities: .init(
            supportsMovement: true,
            supportsTemperatureControl: true,
            supportsBedTemperature: false,
            supportsFanControl: false,
            supportsHoming: true,
            supportedAxes: ["X", "Y", "Z"]
        ),
        printerState: "ready",
        isOnline: true,
        startPendingPreset: nil
    )
    .padding(16)
    .background(Color.pfCard)
    .preferredColorScheme(.dark)
}

#Preview("Hidden — no temp control") {
    PreheatSubgroupPreviewHost(
        capabilities: .init(
            supportsMovement: false,
            supportsTemperatureControl: false,
            supportsBedTemperature: false,
            supportsFanControl: false,
            supportsHoming: false,
            supportedAxes: []
        ),
        printerState: "ready",
        isOnline: true,
        startPendingPreset: nil
    )
    .padding(16)
    .background(Color.pfCard)
    .overlay(
        Text("(Subgroup hidden — no temperature control)")
            .font(.caption)
            .foregroundStyle(.secondary)
    )
    .preferredColorScheme(.dark)
}

/// Preview-only host that wires a fake `PrinterControlsViewModel` so the
/// canvas renders without needing the real network stack.
private struct PreheatSubgroupPreviewHost: View {
    let capabilities: PrinterBackendCapabilities
    let printerState: String
    let isOnline: Bool
    let startPendingPreset: PreheatPreset?

    var body: some View {
        let vm = PreheatSubgroupPreviewFactory.makeViewModel(
            capabilities: capabilities,
            printerState: printerState,
            isOnline: isOnline,
            startPendingPreset: startPendingPreset
        )
        return PreheatSubgroup(viewModel: vm)
    }
}

private enum PreheatSubgroupPreviewFactory {
    @MainActor
    static func makeViewModel(
        capabilities: PrinterBackendCapabilities,
        printerState: String,
        isOnline: Bool,
        startPendingPreset: PreheatPreset?
    ) -> PrinterControlsViewModel {
        let printer = Printer.previewFallbackPrinter(state: printerState, isOnline: isOnline)
        let service = PreheatSubgroupPreviewService(capabilities: capabilities, hangForever: startPendingPreset != nil)
        let serverID = UUID()
        let composition = PrinterControlsComposition(
            identity: .init(serverID: serverID, generation: 0, revision: 0), printerService: service
        )
        let vm = PrinterControlsViewModel(composition: composition, printer: printer)
        vm.configureAccess(serverID: serverID) { nil }
        // Asynchronously load preview capabilities immediately so the canvas
        // settles on the configured visibility state.
        vm.previewLoadCapabilitiesAsync()
        if let preset = startPendingPreset {
            // Kick off a preheat that the preview service will never resolve,
            // pinning the button in pending state.
            Task { @MainActor in await vm.preheat(preset) }
        }
        return vm
    }
}

/// Minimal `PrinterServiceProtocol` shim used by previews. Returns the canned
/// capabilities and either resolves immediately or hangs forever (for the
/// pending preview). Not used outside `#Preview`.
private final class PreheatSubgroupPreviewService: PrinterServiceProtocol, @unchecked Sendable {
    private let capabilities: PrinterBackendCapabilities
    private let hangForever: Bool

    init(capabilities: PrinterBackendCapabilities, hangForever: Bool) {
        self.capabilities = capabilities
        self.hangForever = hangForever
    }

    func list(includeDisabled: Bool) async throws -> [Printer] { [] }
    func get(id: UUID) async throws -> Printer { throw NetworkError.notFound }
    func getStatus(id: UUID) async throws -> PrinterStatusDetail { throw NetworkError.notFound }
    func listCameraUrls() async throws -> [PrinterCameraUrls] { [] }
    func getCameraUrl(id: UUID) async throws -> PrinterCameraUrl { throw NetworkError.notFound }
    func getSnapshot(id: UUID) async throws -> Data { Data() }
    func getCurrentJob(id: UUID) async throws -> PrintJobStatusInfo? { nil }
    func getHistory(id: UUID, limit: Int?) async throws -> PrinterHistoryList { PrinterHistoryList(count: 0, jobs: []) }
    func pause(id: UUID) async throws -> CommandResult { CommandResult(success: true, message: nil) }
    func resume(id: UUID) async throws -> CommandResult { CommandResult(success: true, message: nil) }
    func cancel(id: UUID) async throws -> CommandResult { CommandResult(success: true, message: nil) }
    func stop(id: UUID) async throws -> CommandResult { CommandResult(success: true, message: nil) }
    func emergencyStop(id: UUID) async throws -> CommandResult { CommandResult(success: true, message: nil) }
    func setMaintenanceMode(
        id: UUID,
        inMaintenance: Bool,
        reviewedRowVersion: String
    ) async throws -> Printer {
        Printer.previewFallbackPrinter(state: "ready", isOnline: true)
    }
    func getQueueOverview(model: String?, nozzle: Double?, material: String?) async throws -> [QueueOverview] { [] }
    func setActiveSpool(
        printerId: UUID,
        spoolId: Int?,
        reviewedRowVersion: String
    ) async throws -> CommandResult {
        CommandResult(success: true, message: nil)
    }
    func bindToolheadSpool(printerId: UUID, toolheadIndex: Int, request: ToolheadSpoolBindRequest, idempotencyKey: String) async throws -> CommandResult { CommandResult(success: true, message: nil) }
    func listAvailableSpools(printerId: UUID) async throws -> [SpoolmanSpool] { [] }
    func loadFilament(printerId: UUID) async throws -> CommandResult { CommandResult(success: true, message: nil) }
    func unloadFilament(printerId: UUID) async throws -> CommandResult { CommandResult(success: true, message: nil) }
    func changeFilament(printerId: UUID) async throws -> CommandResult { CommandResult(success: true, message: nil) }

    func setTemperatures(printerId: UUID, hotend: Double?, bed: Double?) async throws {
        if hangForever { try await Task.sleep(nanoseconds: .max) }
    }

    func home(printerId: UUID, axes: [String]) async throws {}
    func homeXY(printerId: UUID) async throws {}
    func homeZ(printerId: UUID) async throws {}
    func move(printerId: UUID, axis: String, distanceMm: Double, feedrateMmMin: Int) async throws {}
    func moveTo(printerId: UUID, x: Double?, y: Double?, z: Double?, feedrateMmMin: Int?) async throws -> CommandResult { throw NetworkError.notFound }
    func extrude(printerId: UUID, distanceMm: Double, feedrateMmPerMinute: Int) async throws -> CommandResult { throw NetworkError.notFound }
    func disableMotors(printerId: UUID) async throws -> CommandResult { throw NetworkError.notFound }
    func saveZOffset(printerId: UUID, offsetMm: Double, saveToFirmware: Bool, reviewedRowVersion: String) async throws -> CommandResult { throw NetworkError.notFound }
    func unloadFilament(printerId: UUID, toolheadIndex: Int?) async throws -> FilamentUnloadResult { throw NetworkError.notFound }

    func getBackendCapabilities(printerId: UUID) async throws -> PrinterBackendCapabilities { capabilities }

    // MARK: - #711 F6 preview stubs

    func getDetails(id: UUID) async throws -> PrinterDetails {
        throw NetworkError.notFound
    }
    func listFallbackGroups(printerId: UUID) async throws -> [FilamentFallbackGroup] { [] }
    func getFallbackGroup(printerId: UUID, groupId: UUID) async throws -> FilamentFallbackGroup {
        throw NetworkError.notFound
    }
    func createFallbackGroup(printerId: UUID, _ request: CreateFilamentFallbackGroupRequest) async throws -> FilamentFallbackGroup {
        throw NetworkError.notFound
    }
    func updateFallbackGroup(printerId: UUID, groupId: UUID, _ request: UpdateFilamentFallbackGroupRequest) async throws -> FilamentFallbackGroup {
        throw NetworkError.notFound
    }
    func deleteFallbackGroup(printerId: UUID, groupId: UUID) async throws {}
    func getAvailableFallback(printerId: UUID, sourceToolheadId: UUID, material: String) async throws -> AvailableFallbackMember? { nil }
}

// MARK: - Preview seam on the ViewModel

private extension PrinterControlsViewModel {
    /// Kicks off the existing `loadCapabilities()` path through the preview
    /// service so the canvas converges on the configured capabilities without
    /// a production-code-affecting back door.
    func previewLoadCapabilitiesAsync() {
        Task { @MainActor in await self.loadCapabilities() }
    }
}

// MARK: - Printer preview stub

private extension Printer {
    /// Decodes a minimal `Printer` from a JSON literal for SwiftUI previews
    /// and preview-only service shims. The struct has no memberwise init, so
    /// we round-trip through `JSONDecoder`. This is preview infrastructure —
    /// never called from production code paths.
    static func previewFallbackPrinter(state: String, isOnline: Bool) -> Printer {
        let json = """
        {
            "id": "11111111-1111-1111-1111-111111111111",
            "name": "Preview Printer",
            "backend": "moonraker",
            "backendPort": 80,
            "inMaintenance": false,
            "isEnabled": true,
            "isOnline": \(isOnline),
            "state": "\(state)",
            "obicoEnabled": false
        }
        """
        // Decoding a string literal under our control is preview-only and
        // should never fail; if it does, surfacing it immediately is useful.
        return try! JSONDecoder().decode(Printer.self, from: Data(json.utf8))
    }
}
