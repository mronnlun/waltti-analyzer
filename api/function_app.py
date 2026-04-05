import json
import logging
import os

import azure.functions as func

from shared import config
from shared.analyzer import (
    get_delay_by_hour,
    get_route_breakdown,
    get_summary,
    parse_time,
)
from shared.collector import collect_daily, discover_stops, poll_realtime_once
from shared.db import (
    connect,
    get_all_routes,
    get_all_stops,
    get_latest_collection,
    get_latest_observations,
    get_observations,
    get_routes_for_stop,
    init_db,
)

logger = logging.getLogger(__name__)

app = func.FunctionApp()

# ---------------------------------------------------------------------------
# Ensure DB schema is initialised on cold start
# ---------------------------------------------------------------------------

_db_initialised = False


def _ensure_db() -> None:
    global _db_initialised
    if not _db_initialised:
        os.makedirs(os.path.dirname(config.DATABASE_PATH) or ".", exist_ok=True)
        init_db(config.DATABASE_PATH)
        _db_initialised = True


def _json_response(body: dict | list, status_code: int = 200) -> func.HttpResponse:
    return func.HttpResponse(
        json.dumps(body, default=str),
        status_code=status_code,
        mimetype="application/json",
    )


def _cors_headers() -> dict[str, str]:
    return {
        "Access-Control-Allow-Origin": "*",
        "Access-Control-Allow-Methods": "GET, POST, OPTIONS",
        "Access-Control-Allow-Headers": "Content-Type",
    }


def _cors_response(body: dict | list, status_code: int = 200) -> func.HttpResponse:
    return func.HttpResponse(
        json.dumps(body, default=str),
        status_code=status_code,
        mimetype="application/json",
        headers=_cors_headers(),
    )


def _cors_preflight() -> func.HttpResponse:
    return func.HttpResponse(status_code=204, headers=_cors_headers())


# ---------------------------------------------------------------------------
# Timer trigger — single function for all synchronization tasks
# ---------------------------------------------------------------------------


@app.timer_trigger(
    schedule="0 */3 * * * *",
    arg_name="timer",
    run_on_startup=False,
)
def sync_bus_data(timer: func.TimerRequest) -> None:
    """Runs every 3 minutes. Always polls realtime data.

    Additionally checks the current Helsinki time to decide whether
    to run daily collection (at 03:xx and 23:xx) or weekly discovery
    (Monday 02:xx).
    """
    from datetime import datetime
    from zoneinfo import ZoneInfo

    _ensure_db()

    helsinki = ZoneInfo("Europe/Helsinki")
    now = datetime.now(helsinki)
    hour = now.hour
    minute = now.minute
    weekday = now.weekday()  # 0 = Monday

    api_url = config.DIGITRANSIT_API_URL
    api_key = config.DIGITRANSIT_API_KEY
    feed_id = config.FEED_ID
    db_path = config.DATABASE_PATH

    if not api_key:
        logger.warning("DIGITRANSIT_API_KEY not set — skipping sync")
        return

    # Weekly discovery: Monday 02:00–02:02
    if weekday == 0 and hour == 2 and minute < 3:
        logger.info("Running weekly stop discovery")
        result = discover_stops(db_path, api_url, api_key, feed_id)
        logger.info("Discovery result: %s", result)

    # Daily collection: 03:00–03:02 and 23:00–23:02
    if hour in (3, 23) and minute < 3:
        logger.info("Running daily collection at %02d:%02d", hour, minute)
        result = collect_daily(db_path, api_url, api_key, feed_id=feed_id)
        logger.info("Daily collection result: %s", result)

    # Realtime polling: every invocation
    result = poll_realtime_once(db_path, api_url, api_key, feed_id=feed_id)
    logger.info("Realtime poll result: %s", result)


# ---------------------------------------------------------------------------
# HTTP triggers — API endpoints for the SPA frontend
# ---------------------------------------------------------------------------


@app.route(route="api/status", methods=["GET", "OPTIONS"], auth_level=func.AuthLevel.ANONYMOUS)
def api_status(req: func.HttpRequest) -> func.HttpResponse:
    if req.method == "OPTIONS":
        return _cors_preflight()
    _ensure_db()
    db = connect(config.DATABASE_PATH)
    feed_id = config.FEED_ID

    daily = get_latest_collection(db, feed_id, "daily")
    realtime = get_latest_collection(db, feed_id, "realtime")
    db.close()

    return _cors_response(
        {
            "feed_id": feed_id,
            "last_daily": dict(daily) if daily else None,
            "last_realtime": dict(realtime) if realtime else None,
        }
    )


@app.route(route="api/stops", methods=["GET", "OPTIONS"], auth_level=func.AuthLevel.ANONYMOUS)
def api_stops(req: func.HttpRequest) -> func.HttpResponse:
    if req.method == "OPTIONS":
        return _cors_preflight()
    _ensure_db()
    db = connect(config.DATABASE_PATH)
    stops = get_all_stops(db, config.FEED_ID)
    result = [dict(s) for s in stops]
    db.close()
    return _cors_response(result)


@app.route(route="api/routes", methods=["GET", "OPTIONS"], auth_level=func.AuthLevel.ANONYMOUS)
def api_routes(req: func.HttpRequest) -> func.HttpResponse:
    if req.method == "OPTIONS":
        return _cors_preflight()
    _ensure_db()
    db = connect(config.DATABASE_PATH)
    routes = get_all_routes(db, config.FEED_ID)
    db.close()
    return _cors_response(routes)


@app.route(
    route="api/routes-for-stop",
    methods=["GET", "OPTIONS"],
    auth_level=func.AuthLevel.ANONYMOUS,
)
def api_routes_for_stop(req: func.HttpRequest) -> func.HttpResponse:
    if req.method == "OPTIONS":
        return _cors_preflight()
    stop_id = req.params.get("stop_id", "")
    if not stop_id:
        return _cors_response({"error": "stop_id parameter required"}, 400)
    _ensure_db()
    db = connect(config.DATABASE_PATH)
    routes = get_routes_for_stop(db, stop_id)
    db.close()
    return _cors_response(routes)


@app.route(
    route="api/observations",
    methods=["GET", "OPTIONS"],
    auth_level=func.AuthLevel.ANONYMOUS,
)
def api_observations(req: func.HttpRequest) -> func.HttpResponse:
    if req.method == "OPTIONS":
        return _cors_preflight()
    stop_id = req.params.get("stop_id", "")
    date_str = req.params.get("date", "")
    start_date = req.params.get("from", date_str)
    end_date = req.params.get("to", date_str)

    if not start_date or not end_date:
        return _cors_response({"error": "date or from/to parameters required"}, 400)
    if not stop_id:
        return _cors_response({"error": "stop_id parameter required"}, 400)

    route = req.params.get("route") or None
    time_from = parse_time(req.params.get("time_from"))
    time_to = parse_time(req.params.get("time_to"))

    _ensure_db()
    db = connect(config.DATABASE_PATH)
    rows = get_observations(db, stop_id, start_date, end_date, route, time_from, time_to)
    result = [dict(r) for r in rows]
    db.close()
    return _cors_response(result)


@app.route(
    route="api/latest-observations",
    methods=["GET", "OPTIONS"],
    auth_level=func.AuthLevel.ANONYMOUS,
)
def api_latest_observations(req: func.HttpRequest) -> func.HttpResponse:
    if req.method == "OPTIONS":
        return _cors_preflight()
    _ensure_db()
    db = connect(config.DATABASE_PATH)
    rows = get_latest_observations(db, limit=100, feed_id=config.FEED_ID)
    result = [dict(r) for r in rows]
    db.close()
    return _cors_response(result)


@app.route(route="api/summary", methods=["GET", "OPTIONS"], auth_level=func.AuthLevel.ANONYMOUS)
def api_summary(req: func.HttpRequest) -> func.HttpResponse:
    if req.method == "OPTIONS":
        return _cors_preflight()
    stop_id = req.params.get("stop_id", "")
    from_date = req.params.get("from", "")
    to_date = req.params.get("to", "")
    route = req.params.get("route") or None

    if not from_date or not to_date or not stop_id:
        return _cors_response({"error": "stop_id, from, and to parameters required"}, 400)

    time_from = parse_time(req.params.get("time_from"))
    time_to = parse_time(req.params.get("time_to"))

    _ensure_db()
    db = connect(config.DATABASE_PATH)
    result = get_summary(db, stop_id, from_date, to_date, route, time_from, time_to)
    db.close()
    return _cors_response(result)


@app.route(
    route="api/route-breakdown",
    methods=["GET", "OPTIONS"],
    auth_level=func.AuthLevel.ANONYMOUS,
)
def api_route_breakdown(req: func.HttpRequest) -> func.HttpResponse:
    if req.method == "OPTIONS":
        return _cors_preflight()
    stop_id = req.params.get("stop_id", "")
    from_date = req.params.get("from", "")
    to_date = req.params.get("to", "")
    route = req.params.get("route") or None

    if not from_date or not to_date or not stop_id:
        return _cors_response({"error": "stop_id, from, and to parameters required"}, 400)

    time_from = parse_time(req.params.get("time_from"))
    time_to = parse_time(req.params.get("time_to"))

    _ensure_db()
    db = connect(config.DATABASE_PATH)
    result = get_route_breakdown(db, stop_id, from_date, to_date, route, time_from, time_to)
    db.close()
    return _cors_response(result)


@app.route(
    route="api/delay-by-hour",
    methods=["GET", "OPTIONS"],
    auth_level=func.AuthLevel.ANONYMOUS,
)
def api_delay_by_hour(req: func.HttpRequest) -> func.HttpResponse:
    if req.method == "OPTIONS":
        return _cors_preflight()
    stop_id = req.params.get("stop_id", "")
    from_date = req.params.get("from", "")
    to_date = req.params.get("to", "")
    route = req.params.get("route") or None

    if not from_date or not to_date or not stop_id:
        return _cors_response({"error": "stop_id, from, and to parameters required"}, 400)

    time_from = parse_time(req.params.get("time_from"))
    time_to = parse_time(req.params.get("time_to"))

    _ensure_db()
    db = connect(config.DATABASE_PATH)
    result = get_delay_by_hour(db, stop_id, from_date, to_date, route, time_from, time_to)
    db.close()
    return _cors_response(result)


@app.route(
    route="api/collect/daily",
    methods=["POST", "OPTIONS"],
    auth_level=func.AuthLevel.ANONYMOUS,
)
def api_collect_daily(req: func.HttpRequest) -> func.HttpResponse:
    if req.method == "OPTIONS":
        return _cors_preflight()
    _ensure_db()
    try:
        body = req.get_json()
    except ValueError:
        body = {}
    date_str = body.get("date") if body else None
    stop_id = body.get("stop_id") if body else None
    result = collect_daily(
        config.DATABASE_PATH,
        config.DIGITRANSIT_API_URL,
        config.DIGITRANSIT_API_KEY,
        stop_id=stop_id,
        service_date=date_str,
        feed_id=config.FEED_ID,
    )
    return _cors_response(result)


@app.route(
    route="api/collect/realtime",
    methods=["POST", "OPTIONS"],
    auth_level=func.AuthLevel.ANONYMOUS,
)
def api_collect_realtime(req: func.HttpRequest) -> func.HttpResponse:
    if req.method == "OPTIONS":
        return _cors_preflight()
    _ensure_db()
    try:
        body = req.get_json()
    except ValueError:
        body = {}
    stop_id = body.get("stop_id") if body else None
    result = poll_realtime_once(
        config.DATABASE_PATH,
        config.DIGITRANSIT_API_URL,
        config.DIGITRANSIT_API_KEY,
        stop_id=stop_id,
        feed_id=config.FEED_ID,
    )
    return _cors_response(result)


@app.route(
    route="api/discover",
    methods=["POST", "OPTIONS"],
    auth_level=func.AuthLevel.ANONYMOUS,
)
def api_discover(req: func.HttpRequest) -> func.HttpResponse:
    if req.method == "OPTIONS":
        return _cors_preflight()
    _ensure_db()
    result = discover_stops(
        config.DATABASE_PATH,
        config.DIGITRANSIT_API_URL,
        config.DIGITRANSIT_API_KEY,
        config.FEED_ID,
    )
    return _cors_response(result)
