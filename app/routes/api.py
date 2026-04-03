from flask import Blueprint, jsonify, request, current_app

from app.db import get_db, get_observations, get_latest_collection
from app.analyzer import get_summary
from app.collector import collect_daily, poll_realtime_once

api_bp = Blueprint("api", __name__)


@api_bp.route("/collect/daily", methods=["POST"])
def trigger_daily():
    data = request.get_json(silent=True) or {}
    date = data.get("date")
    stop_id = current_app.config["TARGET_STOP_ID"]
    api_key = current_app.config["DIGITRANSIT_API_KEY"]
    api_url = current_app.config["DIGITRANSIT_API_URL"]
    db_path = current_app.config["DATABASE_PATH"]

    result = collect_daily(db_path, api_url, api_key, stop_id, date)
    return jsonify(result)


@api_bp.route("/collect/realtime", methods=["POST"])
def trigger_realtime():
    stop_id = current_app.config["TARGET_STOP_ID"]
    api_key = current_app.config["DIGITRANSIT_API_KEY"]
    api_url = current_app.config["DIGITRANSIT_API_URL"]
    db_path = current_app.config["DATABASE_PATH"]

    result = poll_realtime_once(db_path, api_url, api_key, stop_id)
    return jsonify(result)


@api_bp.route("/status")
def status():
    db = get_db()
    stop_id = current_app.config["TARGET_STOP_ID"]

    daily = get_latest_collection(db, stop_id, "daily")
    realtime = get_latest_collection(db, stop_id, "realtime")

    return jsonify({
        "stop_id": stop_id,
        "last_daily": dict(daily) if daily else None,
        "last_realtime": dict(realtime) if realtime else None,
    })


@api_bp.route("/observations")
def observations():
    db = get_db()
    stop_id = current_app.config["TARGET_STOP_ID"]
    date = request.args.get("date", "")

    if not date:
        return jsonify({"error": "date parameter required (YYYY-MM-DD)"}), 400

    rows = get_observations(db, stop_id, date, date)
    return jsonify([dict(r) for r in rows])


@api_bp.route("/summary")
def summary():
    db = get_db()
    stop_id = current_app.config["TARGET_STOP_ID"]
    from_date = request.args.get("from", "")
    to_date = request.args.get("to", "")
    route = request.args.get("route")

    if not from_date or not to_date:
        return jsonify({"error": "from and to parameters required (YYYY-MM-DD)"}), 400

    result = get_summary(db, stop_id, from_date, to_date, route)
    return jsonify(result)
