import time

from app.analyzer import (
    format_delay,
    get_delay_by_hour,
    get_route_breakdown,
    get_summary,
    parse_time,
)
from app.db import upsert_observations_batch, upsert_trips_batch


def _setup_trips_and_obs(db, obs_specs):
    """Create trips and observations from specs. Returns observation count."""
    trips = {}
    observations = []
    for spec in obs_specs:
        trip_id = f"Vaasa:{spec['trip_id']}"
        if trip_id not in trips:
            trips[trip_id] = {
                "gtfs_id": trip_id,
                "route_short_name": spec["route"],
                "route_long_name": f"Route {spec['route']}",
                "mode": "BUS",
                "headsign": "Test",
                "direction_id": 1,
            }
        realtime = spec.get("realtime", True)
        delay = spec["delay"]
        scheduled_dep = spec["scheduled_dep"]
        observations.append(
            {
                "stop_gtfs_id": "Vaasa:309392",
                "trip_gtfs_id": trip_id,
                "service_date": spec.get("date", "2026-04-02"),
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
        )
    upsert_trips_batch(db, list(trips.values()))
    upsert_observations_batch(db, observations)
    return len(observations)


def test_summary_empty(db):
    summary = get_summary(db, "Vaasa:309392", "2026-04-01", "2026-04-30")
    assert summary["total_departures"] == 0


def test_summary_with_data(db):
    _setup_trips_and_obs(
        db,
        [
            {"trip_id": "trip1", "route": "3", "scheduled_dep": 24000, "delay": 30},
            {"trip_id": "trip2", "route": "3", "scheduled_dep": 25800, "delay": 120},
            {"trip_id": "trip3", "route": "3", "scheduled_dep": 27600, "delay": 300},
            {"trip_id": "trip4", "route": "9", "scheduled_dep": 29400, "delay": 0},
            {
                "trip_id": "trip5",
                "route": "3",
                "scheduled_dep": 31200,
                "delay": 0,
                "realtime": False,
            },
        ],
    )

    summary = get_summary(db, "Vaasa:309392", "2026-04-02", "2026-04-02")
    assert summary["total_departures"] == 5
    assert summary["with_realtime"] == 4
    assert summary["static_only"] == 1
    assert summary["on_time"] == 3
    assert summary["slightly_late"] == 1


def test_summary_route_filter(db):
    _setup_trips_and_obs(
        db,
        [
            {"trip_id": "trip1", "route": "3", "scheduled_dep": 24000, "delay": 30},
            {"trip_id": "trip2", "route": "9", "scheduled_dep": 25800, "delay": 120},
        ],
    )

    summary = get_summary(db, "Vaasa:309392", "2026-04-02", "2026-04-02", route="3")
    assert summary["total_departures"] == 1
    assert summary["with_realtime"] == 1


def test_route_breakdown(db):
    _setup_trips_and_obs(
        db,
        [
            {"trip_id": "trip1", "route": "3", "scheduled_dep": 24000, "delay": 30},
            {"trip_id": "trip2", "route": "3", "scheduled_dep": 25800, "delay": 300},
            {"trip_id": "trip3", "route": "9", "scheduled_dep": 27600, "delay": 0},
        ],
    )

    breakdown = get_route_breakdown(db, "Vaasa:309392", "2026-04-02", "2026-04-02")
    assert len(breakdown) == 2

    route3 = next(r for r in breakdown if r["route"] == "3")
    assert route3["departures"] == 2
    assert route3["with_realtime"] == 2

    route9 = next(r for r in breakdown if r["route"] == "9")
    assert route9["departures"] == 1


def test_delay_by_hour(db):
    _setup_trips_and_obs(
        db,
        [
            {"trip_id": "trip1", "route": "3", "scheduled_dep": 6 * 3600 + 400, "delay": 60},
            {"trip_id": "trip2", "route": "3", "scheduled_dep": 6 * 3600 + 1800, "delay": 120},
            {"trip_id": "trip3", "route": "3", "scheduled_dep": 8 * 3600 + 100, "delay": 300},
        ],
    )

    hourly = get_delay_by_hour(db, "Vaasa:309392", "2026-04-02", "2026-04-02")
    assert len(hourly) == 2

    hour6 = next(h for h in hourly if h["hour"] == 6)
    assert hour6["departures"] == 2
    assert hour6["avg_late_seconds"] == 90.0  # (60+120)/2


def test_summary_time_filter(db):
    _setup_trips_and_obs(
        db,
        [
            {"trip_id": "early", "route": "3", "scheduled_dep": 6 * 3600, "delay": 30},
            {"trip_id": "target", "route": "3", "scheduled_dep": 16 * 3600 + 300, "delay": 120},
            {"trip_id": "late", "route": "3", "scheduled_dep": 20 * 3600, "delay": 0},
        ],
    )

    # Filter to 16:04–16:06 (57840–57960 seconds) — only "target" matches
    summary = get_summary(
        db,
        "Vaasa:309392",
        "2026-04-02",
        "2026-04-02",
        time_from=16 * 3600 + 240,
        time_to=16 * 3600 + 360,
    )
    assert summary["total_departures"] == 1
    assert summary["with_realtime"] == 1

    # Route breakdown with time filter
    breakdown = get_route_breakdown(
        db,
        "Vaasa:309392",
        "2026-04-02",
        "2026-04-02",
        time_from=16 * 3600 + 240,
        time_to=16 * 3600 + 360,
    )
    assert len(breakdown) == 1
    assert breakdown[0]["departures"] == 1


def test_parse_time():
    assert parse_time("16:05") == 16 * 3600 + 5 * 60
    assert parse_time("00:00") == 0
    assert parse_time("") is None
    assert parse_time(None) is None
    assert parse_time("invalid") is None


def test_format_delay():
    assert format_delay(0) == "+0s"
    assert format_delay(90) == "+1m 30s"
    assert format_delay(-45) == "-45s"
    assert format_delay(660) == "+11m 00s"
    assert format_delay(None) == "N/A"
