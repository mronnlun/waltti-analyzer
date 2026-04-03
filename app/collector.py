import logging
import time
from datetime import date, datetime, timezone
from zoneinfo import ZoneInfo

from app.db import connect_direct, upsert_observations_batch, upsert_stop, log_collection
from app.digitransit import DigitransitClient

logger = logging.getLogger(__name__)

HELSINKI_TZ = ZoneInfo("Europe/Helsinki")


def _service_day_to_date(service_day_unix: int) -> str:
    """Convert serviceDay unix timestamp to YYYY-MM-DD string in Helsinki time."""
    dt = datetime.fromtimestamp(service_day_unix, tz=timezone.utc)
    return dt.astimezone(HELSINKI_TZ).strftime("%Y-%m-%d")


def collect_daily(db_path: str, api_url: str, api_key: str,
                  stop_id: str, service_date: str | None = None) -> dict:
    """Fetch full day schedule and store in DB. Returns status dict."""
    client = DigitransitClient(api_url, api_key)
    db = connect_direct(db_path)
    now = int(time.time())

    target_date = service_date
    if target_date is None:
        target_date = datetime.now(HELSINKI_TZ).strftime("%Y-%m-%d")

    try:
        stop_data = client.fetch_daily_schedule(
            stop_id, date.fromisoformat(target_date)
        )

        if stop_data is None:
            log_collection(db, stop_id, "daily", target_date, error="Stop not found")
            db.close()
            return {"status": "error", "message": f"Stop {stop_id} not found"}

        # Upsert stop info
        upsert_stop(db, stop_data["gtfsId"], stop_data["name"],
                     stop_data.get("code"), stop_data.get("lat"), stop_data.get("lon"))

        # Check for no-service day
        patterns = stop_data.get("stoptimesForServiceDate", [])
        all_empty = all(len(p.get("stoptimes", [])) == 0 for p in patterns)

        if all_empty:
            log_collection(db, stop_id, "daily", target_date,
                           departures_found=0, no_service=1)
            db.close()
            return {"status": "no_service", "date": target_date,
                    "message": f"No service on {target_date}"}

        # Build observation rows
        observations = []
        for pattern_data in patterns:
            route = pattern_data["pattern"]["route"]
            direction_id = pattern_data["pattern"].get("directionId")

            for st in pattern_data.get("stoptimes", []):
                trip_id = st["trip"]["gtfsId"]
                observations.append({
                    "stop_gtfs_id": stop_id,
                    "trip_gtfs_id": trip_id,
                    "route_short_name": route.get("shortName"),
                    "route_long_name": route.get("longName"),
                    "mode": route.get("mode"),
                    "headsign": st.get("headsign"),
                    "direction_id": direction_id,
                    "service_date": target_date,
                    "service_day_unix": None,
                    "scheduled_arrival": st.get("scheduledArrival"),
                    "scheduled_departure": st["scheduledDeparture"],
                    "realtime_arrival": st.get("realtimeArrival"),
                    "realtime_departure": st.get("realtimeDeparture"),
                    "arrival_delay": st.get("arrivalDelay", 0),
                    "departure_delay": st.get("departureDelay", 0),
                    "realtime": 1 if st.get("realtime") else 0,
                    "realtime_state": st.get("realtimeState"),
                    "queried_at": now,
                })

        upsert_observations_batch(db, observations)
        log_collection(db, stop_id, "daily", target_date,
                       departures_found=len(observations))

        logger.info("Collected %d departures for %s on %s",
                    len(observations), stop_id, target_date)
        db.close()
        return {"status": "ok", "date": target_date, "departures": len(observations)}

    except Exception as e:
        logger.exception("Daily collection failed for %s on %s", stop_id, target_date)
        log_collection(db, stop_id, "daily", target_date, error=str(e))
        db.close()
        return {"status": "error", "message": str(e)}


def poll_realtime_once(db_path: str, api_url: str, api_key: str,
                       stop_id: str) -> dict:
    """Single realtime poll — fetch upcoming departures and update DB."""
    client = DigitransitClient(api_url, api_key)
    db = connect_direct(db_path)
    now = int(time.time())

    try:
        stop_data = client.fetch_realtime(stop_id)

        if stop_data is None:
            log_collection(db, stop_id, "realtime", error="Stop not found")
            db.close()
            return {"status": "error", "message": f"Stop {stop_id} not found"}

        stoptimes = stop_data.get("stoptimesWithoutPatterns", [])

        if not stoptimes:
            log_collection(db, stop_id, "realtime", departures_found=0)
            db.close()
            return {"status": "ok", "updated": 0, "message": "No upcoming departures"}

        observations = []
        for st in stoptimes:
            trip = st["trip"]
            trip_id = trip["gtfsId"]
            route = trip.get("route", {})
            service_day = st.get("serviceDay")
            service_date = _service_day_to_date(service_day) if service_day else None

            if service_date is None:
                continue

            observations.append({
                "stop_gtfs_id": stop_id,
                "trip_gtfs_id": trip_id,
                "route_short_name": route.get("shortName"),
                "route_long_name": route.get("longName"),
                "mode": None,
                "headsign": st.get("headsign"),
                "direction_id": None,
                "service_date": service_date,
                "service_day_unix": service_day,
                "scheduled_arrival": st.get("scheduledArrival"),
                "scheduled_departure": st["scheduledDeparture"],
                "realtime_arrival": st.get("realtimeArrival"),
                "realtime_departure": st.get("realtimeDeparture"),
                "arrival_delay": st.get("arrivalDelay", 0),
                "departure_delay": st.get("departureDelay", 0),
                "realtime": 1 if st.get("realtime") else 0,
                "realtime_state": st.get("realtimeState"),
                "queried_at": now,
            })

        upsert_observations_batch(db, observations)
        log_collection(db, stop_id, "realtime", departures_found=len(observations))

        realtime_count = sum(1 for o in observations if o["realtime"])
        logger.info("Realtime poll: %d departures (%d with realtime) for %s",
                    len(observations), realtime_count, stop_id)
        db.close()
        return {"status": "ok", "updated": len(observations),
                "with_realtime": realtime_count}

    except Exception as e:
        logger.exception("Realtime poll failed for %s", stop_id)
        log_collection(db, stop_id, "realtime", error=str(e))
        db.close()
        return {"status": "error", "message": str(e)}
