import XCTest

@MainActor
final class ScanStationUITests: PrintFarmerUITestCase {
    override var waitsForNavigationReadiness: Bool { true }

    func testFloatingScanOpensBarcodeAndNFCFromEveryTab() {
        for id in ["tab.farm", "tab.queue", "tab.filament"] {
            let destination = shellDestinationButton(tabIdentifier: id, timeout: 8)
            XCTAssertTrue(destination.exists)
            destination.tap()
            let scan = app.buttons["navigation.scan"]
            XCTAssertTrue(scan.waitForExistence(timeout: 5))
            scan.tap()
            XCTAssertTrue(app.buttons["scan.primary"].waitForExistence(timeout: 5))
            XCTAssertTrue(app.buttons["scan.nfc"].exists)
            XCTAssertTrue(app.staticTexts["scan.nfc.hint"].exists)
            app.buttons["Done"].tap()
            XCTAssertTrue(scan.waitForExistence(timeout: 5))
        }
    }

    func testFilamentKeepsContinuousBarcodeIntake() {
        let filament = shellDestinationButton(tabIdentifier: "tab.filament", timeout: 8)
        XCTAssertTrue(filament.exists)
        filament.tap()
        app.buttons["inventory.actions"].tap()
        let intake = app.buttons["inventory.scan.barcodeIntake"]
        XCTAssertTrue(intake.waitForExistence(timeout: 5))
        intake.tap()
        XCTAssertTrue(app.navigationBars["Barcode Intake"].waitForExistence(timeout: 5))
    }

    func testFnskuFirstScanRemembersPickedFilamentAndRescanAddsAnotherSpool() async throws {
        let isLive = try await configureLiveIntakeIfRequested()
        relaunchAppForTest(additionalArguments: ["--uitesting-scan-code", "X0022Z40I1"])
        openScanAndScan()

        XCTAssertTrue(app.navigationBars["Barcode Intake"].waitForExistence(timeout: 8))
        XCTAssertTrue(app.navigationBars["Remember Code"].waitForExistence(timeout: 8))
        XCTAssertFalse(app.staticTexts["Unrecognized Code"].exists, "the field-reported FNSKU must not dead-end")
        XCTAssertEqual(app.staticTexts["mapBarcode.code"].label, "X0022Z40I1")
        XCTAssertTrue(app.buttons["mapBarcode.showCreate"].exists, "creating a new filament is offered")
        let filament = !isLive
            ? app.buttons.matching(NSPredicate(format: "identifier BEGINSWITH 'mapBarcode.filament.'")).firstMatch
            : app.buttons["mapBarcode.filament.42"]
        XCTAssertTrue(filament.waitForExistence(timeout: 8), "picking an existing filament is offered")
        attachScreenshot(named: "fnsku-first-scan-remember-code")
        filament.tap()

        XCTAssertTrue(app.staticTexts["Imported This Session (1)"].waitForExistence(timeout: 8), "a spool was created")
        XCTAssertFalse(app.navigationBars["Remember Code"].exists)
        attachScreenshot(named: "fnsku-first-scan-spool-added")
        app.buttons["Done with barcode intake"].tap()
        XCTAssertTrue(app.navigationBars["Barcode Intake"].waitForNonExistence(timeout: 8))

        if isLive {
            // A fresh app process must resolve the server's durable mapping.
            relaunchAppForTest(additionalArguments: [])
            openScanAndScan()
        } else {
            app.buttons["scan.primary"].tap()
        }
        XCTAssertTrue(app.navigationBars["Barcode Intake"].waitForExistence(timeout: 8))
        let addAnother = app.buttons["barcodeIntake.aliasAddSpool"]
        let intakeList = app.collectionViews.containing(.button, identifier: "barcodeIntake.aliasCancel").firstMatch
        for _ in 0..<3 where !addAnother.isHittable { intakeList.swipeUp() }
        XCTAssertTrue(addAnother.waitForExistence(timeout: 8), "a rescan offers the remembered filament")
        XCTAssertFalse(app.navigationBars["Remember Code"].exists, "a remembered code skips filament selection")
        XCTAssertFalse(app.staticTexts["Imported This Session (1)"].exists, "nothing is created before confirmation")
        attachScreenshot(named: "fnsku-rescan-remembered")
        addAnother.tap()
        XCTAssertTrue(addAnother.waitForNonExistence(timeout: 8))
        XCTAssertTrue(app.staticTexts["Imported This Session (1)"].waitForExistence(timeout: 8), "a second spool was added")
        attachScreenshot(named: "fnsku-rescan-second-spool")
    }

    func testFnskuFirstScanOffersCreatingANewFilament() async throws {
        let isLive = try await configureLiveIntakeIfRequested()
        relaunchAppForTest(additionalArguments: ["--uitesting-scan-code", "X0022Z40I1"])
        openScanAndScan()
        XCTAssertTrue(app.navigationBars["Remember Code"].waitForExistence(timeout: 8))
        let showCreate = app.buttons["mapBarcode.showCreate"]
        XCTAssertTrue(showCreate.waitForExistence(timeout: 5))
        showCreate.tap()
        let list = app.collectionViews["mapBarcode.list"]
        let materialField = app.textFields["mapBarcode.material"]
        if materialField.exists {
            materialField.tap()
            materialField.typeText("PLA")
        } else {
            app.buttons["mapBarcode.material"].tap()
            app.buttons["PLA"].firstMatch.tap()
        }
        let name = app.textFields["Filament Name"]
        XCTAssertTrue(name.waitForExistence(timeout: 5))
        name.tap()
        name.typeText("FNSKU new filament")
        let create = app.buttons["mapBarcode.create"]
        for _ in 0..<4 where !create.isHittable { list.swipeUp() }
        XCTAssertTrue(create.waitForExistence(timeout: 5))
        XCTAssertEqual(create.label, "Create filament and add spool")
        attachScreenshot(named: "fnsku-first-scan-create-filament")
        XCTAssertTrue(create.isEnabled)
        // DemoSpoolService deliberately rejects writes; connected runs prove submission.
        if isLive {
            create.tap()
            XCTAssertTrue(app.staticTexts["Imported This Session (1)"].waitForExistence(timeout: 8))
            XCTAssertFalse(app.navigationBars["Remember Code"].exists)
            attachScreenshot(named: "fnsku-new-filament-spool-added")
        }
    }

    func testFnskuRememberFailureShowsRetryInTheSheetAndRetryAddsOneSpool() {
        relaunchAppForTest(additionalArguments: [
            "--uitesting-scan-code", "X0022Z40I1",
            "--uitesting-scan-intake-fault", "alias-save-unreachable",
        ])
        openScanAndScan()
        XCTAssertTrue(app.navigationBars["Remember Code"].waitForExistence(timeout: 8))
        let filament = app.buttons.matching(NSPredicate(format: "identifier BEGINSWITH 'mapBarcode.filament.'")).firstMatch
        XCTAssertTrue(filament.waitForExistence(timeout: 8))
        filament.tap()

        let retry = app.buttons["mapBarcode.retry"]
        XCTAssertTrue(retry.waitForExistence(timeout: 8), "a definite failure offers a labelled Retry in the sheet")
        XCTAssertEqual(retry.label, "Retry adding spool")
        XCTAssertTrue(app.navigationBars["Remember Code"].exists, "the sheet stays open after a failed save")
        attachScreenshot(named: "fnsku-remember-failure-retry")
        retry.tap()

        XCTAssertTrue(app.staticTexts["Imported This Session (1)"].waitForExistence(timeout: 8), "retry adds exactly one spool")
        XCTAssertFalse(app.navigationBars["Remember Code"].exists)
    }

    func testFnskuUncertainSpoolOutcomeWithholdsRetry() {
        relaunchAppForTest(additionalArguments: [
            "--uitesting-scan-code", "X0022Z40I1",
            "--uitesting-scan-intake-fault", "spool-create-timeout",
        ])
        openScanAndScan()
        XCTAssertTrue(app.navigationBars["Remember Code"].waitForExistence(timeout: 8))
        let filament = app.buttons.matching(NSPredicate(format: "identifier BEGINSWITH 'mapBarcode.filament.'")).firstMatch
        XCTAssertTrue(filament.waitForExistence(timeout: 8))
        filament.tap()

        let done = app.buttons["mapBarcode.uncertainDone"]
        XCTAssertTrue(done.waitForExistence(timeout: 8))
        XCTAssertFalse(app.buttons["mapBarcode.retry"].exists, "a spool that may exist is never retried")
        XCTAssertFalse(app.buttons["mapBarcode.showCreate"].exists, "filament choices are withdrawn")
        attachScreenshot(named: "fnsku-uncertain-outcome")
        done.tap()
        XCTAssertTrue(app.navigationBars["Remember Code"].waitForNonExistence(timeout: 8))
    }

    func testAppLinkThatIsNotASpoolOffersManualAddSpool() {
        relaunchAppForTest(additionalArguments: [
            "--uitesting-scan-code", "printfarmer://printer/00000000-0000-0000-0000-000000000001/ready"
        ])
        openScanAndScan()
        XCTAssertTrue(app.staticTexts["Unrecognized Code"].waitForExistence(timeout: 5))
        let addSpool = app.buttons["scan.unrecognized.addSpool"]
        XCTAssertTrue(addSpool.waitForExistence(timeout: 5))
        attachScreenshot(named: "unrecognized-app-link")
        addSpool.tap()
        XCTAssertTrue(app.navigationBars["Add Spool"].waitForExistence(timeout: 8))
        XCTAssertFalse(app.navigationBars["Barcode Intake"].exists, "an app link is never remembered")
    }

    func testUnknownGtinScanOpensBarcodeIntake() {
        relaunchAppForTest(additionalArguments: ["--uitesting-scan-code", "4006381333931"])
        openScanAndScan()
        XCTAssertTrue(app.navigationBars["Barcode Intake"].waitForExistence(timeout: 8))
        XCTAssertTrue(app.navigationBars["Map Barcode"].waitForExistence(timeout: 8))
    }

    func testMissingSpoolLinkOffersManualAddSpool() {
        relaunchAppForTest(additionalArguments: ["--uitesting-scan-code", "printfarmer://spool/987654"])
        openScanAndScan()
        XCTAssertTrue(app.staticTexts["Spool Not Found"].waitForExistence(timeout: 8))
        let addSpool = app.buttons["scan.unrecognized.addSpool"]
        XCTAssertTrue(addSpool.waitForExistence(timeout: 5))
        addSpool.tap()
        XCTAssertTrue(app.navigationBars["Add Spool"].waitForExistence(timeout: 8))
        XCTAssertFalse(app.navigationBars["Barcode Intake"].exists, "a spool link never enters GTIN intake")
    }

    private func attachScreenshot(named name: String) {
        let attachment = XCTAttachment(screenshot: app.screenshot())
        attachment.name = name
        attachment.lifetime = .keepAlways
        add(attachment)
    }

    private func configureLiveIntakeIfRequested() async throws -> Bool {
        guard let rawURL = ProcessInfo.processInfo.environment["PRINTFARMER_ALIAS_API_URL"] else { return false }
        let url = try XCTUnwrap(URL(string: rawURL))
        _ = try XCTUnwrap(url.scheme == "http" && url.host == "127.0.0.1" ? url : nil,
                          "Live intake fixtures must use a loopback HTTP API")
        let token = try XCTUnwrap(ProcessInfo.processInfo.environment["PRINTFARMER_ALIAS_API_TOKEN"])
        var components = try XCTUnwrap(URLComponents(
            url: url.appendingPathComponent("api/spoolman/code-aliases/filament"),
            resolvingAgainstBaseURL: false
        ))
        components.queryItems = [URLQueryItem(name: "code", value: "X0022Z40I1")]
        var request = URLRequest(url: try XCTUnwrap(components.url))
        request.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization")
        let (data, response) = try await URLSession.shared.data(for: request)
        XCTAssertEqual(try XCTUnwrap(response as? HTTPURLResponse).statusCode, 200)
        struct LookupState: Decodable { let state: String }
        let lookup = try JSONDecoder().decode(LookupState.self, from: data)
        XCTAssertEqual(lookup.state, "NotRemembered",
                       "Reset the isolated fixture's X0022Z40I1 alias before EACH connected first-scan test")
        app.launchEnvironment["PRINTFARMER_UITEST_INTAKE_URL"] = rawURL
        app.launchEnvironment["PRINTFARMER_UITEST_INTAKE_TOKEN"] = token
        return true
    }

    private func openScanAndScan() {
        let scan = app.buttons["navigation.scan"]
        XCTAssertTrue(scan.waitForExistence(timeout: 8))
        scan.tap()
        let primary = app.buttons["scan.primary"]
        XCTAssertTrue(primary.waitForExistence(timeout: 5))
        primary.tap()
    }
}
