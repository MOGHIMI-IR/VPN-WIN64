-- MIX-MOGHIMI Windows-only device registration, rolling weekly trial and traffic accounting.
-- Scoped to mix_windows_* objects; Android schemas are not touched.
CREATE TABLE IF NOT EXISTS public.mix_windows_devices (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  installation_hash text NOT NULL UNIQUE CHECK (installation_hash ~ '^[a-f0-9]{64}$'),
  device_token_hash text NOT NULL UNIQUE CHECK (device_token_hash ~ '^[a-f0-9]{64}$'),
  status text NOT NULL DEFAULT 'active' CHECK (status IN ('active', 'blocked')),
  period_started_at timestamptz NOT NULL DEFAULT clock_timestamp(),
  used_milliseconds bigint NOT NULL DEFAULT 0 CHECK (used_milliseconds BETWEEN 0 AND 10800000),
  session_id uuid,
  session_active boolean NOT NULL DEFAULT false,
  session_started_at timestamptz,
  last_heartbeat_at timestamptz,
  last_sequence bigint NOT NULL DEFAULT 0 CHECK (last_sequence >= 0),
  last_rx_bytes bigint NOT NULL DEFAULT 0 CHECK (last_rx_bytes >= 0),
  last_tx_bytes bigint NOT NULL DEFAULT 0 CHECK (last_tx_bytes >= 0),
  week_download_bytes bigint NOT NULL DEFAULT 0 CHECK (week_download_bytes >= 0),
  week_upload_bytes bigint NOT NULL DEFAULT 0 CHECK (week_upload_bytes >= 0),
  total_download_bytes bigint NOT NULL DEFAULT 0 CHECK (total_download_bytes >= 0),
  total_upload_bytes bigint NOT NULL DEFAULT 0 CHECK (total_upload_bytes >= 0),
  created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
  last_seen_at timestamptz NOT NULL DEFAULT clock_timestamp(),
  updated_at timestamptz NOT NULL DEFAULT clock_timestamp()
);
CREATE INDEX IF NOT EXISTS mix_windows_devices_status_seen_idx ON public.mix_windows_devices(status, last_seen_at DESC);
ALTER TABLE public.mix_windows_devices ENABLE ROW LEVEL SECURITY;
REVOKE ALL ON TABLE public.mix_windows_devices FROM PUBLIC, anon, authenticated;
GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE public.mix_windows_devices TO service_role;
DROP POLICY IF EXISTS mix_windows_devices_service_role_only ON public.mix_windows_devices;
CREATE POLICY mix_windows_devices_service_role_only
  ON public.mix_windows_devices FOR ALL TO service_role
  USING (true) WITH CHECK (true);

CREATE OR REPLACE FUNCTION public.mix_windows_prepare_period(p_device_id uuid)
RETURNS public.mix_windows_devices LANGUAGE plpgsql SECURITY DEFINER SET search_path = public, pg_temp AS $function$
DECLARE
  v_row public.mix_windows_devices%rowtype;
  v_now timestamptz := clock_timestamp();
  v_elapsed_ms bigint := 0;
  v_periods bigint;
BEGIN
  SELECT * INTO v_row FROM public.mix_windows_devices WHERE id = p_device_id FOR UPDATE;
  IF NOT FOUND THEN RAISE EXCEPTION 'device_not_registered'; END IF;
  IF v_row.status <> 'active' THEN RAISE EXCEPTION 'device_blocked'; END IF;
  IF v_row.session_active AND v_row.last_heartbeat_at IS NOT NULL THEN
    v_elapsed_ms := greatest(0, floor(extract(epoch FROM (v_now - v_row.last_heartbeat_at)) * 1000)::bigint);
  END IF;
  IF v_now >= v_row.period_started_at + interval '7 days' THEN
    IF v_row.session_active THEN
      UPDATE public.mix_windows_devices SET used_milliseconds = least(10800000, used_milliseconds + least(v_elapsed_ms, 10000)),
        session_active = false, last_heartbeat_at = NULL, updated_at = v_now WHERE id = p_device_id;
    END IF;
    v_periods := greatest(1, floor(extract(epoch FROM (v_now - v_row.period_started_at)) / 604800)::bigint);
    UPDATE public.mix_windows_devices SET period_started_at = v_row.period_started_at + (v_periods * interval '7 days'),
      used_milliseconds = 0, session_id = NULL, session_active = false, session_started_at = NULL,
      last_heartbeat_at = NULL, last_sequence = 0, last_rx_bytes = 0, last_tx_bytes = 0,
      week_download_bytes = 0, week_upload_bytes = 0, last_seen_at = v_now, updated_at = v_now
      WHERE id = p_device_id;
  ELSIF v_row.session_active AND v_elapsed_ms > 10000 THEN
    UPDATE public.mix_windows_devices SET used_milliseconds = least(10800000, used_milliseconds + 10000),
      session_active = false, last_heartbeat_at = NULL, last_seen_at = v_now, updated_at = v_now WHERE id = p_device_id;
  ELSE
    UPDATE public.mix_windows_devices SET last_seen_at = v_now, updated_at = v_now WHERE id = p_device_id;
  END IF;
  SELECT * INTO v_row FROM public.mix_windows_devices WHERE id = p_device_id;
  RETURN v_row;
END;
$function$;

CREATE OR REPLACE FUNCTION public.mix_windows_snapshot(p_device_id uuid)
RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER SET search_path = public, pg_temp AS $function$
DECLARE v_row public.mix_windows_devices%rowtype;
BEGIN
  v_row := public.mix_windows_prepare_period(p_device_id);
  RETURN jsonb_build_object('ok', true, 'access_allowed', v_row.status = 'active' AND v_row.used_milliseconds < 10800000,
    'trial_limit_ms', 10800000, 'trial_used_ms', v_row.used_milliseconds,
    'trial_remaining_ms', greatest(0, 10800000 - v_row.used_milliseconds),
    'period_started_at', v_row.period_started_at, 'reset_at', v_row.period_started_at + interval '7 days',
    'server_time', clock_timestamp(), 'session_active', v_row.session_active,
    'week_download_bytes', v_row.week_download_bytes, 'week_upload_bytes', v_row.week_upload_bytes,
    'total_download_bytes', v_row.total_download_bytes, 'total_upload_bytes', v_row.total_upload_bytes);
END;
$function$;

CREATE OR REPLACE FUNCTION public.mix_windows_start(p_device_id uuid, p_session_id uuid)
RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER SET search_path = public, pg_temp AS $function$
DECLARE v_row public.mix_windows_devices%rowtype; v_now timestamptz := clock_timestamp();
BEGIN
  IF p_session_id IS NULL THEN RETURN jsonb_build_object('ok', false, 'code', 'invalid_session'); END IF;
  v_row := public.mix_windows_prepare_period(p_device_id);
  IF v_row.session_active THEN
    IF v_row.session_id = p_session_id THEN RETURN public.mix_windows_snapshot(p_device_id) || jsonb_build_object('duplicate', true); END IF;
    RETURN public.mix_windows_snapshot(p_device_id) || jsonb_build_object('ok', false, 'code', 'session_already_active');
  END IF;
  IF v_row.used_milliseconds >= 10800000 THEN RETURN public.mix_windows_snapshot(p_device_id) || jsonb_build_object('ok', false, 'code', 'trial_exhausted'); END IF;
  UPDATE public.mix_windows_devices SET session_id = p_session_id, session_active = true,
    session_started_at = v_now, last_heartbeat_at = v_now, last_sequence = 0,
    last_rx_bytes = 0, last_tx_bytes = 0, last_seen_at = v_now, updated_at = v_now WHERE id = p_device_id;
  RETURN public.mix_windows_snapshot(p_device_id);
END;
$function$;

CREATE OR REPLACE FUNCTION public.mix_windows_heartbeat(
  p_device_id uuid, p_session_id uuid, p_sequence bigint, p_rx_total bigint, p_tx_total bigint,
  p_is_final boolean DEFAULT false)
RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER SET search_path = public, pg_temp AS $function$
DECLARE
  v_row public.mix_windows_devices%rowtype; v_now timestamptz := clock_timestamp();
  v_elapsed_ms bigint; v_delta_rx bigint; v_delta_tx bigint; v_new_used bigint; v_lease_expired boolean;
BEGIN
  IF p_session_id IS NULL OR p_sequence < 1 OR p_rx_total < 0 OR p_tx_total < 0 OR p_rx_total > 9223372036854775807 - p_tx_total THEN
    RETURN jsonb_build_object('ok', false, 'code', 'invalid_heartbeat');
  END IF;
  v_row := public.mix_windows_prepare_period(p_device_id);
  IF v_row.session_id IS DISTINCT FROM p_session_id THEN RETURN public.mix_windows_snapshot(p_device_id) || jsonb_build_object('ok', false, 'code', 'stale_session'); END IF;
  -- A lost HTTP response can cause the exact same sequence to be retried.
  -- Re-accept that sequence as a new cumulative checkpoint (byte deltas remain idempotent).
  -- Older sequences are stale and must not change accounting or extend the lease.
  IF p_sequence < v_row.last_sequence THEN RETURN public.mix_windows_snapshot(p_device_id) || jsonb_build_object('duplicate', true); END IF;
  IF NOT v_row.session_active OR v_row.last_heartbeat_at IS NULL THEN RETURN public.mix_windows_snapshot(p_device_id) || jsonb_build_object('ok', false, 'code', 'lease_expired'); END IF;
  IF p_sequence > v_row.last_sequence + 1 THEN RETURN public.mix_windows_snapshot(p_device_id) || jsonb_build_object('ok', false, 'code', 'sequence_gap', 'expected_sequence', v_row.last_sequence + 1); END IF;
  IF p_rx_total < v_row.last_rx_bytes OR p_tx_total < v_row.last_tx_bytes THEN RETURN public.mix_windows_snapshot(p_device_id) || jsonb_build_object('ok', false, 'code', 'counter_reset'); END IF;
  v_delta_rx := p_rx_total - v_row.last_rx_bytes;
  v_delta_tx := p_tx_total - v_row.last_tx_bytes;
  IF v_delta_rx + v_delta_tx > 268435456 THEN RETURN jsonb_build_object('ok', false, 'code', 'batch_too_large'); END IF;
  v_elapsed_ms := greatest(0, floor(extract(epoch FROM (v_now - v_row.last_heartbeat_at)) * 1000)::bigint);
  v_lease_expired := v_elapsed_ms > 10000;
  v_new_used := least(10800000, v_row.used_milliseconds + least(v_elapsed_ms, 10000));
  UPDATE public.mix_windows_devices SET used_milliseconds = v_new_used,
    week_download_bytes = CASE WHEN week_download_bytes > 9223372036854775807 - v_delta_rx THEN 9223372036854775807 ELSE week_download_bytes + v_delta_rx END,
    week_upload_bytes = CASE WHEN week_upload_bytes > 9223372036854775807 - v_delta_tx THEN 9223372036854775807 ELSE week_upload_bytes + v_delta_tx END,
    total_download_bytes = CASE WHEN total_download_bytes > 9223372036854775807 - v_delta_rx THEN 9223372036854775807 ELSE total_download_bytes + v_delta_rx END,
    total_upload_bytes = CASE WHEN total_upload_bytes > 9223372036854775807 - v_delta_tx THEN 9223372036854775807 ELSE total_upload_bytes + v_delta_tx END,
    last_sequence = p_sequence, last_rx_bytes = p_rx_total, last_tx_bytes = p_tx_total,
    last_heartbeat_at = CASE WHEN v_lease_expired OR p_is_final OR v_new_used >= 10800000 THEN NULL ELSE v_now END,
    session_active = CASE WHEN v_lease_expired OR p_is_final OR v_new_used >= 10800000 THEN false ELSE true END,
    last_seen_at = v_now, updated_at = v_now WHERE id = p_device_id RETURNING * INTO v_row;
  IF v_lease_expired THEN RETURN public.mix_windows_snapshot(p_device_id) || jsonb_build_object('ok', false, 'code', 'lease_expired', 'accepted_bytes', v_delta_rx + v_delta_tx); END IF;
  RETURN public.mix_windows_snapshot(p_device_id) || jsonb_build_object('accepted_bytes', v_delta_rx + v_delta_tx, 'final', p_is_final);
END;
$function$;

REVOKE ALL ON FUNCTION public.mix_windows_prepare_period(uuid) FROM PUBLIC, anon, authenticated;
REVOKE ALL ON FUNCTION public.mix_windows_snapshot(uuid) FROM PUBLIC, anon, authenticated;
REVOKE ALL ON FUNCTION public.mix_windows_start(uuid, uuid) FROM PUBLIC, anon, authenticated;
REVOKE ALL ON FUNCTION public.mix_windows_heartbeat(uuid, uuid, bigint, bigint, bigint, boolean) FROM PUBLIC, anon, authenticated;
GRANT EXECUTE ON FUNCTION public.mix_windows_prepare_period(uuid) TO service_role;
GRANT EXECUTE ON FUNCTION public.mix_windows_snapshot(uuid) TO service_role;
GRANT EXECUTE ON FUNCTION public.mix_windows_start(uuid, uuid) TO service_role;
GRANT EXECUTE ON FUNCTION public.mix_windows_heartbeat(uuid, uuid, bigint, bigint, bigint, boolean) TO service_role;
