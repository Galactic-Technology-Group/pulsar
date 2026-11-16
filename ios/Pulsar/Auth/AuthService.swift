import AuthenticationServices
import CryptoKit
import Foundation
import Observation
import UIKit

struct SignedInUser: Equatable {
    var name: String
    var username: String
    var roles: [String]

    var isAdmin: Bool { roles.contains("Nms.Admin") }
    /// Admins and operators may acknowledge incidents and run checks; viewers only read.
    var canWrite: Bool { isAdmin || roles.contains("Nms.Operator") }
    var roleLabel: String {
        if isAdmin { return "Administrator" }
        if roles.contains("Nms.Operator") { return "Operator" }
        if roles.contains("Nms.Viewer") { return "Viewer" }
        return "No Pulsar role"
    }
}

enum AuthError: LocalizedError {
    case notConfigured([String])
    case cancelled
    case signedOut
    /// Entra refused the stored sign-in (revoked, expired, password changed): the user has to sign in again.
    case rejected(String)
    case failed(String)

    var errorDescription: String? {
        switch self {
        case .notConfigured(let keys): return "This build is not configured. Set \(keys.joined(separator: ", ")) in Config/Pulsar.xcconfig."
        case .cancelled: return "Sign-in was cancelled."
        case .signedOut: return "You are signed out."
        case .rejected(let why): return "Please sign in again. \(why)"
        case .failed(let why): return why
        }
    }
}

/// Signs the user in to Entra ID with the authorization-code flow and PKCE in the system browser sheet, then keeps an
/// access token for the Pulsar API fresh. No client secret: the app is a public client.
@MainActor
@Observable
final class AuthService {
    let config: AppConfig
    private(set) var user: SignedInUser?
    /// True while a stored sign-in is being restored at launch.
    private(set) var isRestoring = true
    private(set) var isSigningIn = false
    var lastError: String?

    @ObservationIgnored private var accessToken: String?
    @ObservationIgnored private var expires = Date.distantPast
    @ObservationIgnored private var refreshTask: Task<String, Error>?
    @ObservationIgnored private var session: ASWebAuthenticationSession?
    @ObservationIgnored private let anchor = PresentationAnchor()
    private static let refreshKey = "refresh-token"

    init(config: AppConfig) { self.config = config }

    // MARK: Session

    /// Called once at launch: use the stored refresh token, if any, to sign back in silently.
    func restore() async {
        defer { isRestoring = false }
        guard config.missing.isEmpty, Keychain.get(Self.refreshKey) != nil else { return }
        do { _ = try await validAccessToken(forceRefresh: true) }
        catch AuthError.rejected { /* cleared; the sign-in screen shows */ }
        catch { lastError = "Could not reach the sign-in service. Check your connection and sign in again." }
    }

    func signIn() async {
        lastError = nil
        guard config.missing.isEmpty else { lastError = AuthError.notConfigured(config.missing).localizedDescription; return }
        isSigningIn = true
        defer { isSigningIn = false }
        do {
            let verifier = Self.randomToken(bytes: 48)
            let state = Self.randomToken(bytes: 16)
            let callback = try await authorize(challenge: Self.challenge(for: verifier), state: state)
            let items = URLComponents(url: callback, resolvingAgainstBaseURL: false)?.queryItems ?? []
            func item(_ name: String) -> String? { items.first { $0.name == name }?.value }
            if let error = item("error") { throw AuthError.failed(item("error_description") ?? error) }
            guard item("state") == state, let code = item("code") else { throw AuthError.failed("The sign-in response was not valid.") }
            _ = try await redeem(["grant_type": "authorization_code", "code": code, "redirect_uri": config.redirectURI, "code_verifier": verifier])
        }
        catch AuthError.cancelled { }
        catch { lastError = error.localizedDescription }
    }

    func signOut() {
        Keychain.remove(Self.refreshKey)
        accessToken = nil
        expires = .distantPast
        user = nil
    }

    /// A token for the API, refreshed when it is within a minute of expiring.
    func validAccessToken(forceRefresh: Bool = false) async throws -> String {
        if !forceRefresh, let token = accessToken, expires.timeIntervalSinceNow > 60 { return token }
        if let running = refreshTask { return try await running.value }
        guard let refresh = Keychain.get(Self.refreshKey) else { signOut(); throw AuthError.signedOut }
        let task = Task<String, Error> { try await self.redeem(["grant_type": "refresh_token", "refresh_token": refresh]) }
        refreshTask = task
        defer { refreshTask = nil }
        do { return try await task.value }
        catch AuthError.rejected(let why) { signOut(); throw AuthError.rejected(why) }
    }

    // MARK: Entra

    private func authorize(challenge: String, state: String) async throws -> URL {
        var components = URLComponents(url: config.authority.appendingPathComponent("authorize"), resolvingAgainstBaseURL: false)!
        components.queryItems = [
            .init(name: "client_id", value: config.clientId),
            .init(name: "response_type", value: "code"),
            .init(name: "redirect_uri", value: config.redirectURI),
            .init(name: "response_mode", value: "query"),
            .init(name: "scope", value: scopes),
            .init(name: "state", value: state),
            .init(name: "code_challenge", value: challenge),
            .init(name: "code_challenge_method", value: "S256"),
            .init(name: "prompt", value: "select_account"),
        ]
        let url = components.url!
        let scheme = config.redirectScheme
        return try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<URL, Error>) in
            let session = ASWebAuthenticationSession(url: url, callbackURLScheme: scheme) { callback, error in
                if let callback { continuation.resume(returning: callback) }
                else if let error = error as? ASWebAuthenticationSessionError, error.code == .canceledLogin { continuation.resume(throwing: AuthError.cancelled) }
                else { continuation.resume(throwing: AuthError.failed(error?.localizedDescription ?? "Sign-in failed.")) }
            }
            session.presentationContextProvider = anchor
            self.session = session
            if !session.start() { continuation.resume(throwing: AuthError.failed("The sign-in page could not be opened.")) }
        }
    }

    private var scopes: String { "\(config.apiScope) openid profile offline_access" }

    /// Trade an authorization code or a refresh token for tokens, store them, and return the access token.
    private func redeem(_ grant: [String: String]) async throws -> String {
        var form = grant
        form["client_id"] = config.clientId
        form["scope"] = scopes
        var request = URLRequest(url: config.authority.appendingPathComponent("token"))
        request.httpMethod = "POST"
        request.setValue("application/x-www-form-urlencoded", forHTTPHeaderField: "Content-Type")
        request.httpBody = Data(form.map { "\(Self.formEncode($0.key))=\(Self.formEncode($0.value))" }.joined(separator: "&").utf8)

        let data: Data
        do { (data, _) = try await URLSession.shared.data(for: request) }
        catch { throw AuthError.failed("Could not reach the sign-in service: \(error.localizedDescription)") }
        guard let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else { throw AuthError.failed("The sign-in service sent an unreadable reply.") }

        guard let token = json["access_token"] as? String else {
            let code = json["error"] as? String ?? "error"
            let text = (json["error_description"] as? String)?.components(separatedBy: "\r\n").first ?? code
            // These mean the stored sign-in is no longer good; anything else may be temporary.
            if ["invalid_grant", "interaction_required", "consent_required", "login_required"].contains(code) { throw AuthError.rejected(text) }
            throw AuthError.failed(text)
        }
        accessToken = token
        expires = Date().addingTimeInterval((json["expires_in"] as? Double) ?? 3000)
        if let refresh = json["refresh_token"] as? String { Keychain.set(refresh, for: Self.refreshKey) }

        let access = Self.claims(of: token)
        let id = (json["id_token"] as? String).map(Self.claims(of:)) ?? [:]
        let username = (id["preferred_username"] as? String) ?? (access["preferred_username"] as? String) ?? (access["upn"] as? String) ?? user?.username ?? ""
        let name = (id["name"] as? String) ?? (access["name"] as? String) ?? user?.name ?? username
        user = SignedInUser(name: name, username: username, roles: (access["roles"] as? [String]) ?? [])
        lastError = nil
        return token
    }

    // MARK: Helpers

    /// The payload of a JWT, read only to show the user's name and roles. The API validates the token itself.
    static func claims(of jwt: String) -> [String: Any] {
        let parts = jwt.split(separator: ".")
        guard parts.count >= 2 else { return [:] }
        var base64 = parts[1].replacingOccurrences(of: "-", with: "+").replacingOccurrences(of: "_", with: "/")
        while base64.count % 4 != 0 { base64.append("=") }
        guard let data = Data(base64Encoded: base64), let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else { return [:] }
        return json
    }

    static func randomToken(bytes: Int) -> String {
        var buffer = [UInt8](repeating: 0, count: bytes)
        _ = SecRandomCopyBytes(kSecRandomDefault, bytes, &buffer)
        return base64URL(Data(buffer))
    }

    static func challenge(for verifier: String) -> String { base64URL(Data(SHA256.hash(data: Data(verifier.utf8)))) }

    static func base64URL(_ data: Data) -> String {
        data.base64EncodedString().replacingOccurrences(of: "+", with: "-").replacingOccurrences(of: "/", with: "_").replacingOccurrences(of: "=", with: "")
    }

    private static let formAllowed = CharacterSet(charactersIn: "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-._~")
    static func formEncode(_ value: String) -> String { value.addingPercentEncoding(withAllowedCharacters: formAllowed) ?? value }
}

/// Tells the sign-in sheet which window to appear over.
@MainActor
private final class PresentationAnchor: NSObject, ASWebAuthenticationPresentationContextProviding {
    func presentationAnchor(for session: ASWebAuthenticationSession) -> ASPresentationAnchor {
        let scenes = UIApplication.shared.connectedScenes.compactMap { $0 as? UIWindowScene }
        return scenes.flatMap { $0.windows }.first { $0.isKeyWindow } ?? ASPresentationAnchor()
    }
}
