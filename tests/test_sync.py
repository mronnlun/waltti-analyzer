from unittest.mock import patch

import pytest
from shared.db import (
    connect,
    init_db,
    upsert_stop,
)


@pytest.fixture
def db_path(tmp_path):
    path = str(tmp_path / "test.db")
    init_db(path)
    conn = connect(path)
    upsert_stop(conn, "Vaasa:309392", "Gerbynmäentie", None, 63.14, 21.57)
    conn.close()
    return path


def test_sync_runs_realtime_poll(db_path, monkeypatch):
    """Timer function always runs realtime polling."""
    monkeypatch.setenv("DATABASE_PATH", db_path)
    monkeypatch.setenv("DIGITRANSIT_API_KEY", "test-key")
    monkeypatch.setenv("FEED_ID", "Vaasa")

    # Re-import config after setting env vars
    import importlib

    import shared.config

    importlib.reload(shared.config)

    from shared.collector import poll_realtime_once

    with patch("shared.collector.DigitransitClient") as MockClient:
        instance = MockClient.return_value
        instance.fetch_bulk_realtime.return_value = []

        result = poll_realtime_once(db_path, "http://test/api", "test-key", feed_id="Vaasa")
        assert result["status"] == "ok"


def test_daily_collection_runs(db_path):
    """collect_daily works with a configured db."""
    with patch("shared.collector.DigitransitClient") as MockClient:
        instance = MockClient.return_value
        instance.fetch_bulk_daily.return_value = [
            {
                "gtfsId": "Vaasa:309392",
                "name": "Gerbynmäentie",
                "code": None,
                "lat": 63.14,
                "lon": 21.57,
                "stoptimesForServiceDate": [
                    {
                        "pattern": {
                            "route": {"shortName": "3", "longName": "Gerby", "mode": "BUS"},
                            "directionId": 1,
                        },
                        "stoptimes": [
                            {
                                "scheduledArrival": 24000,
                                "scheduledDeparture": 24100,
                                "realtimeArrival": None,
                                "realtimeDeparture": None,
                                "arrivalDelay": 0,
                                "departureDelay": 0,
                                "realtime": False,
                                "realtimeState": "SCHEDULED",
                                "headsign": "Keskusta",
                                "trip": {"gtfsId": "Vaasa:trip1"},
                            }
                        ],
                    }
                ],
            }
        ]

        from shared.collector import collect_daily

        result = collect_daily(
            db_path,
            "http://test/api",
            "test-key",
            stop_id="Vaasa:309392",
            service_date="2026-04-02",
        )
        assert result["status"] == "ok"
        assert result["departures"] == 1


def test_discover_stops_runs(db_path):
    """discover_stops works with a configured db."""
    with patch("shared.collector.DigitransitClient") as MockClient:
        instance = MockClient.return_value
        instance.discover_feed_stops.return_value = (
            [
                {
                    "gtfs_id": "Vaasa:309392",
                    "name": "Gerbynmäentie",
                    "code": None,
                    "lat": 63.14,
                    "lon": 21.57,
                }
            ],
            [
                {
                    "gtfs_id": "Vaasa:route1",
                    "short_name": "3",
                    "long_name": "Gerby",
                    "mode": "BUS",
                    "stop_ids": [],
                }
            ],
        )

        from shared.collector import discover_stops

        result = discover_stops(db_path, "http://test/api", "test-key", "Vaasa")
        assert result["status"] == "ok"
        assert result["stops"] == 1
