# MIX-MOGHIMI

MIX-MOGHIMI is a fresh Windows x64 VPN client project.

## Planned architecture
- .NET 8 WPF desktop UI, Persian RTL, modern dark glass visual system.
- sing-box as the connection engine.
- Shadowsocks server support with a local SOCKS5/HTTP proxy and Windows System Proxy integration.
- Automatic restoration of the user's previous system proxy state.
- Three hours of connection time per rolling seven-day period, enforced by the Windows-only Supabase backend.
- Server-authoritative time and monotonic cumulative traffic accounting.
- Device token protected locally with Windows DPAPI; private server configs are requested only when connecting.
- No account, profile, subscription, copy, or share screens.

## Separate Windows backend
Supabase project reference: `hxfnoaeyjnkecegpppcy`.

A dedicated `mix-api` Edge Function and `mix_windows_devices` quota table/RPCs are deployed in that project. They do not replace the existing `windows-api` or Telegram functions and do not touch the Android app or its Supabase project.

## Current verification limits
- SQL accounting, retry/idempotency, stale-heartbeat closure, rolling-week reset, and service-role-only access have been tested in transactions.
- The Windows server catalogue currently has no active rows. Add an active Windows server entry before testing an end-to-end connection.
- A connection test to the supplied test endpoint from the current network returned `Connection refused`; this does not establish whether that server is reachable from another network.
- A real Windows build and live connection test are still required. Do not treat this project as a stable release yet.
