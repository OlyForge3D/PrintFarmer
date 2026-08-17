import SwiftUI

#if canImport(UIKit)
  import UIKit
#endif

struct CertificateTrustView: View {
  let request: CertificateTrustRequest
  let onDecision: (Bool) -> Void

  var body: some View {
    NavigationStack {
      ScrollView {
        VStack(alignment: .leading, spacing: 20) {
          Label("Untrusted Certificate", systemImage: "exclamationmark.triangle.fill")
            .font(.title2.bold())
            .foregroundStyle(Color.pfWarning)

          Text(
            "This certificate is not issued by a trusted authority. Only continue if this fingerprint matches the one shown by your PrintFarmer server. If you did not expect this, tap Cancel."
          )

          detail("Endpoint", value: request.endpoint)
            .accessibilityIdentifier("certificateTrustEndpointText")

          VStack(alignment: .leading, spacing: 8) {
            Text("SHA-256 public-key fingerprint")
              .font(.headline)
            Text(CertificateFingerprint.display(request.fingerprint))
              .font(.system(.body, design: .monospaced))
              .textSelection(.enabled)
              .fixedSize(horizontal: false, vertical: true)
              .speechSpellsOutCharacters()
              .accessibilityLabel(
                "SHA-256 public-key fingerprint \(CertificateFingerprint.display(request.fingerprint))"
              )
              .accessibilityIdentifier("certificateFingerprintText")
            Button("Copy Fingerprint") {
              #if canImport(UIKit)
                UIPasteboard.general.string = request.fingerprint
              #endif
            }

          }

          Group {
            detail(
              "Subject common name — Reported by the server — not verified",
              value: request.subjectCommonName ?? "Not provided")
            detail(
              "Issuer common name — Reported by the server — not verified",
              value: request.issuerCommonName ?? "Not provided")
            detail(
              "Valid from — Reported by the server — not verified",
              value: formatted(request.notBefore))
            detail(
              "Valid until — Reported by the server — not verified",
              value: formatted(request.notAfter))
            detail(
              "Subject alternative names — Reported by the server — not verified",
              value: request.subjectAlternativeNames.isEmpty
                ? "Not provided"
                : request.subjectAlternativeNames.joined(separator: ", ")
            )
          }

          if let warning = request.warning {
            Label(warning, systemImage: "exclamationmark.shield.fill")
              .foregroundStyle(Color.pfWarning)
              .accessibilityIdentifier("certificateTrustWarningText")
          }
        }

        .padding()
      }
      .navigationTitle("Verify Server")
      .navigationBarTitleDisplayMode(.inline)
      .safeAreaInset(edge: .bottom) {
        VStack(spacing: 12) {
          Button("Trust This Certificate", role: .destructive) {
            onDecision(true)
          }
          .buttonStyle(.borderedProminent)
          .accessibilityLabel("Trust this certificate for \(request.endpoint)")
          .accessibilityIdentifier("certificateTrustAcceptButton")

          Button("Cancel", role: .cancel) {
            onDecision(false)
          }
          .buttonStyle(.bordered)
          .accessibilityLabel("Cancel, do not trust this certificate")
          .accessibilityIdentifier("certificateTrustCancelButton")
        }
        .padding()
        .frame(maxWidth: .infinity)
        .background(.bar)
      }
    }
    .interactiveDismissDisabled(false)
    .accessibilityAddTraits(.isModal)
    .accessibilityIdentifier("certificateTrustSheet")
  }

  private func detail(_ label: String, value: String) -> some View {
    VStack(alignment: .leading, spacing: 4) {
      Text(label)
        .font(.headline)
      Text(value)
        .font(.system(.body, design: .monospaced))
        .textSelection(.enabled)
        .fixedSize(horizontal: false, vertical: true)
    }
  }

  private func formatted(_ date: Date?) -> String {
    date?.formatted(date: .abbreviated, time: .standard) ?? "Not provided"
  }
}
