import Foundation

/// Manages login form state, validation, and server URL persistence.
@MainActor @Observable
final class LoginViewModel {
    var serverURL: String = ""
    var usernameOrEmail: String = ""
    var password: String = ""
    var isServerURLExpanded: Bool = false

    // MARK: - Validation

    var isFormValid: Bool {
        !usernameOrEmail.trimmingCharacters(in: .whitespaces).isEmpty
            && !password.isEmpty
            && isValidServerURL
    }

    var isValidServerURL: Bool {
        normalizedServerURL != nil
    }

    var serverURLValidationError: String? {
        let trimmed = serverURL.trimmingCharacters(in: .whitespaces)
        if trimmed.isEmpty { return "Server URL is required" }
        if !isValidServerURL { return "Enter a valid URL (e.g. https://print.example.com)" }
        return nil
    }

    /// Normalizes user input into a clean URL string for the API layer.
    /// All connections use `https://` by default. The APIClient handles
    /// self-signed certificates for IP addresses / private networks.
    var normalizedServerURL: String? {
        APIClient.normalizedServerURLString(serverURL)
    }

    // MARK: - Initialization

    init() {
        if let saved = APIClient.savedServerURLString(), !saved.isEmpty {
            self.serverURL = saved
            self.isServerURLExpanded = false
        } else {
            self.isServerURLExpanded = true
        }
    }

    // MARK: - Login

    func login(using authViewModel: AuthViewModel) async {
        guard let serverURL = normalizedServerURL else { return }

        await authViewModel.login(
            serverURL: serverURL,
            username: usernameOrEmail.trimmingCharacters(in: .whitespaces),
            password: password
        )
    }
}
