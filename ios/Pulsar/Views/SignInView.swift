import SwiftUI

struct SignInView: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        VStack(spacing: 20) {
            Spacer()
            Image(systemName: "dot.radiowaves.left.and.right")
                .font(.system(size: 56, weight: .semibold))
                .foregroundStyle(.tint)
                .accessibilityHidden(true)
            Text("Pulsar")
                .font(.largeTitle.bold())
            Text("Network monitoring for your sites")
                .foregroundStyle(.secondary)
            Spacer()

            if !model.config.missing.isEmpty {
                Label("This build has no sign-in settings yet. Set \(model.config.missing.joined(separator: ", ")) in Config/Pulsar.xcconfig and rebuild.", systemImage: "wrench.and.screwdriver")
                    .font(.footnote)
                    .foregroundStyle(.secondary)
            }
            if let error = model.auth.lastError {
                Text(error)
                    .font(.footnote)
                    .foregroundStyle(.red)
                    .multilineTextAlignment(.center)
            }

            Button {
                Task { await model.auth.signIn() }
            } label: {
                HStack {
                    if model.auth.isSigningIn { ProgressView().tint(.white) }
                    Text("Sign in with Microsoft")
                }
                .frame(maxWidth: .infinity)
            }
            .buttonStyle(.borderedProminent)
            .controlSize(.large)
            .disabled(model.auth.isSigningIn || !model.config.missing.isEmpty)

            Text(model.config.apiBaseURL.host ?? "")
                .font(.caption)
                .foregroundStyle(.tertiary)
        }
        .padding(24)
        .frame(maxWidth: 480)
    }
}
