import time

from app.analyzer import format_delay, get_delay_by_hour, get_route_breakdown, get_summary
from app.db import upsert_observations_batch


def _make_obs(trip_id, route, scheduled_dep, delay, realtime=True, date="2026-04-02"):
    return {
        "stop_gtfs_id": "Vaasa:309392",
        "trip_gtfs_id": f"Vaasa:{trip_id}",
        "route_short_name": route,
        "route_long_name": f"Route {route}",
        "mode": "BUS",
        "headsign": "Test",
        "direction_id": 1,
        "service_date": date,
        "service_day_unix": None,
        "scheduled_arrival": scheduled_dep - 100,
        "scheduled_departure": scheduled_dep,
        "realtime_arrival": (scheduled_dep - 100 + delay) if realtime else None,
        "realtime_departure": (scheduled_dep + delay) if realtime else None,
        "arrival_delay": delay if realtime else 0,
        "departure_delay": delay if realtime else 0,
        "realtime": 1 if realtime else 0,
        "realtime_state": "UPDATED" if realtime else "SCHEDULED",
        "queried_at": int(time.time()),
    }


def test_summary_empty(db):
    summary = get_summary(db, "Vaasa:309392", "2026-04-01", "2026-04-30")
    assert summary["total_departures"] == 0


def test_summary_with_data(db):
    observations = [
        _make_obs("trip1", "3", 24000, 30),  # on time (30s)
        _make_obs("trip2", "3", 25800, 120),  # on time (2min)
        _make_obs("trip3", "3", 27600, 300),  # slightly late (5min)
        _make_obs("trip4", "9", 29400, 0),  # on time
        _make_obs("trip5", "3", 31200, 0, False),  # static only
    ]
    upsert_observations_batch(db, observations)

    summary = get_summary(db, "Vaasa:309392", "2026-04-02", "2026-04-02")
    assert summary["total_departures"] == 5
    assert summary["with_realtime"] == 4
    assert summary["static_only"] == 1
    assert summary["on_time"] == 3
    assert summary["slightly_late"] == 1


def test_summary_route_filter(db):
    observations = [
        _make_obs("trip1", "3", 24000, 30),
        _make_obs("trip2", "9", 25800, 120),
    ]
    upsert_observations_batch(db, observations)

    summary = get_summary(db, "Vaasa:309392", "2026-04-02", "2026-04-02", route="3")
    assert summary["total_departures"] == 1
    assert summary["with_realtime"] == 1


def test_route_breakdown(db):
    observations = [
        _make_obs("trip1", "3", 24000, 30),
        _make_obs("trip2", "3", 25800, 300),
        _make_obs("trip3", "9", 27600, 0),
    ]
    upsert_observations_batch(db, observations)

    breakdown = get_route_breakdown(db, "Vaasa:309392", "2026-04-02", "2026-04-02")
    assert len(breakdown) == 2

    route3 = next(r for r in breakdown if r["route"] == "3")
    assert route3["departures"] == 2
    assert route3["with_realtime"] == 2

    route9 = next(r for r in breakdown if r["route"] == "9")
    assert route9["departures"] == 1


def test_delay_by_hour(db):
    observations = [
        _make_obs("trip1", "3", 6 * 3600 + 400, 60),  # hour 6
        _make_obs("trip2", "3", 6 * 3600 + 1800, 120),  # hour 6
        _make_obs("trip3", "3", 8 * 3600 + 100, 300),  # hour 8
    ]
    upsert_observations_batch(db, observations)

    hourly = get_delay_by_hour(db, "Vaasa:309392", "2026-04-02", "2026-04-02")
    assert len(hourly) == 2

    hour6 = next(h for h in hourly if h["hour"] == 6)
    assert hour6["departures"] == 2
    assert hour6["avg_delay_seconds"] == 90.0  # (60+120)/2


def test_format_delay():
    assert format_delay(0) == "+0s"
    assert format_delay(90) == "+1m 30s"
    assert format_delay(-45) == "-45s"
    assert format_delay(660) == "+11m 00s"
    assert format_delay(None) == "N/A"
