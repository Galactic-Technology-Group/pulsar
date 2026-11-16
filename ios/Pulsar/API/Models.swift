import Foundation

// Shapes returned by the Pulsar API (camelCase JSON, enums as their names). Only the fields the app shows are decoded,
// and fields that newer or older API versions may omit are optional.

enum CheckState: String, Decodable, CaseIterable {
    case unknown = "Unknown", up = "Up", degraded = "Degraded", down = "Down"

    init(from decoder: Decoder) throws {
        self = CheckState(rawValue: try decoder.singleValueContainer().decode(String.self)) ?? .unknown
    }

    /// Worst first, for sorting.
    var severity: Int {
        switch self {
        case .down: return 0
        case .degraded: return 1
        case .unknown: return 2
        case .up: return 3
        }
    }
}

struct SiteSummary: Decodable, Identifiable, Hashable {
    let id: UUID
    let name: String
    let devices: Int
    let up: Int
    let down: Int
    let degraded: Int
    let unknown: Int
    let agentsOnline: Int
    let agentsTotal: Int
    let openIncidents: Int
    let gateway: CheckState?
    let gatewayLatencyMs: Int?
    let gatewayAddress: String?

    /// One state for the whole site: an offline agent or a down check is red, a degraded check amber.
    var overall: CheckState {
        if agentsTotal > 0 && agentsOnline == 0 { return .down }
        if down > 0 || gateway == .down { return .down }
        if degraded > 0 || agentsOnline < agentsTotal { return .degraded }
        if up == 0 { return .unknown }
        return .up
    }
}

struct Site: Decodable, Identifiable, Hashable {
    let id: UUID
    let name: String
    let timeZone: String?
    let vlanMode: String?
    let address: String?
}

struct AgentInterface: Decodable, Hashable {
    let name: String
    let vlanId: Int?
    let address: String
    let prefixLength: Int
}

struct Agent: Decodable, Identifiable, Hashable {
    let id: UUID
    let name: String
    let hostname: String?
    let version: String?
    let rollbackFromVersion: String?
    let lastSeenUtc: Date?
    let status: String
    let interfaces: [AgentInterface]?

    var isOnline: Bool { status == "Online" }
    var statusLabel: String {
        switch status {
        case "PendingEnrollment": return "Pending"
        default: return status
        }
    }
}

struct Check: Decodable, Identifiable, Hashable {
    let id: UUID
    let deviceId: UUID
    let type: String
    let port: Int?
    let httpPath: String?
    let intervalSec: Int
    let failureThreshold: Int
    let enabled: Bool
    let state: CheckState
    let lastResultUtc: Date?
    let lastLatencyMs: Int?
    let stateChangedUtc: Date?
    let snmpTemplate: String?
    let lastMetricsJson: String?

    var isSnmp: Bool { type == "Snmp" }

    var title: String {
        switch type {
        case "Icmp": return "Ping"
        case "TcpPort": return "TCP port \(port.map(String.init) ?? "?")"
        case "Http": return "HTTP \(httpPath ?? "/")"
        case "GatewayPing": return port == nil ? "Gateway ping" : "Gateway TCP \(port!)"
        case "Snmp": return "SNMP"
        default: return type
        }
    }

    /// The newest SNMP readings, stored on the check as a JSON array.
    var readings: [MetricSample] {
        guard let json = lastMetricsJson, let data = json.data(using: .utf8) else { return [] }
        return (try? JSONDecoder().decode([MetricSample].self, from: data)) ?? []
    }
}

struct MetricSample: Decodable, Hashable, Identifiable {
    let key: String
    let instance: String?
    let value: Double

    var id: String { "\(key)|\(instance ?? "")" }

    private enum CodingKeys: String, CodingKey { case key, instance, value, Key, Instance, Value }

    init(from decoder: Decoder) throws {
        // Accept either casing: the stored JSON follows the API's serializer settings at the time it was written.
        let c = try decoder.container(keyedBy: CodingKeys.self)
        key = try c.decodeIfPresent(String.self, forKey: .key) ?? c.decode(String.self, forKey: .Key)
        instance = try c.decodeIfPresent(String.self, forKey: .instance) ?? c.decodeIfPresent(String.self, forKey: .Instance)
        value = try c.decodeIfPresent(Double.self, forKey: .value) ?? c.decode(Double.self, forKey: .Value)
    }
}

struct Device: Decodable, Identifiable, Hashable {
    let id: UUID
    let siteId: UUID
    let name: String
    let address: String
    let macAddress: String?
    let vlanId: Int?
    let isCritical: Bool
    let tags: [String]?
    let notes: String?
    let kind: String
    let checks: [Check]?

    var isGateway: Bool { kind == "Gateway" }
    var enabledChecks: [Check] { (checks ?? []).filter(\.enabled) }
    /// The worst state among the device's enabled checks.
    var state: CheckState { enabledChecks.map(\.state).min { $0.severity < $1.severity } ?? .unknown }
}

struct Incident: Decodable, Identifiable, Hashable {
    let id: UUID
    let kind: String
    let siteId: UUID
    let checkId: UUID?
    let openedUtc: Date
    let closedUtc: Date?
    let ackBy: String?
    let ackUtc: Date?
    let summary: String

    var isOpen: Bool { closedUtc == nil }
    var kindLabel: String {
        switch kind {
        case "CheckDown": return "Check down"
        case "AgentOffline": return "Agent offline"
        case "GatewayDown": return "Gateway unreachable"
        case "Threshold": return "Limit crossed"
        default: return kind
        }
    }
    var symbol: String {
        switch kind {
        case "AgentOffline": return "antenna.radiowaves.left.and.right.slash"
        case "GatewayDown": return "network.slash"
        case "Threshold": return "gauge.with.dots.needle.67percent"
        default: return "exclamationmark.triangle.fill"
        }
    }
}

struct LatencyPoint: Decodable, Identifiable, Hashable {
    let bucket: Date
    let samples: Int
    let okSamples: Int
    let avgLatencyMs: Double?
    let maxLatencyMs: Int?

    var id: Date { bucket }
    var availability: Double { samples == 0 ? 1 : Double(okSamples) / Double(samples) }
}

struct MetricPoint: Decodable, Identifiable, Hashable {
    let time: Date
    let value: Double
    let min: Double?
    let max: Double?

    var id: Date { time }
}

struct SnmpTemplateMetric: Decodable, Hashable {
    let key: String
    let name: String
    let unit: String?
    let table: Bool?
    let values: [String: String]?

    /// A reading as text: enum readings as their names, traffic with SI prefixes, everything else with its unit.
    func format(_ value: Double) -> String {
        if let values, let name = values[String(Int(value.rounded()))] { return name }
        if unit == "bit/s" {
            let steps: [(Double, String)] = [(1e9, "Gbit/s"), (1e6, "Mbit/s"), (1e3, "kbit/s")]
            for (size, label) in steps where abs(value) >= size { return "\(Self.number(value / size)) \(label)" }
            return "\(Self.number(value)) bit/s"
        }
        let text = Self.number(value)
        return unit.map { "\(text) \($0)" } ?? text
    }

    private static func number(_ value: Double) -> String {
        let f = NumberFormatter()
        f.numberStyle = .decimal
        f.maximumFractionDigits = abs(value) >= 100 ? 0 : (abs(value) >= 10 ? 1 : 2)
        return f.string(from: value as NSNumber) ?? String(value)
    }
}

struct SnmpTemplate: Decodable, Hashable {
    let key: String
    let name: String
    let metrics: [SnmpTemplateMetric]

    func metric(_ key: String) -> SnmpTemplateMetric? { metrics.first { $0.key == key } }
}
