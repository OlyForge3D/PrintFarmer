import Foundation
import Network
import XCTest

// MARK: - MockAPIServer

/// A lightweight localhost HTTP server for integration and XCUITest scenarios.
///
/// Unlike `MockURLProtocol` (which only works in-process), this starts a real
/// TCP listener that the app can connect to from a separate process.
///
/// Usage:
/// ```swift
/// let server = MockAPIServer()
/// try server.start()
/// let baseURL = server.baseURL  // e.g. http://localhost:52341
/// // ... configure app to use baseURL ...
/// server.stop()
/// ```
final class MockAPIServer: @unchecked Sendable {

    // MARK: - Types

    /// A canned response the server will return for a matching route.
    struct Route: Sendable {
        let method: String
        let pathPattern: String  // e.g. "/api/printers" or "/api/printers/*"
        let statusCode: Int
        let body: Data
        let headers: [String: String]

        init(
            method: String = "GET",
            path: String,
            statusCode: Int = 200,
            json: String,
            headers: [String: String] = ["Content-Type": "application/json"]
        ) {
            self.method = method.uppercased()
            self.pathPattern = path
            self.statusCode = statusCode
            self.body = Data(json.utf8)
            self.headers = headers
        }

        init(
            method: String = "GET",
            path: String,
            statusCode: Int = 200,
            body: Data = Data(),
            headers: [String: String] = ["Content-Type": "application/json"]
        ) {
            self.method = method.uppercased()
            self.pathPattern = path
            self.statusCode = statusCode
            self.body = body
            self.headers = headers
        }

        func matches(_ requestMethod: String, path requestPath: String) -> Bool {
            guard method == requestMethod.uppercased() else { return false }

            if pathPattern.hasSuffix("/*") {
                let prefix = String(pathPattern.dropLast(2))
                return requestPath.hasPrefix(prefix) && requestPath.count > prefix.count
            }
            return pathPattern == requestPath
        }
    }

    // MARK: - Properties

    private var listener: NWListener?
    private let queue = DispatchQueue(label: "MockAPIServer", qos: .userInitiated)
    private var connections: [NWConnection] = []

    /// Routes are matched in order; first match wins.
    private var routes: [Route] = []
    private let routeLock = NSLock()

    /// All requests received, for assertion.
    private(set) var receivedRequests: [(method: String, path: String, body: Data?)] = []
    private let requestLock = NSLock()

    /// The port the server is listening on (available after `start()`).
    private(set) var port: UInt16 = 0

    /// Base URL for the running server, e.g. `http://localhost:52341`.
    var baseURL: URL {
        URL(string: "http://localhost:\(port)")!
    }

    // MARK: - Lifecycle

    /// Starts the server on a random available port.
    func start() throws {
        let params = NWParameters.tcp
        params.allowLocalEndpointReuse = true

        let nwListener = try NWListener(using: params, on: .any)
        self.listener = nwListener

        let semaphore = DispatchSemaphore(value: 0)
        // Use a class box to safely share the error across the sendable closure boundary
        final class ErrorBox: @unchecked Sendable {
            var error: Error?
        }
        let errorBox = ErrorBox()

        nwListener.stateUpdateHandler = { state in
            switch state {
            case .ready:
                if let nwPort = nwListener.port {
                    self.port = nwPort.rawValue
                }
                semaphore.signal()
            case .failed(let error):
                errorBox.error = error
                semaphore.signal()
            default:
                break
            }
        }

        nwListener.newConnectionHandler = { [weak self] connection in
            self?.handleConnection(connection)
        }

        nwListener.start(queue: queue)

        let result = semaphore.wait(timeout: .now() + 5)
        if result == .timedOut {
            throw MockServerError.startTimeout
        }
        if let error = errorBox.error {
            throw error
        }
    }

    /// Stops the server and cleans up.
    func stop() {
        listener?.cancel()
        listener = nil
        routeLock.lock()
        let conns = connections
        connections.removeAll()
        routeLock.unlock()
        for conn in conns {
            conn.cancel()
        }
    }

    // MARK: - Route Configuration

    /// Register a route. First match wins.
    func route(_ route: Route) {
        routeLock.lock()
        routes.append(route)
        routeLock.unlock()
    }

    /// Convenience: register a GET route returning JSON.
    func get(_ path: String, json: String, statusCode: Int = 200) {
        route(Route(method: "GET", path: path, statusCode: statusCode, json: json))
    }

    /// Convenience: register a POST route returning JSON.
    func post(_ path: String, json: String, statusCode: Int = 200) {
        route(Route(method: "POST", path: path, statusCode: statusCode, json: json))
    }

    /// Remove all routes.
    func resetRoutes() {
        routeLock.lock()
        routes.removeAll()
        routeLock.unlock()
    }

    /// Remove all recorded requests.
    func resetRequests() {
        requestLock.lock()
        receivedRequests.removeAll()
        requestLock.unlock()
    }

    /// Load the default set of routes that cover common API endpoints.
    func loadDefaultRoutes() {
        post("/api/auth/login", json: MockResponses.authSuccess)
        get("/api/printers", json: MockResponses.printerList)
        get("/api/printers/*", json: MockResponses.printerDetail)
        get("/api/jobs", json: MockResponses.jobList)
        get("/api/spoolman/spools", json: MockResponses.spoolList)
        route(Route(method: "POST", path: "/api/printers/*/active-spool",
                     statusCode: 200, json: MockResponses.commandSuccess))
    }

    // MARK: - Connection Handling

    private func handleConnection(_ connection: NWConnection) {
        routeLock.lock()
        connections.append(connection)
        routeLock.unlock()

        connection.start(queue: queue)
        receiveHTTP(on: connection)
    }

    private func receiveHTTP(on connection: NWConnection) {
        connection.receive(minimumIncompleteLength: 1, maximumLength: 65536) { [weak self] data, _, isComplete, error in
            guard let self, let data else {
                connection.cancel()
                return
            }

            let response = self.processRequest(data)
            connection.send(content: response, completion: .contentProcessed { _ in
                connection.cancel()
            })
        }
    }

    private func processRequest(_ data: Data) -> Data {
        guard let requestString = String(data: data, encoding: .utf8) else {
            return buildHTTPResponse(statusCode: 400, body: Data())
        }

        let lines = requestString.components(separatedBy: "\r\n")
        guard let requestLine = lines.first else {
            return buildHTTPResponse(statusCode: 400, body: Data())
        }

        let parts = requestLine.split(separator: " ", maxSplits: 2)
        guard parts.count >= 2 else {
            return buildHTTPResponse(statusCode: 400, body: Data())
        }

        let method = String(parts[0])
        let fullPath = String(parts[1])
        // Strip query string for route matching
        let path = fullPath.components(separatedBy: "?").first ?? fullPath

        // Extract body (after blank line)
        var requestBody: Data?
        if let range = requestString.range(of: "\r\n\r\n") {
            let bodyString = String(requestString[range.upperBound...])
            if !bodyString.isEmpty {
                requestBody = Data(bodyString.utf8)
            }
        }

        // Record request
        requestLock.lock()
        receivedRequests.append((method: method, path: path, body: requestBody))
        requestLock.unlock()

        // Find matching route
        routeLock.lock()
        let matchedRoute = routes.first { $0.matches(method, path: path) }
        routeLock.unlock()

        guard let matched = matchedRoute else {
            let notFound = Data("""
            {"error": "No mock route for \(method) \(path)"}
            """.utf8)
            return buildHTTPResponse(statusCode: 404, body: notFound)
        }

        return buildHTTPResponse(
            statusCode: matched.statusCode,
            body: matched.body,
            headers: matched.headers
        )
    }

    private func buildHTTPResponse(
        statusCode: Int,
        body: Data,
        headers: [String: String] = ["Content-Type": "application/json"]
    ) -> Data {
        var headerLines = headers.map { "\($0.key): \($0.value)" }
        headerLines.append("Content-Length: \(body.count)")
        headerLines.append("Connection: close")

        let statusText = HTTPURLResponse.localizedString(forStatusCode: statusCode).capitalized
        let responseLine = "HTTP/1.1 \(statusCode) \(statusText)\r\n"
        let headerBlock = headerLines.joined(separator: "\r\n")
        let fullHeader = responseLine + headerBlock + "\r\n\r\n"

        var responseData = Data(fullHeader.utf8)
        responseData.append(body)
        return responseData
    }

    // MARK: - Errors

    enum MockServerError: Error, LocalizedError {
        case startTimeout

        var errorDescription: String? {
            switch self {
            case .startTimeout:
                return "MockAPIServer failed to start within 5 seconds"
            }
        }
    }
}

// MARK: - Canned Responses

/// Pre-built JSON responses matching the app's Codable models.
/// These reuse fixtures from `TestJSON` where possible and add
/// Spoolman fixtures that were previously missing.
enum MockResponses {

    // MARK: Auth

    static let authSuccess = """
    {
        "success": true,
        "token": "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiJhZG1pbiIsImV4cCI6OTk5OTk5OTk5OX0.mock",
        "expiresAt": "2099-12-31T23:59:59Z",
        "user": {
            "id": "aab2c3d4-e5f6-7890-abcd-ef1234567890",
            "username": "admin",
            "email": "admin@printfarmer.local",
            "firstName": "Admin",
            "lastName": "User",
            "isActive": true,
            "emailConfirmed": true,
            "lastLogin": "2025-07-17T09:00:00Z",
            "createdAt": "2025-01-01T00:00:00Z",
            "roles": ["Admin"],
            "permissions": ["printers.manage", "jobs.manage"]
        }
    }
    """

    static let authFailure = """
    {"success": false, "error": "Invalid username or password"}
    """

    // MARK: Printers

    static let printerDetail = """
    {
        "id": "550e8400-e29b-41d4-a716-446655440000",
        "name": "Prusa MK4",
        "notes": "Workshop printer",
        "manufacturerName": "Prusa Research",
        "modelName": "MK4",
        "motionType": "Cartesian",
        "backend": "Moonraker",
        "apiKey": "test-api-key",
        "originalServerUrl": "http://192.168.1.100",
        "backendPort": 7125,
        "frontendPort": 80,
        "inMaintenance": false,
        "isEnabled": true,
        "isOnline": true,
        "state": "printing",
        "progress": 45.5,
        "jobName": "benchy.gcode",
        "hotendTemp": 215.0,
        "bedTemp": 60.0,
        "hotendTarget": 215.0,
        "bedTarget": 60.0,
        "homedAxes": "xyz",
        "backendUrl": "http://192.168.1.100:7125",
        "frontendUrl": "http://192.168.1.100"
    }
    """

    static let printerList = """
    [
        {
            "id": "550e8400-e29b-41d4-a716-446655440000",
            "name": "Prusa MK4",
            "backend": "Moonraker",
            "backendPort": 7125,
            "inMaintenance": false,
            "isEnabled": true,
            "isOnline": true,
            "state": "printing",
            "progress": 45.5,
            "jobName": "benchy.gcode",
            "hotendTemp": 215.0,
            "bedTemp": 60.0
        },
        {
            "id": "660e8400-e29b-41d4-a716-446655440001",
            "name": "Ender 3",
            "backend": "Moonraker",
            "backendPort": 7125,
            "inMaintenance": false,
            "isEnabled": true,
            "isOnline": false
        }
    ]
    """

    // MARK: Jobs

    static let jobList = """
    [
        {
            "id": "770e8400-e29b-41d4-a716-446655440002",
            "status": "Printing",
            "priority": 1,
            "queuePosition": 1,
            "gcodeFileName": "benchy.gcode",
            "assignedPrinterId": "550e8400-e29b-41d4-a716-446655440000",
            "assignedPrinterName": "Prusa MK4",
            "createdAt": "2025-07-17T10:00:00Z",
            "updatedAt": "2025-07-17T10:30:00Z",
            "copies": 3,
            "completedCopies": 1,
            "remainingCopies": 2
        },
        {
            "id": "990e8400-e29b-41d4-a716-446655440004",
            "status": "Queued",
            "priority": 2,
            "queuePosition": 2,
            "gcodeFileName": "phone_case.gcode",
            "assignedPrinterName": "",
            "createdAt": "2025-07-17T09:00:00Z",
            "updatedAt": "2025-07-17T09:00:00Z",
            "copies": 1,
            "completedCopies": 0,
            "remainingCopies": 1
        }
    ]
    """

    // MARK: Spoolman / Spools

    static let spoolList = """
    {
        "items": [
            {
                "id": 1,
                "name": "PLA Basic Black",
                "material": "PLA",
                "colorHex": "#000000",
                "inUse": true,
                "filamentName": "Prusament PLA",
                "vendor": "Prusa Research",
                "registeredAt": "2025-01-15T10:00:00Z",
                "firstUsedAt": "2025-01-20T14:00:00Z",
                "lastUsedAt": "2025-07-17T10:00:00Z",
                "remainingWeightG": 750.0,
                "initialWeightG": 1000.0,
                "usedWeightG": 250.0,
                "spoolWeightG": 200.0,
                "remainingLengthMm": 250000.0,
                "usedLengthMm": 83000.0,
                "archived": false,
                "price": 25.99,
                "usedPercent": 25.0,
                "remainingPercent": 75.0
            },
            {
                "id": 2,
                "name": "PETG Orange",
                "material": "PETG",
                "colorHex": "#FF6600",
                "inUse": false,
                "filamentName": "eSUN PETG",
                "vendor": "eSUN",
                "registeredAt": "2025-03-01T08:00:00Z",
                "remainingWeightG": 1000.0,
                "initialWeightG": 1000.0,
                "usedWeightG": 0.0,
                "spoolWeightG": 250.0,
                "archived": false,
                "price": 19.99,
                "usedPercent": 0.0,
                "remainingPercent": 100.0
            },
            {
                "id": 3,
                "name": "ABS White",
                "material": "ABS",
                "colorHex": "#FFFFFF",
                "inUse": false,
                "filamentName": "Hatchbox ABS",
                "vendor": "Hatchbox",
                "registeredAt": "2025-02-10T12:00:00Z",
                "remainingWeightG": 500.0,
                "initialWeightG": 1000.0,
                "usedWeightG": 500.0,
                "spoolWeightG": 200.0,
                "archived": false,
                "price": 22.50,
                "usedPercent": 50.0,
                "remainingPercent": 50.0
            }
        ],
        "totalCount": 3
    }
    """

    static let spoolListEmpty = """
    {"items": [], "totalCount": 0}
    """

    // MARK: Command Result

    static let commandSuccess = """
    {"success": true, "message": "Command executed"}
    """

    static let commandFailure = """
    {"success": false, "message": "Printer not ready"}
    """
}
