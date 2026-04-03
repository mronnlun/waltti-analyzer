import time

from app.db import (
    get_stop,
    upsert_stop,
    upsert_observation,
    upsert_observations_batch,
    get_observations,
    get_recent_observations,
    log_collection,
    get_latest_collection,
)


def test_upsert_and_get_stop(db):
    upsert_stop(db, "Vaasa:309392", "Gerbynmäentie", None, 63.14, 21.57)
    stop = get_stop(db, "Vaasa:309392")
    assert stop is not None
    assert stop["name"] == "Gerbynmäentie"
    assert stop["lat"] == 63.14


def test_upsert_stop_updates(db):
    upsert_stop(db, "Vaasa:309392", "Old Name", None, 63.14, 21.57)
    upsert_stop(db, "Vaasa:309392", "New Name", None, 63.14, 21.57)
    stop = get_stop(db, "Vaasa:309392")
    assert stop["name"] == "New Name"


def test_upsert_observation(db):
    obs = {
        "stop_gtfs_id": "Vaasa:309392",
        "trip_gtfs_id": "Vaasa:trip1",
        "route_short_name": "3",
        "route_long_name": "Gerby - Keskusta",
        "mode": "BUS",
        "headsign": "Keskusta",
        "direction_id": 1,
        "service_date": "2026-04-02",
        "service_day_unix": None,
        "scheduled_arrival": 24000,
        "scheduled_departure": 24100,
        "realtime_arrival": None,
        "realtime_departure": None,
        "arrival_delay": 0,
        "departure_delay": 0,
        "realtime": 0,
        "realtime_state": "SCHEDULED",
        "queried_at": int(time.time()),
    }
    upsert_observation(db, **obs)

    rows = get_observations(db, "Vaasa:309392", "2026-04-02", "2026-04-02")
    assert len(rows) == 1
    assert rows[0]["route_short_name"] == "3"


def test_upsert_observation_updates_on_conflict(db):
    now = int(time.time())
    base = {
        "stop_gtfs_id": "Vaasa:309392",
        "trip_gtfs_id": "Vaasa:trip1",
        "route_short_name": "3",
        "route_long_name": "Gerby - Keskusta",
        "mode": "BUS",
        "headsign": "Keskusta",
        "direction_id": 1,
        "service_date": "2026-04-02",
        "service_day_unix": None,
        "scheduled_arrival": 24000,
        "scheduled_departure": 24100,
        "realtime_arrival": None,
        "realtime_departure": None,
        "arrival_delay": 0,
        "departure_delay": 0,
        "realtime": 0,
        "realtime_state": "SCHEDULED",
        "queried_at": now,
    }
    upsert_observation(db, **base)

    # Update with realtime data
    base["realtime"] = 1
    base["departure_delay"] = 120
    base["realtime_departure"] = 24220
    base["realtime_state"] = "UPDATED"
    base["queried_at"] = now + 60
    upsert_observation(db, **base)

    rows = get_observations(db, "Vaasa:309392", "2026-04-02", "2026-04-02")
    assert len(rows) == 1
    assert rows[0]["realtime"] == 1
    assert rows[0]["departure_delay"] == 120


def test_batch_upsert(db):
    now = int(time.time())
    observations = [
        {
            "stop_gtfs_id": "Vaasa:309392",
            "trip_gtfs_id": f"Vaasa:trip{i}",
            "route_short_name": "3",
            "route_long_name": "Gerby - Keskusta",
            "mode": "BUS",
            "headsign": "Keskusta",
            "direction_id": 1,
            "service_date": "2026-04-02",
            "service_day_unix": None,
            "scheduled_arrival": 24000 + i * 1800,
            "scheduled_departure": 24100 + i * 1800,
            "realtime_arrival": None,
            "realtime_departure": None,
            "arrival_delay": 0,
            "departure_delay": 0,
            "realtime": 0,
            "realtime_state": "SCHEDULED",
            "queried_at": now,
        }
        for i in range(5)
    ]
    upsert_observations_batch(db, observations)

    rows = get_observations(db, "Vaasa:309392", "2026-04-02", "2026-04-02")
    assert len(rows) == 5


def test_get_observations_with_route_filter(db):
    now = int(time.time())
    for route, trip in [("3", "trip_a"), ("9", "trip_b")]:
        upsert_observation(
            db,
            stop_gtfs_id="Vaasa:309392",
            trip_gtfs_id=f"Vaasa:{trip}",
            route_short_name=route,
            route_long_name="Test",
            mode="BUS",
            headsign="Test",
            direction_id=1,
            service_date="2026-04-02",
            service_day_unix=None,
            scheduled_arrival=24000,
            scheduled_departure=24100,
            realtime_arrival=None,
            realtime_departure=None,
            arrival_delay=0,
            departure_delay=0,
            realtime=0,
            realtime_state="SCHEDULED",
            queried_at=now,
        )

    all_rows = get_observations(db, "Vaasa:309392", "2026-04-02", "2026-04-02")
    assert len(all_rows) == 2

    route3 = get_observations(db, "Vaasa:309392", "2026-04-02", "2026-04-02", route="3")
    assert len(route3) == 1
    assert route3[0]["route_short_name"] == "3"


def test_recent_observations(db):
    now = int(time.time())
    for i in range(25):
        upsert_observation(
            db,
            stop_gtfs_id="Vaasa:309392",
            trip_gtfs_id=f"Vaasa:trip{i}",
            route_short_name="3",
            route_long_name="Test",
            mode="BUS",
            headsign="Test",
            direction_id=1,
            service_date="2026-04-02",
            service_day_unix=None,
            scheduled_arrival=24000 + i * 1800,
            scheduled_departure=24100 + i * 1800,
            realtime_arrival=None,
            realtime_departure=None,
            arrival_delay=0,
            departure_delay=0,
            realtime=0,
            realtime_state="SCHEDULED",
            queried_at=now,
        )

    recent = get_recent_observations(db, "Vaasa:309392", limit=20)
    assert len(recent) == 20


def test_collection_log(db):
    log_collection(db, "Vaasa:309392", "daily", "2026-04-02", departures_found=20)
    log_collection(db, "Vaasa:309392", "realtime", departures_found=5)

    latest_daily = get_latest_collection(db, "Vaasa:309392", "daily")
    assert latest_daily is not None
    assert latest_daily["departures_found"] == 20

    latest_rt = get_latest_collection(db, "Vaasa:309392", "realtime")
    assert latest_rt is not None
    assert latest_rt["departures_found"] == 5
