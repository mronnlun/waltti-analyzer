from flask import Blueprint, current_app, render_template, request

from app.analyzer import get_delay_by_hour, get_route_breakdown, get_summary
from app.db import (
    get_all_stops,
    get_db,
    get_recent_observations,
    get_routes_for_stop,
)

dashboard_bp = Blueprint("dashboard", __name__)


@dashboard_bp.route("/")
def index():
    db = get_db()
    feed_id = current_app.config["FEED_ID"]
    default_stop = current_app.config["DEFAULT_STOP_ID"]

    stop_id = request.args.get("stop_id", default_stop)
    from_date = request.args.get("from", "")
    to_date = request.args.get("to", "")
    route = request.args.get("route") or None

    all_stops = get_all_stops(db, feed_id)
    stop_routes = get_routes_for_stop(db, stop_id) if stop_id else []

    summary = None
    routes_breakdown = []
    hourly_delays = []
    if from_date and to_date and stop_id:
        summary = get_summary(db, stop_id, from_date, to_date, route)
        routes_breakdown = get_route_breakdown(db, stop_id, from_date, to_date)
        hourly_delays = get_delay_by_hour(db, stop_id, from_date, to_date, route)

    recent = get_recent_observations(db, stop_id, limit=20) if stop_id else []

    return render_template(
        "dashboard.html",
        stop_id=stop_id,
        from_date=from_date,
        to_date=to_date,
        route_filter=route,
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
