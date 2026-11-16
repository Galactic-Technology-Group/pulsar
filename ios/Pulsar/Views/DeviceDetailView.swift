import Charts
import SwiftUI

/// One device and its checks.
struct DeviceDetailView: View {
    let device: Device
    @Environment(AppModel.self) private var model
    @State private var loader = Loader<Device>()

    private var current: Device { loader.value ?? device }

    var body: some View {
        List {
            ErrorRow(message: loader.error)
            Section {
                LabeledContent("Address", value: current.address)
                if let mac = current.macAddress, !mac.isEmpty { LabeledContent("MAC", value: mac) }
                if let vlan = current.vlanId { LabeledContent("VLAN", value: String(vlan)) }
                LabeledContent("Alerts", value: current.isCritical ? "Pages when down" : "Recorded only")
                if let tags = current.tags, !tags.isEmpty { LabeledContent("Tags", value: tags.joined(separator: ", ")) }
                if let notes = current.notes, !notes.isEmpty { Text(notes).font(.footnote).foregroundStyle(.secondary) }
            }
            Section("Checks") {
                let checks = (current.checks ?? []).sorted { $0.state.severity < $1.state.severity }
                if checks.isEmpty { Text("No checks").foregroundStyle(.secondary) }
                ForEach(checks) { check in
                    NavigationLink(value: CheckRef(check: check, deviceName: current.name)) { CheckRow(check: check) }
                }
            }
        }
        .navigationTitle(current.name)
        .navigationBarTitleDisplayMode(.inline)
        .refreshable { await load() }
        .autoRefresh { await load() }
    }

    private func load() async { await loader.load { try await model.api.device(device.id) } }
}

private struct CheckRow: View {
    let check: Check

    var body: some View {
        HStack(spacing: 12) {
            StateIcon(state: check.enabled ? check.state : .unknown)
            VStack(alignment: .leading, spacing: 2) {
                Text(check.title)
                Text(subtitle).font(.caption).foregroundStyle(.secondary)
            }
        }
    }

    private var subtitle: String {
        if !check.enabled { return "Disabled" }
        var parts = [check.state.label]
        if let ms = check.lastLatencyMs { parts.append("\(ms) ms") }
        parts.append("checked \(Ago.text(check.lastResultUtc))")
        return parts.joined(separator: " · ")
    }
}

/// One check: state, response-time history, and for SNMP checks the latest readings.
struct CheckDetailView: View {
    let ref: CheckRef
    @Environment(AppModel.self) private var model
    @State private var history = Loader<[LatencyPoint]>()
    @State private var range = ChartRange.day
    @State private var runMessage: String?
    @State private var running = false

    private var check: Check { ref.check }

    var body: some View {
        List {
            Section {
                HStack {
                    StateIcon(state: check.state).font(.title2)
                    VStack(alignment: .leading) {
                        Text(check.state.label).font(.headline)
                        if let changed = check.stateChangedUtc { Text("since \(Ago.text(changed))").font(.caption).foregroundStyle(.secondary) }
                    }
                }
                LabeledContent("Last result", value: Ago.text(check.lastResultUtc))
                LabeledContent("Interval", value: "\(check.intervalSec) s")
                LabeledContent("Down after", value: "\(check.failureThreshold) failed \(check.failureThreshold == 1 ? "poll" : "polls")")
            }

            Section("Response time") {
                Picker("Range", selection: $range) {
                    ForEach(ChartRange.allCases) { Text($0.label).tag($0) }
                }
                .pickerStyle(.segmented)
                ErrorRow(message: history.error)
                if let points = history.value {
                    LatencyChart(points: points)
                } else if history.error == nil {
                    ProgressView().frame(maxWidth: .infinity, minHeight: 160)
                }
            }

            if check.isSnmp { ReadingsSections(check: check) }

            if model.auth.user?.canWrite == true, check.enabled, check.type != "GatewayPing" {
                Section {
                    Button {
                        Task { await runNow() }
                    } label: {
                        Label(running ? "Asking the agent…" : "Run this check now", systemImage: "arrow.clockwise")
                    }
                    .disabled(running)
                } footer: {
                    if let runMessage { Text(runMessage) }
                }
            }
        }
        .navigationTitle("\(ref.deviceName) · \(check.title)")
        .navigationBarTitleDisplayMode(.inline)
        .task(id: range) { await history.load { try await model.api.latency(check: check.id, hours: range.rawValue) } }
        .task { await model.loadTemplatesIfNeeded() }
    }

    private func runNow() async {
        running = true
        defer { running = false }
        do {
            try await model.api.runNow(check: check.id)
            runMessage = "Sent to the site's agent. The result shows on the device page within a few seconds."
        }
        catch { runMessage = error.localizedDescription }
    }
}

private struct LatencyChart: View {
    let points: [LatencyPoint]

    var body: some View {
        let measured = points.filter { $0.avgLatencyMs != nil }
        let failed = points.filter { $0.samples > 0 && $0.okSamples < $0.samples }
        if points.isEmpty {
            Text("No results in this range").foregroundStyle(.secondary).frame(maxWidth: .infinity, minHeight: 80)
        } else {
            VStack(alignment: .leading, spacing: 6) {
                Chart {
                    ForEach(measured) { point in
                        LineMark(x: .value("Time", point.bucket), y: .value("Average ms", point.avgLatencyMs ?? 0))
                            .interpolationMethod(.monotone)
                    }
                    ForEach(failed) { point in
                        RuleMark(x: .value("Time", point.bucket))
                            .foregroundStyle(.red.opacity(0.35))
                    }
                }
                .chartYAxisLabel("ms")
                .frame(height: 180)
                .accessibilityLabel("Response time chart")
                if !failed.isEmpty {
                    Label("Red lines mark periods with failed polls", systemImage: "minus")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }
            }
        }
    }
}

/// The newest SNMP readings: single values first, then one group per table row (interface, probe, processor).
private struct ReadingsSections: View {
    let check: Check
    @Environment(AppModel.self) private var model

    var body: some View {
        let template = check.snmpTemplate.flatMap { model.templates[$0] }
        let readings = check.readings
        let scalars = readings.filter { ($0.instance ?? "").isEmpty }
        let rows = Dictionary(grouping: readings.filter { !($0.instance ?? "").isEmpty }, by: { $0.instance ?? "" })

        if readings.isEmpty {
            Section("Readings") {
                Text("No readings yet. They appear after the agent's next poll.").foregroundStyle(.secondary)
            }
        }
        if !scalars.isEmpty {
            Section(template?.name ?? "Readings") {
                ForEach(scalars) { ReadingRow(checkId: check.id, sample: $0, template: template, templateKey: check.snmpTemplate) }
            }
        }
        if !rows.isEmpty {
            Section(scalars.isEmpty ? (template?.name ?? "Readings") : "By row") {
                ForEach(rows.keys.sorted { $0.localizedStandardCompare($1) == .orderedAscending }, id: \.self) { name in
                    DisclosureGroup(name) {
                        ForEach(rows[name] ?? []) { ReadingRow(checkId: check.id, sample: $0, template: template, templateKey: check.snmpTemplate) }
                    }
                }
            }
        }
    }
}

private struct ReadingRow: View {
    let checkId: UUID
    let sample: MetricSample
    let template: SnmpTemplate?
    let templateKey: String?

    var body: some View {
        let metric = template?.metric(sample.key)
        let name = metric?.name ?? sample.key
        let title = (sample.instance ?? "").isEmpty ? name : "\(sample.instance ?? "") · \(name)"
        NavigationLink(value: MetricRef(checkId: checkId, key: sample.key, instance: sample.instance, title: title, template: templateKey)) {
            LabeledContent(name, value: metric?.format(sample.value) ?? String(format: "%.2f", sample.value))
        }
    }
}

/// History of one SNMP reading.
struct MetricHistoryView: View {
    let ref: MetricRef
    @Environment(AppModel.self) private var model
    @State private var loader = Loader<[MetricPoint]>()
    @State private var range = ChartRange.day

    var body: some View {
        let metric = ref.template.flatMap { model.templates[$0] }?.metric(ref.key)
        List {
            Section {
                Picker("Range", selection: $range) {
                    ForEach(ChartRange.allCases) { Text($0.label).tag($0) }
                }
                .pickerStyle(.segmented)
                ErrorRow(message: loader.error)
                if let points = loader.value {
                    if points.isEmpty {
                        Text("No readings in this range").foregroundStyle(.secondary).frame(maxWidth: .infinity, minHeight: 80)
                    } else {
                        Chart(points) { point in
                            if let low = point.min, let high = point.max {
                                AreaMark(x: .value("Time", point.time), yStart: .value("Lowest", low), yEnd: .value("Highest", high))
                                    .foregroundStyle(.tint.opacity(0.2))
                            }
                            LineMark(x: .value("Time", point.time), y: .value("Value", point.value))
                                .interpolationMethod(.monotone)
                        }
                        .chartYAxisLabel(metric?.unit ?? "")
                        .frame(height: 220)
                        .accessibilityLabel("\(ref.title) history chart")
                    }
                } else if loader.error == nil {
                    ProgressView().frame(maxWidth: .infinity, minHeight: 220)
                }
            }
            if let points = loader.value, let last = points.last, let low = points.map(\.value).min(), let high = points.map(\.value).max() {
                Section {
                    LabeledContent("Latest", value: text(last.value, metric))
                    LabeledContent("Lowest", value: text(low, metric))
                    LabeledContent("Highest", value: text(high, metric))
                }
            }
        }
        .navigationTitle(ref.title)
        .navigationBarTitleDisplayMode(.inline)
        .task(id: range) {
            await loader.load { try await model.api.metricHistory(check: ref.checkId, metric: ref.key, instance: ref.instance, hours: range.rawValue) }
        }
    }

    private func text(_ value: Double, _ metric: SnmpTemplateMetric?) -> String {
        metric?.format(value) ?? String(format: "%.2f", value)
    }
}
