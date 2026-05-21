import XCTest
@testable import PrintFarmer

/// Tests for LoginViewModel: form validation, URL normalization,
/// server URL persistence.
@MainActor
final class LoginViewModelTests: XCTestCase {

    private var viewModel: LoginViewModel!

    override func setUp() {
        super.setUp()
        // Clear any persisted server URL before each test
        UserDefaults.standard.removeObject(forKey: APIClient.serverURLKey)
        viewModel = LoginViewModel()
    }

    override func tearDown() {
        UserDefaults.standard.removeObject(forKey: APIClient.serverURLKey)
        viewModel = nil
        super.tearDown()
    }

    // MARK: - Form Validation

    func testFormInvalidWhenAllFieldsEmpty() {
        viewModel.serverURL = ""
        viewModel.usernameOrEmail = ""
        viewModel.password = ""

        XCTAssertFalse(viewModel.isFormValid)
    }

    func testFormInvalidWhenUsernameEmpty() {
        viewModel.serverURL = "https://print.example.com"
        viewModel.usernameOrEmail = ""
        viewModel.password = "password"

        XCTAssertFalse(viewModel.isFormValid)
    }

    func testFormInvalidWhenUsernameOnlySpaces() {
        viewModel.serverURL = "https://print.example.com"
        viewModel.usernameOrEmail = "   "
        viewModel.password = "password"

        XCTAssertFalse(viewModel.isFormValid)
    }

    func testFormInvalidWhenPasswordEmpty() {
        viewModel.serverURL = "https://print.example.com"
        viewModel.usernameOrEmail = "admin"
        viewModel.password = ""

        XCTAssertFalse(viewModel.isFormValid)
    }

    func testFormInvalidWhenURLInvalid() {
        viewModel.serverURL = "not a url at all !@#$"
        viewModel.usernameOrEmail = "admin"
        viewModel.password = "password"

        XCTAssertFalse(viewModel.isFormValid)
    }

    func testFormValidWithAllFieldsFilled() {
        viewModel.serverURL = "https://print.example.com"
        viewModel.usernameOrEmail = "admin"
        viewModel.password = "password123"

        XCTAssertTrue(viewModel.isFormValid)
    }

    // MARK: - Server URL Validation

    func testValidHTTPSURL() {
        viewModel.serverURL = "https://print.example.com"
        XCTAssertTrue(viewModel.isValidServerURL)
    }

    func testValidHTTPURL() {
        viewModel.serverURL = "http://192.168.1.100"
        XCTAssertTrue(viewModel.isValidServerURL)
    }

    func testValidURLWithPort() {
        viewModel.serverURL = "http://192.168.1.100:5000"
        XCTAssertTrue(viewModel.isValidServerURL)
    }

    func testEmptyURLIsInvalid() {
        viewModel.serverURL = ""
        XCTAssertFalse(viewModel.isValidServerURL)
    }

    func testWhitespaceOnlyURLIsInvalid() {
        viewModel.serverURL = "   "
        XCTAssertFalse(viewModel.isValidServerURL)
    }

    // MARK: - URL Normalization

    func testNormalizationAddsHTTPSScheme() {
        viewModel.serverURL = "print.example.com"
        XCTAssertEqual(viewModel.normalizedServerURL, "https://print.example.com")
    }

    func testNormalizationAddsHTTPSForBareIP() {
        viewModel.serverURL = "192.168.1.100"
        XCTAssertEqual(viewModel.normalizedServerURL, "https://192.168.1.100")
    }

    func testNormalizationPreservesExplicitHTTP() {
        viewModel.serverURL = "http://192.168.1.100"
        XCTAssertEqual(viewModel.normalizedServerURL, "http://192.168.1.100")
    }

    func testNormalizationStripsTrailingSlash() {
        viewModel.serverURL = "https://print.example.com/"
        XCTAssertEqual(viewModel.normalizedServerURL, "https://print.example.com")
    }

    func testNormalizationTrimsWhitespace() {
        viewModel.serverURL = "  https://print.example.com  "
        XCTAssertEqual(viewModel.normalizedServerURL, "https://print.example.com")
    }

    func testNormalizationReturnsNilForInvalidInput() {
        viewModel.serverURL = ""
        XCTAssertNil(viewModel.normalizedServerURL)
    }

    // MARK: - Validation Error Messages

    func testErrorMessageForEmptyURL() {
        viewModel.serverURL = ""
        XCTAssertEqual(viewModel.serverURLValidationError, "Server URL is required")
    }

    func testErrorMessageForInvalidURL() {
        viewModel.serverURL = "not a url!@#"
        XCTAssertEqual(
            viewModel.serverURLValidationError,
            "Enter a valid URL (e.g. https://print.example.com)"
        )
    }

    func testNoErrorMessageForValidURL() {
        viewModel.serverURL = "https://print.example.com"
        XCTAssertNil(viewModel.serverURLValidationError)
    }

    // MARK: - Server URL Persistence

    func testInitLoadsPersistedServerURL() {
        UserDefaults.standard.set("https://saved.example.com", forKey: APIClient.serverURLKey)

        let vm = LoginViewModel()
        XCTAssertEqual(vm.serverURL, "https://saved.example.com")
        XCTAssertFalse(vm.isServerURLExpanded, "Should collapse URL field when pre-filled")
    }

    func testInitMigratesLegacyHTTPIPURL() {
        UserDefaults.standard.set("http://100.119.81.25", forKey: APIClient.serverURLKey)

        let vm = LoginViewModel()

        XCTAssertEqual(vm.serverURL, "https://100.119.81.25")
        XCTAssertEqual(
            UserDefaults.standard.string(forKey: APIClient.serverURLKey),
            "https://100.119.81.25"
        )
        XCTAssertFalse(vm.isServerURLExpanded)
    }

    func testInitExpandsURLFieldWhenNoSavedURL() {
        UserDefaults.standard.removeObject(forKey: APIClient.serverURLKey)

        let vm = LoginViewModel()
        XCTAssertTrue(vm.isServerURLExpanded, "Should expand URL field when no saved URL")
    }

    func testInitWithEmptySavedURLExpandsField() {
        UserDefaults.standard.set("", forKey: APIClient.serverURLKey)

        let vm = LoginViewModel()
        XCTAssertTrue(vm.isServerURLExpanded)
    }
}
