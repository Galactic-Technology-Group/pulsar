import Foundation

/// Build-time settings from Config/Pulsar.xcconfig, carried in Info.plist.
struct AppConfig {
    let apiBaseURL: URL
    let tenantId: String
    let clientId: String
    let apiClientId: String

    static let placeholder = "00000000-0000-0000-0000-000000000000"

    /// The custom URL scheme registered in Info.plist; Entra sends the sign-in result back on it.
    var redirectScheme: String { "msauth.\(Bundle.main.bundleIdentifier ?? "company.galactic.pulsar")" }
    var redirectURI: String { "\(redirectScheme)://auth" }
    var apiScope: String { "api://\(apiClientId)/access_as_user" }
    var authority: URL { URL(string: "https://login.microsoftonline.com/\(tenantId)/oauth2/v2.0")! }

    /// What still has to be filled in before sign-in can work; empty when the build is configured.
    var missing: [String] {
        var out: [String] = []
        if tenantId == Self.placeholder || tenantId.isEmpty { out.append("PULSAR_TENANT_ID") }
        if clientId == Self.placeholder || clientId.isEmpty { out.append("PULSAR_IOS_CLIENT_ID") }
        if apiClientId == Self.placeholder || apiClientId.isEmpty { out.append("PULSAR_API_CLIENT_ID") }
        return out
    }

    static func load(from bundle: Bundle = .main) -> AppConfig {
        func value(_ key: String) -> String {
            (bundle.object(forInfoDictionaryKey: key) as? String)?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
        }
        let base = URL(string: value("PulsarApiBaseUrl")) ?? URL(string: "https://localhost")!
        return AppConfig(apiBaseURL: base, tenantId: value("PulsarTenantId"), clientId: value("PulsarClientId"), apiClientId: value("PulsarApiClientId"))
    }
}
