from flask import Blueprint, current_app, render_template, request

from app.analyzer import get_delay_by_hour, get_route_breakdown, get_summary
from app.db import get_db, get_recent_observations

dashboard_bp = Blueprint("dashboard", __name__)


@dashboard_bp.route("/")
def index():
    db = get_db()
    stop_id = current_app.config["TARGET_STOP_ID"]

    from_date = request.args.get("from", "")
    to_date = request.args.get("to", "")
    route = request.args.get("route")

    summary = None
    routes_breakdown = []
    hourly_delays = []
    if from_date and to_date:
        summary = get_summary(db, stop_id, from_date, to_date, route)
        routes_breakdown = get_route_breakdown(db, stop_id, from_date, to_date)
        hourly_delays = get_delay_by_hour(db, stop_id, from_date, to_date, route)

    recent = get_recent_observations(db, stop_id, limit=20)

    return render_template(
        "dashboard.html",
        stop_id=stop_id,
        from_date=from_date,
        to_date=to_date,
        route_filter=route,
        summary=summary,
        routes_breakdown=routes_breakdown,
        hourly_delays=hourly_delays,
        recent=recent,
    )


@dashboard_bp.route("/stops")
def stops():
    return render_template("stops.html", stop_id=current_app.config["TARGET_STOP_ID"])
