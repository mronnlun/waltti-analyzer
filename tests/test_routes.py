import time
from unittest.mock import patch

from app.db import upsert_observations_batch, upsert_stop, upsert_trips_batch


def test_dashboard_loads(client):
    resp = client.get("/")
    assert resp.status_code == 200
    assert b"Waltti Analyzer" in resp.data


def test_dashboard_with_date_range(client, db):
    upsert_trips_batch(
        db,
        [
            {
                "gtfs_id": "Vaasa:trip1",
                "route_short_name": "3",
                "route_long_name": "Gerby - Keskusta",
                "mode": "BUS",
                "headsign": "Keskusta",
                "direction_id": 1,
            }
        ],
    )
    upsert_observations_batch(
        db,
        [
            {
                "stop_gtfs_id": "Vaasa:309392",
                "trip_gtfs_id": "Vaasa:trip1",
                "service_date": "2026-04-02",
                "scheduled_arrival": 24000,
                "scheduled_departure": 24100,
                "realtime_arrival": 24120,
                "realtime_departure": 24220,
                "arrival_delay": 120,
                "departure_delay": 120,
                "realtime": 1,
                "realtime_state": "UPDATED",
                "queried_at": int(time.time()),
            }
        ],
    )

    resp = client.get("/?stop_id=Vaasa:309392&from=2026-04-02&to=2026-04-02")
    assert resp.status_code == 200
    assert b"Total Departures" in resp.data


def test_stops_page(client):
    resp = client.get("/stops")
    assert resp.status_code == 200
    assert b"Bus Stops" in resp.data


def test_api_status(client):
    resp = client.get("/api/status")
    assert resp.status_code == 200
    data = resp.get_json()
    assert data["feed_id"] == "Vaasa"


def test_api_observations_requires_date(client):
    resp = client.get("/api/observations")
    assert resp.status_code == 400


def test_api_observations_requires_stop_id(client):
    resp = client.get("/api/observations?date=2026-04-02")
    assert resp.status_code == 400


def test_api_summary_requires_params(client):
    resp = client.get("/api/summary")
    assert resp.status_code == 400


def test_api_collect_daily(client):
    with patch("app.routes.api.collect_daily") as mock_collect:
        mock_collect.return_value = {"status": "ok", "date": "2026-04-02", "departures": 20}
        resp = client.post("/api/collect/daily", json={})
        assert resp.status_code == 200
        data = resp.get_json()
        assert data["status"] == "ok"


def test_api_collect_realtime(client):
    with patch("app.routes.api.poll_realtime_once") as mock_poll:
        mock_poll.return_value = {"status": "ok", "updated": 5, "with_realtime": 3}
        resp = client.post("/api/collect/realtime")
        assert resp.status_code == 200
        data = resp.get_json()
        assert data["status"] == "ok"


def test_api_discover(client):
    with patch("app.routes.api.discover_stops") as mock_discover:
        mock_discover.return_value = {"status": "ok", "stops": 554, "routes": 39}
        resp = client.post("/api/discover")
        assert resp.status_code == 200
        data = resp.get_json()
        assert data["stops"] == 554


def test_api_stops_list(client):
    resp = client.get("/api/stops")
    assert resp.status_code == 200
    assert isinstance(resp.get_json(), list)


def test_api_routes_list(client):
    resp = client.get("/api/routes")
    assert resp.status_code == 200
    assert isinstance(resp.get_json(), list)
