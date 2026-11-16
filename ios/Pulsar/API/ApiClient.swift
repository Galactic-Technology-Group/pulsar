import Foundation

enum ApiError: LocalizedError {
    case forbidden
    case notFound
    case server(Int, String?)
    case network(String)
    case decoding(String)

    var errorDescription: String? {
        switch self {
        case .forbidden: return "Your account does not have permission to do that."
        case .notFound: return "That item no longer exists."
        case .server(let code, let message): return message ?? "The server returned an error (\(code))."
        case .network(let message): return message
        case .decoding(let message): return "The server's reply could not be read. \(message)"
        }
    }
}

/// Calls the Pulsar management API with the signed-in user's token.
@MainActor
final class ApiClient {
    private let baseURL: URL
    private let auth: AuthService
    private let session: URLSession
    private let decoder: JSONDecoder

    init(baseURL: URL, auth: AuthService) {
        self.baseURL = baseURL
        self.auth = auth
        let configuration = URLSessionConfiguration.default
        configuration.timeoutIntervalForRequest = 20
        configuration.waitsForConnectivity = false
        session = URLSession(configuration: configuration)
        decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .custom { decoder in
            let text = try decoder.singleValueContainer().decode(String.self)
            guard let date = ApiDate.parse(text) else {
                throw DecodingError.dataCorrupted(.init(codingPath: decoder.codingPath, debugDescription: "Unreadable date \(text)"))
            }
            return date
        }
    }

    // MARK: Endpoints

    func dashboard() async throws -> [SiteSummary] { try await get("api/dashboard/sites") }
    func sites() async throws -> [Site] { try await get("api/sites") }
    func agents(site: UUID) async throws -> [Agent] { try await get("api/sites/\(site.path)/agents") }
    func devices(site: UUID) async throws -> [Device] { try await get("api/sites/\(site.path)/devices") }
    func device(_ id: UUID) async throws -> Device { try await get("api/devices/\(id.path)") }

    func incidents(open: Bool, site: UUID? = nil, limit: Int = 200) async throws -> [Incident] {
        var query = [URLQueryItem(name: "open", value: open ? "true" : "false"), URLQueryItem(name: "limit", value: String(limit))]
        if let site { query.append(.init(name: "siteId", value: site.path)) }
        return try await get("api/incidents", query: query)
    }

    func acknowledge(incident id: UUID) async throws { try await send("POST", "api/incidents/\(id.path)/ack") }
    func runNow(check id: UUID) async throws { try await send("POST", "api/checks/\(id.path)/run") }

    func latency(check id: UUID, hours: Int) async throws -> [LatencyPoint] {
        try await get("api/checks/\(id.path)/history", query: range(hours: hours))
    }

    func metricHistory(check id: UUID, metric: String, instance: String?, hours: Int) async throws -> [MetricPoint] {
        var query = range(hours: hours)
        query.append(.init(name: "metric", value: metric))
        if let instance, !instance.isEmpty { query.append(.init(name: "instance", value: instance)) }
        return try await get("api/checks/\(id.path)/metrics", query: query)
    }

    func snmpTemplates() async throws -> [SnmpTemplate] { try await get("api/snmp/templates") }

    func registerPush(token: String, environment: String, deviceName: String) async throws {
        try await send("POST", "api/push/devices", body: ["token": token, "environment": environment, "deviceName": deviceName])
    }

    func unregisterPush(token: String) async throws { try await send("DELETE", "api/push/devices/by-token/\(token)") }

    // MARK: Transport

    private func range(hours: Int) -> [URLQueryItem] {
        let now = Date()
        return [.init(name: "from", value: ApiDate.string(now.addingTimeInterval(-Double(hours) * 3600))), .init(name: "to", value: ApiDate.string(now))]
    }

    private func get<T: Decodable>(_ path: String, query: [URLQueryItem] = []) async throws -> T {
        let data = try await request("GET", path, query: query)
        do { return try decoder.decode(T.self, from: data) }
        catch { throw ApiError.decoding(String(describing: error)) }
    }

    private func send(_ method: String, _ path: String, body: [String: String]? = nil) async throws {
        _ = try await request(method, path, query: [], body: try body.map { try JSONEncoder().encode($0) })
    }

    private func request(_ method: String, _ path: String, query: [URLQueryItem], body: Data? = nil) async throws -> Data {
        var components = URLComponents(url: baseURL.appendingPathComponent(path), resolvingAgainstBaseURL: false)!
        if !query.isEmpty {
            components.queryItems = query
            // URLComponents leaves "+" alone, and the server would read it as a space in a time-zone offset.
            components.percentEncodedQuery = components.percentEncodedQuery?.replacingOccurrences(of: "+", with: "%2B")
        }
        var request = URLRequest(url: components.url!)
        request.httpMethod = method
        request.setValue("application/json", forHTTPHeaderField: "Accept")
        if let body {
            request.httpBody = body
            request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        }

        // A 401 with a token we believed valid gets one retry with a freshly issued token.
        for attempt in 0..<2 {
            let token = try await auth.validAccessToken(forceRefresh: attempt > 0)
            request.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization")
            let data: Data
            let response: URLResponse
            do { (data, response) = try await session.data(for: request) }
            catch let error as URLError where error.code == .cancelled { throw CancellationError() }
            catch { throw ApiError.network(error.localizedDescription) }

            let status = (response as? HTTPURLResponse)?.statusCode ?? 0
            switch status {
            case 200..<300: return data
            case 401 where attempt == 0: continue
            case 401, 403: throw ApiError.forbidden
            case 404: throw ApiError.notFound
            default:
                let message = (try? JSONSerialization.jsonObject(with: data) as? [String: Any])?["error"] as? String
                throw ApiError.server(status, message)
            }
        }
        throw ApiError.forbidden
    }
}

private extension UUID {
    var path: String { uuidString.lowercased() }
}

/// The API writes times as ISO 8601 with up to seven fractional digits; Foundation reads at most three.
enum ApiDate {
    private static let fractional: ISO8601DateFormatter = {
        let f = ISO8601DateFormatter()
        f.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        return f
    }()
    private static let whole: ISO8601DateFormatter = {
        let f = ISO8601DateFormatter()
        f.formatOptions = [.withInternetDateTime]
        return f
    }()

    static func parse(_ text: String) -> Date? {
        guard let dot = text.firstIndex(of: ".") else { return whole.date(from: text) }
        let afterDot = text[text.index(after: dot)...]
        let digits = afterDot.prefix { $0.isNumber }
        let zone = afterDot.dropFirst(digits.count)
        let millis = String(digits.prefix(3)).padding(toLength: 3, withPad: "0", startingAt: 0)
        return fractional.date(from: "\(text[..<dot]).\(millis)\(zone)")
    }

    static func string(_ date: Date) -> String { whole.string(from: date) }
}
