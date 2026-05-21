import SwiftUI
#if canImport(CoreNFC)
import CoreNFC
#endif

struct NFCScanButton: View {
    let action: () -> Void
    var compact: Bool = false

    private var isNFCAvailable: Bool {
        #if canImport(CoreNFC)
        return NFCNDEFReaderSession.readingAvailable
        #else
        return false
        #endif
    }

    var body: some View {
        Button(action: action) {
            if compact {
                Label("NFC", systemImage: "wave.3.right")
                    .font(.subheadline)
            } else {
                Label("Scan Tag", systemImage: "wave.3.right")
                    .frame(maxWidth: .infinity, minHeight: 44)
            }
        }
        .buttonStyle(.bordered)
        .tint(Color.pfAccent)
        .disabled(!isNFCAvailable)
        .help(isNFCAvailable ? "Scan an NFC tag on a spool" : "NFC is not available on this device")
    }
}
