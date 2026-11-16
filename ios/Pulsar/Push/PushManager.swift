import Observation
import UIKit
import UserNotifications

/// Asks for notification permission, gets this phone's push token from Apple and hands it to the Pulsar API, which
/// stores it so the worker can send alerts here.
@MainActor
@Observable
final class PushManager: NSObject, UNUserNotificationCenterDelegate {
    static let shared = PushManager()

    enum Status {
        case unknown
        /// Not asked yet, or turned off in the app.
        case off
        /// Refused in iOS Settings; only the user can change that there.
        case denied
        case on
    }

    private(set) var status: Status = .unknown
    private(set) var registered = false
    private(set) var lastError: String?
    /// Set when the user taps a notification; the tab bar switches to Incidents and clears it.
    var openIncidents = false

    /// Sends the token to the API; set by AppModel once the API client exists.
    @ObservationIgnored var upload: ((String) async throws -> Void)?
    @ObservationIgnored private(set) var token: String?

    private static let optOutKey = "push.optOut"
    private var optedOut: Bool {
        get { UserDefaults.standard.bool(forKey: Self.optOutKey) }
        set { UserDefaults.standard.set(newValue, forKey: Self.optOutKey) }
    }

    /// Debug builds from Xcode get tokens for Apple's sandbox service; TestFlight and App Store builds for production.
    static var environment: String {
        #if DEBUG
        return "sandbox"
        #else
        return "production"
        #endif
    }

    /// Called after sign-in and whenever the Account tab appears: if iOS already allows notifications, (re)register so
    /// the server always has this phone's current token.
    func refresh() async {
        let settings = await UNUserNotificationCenter.current().notificationSettings()
        switch settings.authorizationStatus {
        case .denied:
            status = .denied
        case .authorized, .provisional, .ephemeral:
            if optedOut { status = .off } else { status = .on; UIApplication.shared.registerForRemoteNotifications() }
        default:
            status = .off
        }
    }

    /// The user tapped "Turn on notifications".
    func enable() async {
        lastError = nil
        optedOut = false
        do { _ = try await UNUserNotificationCenter.current().requestAuthorization(options: [.alert, .sound]) }
        catch { lastError = error.localizedDescription }
        await refresh()
    }

    /// The user turned notifications off in the app. Returns the token so the caller can remove it from the server.
    func disable() -> String? {
        optedOut = true
        status = .off
        registered = false
        UIApplication.shared.unregisterForRemoteNotifications()
        return token
    }

    // MARK: From the app delegate

    func didRegister(deviceToken: Data) {
        let hex = deviceToken.map { String(format: "%02x", $0) }.joined()
        token = hex
        guard !optedOut else { return }
        Task { await send(hex) }
    }

    func didFailToRegister(_ error: Error) {
        registered = false
        lastError = "This device could not register for notifications: \(error.localizedDescription)"
    }

    private func send(_ hex: String) async {
        guard let upload else { return }
        do {
            try await upload(hex)
            registered = true
            lastError = nil
        }
        catch is CancellationError { }
        catch {
            registered = false
            lastError = "Could not register this phone with Pulsar: \(error.localizedDescription)"
        }
    }

    // MARK: UNUserNotificationCenterDelegate

    /// Show alerts as banners even while the app is open.
    nonisolated func userNotificationCenter(_ center: UNUserNotificationCenter, willPresent notification: UNNotification) async -> UNNotificationPresentationOptions {
        [.banner, .list, .sound]
    }

    nonisolated func userNotificationCenter(_ center: UNUserNotificationCenter, didReceive response: UNNotificationResponse) async {
        await MainActor.run { self.openIncidents = true }
    }
}

final class AppDelegate: NSObject, UIApplicationDelegate {
    func application(_ application: UIApplication, didFinishLaunchingWithOptions launchOptions: [UIApplication.LaunchOptionsKey: Any]? = nil) -> Bool {
        UNUserNotificationCenter.current().delegate = PushManager.shared
        return true
    }

    func application(_ application: UIApplication, didRegisterForRemoteNotificationsWithDeviceToken deviceToken: Data) {
        PushManager.shared.didRegister(deviceToken: deviceToken)
    }

    func application(_ application: UIApplication, didFailToRegisterForRemoteNotificationsWithError error: Error) {
        PushManager.shared.didFailToRegister(error)
    }
}
