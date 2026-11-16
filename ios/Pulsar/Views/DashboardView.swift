import SwiftUI

/// All sites at a glance, worst first.
struct DashboardView: View {
    @Environment(AppModel.self) private var model
    @State private var loader = Loader<[SiteSummary]>()
    @State private var search = ""

    private var sites: [SiteSummary] {
        let all = (loader.value ?? []).sorted {
            $0.overall.severity != $1.overall.severity ? $0.overall.severity < $1.overall.severity : $0.name.localizedCaseInsensitiveCompare($1.name) == .orderedAscending
        }
        return search.isEmpty ? all : all.filter { $0.name.localizedCaseInsensitiveContains(search) }
    }

    var body: some View {
        NavigationStack {
            List {
                ErrorRow(message: loader.error)
                if let all = loader.value {
                    if all.isEmpty {
                        ContentUnavailableView("No sites yet", systemImage: "building.2", description: Text("Add a site in the Pulsar web portal."))
                    } else {
                        Section { Totals(sites: all) }
                        Section {
                            ForEach(sites) { site in
                                NavigationLink(value: SiteRef(id: site.id, name: site.name)) { SiteRow(site: site) }
                            }
                        }
                    }
                } else if loader.error == nil {
                    HStack { Spacer(); ProgressView(); Spacer() }
                        .listRowBackground(Color.clear)
                }
            }
            .navigationTitle("Sites")
            .searchable(text: $search, prompt: "Find a site")
            .refreshable { await load() }
            .pulsarDestinations()
        }
        .autoRefresh { await load() }
    }

    private func load() async {
        await loader.load { try await model.api.dashboard() }
        if let sites = loader.value { model.noteSites(sites) }
    }
}

private struct Totals: View {
    let sites: [SiteSummary]

    var body: some View {
        let down = sites.filter { $0.overall == .down }.count
        let degraded = sites.filter { $0.overall == .degraded }.count
        let incidents = sites.reduce(0) { $0 + $1.openIncidents }
        HStack {
            Stat(value: sites.count, label: sites.count == 1 ? "site" : "sites", color: .primary)
            Stat(value: down, label: "down", color: down > 0 ? .red : .secondary)
            Stat(value: degraded, label: "degraded", color: degraded > 0 ? .orange : .secondary)
            Stat(value: incidents, label: incidents == 1 ? "incident" : "incidents", color: incidents > 0 ? .red : .secondary)
        }
    }

    private struct Stat: View {
        let value: Int
        let label: String
        let color: Color

        var body: some View {
            VStack(spacing: 2) {
                Text("\(value)").font(.title2.weight(.semibold)).monospacedDigit().foregroundStyle(color)
                Text(label).font(.caption).foregroundStyle(.secondary)
            }
            .frame(maxWidth: .infinity)
            .accessibilityElement(children: .combine)
        }
    }
}

private struct SiteRow: View {
    let site: SiteSummary

    var body: some View {
        HStack(alignment: .top, spacing: 12) {
            StateIcon(state: site.overall)
                .font(.title3)
            VStack(alignment: .leading, spacing: 4) {
                Text(site.name).font(.headline)
                HStack(spacing: 10) {
                    CountChip(count: site.down, state: .down)
                    CountChip(count: site.degraded, state: .degraded)
                    CountChip(count: site.up, state: .up)
                    CountChip(count: site.unknown, state: .unknown)
                }
                Text(detail)
                    .font(.caption)
                    .foregroundStyle(site.agentsTotal > 0 && site.agentsOnline == 0 ? Color.red : Color.secondary)
            }
            Spacer()
            if site.openIncidents > 0 {
                Text("\(site.openIncidents)")
                    .font(.caption.weight(.bold))
                    .monospacedDigit()
                    .padding(.horizontal, 7)
                    .padding(.vertical, 2)
                    .background(.red, in: Capsule())
                    .foregroundStyle(.white)
                    .accessibilityLabel("\(site.openIncidents) open incidents")
            }
        }
        .padding(.vertical, 2)
    }

    private var detail: String {
        var parts: [String] = []
        if site.agentsTotal == 0 { parts.append("No agent") }
        else if site.agentsOnline == 0 { parts.append("Agent offline") }
        else if site.agentsTotal > 1 { parts.append("\(site.agentsOnline)/\(site.agentsTotal) agents online") }
        else { parts.append("Agent online") }
        parts.append("\(site.devices) \(site.devices == 1 ? "device" : "devices")")
        if let gateway = site.gateway, gateway != .up, site.gatewayAddress?.isEmpty == false { parts.append("gateway \(gateway.label.lowercased())") }
        return parts.joined(separator: " · ")
    }
}
