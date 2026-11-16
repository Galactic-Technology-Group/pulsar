import Observation
import SwiftUI

// MARK: Loading

/// Holds one screen's data: the last good value stays on screen while a refresh runs or fails.
@MainActor
@Observable
final class Loader<Value> {
    private(set) var value: Value?
    private(set) var error: String?
    private(set) var isLoading = false

    func load(_ operation: () async throws -> Value) async {
        isLoading = true
        defer { isLoading = false }
        do {
            value = try await operation()
            error = nil
        }
        catch is CancellationError { }
        catch { self.error = error.localizedDescription }
    }
}

extension View {
    /// Load when the screen appears and again every `seconds` while it stays on screen.
    func autoRefresh(every seconds: Double = 30, _ action: @escaping @MainActor () async -> Void) -> some View {
        task {
            while !Task.isCancelled {
                await action()
                try? await Task.sleep(for: .seconds(seconds))
            }
        }
    }
}

/// A one-line notice above a list when the last refresh failed.
struct ErrorRow: View {
    let message: String?

    var body: some View {
        if let message {
            Label(message, systemImage: "wifi.exclamationmark")
                .font(.footnote)
                .foregroundStyle(.red)
        }
    }
}

// MARK: State

extension CheckState {
    var color: Color {
        switch self {
        case .up: return .green
        case .degraded: return .orange
        case .down: return .red
        case .unknown: return .gray
        }
    }

    var symbol: String {
        switch self {
        case .up: return "checkmark.circle.fill"
        case .degraded: return "exclamationmark.circle.fill"
        case .down: return "xmark.octagon.fill"
        case .unknown: return "questionmark.circle"
        }
    }

    var label: String { rawValue }
}

/// State as an icon with a distinct shape per state, so it does not rely on colour alone.
struct StateIcon: View {
    let state: CheckState

    var body: some View {
        Image(systemName: state.symbol)
            .foregroundStyle(state.color)
            .accessibilityLabel(state.label)
    }
}

/// A small count with an icon, e.g. "3 down".
struct CountChip: View {
    let count: Int
    let state: CheckState

    var body: some View {
        if count > 0 {
            HStack(spacing: 3) {
                Image(systemName: state.symbol)
                Text("\(count)").monospacedDigit()
            }
            .font(.caption.weight(.medium))
            .foregroundStyle(state.color)
            .accessibilityElement(children: .ignore)
            .accessibilityLabel("\(count) \(state.label.lowercased())")
        }
    }
}

// MARK: Text

enum Ago {
    private static let formatter: RelativeDateTimeFormatter = {
        let f = RelativeDateTimeFormatter()
        f.unitsStyle = .abbreviated
        return f
    }()

    static func text(_ date: Date?) -> String {
        guard let date else { return "never" }
        if abs(date.timeIntervalSinceNow) < 5 { return "just now" }
        return formatter.localizedString(for: date, relativeTo: Date())
    }

    /// How long something lasted, e.g. "1h 12m".
    static func duration(from start: Date, to end: Date) -> String {
        let f = DateComponentsFormatter()
        f.unitsStyle = .abbreviated
        f.maximumUnitCount = 2
        f.allowedUnits = [.day, .hour, .minute, .second]
        return f.string(from: max(0, end.timeIntervalSince(start))) ?? ""
    }
}

// MARK: Navigation

struct SiteRef: Hashable {
    let id: UUID
    let name: String
}

struct CheckRef: Hashable {
    let check: Check
    let deviceName: String
}

struct MetricRef: Hashable {
    let checkId: UUID
    let key: String
    let instance: String?
    let title: String
    let template: String?
}

extension View {
    /// The screens any tab can push; applied once at the root of each NavigationStack.
    func pulsarDestinations() -> some View {
        self
            .navigationDestination(for: SiteRef.self) { SiteDetailView(site: $0) }
            .navigationDestination(for: Device.self) { DeviceDetailView(device: $0) }
            .navigationDestination(for: CheckRef.self) { CheckDetailView(ref: $0) }
            .navigationDestination(for: MetricRef.self) { MetricHistoryView(ref: $0) }
    }
}

/// The time ranges offered on charts.
enum ChartRange: Int, CaseIterable, Identifiable {
    case sixHours = 6, day = 24, week = 168

    var id: Int { rawValue }
    var label: String {
        switch self {
        case .sixHours: return "6 h"
        case .day: return "24 h"
        case .week: return "7 d"
        }
    }
}
