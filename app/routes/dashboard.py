from datetime import datetime, timedelta
from zoneinfo import ZoneInfo

from flask import Blueprint, current_app, render_template, request

from app.analyzer import get_delay_by_hour, get_route_breakdown, get_summary, parse_time
from app.db import (
    get_all_stops,
    get_db,
    get_recent_observations,
    get_routes_for_stop,
)

dashboard_bp = Blueprint("dashboard", __name__)

HELSINKI_TZ = ZoneInfo("Europe/Helsinki")


@dashboard_bp.route("/")
def index():
    db = get_db()
    feed_id = current_app.config["FEED_ID"]
    default_stop = current_app.config["DEFAULT_STOP_ID"]

    # Default dates: 5 days ago → yesterday
    now = datetime.now(HELSINKI_TZ)
    default_from = (now - timedelta(days=5)).strftime("%Y-%m-%d")
    default_to = now.strftime("%Y-%m-%d")

    stop_id = request.args.get("stop_id", default_stop)
    from_date = request.args.get("from", default_from)
    to_date = request.args.get("to", default_to)
    route = request.args.get("route") or None
    time_from_str = request.args.get("time_from") or None
    time_to_str = request.args.get("time_to") or None
    time_from = parse_time(time_from_str)
    time_to = parse_time(time_to_str)

    all_stops = get_all_stops(db, feed_id)
    stop_routes = get_routes_for_stop(db, stop_id) if stop_id else []

    summary = None
    routes_breakdown = []
    hourly_delays = []
    if from_date and to_date and stop_id:
        summary = get_summary(db, stop_id, from_date, to_date, route, time_from, time_to)
        routes_breakdown = get_route_breakdown(
            db, stop_id, from_date, to_date, route, time_from, time_to
        )
        hourly_delays = get_delay_by_hour(
            db, stop_id, from_date, to_date, route, time_from, time_to
        )

    recent = get_recent_observations(db, stop_id, limit=20, route=route) if stop_id else []

    return render_template(
        "dashboard.html",
        stop_id=stop_id,
        from_date=from_date,
        to_date=to_date,
        route_filter=route,
        time_from=time_from_str or "",
        time_to=time_to_str or "",
        all_stops=all_stops,
        stop_routes=stop_routes,
        summary=summary,
        routes_breakdown=routes_breakdown,
        hourly_delays=hourly_delays,
        recent=recent,
    )


@dashboard_bp.route("/stops")
def stops():
    db = get_db()
    feed_id = current_app.config["FEED_ID"]
    all_stops = get_all_stops(db, feed_id)
    return render_template(
        "stops.html",
        feed_id=feed_id,
        all_stops=all_stops,
    )
