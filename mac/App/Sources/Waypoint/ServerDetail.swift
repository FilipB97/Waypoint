import SwiftUI
import WaypointCore

struct ServerDetail: View {
    @Environment(AppModel.self) private var model
    let server: Server

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 20) {
                HStack(spacing: 14) {
                    Avatar(server: server, size: 52)
                    VStack(alignment: .leading, spacing: 3) {
                        Text(server.displayName).font(.title2.weight(.semibold))
                        Text(address).foregroundStyle(.secondary).textSelection(.enabled)
                    }
                    Spacer()
                    Button(L("act.edit")) { model.beginEdit(server) }
                    Button(L("act.connect")) { model.connect(server) }
                        .buttonStyle(.borderedProminent)
                        .keyboardShortcut(.defaultAction)
                        .disabled(!(server.proto?.supportedOnMac ?? false))
                }

                if !(server.proto?.supportedOnMac ?? false) {
                    Label(L("detail.unsupported"), systemImage: "info.circle")
                        .foregroundStyle(.secondary)
                }

                Grid(alignment: .leadingFirstTextBaseline, horizontalSpacing: 16, verticalSpacing: 8) {
                    row(L("f.protocol"), server.proto?.badge ?? server.protocolName)
                    row(L("f.host"), server.host)
                    row(L("f.port"), String(server.port))
                    if !server.username.isEmpty { row(L("f.user"), server.username) }
                    if !server.domain.isEmpty { row(L("f.domain"), server.domain) }
                    if !server.privateKeyPath.isEmpty { row(L("f.key"), server.privateKeyPath) }
                    if !server.group.isEmpty { row(L("f.group"), server.group) }
                    if !server.tags.isEmpty { row(L("f.tags"), server.tags.joined(separator: ", ")) }
                    if !server.tunnels.isEmpty { row(L("f.tunnels"), server.tunnels.joined(separator: "\n")) }
                }
                .padding(16)
                .frame(maxWidth: .infinity, alignment: .leading)
                .background(.background.secondary, in: RoundedRectangle(cornerRadius: 10))

                if !server.notes.isEmpty {
                    VStack(alignment: .leading, spacing: 6) {
                        Text(L("f.notes")).font(.headline)
                        Text(server.notes).textSelection(.enabled)
                    }
                }
            }
            .padding(24)
            .frame(maxWidth: 720, alignment: .leading)
        }
        .navigationTitle(server.displayName)
    }

    private var address: String {
        let user = server.username.isEmpty ? "" : server.username + "@"
        let port = server.port == server.proto?.defaultPort ? "" : ":\(server.port)"
        return user + server.host + port
    }

    @ViewBuilder
    private func row(_ label: String, _ value: String) -> some View {
        GridRow {
            Text(label).foregroundStyle(.secondary).gridColumnAlignment(.trailing)
            Text(value).textSelection(.enabled)
        }
    }
}
