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
                Button("Sign out", role: .destructive) { model.auth.signOut() }
            }
        }
    }

    private var version: String {
        let info = Bundle.main.infoDictionary
        return "\(info?["CFBundleShortVersionString"] as? String ?? "?") (\(info?["CFBundleVersion"] as? String ?? "?"))"
    }
}
