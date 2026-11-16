import SwiftUI

/// One site: its open incidents, agents, gateway check and devices.
struct SiteDetailView: View {
    let site: SiteRef
    @Environment(AppModel.self) private var model
    @State private var loader = Loader<Content>()
    @State private var search = ""

    struct Content {
        var agents: [Agent]
        var devices: [Device]
        var incidents: [Incident]
    }

    var body: some View {
        List {
            ErrorRow(message: loader.error)
            if let content = loader.value {
                if !content.incidents.isEmpty {
                    Section("Open incidents") {
                        ForEach(content.incidents) { IncidentRow(incident: $0, siteName: nil) }
                    }
                }

                Section("Agents") {
                    if content.agents.isEmpty { Text("No agent enrolled").foregroundStyle(.secondary) }
                    ForEach(content.agents) { AgentRow(agent: $0) }
                }

                if let gateway = content.devices.first(where: \.isGateway), !gateway.address.isEmpty {
                    Section("Gateway") {
                        NavigationLink(value: gateway) { DeviceRow(device: gateway) }
                    }
                }

                let hosts = devices(in: content)
                Section("Devices") {
                    if hosts.isEmpty { Text(search.isEmpty ? "No devices yet" : "No matching devices").foregroundStyle(.secondary) }
                    ForEach(hosts) { device in
                        NavigationLink(value: device) { DeviceRow(device: device) }
                    }
                }
            } else if loader.error == nil {
                HStack { Spacer(); ProgressView(); Spacer() }
                    .listRowBackground(Color.clear)
            }
        }
        .navigationTitle(site.name)
        .navigationBarTitleDisplayMode(.inline)
        .searchable(text: $search, prompt: "Find a device")
        .refreshable { await load() }
        .autoRefresh { await load() }
    }

    private func devices(in content: Content) -> [Device] {
        content.devices
            .filter { !$0.isGateway }
            .filter { search.isEmpty || $0.name.localizedCaseInsensitiveContains(search) || $0.address.localizedCaseInsensitiveContains(search) }
            .sorted { $0.state.severity != $1.state.severity ? $0.state.severity < $1.state.severity : $0.name.localizedCaseInsensitiveCompare($1.name) == .orderedAscending }
    }

    private func load() async {
        await loader.load {
            async let agents = model.api.agents(site: site.id)
            async let devices = model.api.devices(site: site.id)
            async let incidents = model.api.incidents(open: true, site: site.id)
            return try await Content(agents: agents, devices: devices, incidents: incidents)
        }
    }
}

private struct AgentRow: View {
    let agent: Agent

    var body: some View {
        HStack(alignment: .top, spacing: 12) {
            Image(systemName: agent.isOnline ? "antenna.radiowaves.left.and.right" : "antenna.radiowaves.left.and.right.slash")
                .foregroundStyle(agent.isOnline ? Color.green : Color.red)
                .accessibilityHidden(true)
            VStack(alignment: .leading, spacing: 2) {
                Text(agent.name)
                Text("\(agent.statusLabel) · seen \(Ago.text(agent.lastSeenUtc))\(agent.version.map { " · v\($0)" } ?? "")")
                    .font(.caption)
                    .foregroundStyle(agent.isOnline ? Color.secondary : Color.red)
                if let from = agent.rollbackFromVersion {
                    Text("Rolled back from \(from)").font(.caption).foregroundStyle(.red)
                }
            }
        }
    }
}

struct DeviceRow: View {
    let device: Device

    var body: some View {
        HStack(spacing: 12) {
            StateIcon(state: device.state)
            VStack(alignment: .leading, spacing: 2) {
                Text(device.name)
                    .foregroundStyle(device.isCritical ? Color.primary : Color.secondary)
                Text(subtitle)
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
        }
    }

    private var subtitle: String {
        var parts = [device.address]
        if let vlan = device.vlanId { parts.append("VLAN \(vlan)") }
        let checks = device.enabledChecks.count
        parts.append(checks == 0 ? "no checks" : "\(checks) \(checks == 1 ? "check" : "checks")")
        if !device.isCritical { parts.append("non-critical") }
        return parts.joined(separator: " · ")
    }
}
