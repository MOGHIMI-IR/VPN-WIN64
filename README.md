# MIX-MOGHIMI

Fresh Windows x64 proxy client, built around the official sing-box core.

## Included
- Modern Persian RTL WPF desktop UI with dark glass surfaces, background glows, light animations and Vazirmatn font.
- Shadowsocks links, using sing-box as the connection engine.
- Local HTTP/SOCKS mixed proxy and Windows System Proxy integration, with exact prior proxy settings backup/restore.
- Three hours of connection time per rolling seven-day period, calculated by PostgreSQL server time.
- Server-side cumulative download/upload accounting, retry-safe heartbeat sequence handling and an expiring lease to fail closed when the app loses contact with the backend.
- Private server configuration is requested only when the user presses connect; no copy/share controls.
- Per-user install, Start Menu shortcut, optional Desktop shortcut, uninstaller proxy recovery action.

## Build and test
GitHub Actions uses Windows Server 2022, .NET 8, pinned sing-box 1.14.4 (SHA-256 checked), and Inno Setup 6. The workflow runs unit tests, validates the sing-box config, fetches current public Shadowsocks candidates at build time, and records only nodes that pass a real HTTPS request through sing-box. It then publishes a self-contained x64 app and compiles an installable EXE.

## Windows-only backend
Supabase project ref: `hxfnoaeyjnkecegpppcy`.
Dedicated `mix-api` Edge Function and `mix_windows_devices` quota table/RPCs. The existing Windows legacy APIs/Telegram router and the separate Android app/project remain untouched.

## Safety and verification notes
- Public test nodes are third-party, short-lived and not controlled or endorsed by Moghimi. The CI report includes only nodes that passed a live HTTPS probe from that runner. A separate artifact contains those public test URIs for explicit, manual seeding into the Windows test catalogue; they are never baked into the installer or represented as Moghimi-owned servers. Do not use public nodes for bank accounts, passwords or sensitive traffic.
- A node passing from GitHub's data center does not prove that it is reachable from every ISP or from Iran. End-user ISP testing is required.
- The Windows client catalog reads active entries from `mix_windows_servers`; the API returns only the server label in the list, then sends the `ss://` URI only after device authentication and quota validation on connect.
- The application does not expose a copy/share button for Moghimi configs, but no local proxy application can guarantee that a machine administrator cannot extract config data needed by the local engine.
- A 3-hour weekly quota is based on server time and is resistant to changing the Windows clock. Hardware fingerprints are a best-effort friction, not absolute anti-VM/administrator tamper protection.
- Installer is not code-signed unless a trusted signing certificate is supplied; Windows SmartScreen may show an unknown-publisher warning.
