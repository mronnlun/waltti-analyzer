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
    upsert_trips_batch,
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


def _process_daily_stop(stop_data: dict, target_date: str, now: int) -> tuple[dict, list]:
    """Extract trips and observations from a single stop's daily data.

    Returns (trips_dict, observations_list).
    """
    trips = {}
    observations = []
    stop_id = stop_data["gtfsId"]

    for pattern_data in stop_data.get("stoptimesForServiceDate", []):
        route = pattern_data["pattern"]["route"]
        direction_id = pattern_data["pattern"].get("directionId")

        for st in pattern_data.get("stoptimes", []):
            trip_id = st["trip"]["gtfsId"]
            trips[trip_id] = {
                "gtfs_id": trip_id,
                "route_short_name": route.get("shortName"),
                "route_long_name": route.get("longName"),
                "mode": route.get("mode"),
                "headsign": st.get("headsign"),
                "direction_id": direction_id,
            }
            observations.append(
                {
                    "stop_gtfs_id": stop_id,
                    "trip_gtfs_id": trip_id,
                    "service_date": target_date,
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

    return trips, observations


def collect_daily(
    db_path: str,
    api_url: str,
    api_key: str,
    stop_id: str | None = None,
    service_date: str | None = None,
    feed_id: str | None = None,
) -> dict:
    """Fetch full day schedule for one or all stops in a single API call."""
    client = DigitransitClient(api_url, api_key)
    db = connect_direct(db_path)
    now = int(time.time())

    target_date = service_date
    if target_date is None:
        target_date = datetime.now(HELSINKI_TZ).strftime("%Y-%m-%d")

    try:
        # Determine which stop IDs to fetch
        if stop_id:
            query_ids = [stop_id]
        else:
            query_ids = get_all_stop_ids(db, feed_id)
            if not query_ids:
                db.close()
                discover_stops(db_path, api_url, api_key, feed_id or "Vaasa")
                db = connect_direct(db_path)
                query_ids = get_all_stop_ids(db, feed_id)

        # Single bulk API call
        stops_data = client.fetch_bulk_daily(query_ids, date.fromisoformat(target_date))

        all_trips = {}
        all_observations = []
        stops_with_service = 0

        for stop_data in stops_data:
            # Upsert stop info
            upsert_stop(
                db,
                stop_data["gtfsId"],
                stop_data["name"],
                stop_data.get("code"),
                stop_data.get("lat"),
                stop_data.get("lon"),
            )

            trips, observations = _process_daily_stop(stop_data, target_date, now)
            all_trips.update(trips)
            all_observations.extend(observations)
            if observations:
                stops_with_service += 1

        if all_trips:
            upsert_trips_batch(db, list(all_trips.values()))
        if all_observations:
            upsert_observations_batch(db, all_observations)

        log_collection(
            db,
            stop_id or feed_id or "all",
            "daily",
            target_date,
            departures_found=len(all_observations),
        )
        logger.info(
            "Daily collection: %d departures across %d/%d stops for %s (1 API call)",
            len(all_observations),
            stops_with_service,
            len(stops_data),
            target_date,
        )
        db.close()

        if stop_id and len(all_observations) == 0:
            return {
                "status": "no_service",
                "date": target_date,
                "message": f"No service on {target_date}",
            }

        return {
            "status": "ok",
            "date": target_date,
            "departures": len(all_observations),
            "stops_with_service": stops_with_service,
            "total_stops": len(stops_data),
        }

    except Exception as e:
        logger.exception("Daily collection failed for %s", target_date)
        log_collection(db, stop_id or feed_id or "all", "daily", target_date, error=str(e))
        db.close()
        return {"status": "error", "message": str(e)}


REALTIME_BATCH_SIZE = 50


def _process_realtime_batch(
    stops_data: list[dict], now: int
) -> tuple[dict, list, int]:
    """Parse a batch of stop data into trips and observations."""
    trips = {}
    observations = []
    stops_with_data = 0

    for stop_data in stops_data:
        sid = stop_data["gtfsId"]
        stoptimes = stop_data.get("stoptimesWithoutPatterns", [])
        if not stoptimes:
            continue

        stop_has_data = False
        for st in stoptimes:
            trip = st["trip"]
            trip_id = trip["gtfsId"]
            route = trip.get("route", {})
            service_day = st.get("serviceDay")
            svc_date = _service_day_to_date(service_day) if service_day else None

            if svc_date is None:
                continue

            trips[trip_id] = {
                "gtfs_id": trip_id,
                "route_short_name": route.get("shortName"),
                "route_long_name": route.get("longName"),
                "mode": None,
                "headsign": st.get("headsign"),
                "direction_id": None,
            }
            observations.append(
                {
                    "stop_gtfs_id": sid,
                    "trip_gtfs_id": trip_id,
                    "service_date": svc_date,
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
            stop_has_data = True

        if stop_has_data:
            stops_with_data += 1

    return trips, observations, stops_with_data


def poll_realtime_once(
    db_path: str,
    api_url: str,
    api_key: str,
    stop_id: str | None = None,
    feed_id: str | None = None,
) -> dict:
    """Single realtime poll, processed in batches to limit memory."""
    client = DigitransitClient(api_url, api_key)
    db = connect_direct(db_path)
    now = int(time.time())

    try:
        if stop_id:
            query_ids = [stop_id]
        else:
            query_ids = get_all_stop_ids(db, feed_id)
            if not query_ids:
                db.close()
                return {
                    "status": "ok",
                    "updated": 0,
                    "message": "No stops discovered yet.",
                }

        total_updated = 0
        total_stops_with_data = 0
        total_stops_polled = 0

        # Process in batches to keep memory bounded
        for i in range(0, len(query_ids), REALTIME_BATCH_SIZE):
            batch_ids = query_ids[i : i + REALTIME_BATCH_SIZE]
            stops_data = client.fetch_bulk_realtime(batch_ids)
            total_stops_polled += len(stops_data)

            trips, observations, stops_with_data = _process_realtime_batch(
                stops_data, now
            )

            if trips:
                upsert_trips_batch(db, list(trips.values()))
            if observations:
                upsert_observations_batch(db, observations)

            total_updated += len(observations)
            total_stops_with_data += stops_with_data
            # Batch data is now in DB; free memory
            del stops_data, trips, observations

        log_collection(
            db,
            stop_id or feed_id or "all",
            "realtime",
            departures_found=total_updated,
        )
        logger.info(
            "Realtime poll: %d departures across %d/%d stops"
            " (%d batches)",
            total_updated,
            total_stops_with_data,
            total_stops_polled,
            (len(query_ids) + REALTIME_BATCH_SIZE - 1) // REALTIME_BATCH_SIZE,
        )
        db.close()
        return {
            "status": "ok",
            "updated": total_updated,
            "stops_polled": total_stops_polled,
            "stops_with_data": total_stops_with_data,
        }

    except Exception as e:
        logger.exception("Realtime poll failed")
        log_collection(db, stop_id or feed_id or "all", "realtime", error=str(e))
        db.close()
        return {"status": "error", "message": str(e)}
