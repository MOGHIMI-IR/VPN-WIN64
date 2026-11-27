import "jsr:@supabase/functions-js/edge-runtime.d.ts";

const SUPABASE_URL = (Deno.env.get("SUPABASE_URL") ?? "").replace(/\/$/, "");
const LEGACY_SERVICE_KEY = Deno.env.get("SUPABASE_SERVICE_ROLE_KEY") ?? "";
const SERVICE_KEY = (() => {
  if (LEGACY_SERVICE_KEY) return LEGACY_SERVICE_KEY;
  try {
    const keys = JSON.parse(Deno.env.get("SUPABASE_SECRET_KEYS") ?? "{}");
    return String(keys.default ?? "");
  } catch {
    return "";
  }
})();

const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), {
  status,
  headers: {
    "content-type": "application/json; charset=utf-8",
    "cache-control": "no-store, max-age=0",
    "access-control-allow-origin": "*",
    "access-control-allow-methods": "POST, OPTIONS",
    "access-control-allow-headers": "content-type, apikey, authorization, x-mix-device-token, x-mix-installation-hash"
  }
});

function dbHeaders(prefer?: string): Headers {
  const result = new Headers({
    apikey: SERVICE_KEY,
    "content-type": "application/json"
  });
  // Legacy service_role keys are JWTs; newer sb_secret keys must be sent only as apikey.
  if (LEGACY_SERVICE_KEY) result.set("authorization", `Bearer ${LEGACY_SERVICE_KEY}`);
  if (prefer) result.set("prefer", prefer);
  return result;
}

async function db(path: string, method = "GET", body?: unknown, prefer?: string) {
  const response = await fetch(`${SUPABASE_URL}/rest/v1/${path}`, {
    method,
    headers: dbHeaders(prefer),
    body: body === undefined ? undefined : JSON.stringify(body)
  });
  const raw = await response.text();
  if (!response.ok) {
    console.error("mix_db_error", method, path.split("?")[0], response.status);
    throw new Error("database_unavailable");
  }
  return raw ? JSON.parse(raw) : null;
}

async function sha256(value: string): Promise<string> {
  const digest = await crypto.subtle.digest("SHA-256", new TextEncoder().encode(value));
  return Array.from(new Uint8Array(digest), byte => byte.toString(16).padStart(2, "0")).join("");
}

function randomToken(): string {
  return Array.from(crypto.getRandomValues(new Uint8Array(32)), byte => byte.toString(16).padStart(2, "0")).join("");
}

function validHex64(value: string): boolean {
  return /^[a-f0-9]{64}$/.test(value);
}

function validUuid(value: string): boolean {
  return /^[0-9a-f]{8}-[0-9a-f]{4}-[1-8][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i.test(value);
}

async function authenticate(request: Request) {
  const token = (request.headers.get("x-mix-device-token") ?? "").toLowerCase();
  const installationHash = (request.headers.get("x-mix-installation-hash") ?? "").toLowerCase();
  if (!validHex64(token) || !validHex64(installationHash)) return null;
  const tokenHash = await sha256(token);
  const rows = await db(
    `mix_windows_devices?device_token_hash=eq.${tokenHash}&installation_hash=eq.${installationHash}&select=id,status,installation_hash`
  );
  if (!Array.isArray(rows) || rows.length !== 1 || rows[0].status !== "active") return null;
  return rows[0] as { id: string; status: string; installation_hash: string };
}

async function register(request: Request, body: Record<string, unknown>) {
  const installationHash = String(body.installation_hash ?? "").toLowerCase();
  if (!validHex64(installationHash)) return json({ ok: false, code: "invalid_installation" }, 400);

  const existing = await db(
    `mix_windows_devices?installation_hash=eq.${installationHash}&select=id,status&limit=1`
  );
  if (!Array.isArray(existing)) throw new Error("database_unavailable");
  if (existing.length && existing[0].status !== "active")
    return json({ ok: false, code: "device_blocked" }, 403);

  const token = randomToken();
  const tokenHash = await sha256(token);
  if (existing.length) {
    // Reissuing the local DPAPI token never resets the weekly quota or traffic counters.
    await db(`mix_windows_devices?id=eq.${existing[0].id}`, "PATCH", {
      device_token_hash: tokenHash,
      last_seen_at: new Date().toISOString(),
      updated_at: new Date().toISOString()
    }, "return=minimal");
    return json({ ok: true, device_token: token, device_id: existing[0].id, reissued: true });
  }

  const inserted = await db("mix_windows_devices", "POST", {
    installation_hash: installationHash,
    device_token_hash: tokenHash
  }, "return=representation");
  const row = Array.isArray(inserted) ? inserted[0] : null;
  if (!row?.id) throw new Error("device_registration_failed");
  return json({ ok: true, device_token: token, device_id: row.id, reissued: false });
}

async function snapshot(deviceId: string) {
  const result = await db("rpc/mix_windows_snapshot", "POST", { p_device_id: deviceId });
  if (!result || typeof result !== "object") throw new Error("trial_snapshot_unavailable");
  return result;
}

async function startSession(deviceId: string, body: Record<string, unknown>) {
  const sessionId = String(body.session_id ?? "");
  if (!validUuid(sessionId)) return json({ ok: false, code: "invalid_session" }, 400);
  const result = await db("rpc/mix_windows_start", "POST", {
    p_device_id: deviceId,
    p_session_id: sessionId
  });
  return json(result ?? { ok: false, code: "trial_start_failed" }, result?.ok ? 200 : 409);
}

async function heartbeat(deviceId: string, body: Record<string, unknown>) {
  const sessionId = String(body.session_id ?? "");
  const sequence = Number(body.sequence);
  const rx = Number(body.rx_total);
  const tx = Number(body.tx_total);
  const isFinal = body.is_final === true;
  if (!validUuid(sessionId) || !Number.isSafeInteger(sequence) || sequence < 1 ||
      !Number.isSafeInteger(rx) || rx < 0 || !Number.isSafeInteger(tx) || tx < 0 ||
      rx + tx > Number.MAX_SAFE_INTEGER) {
    return json({ ok: false, code: "invalid_heartbeat" }, 400);
  }
  const result = await db("rpc/mix_windows_heartbeat", "POST", {
    p_device_id: deviceId,
    p_session_id: sessionId,
    p_sequence: sequence,
    p_rx_total: rx,
    p_tx_total: tx,
    p_is_final: isFinal
  });
  return json(result ?? { ok: false, code: "heartbeat_failed" }, result?.ok ? 200 : 409);
}

async function listServers(deviceId: string) {
  const quota = await snapshot(deviceId);
  // Hide protected server entries after quota expiry, but keep the UI able to show the remaining time and stats.
  if (quota?.access_allowed !== true) return json({ ok: true, entries: [], quota });
  const rows = await db(
    "mix_windows_servers?is_active=eq.true&select=id,title,priority&order=priority.desc,created_at.desc&limit=2000"
  );
  if (!Array.isArray(rows) || rows.length > 2000) return json({ ok: false, code: "catalog_invalid" }, 502);
  const entries = rows.map((row: Record<string, unknown>) => ({
    id: String(row.id ?? ""),
    title: String(row.title ?? "سرور Moghimi"),
    priority: Number(row.priority ?? 0)
  }));
  return json({ ok: true, entries, quota });
}

async function getServerConfig(deviceId: string, body: Record<string, unknown>) {
  const serverId = String(body.server_id ?? "");
  if (!validUuid(serverId)) return json({ ok: false, code: "invalid_server" }, 400);
  const quota = await snapshot(deviceId);
  if (quota?.access_allowed !== true) return json({ ok: false, code: "trial_exhausted" }, 403);
  const rows = await db(
    `mix_windows_servers?id=eq.${serverId}&is_active=eq.true&select=connection_uri&limit=1`
  );
  if (!Array.isArray(rows) || rows.length !== 1 || typeof rows[0].connection_uri !== "string" || !rows[0].connection_uri.startsWith("ss://"))
    return json({ ok: false, code: "server_unavailable" }, 404);
  const config = rows[0].connection_uri as string;
  if (new TextEncoder().encode(config).byteLength > 262144)
    return json({ ok: false, code: "server_config_too_large" }, 413);
  return json({ ok: true, config_text: config });
}

Deno.serve(async (request: Request) => {
  if (request.method === "OPTIONS") return json({ ok: true });
  if (request.method !== "POST") return json({ ok: false, code: "method_not_allowed" }, 405);
  if (!SUPABASE_URL || !SERVICE_KEY) return json({ ok: false, code: "server_not_configured" }, 503);

  try {
    const body = await request.json();
    if (!body || typeof body !== "object" || Array.isArray(body))
      return json({ ok: false, code: "invalid_request" }, 400);
    const data = body as Record<string, unknown>;
    switch (String(data.action ?? "")) {
      case "register": return await register(request, data);
      case "snapshot":
      case "servers":
      case "server_config":
      case "start":
      case "heartbeat": {
        const device = await authenticate(request);
        if (!device) return json({ ok: false, code: "device_not_registered" }, 401);
        if (data.action === "snapshot") return json(await snapshot(device.id));
        if (data.action === "servers") return await listServers(device.id);
        if (data.action === "server_config") return await getServerConfig(device.id, data);
        if (data.action === "start") return await startSession(device.id, data);
        return await heartbeat(device.id, data);
      }
      default: return json({ ok: false, code: "unknown_action" }, 400);
    }
  } catch (error) {
    console.error("mix_api_error", error instanceof Error ? error.message : "unexpected_error");
    return json({ ok: false, code: "service_unavailable", message: "سرویس MIX موقتاً در دسترس نیست." }, 503);
  }
});
