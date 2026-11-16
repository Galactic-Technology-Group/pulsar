import Observation
import SwiftUI
import UIKit

/// App-wide state: the signed-in session, the API client, and small lookups several screens share.
@MainActor
@Observable
final class AppModel {
    let config: AppConfig
    let auth: AuthService
    let api: ApiClient
    let push = PushManager.shared

    /// Open incidents across all sites, shown as the badge on the Incidents tab.
    var openIncidents = 0
    private(set) var siteNames: [UUID: String] = [:]
    private(set) var templates: [String: SnmpTemplate] = [:]

    init() {
        config = AppConfig.load()
        auth = AuthService(config: config)
        api = ApiClient(baseURL: config.apiBaseURL, auth: auth)
        push.upload = { [api] token in
            try await api.registerPush(token: token, environment: PushManager.environment, deviceName: UIDevice.current.name)
        }
    }

    /// Stop alerts to this phone before the session goes away; without a token the server could not be told.
    func signOut() async {
        if let token = push.token { try? await api.unregisterPush(token: token) }
        auth.signOut()
    }

    func turnOffNotifications() async {
        if let token = push.disable() { try? await api.unregisterPush(token: token) }
    }

    func noteSites(_ sites: [SiteSummary]) {
        siteNames = Dictionary(uniqueKeysWithValues: sites.map { ($0.id, $0.name) })
        openIncidents = sites.reduce(0) { $0 + $1.openIncidents }
    }

    /// SNMP templates name and format readings; they change only with an API release, so one fetch per launch is enough.
    func loadTemplatesIfNeeded() async {
        guard templates.isEmpty, let list = try? await api.snmpTemplates() else { return }
        templates = Dictionary(list.map { ($0.key, $0) }, uniquingKeysWith: { first, _ in first })
    }
}

@main
struct PulsarApp: App {
    @UIApplicationDelegateAdaptor(AppDelegate.self) private var appDelegate
    @State private var model = AppModel()

    var body: some Scene {
        WindowGroup {
            RootView()
                .environment(model)
        }
    }
}

struct RootView: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        Group {
            if model.auth.isRestoring {
                ProgressView("Signing in…")
            } else if model.auth.user == nil {
                SignInView()
            } else {
                MainTabs()
            }
        }
        .task { await model.auth.restore() }
    }
}

struct MainTabs: View {
    @Environment(AppModel.self) private var model
    @State private var tab = Tab.sites

    enum Tab { case sites, incidents, account }

    var body: some View {
        TabView(selection: $tab) {
            DashboardView()
                .tabItem { Label("Sites", systemImage: "building.2") }
                .tag(Tab.sites)
            IncidentsView()
                .tabItem { Label("Incidents", systemImage: "exclamationmark.triangle") }
                .badge(model.openIncidents)
                .tag(Tab.incidents)
            AccountView()
                .tabItem { Label("Account", systemImage: "person.crop.circle") }
                .tag(Tab.account)
        }
        .task { await model.push.refresh() }
        .onAppear { showIncidentsIfAsked() }
        .onChange(of: model.push.openIncidents) { _, _ in showIncidentsIfAsked() }
    }

    /// A tapped notification opens the incident list.
    private func showIncidentsIfAsked() {
        guard model.push.openIncidents else { return }
        tab = .incidents
        model.push.openIncidents = false
    }
}
