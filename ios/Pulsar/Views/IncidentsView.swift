import SwiftUI

/// Open incidents across all sites, or the recent history including closed ones.
struct IncidentsView: View {
    @Environment(AppModel.self) private var model
    @State private var loader = Loader<[Incident]>()
    @State private var showClosed = false
    @State private var actionError: String?

    var body: some View {
        NavigationStack {
            List {
                Picker("Show", selection: $showClosed) {
                    Text("Open").tag(false)
                    Text("Recent").tag(true)
                }
                .pickerStyle(.segmented)
                .listRowBackground(Color.clear)
                .listRowInsets(EdgeInsets())

                ErrorRow(message: actionError ?? loader.error)

                if let incidents = loader.value {
                    if incidents.isEmpty {
                        ContentUnavailableView(showClosed ? "No incidents recorded" : "All clear", systemImage: "checkmark.seal",
                                               description: Text(showClosed ? "Nothing has gone down yet." : "No open incidents at any site."))
                    }
                    ForEach(incidents) { incident in
                        NavigationLink(value: SiteRef(id: incident.siteId, name: model.siteNames[incident.siteId] ?? "Site")) {
                            IncidentRow(incident: incident, siteName: model.siteNames[incident.siteId])
                        }
                        .swipeActions(edge: .trailing) {
                            if incident.isOpen, incident.ackBy == nil, model.auth.user?.canWrite == true {
                                Button("Acknowledge") { Task { await acknowledge(incident) } }
                                    .tint(.blue)
                            }
                        }
                    }
                } else if loader.error == nil {
                    HStack { Spacer(); ProgressView(); Spacer() }
                        .listRowBackground(Color.clear)
                }
            }
            .navigationTitle("Incidents")
            .refreshable { await load() }
            .pulsarDestinations()
        }
        .task(id: showClosed) {
            while !Task.isCancelled {
                await load()
                try? await Task.sleep(for: .seconds(30))
            }
        }
    }

    private func load() async {
        let closed = showClosed
        await loader.load { try await model.api.incidents(open: !closed, limit: closed ? 100 : 200) }
        if !closed, let open = loader.value { model.openIncidents = open.count }
        if model.siteNames.isEmpty, let sites = try? await model.api.dashboard() { model.noteSites(sites) }
    }

    private func acknowledge(_ incident: Incident) async {
        actionError = nil
        do {
            try await model.api.acknowledge(incident: incident.id)
            await load()
        }
        catch { actionError = error.localizedDescription }
    }
}

struct IncidentRow: View {
    let incident: Incident
    /// Shown when the list spans sites; nil on a site's own page.
    let siteName: String?

    var body: some View {
        HStack(alignment: .top, spacing: 12) {
            Image(systemName: incident.isOpen ? incident.symbol : "checkmark.circle")
                .foregroundStyle(incident.isOpen ? Color.red : Color.secondary)
                .accessibilityLabel(incident.isOpen ? "Open" : "Closed")
            VStack(alignment: .leading, spacing: 3) {
                Text(incident.summary.isEmpty ? incident.kindLabel : incident.summary)
                    .font(.subheadline)
                Text(line)
                    .font(.caption)
                    .foregroundStyle(.secondary)
                if let by = incident.ackBy, incident.isOpen {
                    Label("Acknowledged by \(by)", systemImage: "hand.thumbsup")
                        .font(.caption)
                        .foregroundStyle(.blue)
                }
            }
        }
        .padding(.vertical, 2)
    }

    private var line: String {
        var parts: [String] = []
        if let siteName { parts.append(siteName) }
        parts.append(incident.kindLabel)
        if let closed = incident.closedUtc {
            parts.append("lasted \(Ago.duration(from: incident.openedUtc, to: closed))")
            parts.append("ended \(Ago.text(closed))")
        } else {
            parts.append("started \(Ago.text(incident.openedUtc))")
        }
        return parts.joined(separator: " · ")
    }
}
