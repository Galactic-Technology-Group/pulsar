import SwiftUI

struct AccountView: View {
    @Environment(AppModel.self) private var model
    @State private var confirmSignOut = false

    var body: some View {
        NavigationStack {
            Form {
                if let user = model.auth.user {
                    Section("Signed in as") {
                        LabeledContent("Name", value: user.name)
                        if !user.username.isEmpty { LabeledContent("Account", value: user.username) }
                        LabeledContent("Role", value: user.roleLabel)
                    }
                    if user.roles.isEmpty {
                        Section {
                            Label("Your account has no Pulsar role, so the server will refuse to show data. Ask an administrator to assign you Viewer, Operator or Admin on the Pulsar API application in Entra ID.", systemImage: "lock")
                                .font(.footnote)
                        }
                    }
                }
                NotificationsSection()
                Section("Server") {
                    LabeledContent("Address", value: model.config.apiBaseURL.host ?? model.config.apiBaseURL.absoluteString)
                }
                Section("About") {
                    LabeledContent("Version", value: version)
                    Text("Sites, devices, alert rules and credentials are managed in the Pulsar web portal.")
                        .font(.footnote)
                        .foregroundStyle(.secondary)
                }
                Section {
                    Button("Sign out", role: .destructive) { confirmSignOut = true }
                }
            }
            .navigationTitle("Account")
            .confirmationDialog("Sign out of Pulsar on this device?", isPresented: $confirmSignOut, titleVisibility: .visible) {
                Button("Sign out", role: .destructive) { Task { await model.signOut() } }
            }
        }
    }

    private var version: String {
        let info = Bundle.main.infoDictionary
        return "\(info?["CFBundleShortVersionString"] as? String ?? "?") (\(info?["CFBundleVersion"] as? String ?? "?"))"
    }
}

private struct NotificationsSection: View {
    @Environment(AppModel.self) private var model
    @Environment(\.openURL) private var openURL

    var body: some View {
        let push = model.push
        Section {
            switch push.status {
            case .on:
                Label(push.registered ? "On for this phone" : "On, registering this phone…", systemImage: "bell.badge")
                Button("Turn off on this phone", role: .destructive) { Task { await model.turnOffNotifications() } }
            case .denied:
                Label("Blocked in iOS Settings", systemImage: "bell.slash")
                Button("Open Settings") {
                    if let url = URL(string: UIApplication.openSettingsURLString) { openURL(url) }
                }
            case .off, .unknown:
                Button {
                    Task { await push.enable() }
                } label: {
                    Label("Turn on notifications", systemImage: "bell")
                }
            }
            if let error = push.lastError {
                Text(error).font(.footnote).foregroundStyle(.red)
            }
        } header: {
            Text("Notifications")
        } footer: {
            Text("Outages and recoveries arrive as notifications when an administrator has pointed an alert rule at the Phone push channel in the web portal.")
        }
        .task { await push.refresh() }
    }
}
