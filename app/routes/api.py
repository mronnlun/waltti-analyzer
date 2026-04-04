from flask import Blueprint, current_app, jsonify, request

from app.analyzer import get_summary, parse_time
from app.collector import collect_daily, discover_stops, poll_realtime_once
from app.db import get_all_routes, get_all_stops, get_db, get_latest_collection, get_observations

api_bp = Blueprint("api", __name__)


@api_bp.route("/collect/daily", methods=["POST"])
def trigger_daily():
    data = request.get_json(silent=True) or {}
    date = data.get("date")
    stop_id = data.get("stop_id")
    api_key = current_app.config["DIGITRANSIT_API_KEY"]
    api_url = current_app.config["DIGITRANSIT_API_URL"]
    db_path = current_app.config["DATABASE_PATH"]
    feed_id = current_app.config["FEED_ID"]

    result = collect_daily(
        db_path,
        api_url,
        api_key,
        stop_id=stop_id,
        service_date=date,
        feed_id=feed_id,
    )
    return jsonify(result)


@api_bp.route("/collect/realtime", methods=["POST"])
def trigger_realtime():
    data = request.get_json(silent=True) or {}
    stop_id = data.get("stop_id")
    api_key = current_app.config["DIGITRANSIT_API_KEY"]
    api_url = current_app.config["DIGITRANSIT_API_URL"]
    db_path = current_app.config["DATABASE_PATH"]
    feed_id = current_app.config["FEED_ID"]

    result = poll_realtime_once(
        db_path,
        api_url,
        api_key,
        stop_id=stop_id,
        feed_id=feed_id,
    )
    return jsonify(result)


@api_bp.route("/discover", methods=["POST"])
def trigger_discover():
    api_key = current_app.config["DIGITRANSIT_API_KEY"]
    api_url = current_app.config["DIGITRANSIT_API_URL"]
    db_path = current_app.config["DATABASE_PATH"]
    feed_id = current_app.config["FEED_ID"]

    result = discover_stops(db_path, api_url, api_key, feed_id)
    return jsonify(result)


@api_bp.route("/status")
def status():
    db = get_db()
    feed_id = current_app.config["FEED_ID"]

    daily = get_latest_collection(db, feed_id, "daily")
    realtime = get_latest_collection(db, feed_id, "realtime")

    return jsonify(
        {
            "feed_id": feed_id,
            "last_daily": dict(daily) if daily else None,
            "last_realtime": dict(realtime) if realtime else None,
        }
    )


@api_bp.route("/stops")
def stops_list():
    db = get_db()
    feed_id = current_app.config["FEED_ID"]
    stops = get_all_stops(db, feed_id)
    return jsonify([dict(s) for s in stops])


@api_bp.route("/routes")
def routes_list():
    db = get_db()
    feed_id = current_app.config["FEED_ID"]
    routes = get_all_routes(db, feed_id)
    return jsonify(routes)


@api_bp.route("/observations")
def observations():
    db = get_db()
    stop_id = request.args.get("stop_id", "")
    date = request.args.get("date", "")

    if not date:
        return jsonify({"error": "date parameter required (YYYY-MM-DD)"}), 400
    if not stop_id:
        return jsonify({"error": "stop_id parameter required"}), 400

    rows = get_observations(db, stop_id, date, date)
    return jsonify([dict(r) for r in rows])


@api_bp.route("/summary")
def summary():
    db = get_db()
    stop_id = request.args.get("stop_id", "")
    from_date = request.args.get("from", "")
    to_date = request.args.get("to", "")
    route = request.args.get("route")

    if not from_date or not to_date or not stop_id:
        return jsonify({"error": "stop_id, from, and to parameters required"}), 400

    time_from = parse_time(request.args.get("time_from"))
    time_to = parse_time(request.args.get("time_to"))
    result = get_summary(db, stop_id, from_date, to_date, route, time_from, time_to)
    return jsonify(result)
