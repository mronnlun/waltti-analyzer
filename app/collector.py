import logging
import time
from datetime import date, datetime, timezone
from zoneinfo import ZoneInfo

from app.db import (
    connect_direct,
    get_all_stop_ids,
    log_collection,
    upsert_observations_batch,
    upsert_stop,
    upsert_stops_batch,
)
from app.digitransit import DigitransitClient

logger = logging.getLogger(__name__)

HELSINKI_TZ = ZoneInfo("Europe/Helsinki")


def _service_day_to_date(service_day_unix: int) -> str:
    """Convert serviceDay unix timestamp to YYYY-MM-DD string in Helsinki time."""
    dt = datetime.fromtimestamp(service_day_unix, tz=timezone.utc)
    return dt.astimezone(HELSINKI_TZ).strftime("%Y-%m-%d")


def discover_stops(db_path: str, api_url: str, api_key: str, feed_id: str) -> dict:
    """Discover all stops for a feed and store them in DB."""
    client = DigitransitClient(api_url, api_key)
    db = connect_direct(db_path)

    try:
        stops, routes = client.discover_feed_stops(feed_id)
        upsert_stops_batch(db, stops)
        log_collection(db, feed_id, "discover", departures_found=len(stops))

        logger.info(
            "Discovered %d stops and %d routes for feed %s",
            len(stops),
            len(routes),
            feed_id,
        )
        db.close()
        return {"status": "ok", "stops": len(stops), "routes": len(routes)}
    except Exception as e:
        logger.exception("Stop discovery failed for feed %s", feed_id)
        log_collection(db, feed_id, "discover", error=str(e))
        db.close()
        return {"status": "error", "message": str(e)}


def collect_daily_single(
    client: DigitransitClient,
    db,
    stop_id: str,
    target_date: str,
    now: int,
) -> int:
    """Collect daily schedule for a single stop. Returns number of departures."""
    stop_data = client.fetch_daily_schedule(stop_id, date.fromisoformat(target_date))

    if stop_data is None:
        return 0

    # Upsert stop info
    upsert_stop(
        db,
        stop_data["gtfsId"],
        stop_data["name"],
        stop_data.get("code"),
        stop_data.get("lat"),
        stop_data.get("lon"),
    )

    # Check for no-service day
    patterns = stop_data.get("stoptimesForServiceDate", [])
    all_empty = all(len(p.get("stoptimes", [])) == 0 for p in patterns)
    if all_empty:
        return 0

    # Build observation rows
    observations = []
    for pattern_data in patterns:
        route = pattern_data["pattern"]["route"]
        direction_id = pattern_data["pattern"].get("directionId")

        for st in pattern_data.get("stoptimes", []):
            trip_id = st["trip"]["gtfsId"]
            observations.append(
                {
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
                }
            )

    if observations:
        upsert_observations_batch(db, observations)
    return len(observations)


def collect_daily(
    db_path: str,
    api_url: str,
    api_key: str,
    stop_id: str | None = None,
    service_date: str | None = None,
    feed_id: str | None = None,
    rate_limit_delay: float = 0.1,
) -> dict:
    """Fetch full day schedule for one or all stops. Returns status dict."""
    client = DigitransitClient(api_url, api_key)
    db = connect_direct(db_path)
    now = int(time.time())

    target_date = service_date
    if target_date is None:
        target_date = datetime.now(HELSINKI_TZ).strftime("%Y-%m-%d")

    try:
        if stop_id:
            # Single stop mode
            count = collect_daily_single(client, db, stop_id, target_date, now)
            log_collection(db, stop_id, "daily", target_date, departures_found=count)
            db.close()
            if count == 0:
                return {
                    "status": "no_service",
                    "date": target_date,
                    "message": f"No service on {target_date}",
                }
            return {"status": "ok", "date": target_date, "departures": count}

        # All-stops mode
        stop_ids = get_all_stop_ids(db, feed_id)
        if not stop_ids:
            # No stops yet — discover them first
            db.close()
            discover_stops(db_path, api_url, api_key, feed_id or "Vaasa")
            db = connect_direct(db_path)
            stop_ids = get_all_stop_ids(db, feed_id)

        total = 0
        stops_with_service = 0
        for i, sid in enumerate(stop_ids):
            count = collect_daily_single(client, db, sid, target_date, now)
            total += count
            if count > 0:
                stops_with_service += 1
            if rate_limit_delay > 0 and i < len(stop_ids) - 1:
                time.sleep(rate_limit_delay)

        log_collection(db, feed_id or "all", "daily", target_date, departures_found=total)
        logger.info(
            "Daily collection: %d departures across %d/%d stops for %s",
            total,
            stops_with_service,
            len(stop_ids),
            target_date,
        )
        db.close()
        return {
            "status": "ok",
            "date": target_date,
            "departures": total,
            "stops_with_service": stops_with_service,
            "total_stops": len(stop_ids),
        }

    except Exception as e:
        logger.exception("Daily collection failed for %s", target_date)
        log_collection(db, stop_id or feed_id or "all", "daily", target_date, error=str(e))
        db.close()
        return {"status": "error", "message": str(e)}


def poll_realtime_single(client: DigitransitClient, db, stop_id: str, now: int) -> int:
    """Poll realtime for a single stop. Returns number of observations updated."""
    stop_data = client.fetch_realtime(stop_id)
    if stop_data is None:
        return 0

    stoptimes = stop_data.get("stoptimesWithoutPatterns", [])
    if not stoptimes:
        return 0

    observations = []
    for st in stoptimes:
        trip = st["trip"]
        trip_id = trip["gtfsId"]
        route = trip.get("route", {})
        service_day = st.get("serviceDay")
        service_date = _service_day_to_date(service_day) if service_day else None

        if service_date is None:
            continue

        observations.append(
            {
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
            }
        )

    if observations:
        upsert_observations_batch(db, observations)
    return len(observations)


def poll_realtime_once(
    db_path: str,
    api_url: str,
    api_key: str,
    stop_id: str | None = None,
    feed_id: str | None = None,
    rate_limit_delay: float = 0.1,
) -> dict:
    """Single realtime poll for one or all stops."""
    client = DigitransitClient(api_url, api_key)
    db = connect_direct(db_path)
    now = int(time.time())

    try:
        if stop_id:
            # Single stop mode
            count = poll_realtime_single(client, db, stop_id, now)
            log_collection(db, stop_id, "realtime", departures_found=count)
            db.close()
            return {"status": "ok", "updated": count}

        # All-stops mode
        stop_ids = get_all_stop_ids(db, feed_id)
        if not stop_ids:
            db.close()
            return {
                "status": "ok",
                "updated": 0,
                "message": "No stops discovered yet. Run daily collection first.",
            }

        total = 0
        realtime_count = 0
        for i, sid in enumerate(stop_ids):
            count = poll_realtime_single(client, db, sid, now)
            total += count
            if count > 0:
                realtime_count += 1
            if rate_limit_delay > 0 and i < len(stop_ids) - 1:
                time.sleep(rate_limit_delay)

        log_collection(db, feed_id or "all", "realtime", departures_found=total)
        logger.info(
            "Realtime poll: %d departures across %d/%d stops",
            total,
            realtime_count,
            len(stop_ids),
        )
        db.close()
        return {
            "status": "ok",
            "updated": total,
            "stops_polled": len(stop_ids),
            "stops_with_data": realtime_count,
        }

    except Exception as e:
        logger.exception("Realtime poll failed")
        log_collection(db, stop_id or feed_id or "all", "realtime", error=str(e))
        db.close()
        return {"status": "error", "message": str(e)}
