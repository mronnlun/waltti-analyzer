from unittest.mock import MagicMock, patch

from app.digitransit import DigitransitClient


def _mock_response(data):
    resp = MagicMock()
    resp.json.return_value = {"data": data}
    resp.raise_for_status.return_value = None
    return resp


def test_fetch_daily_schedule():
    client = DigitransitClient("http://test/api", "test-key")

    mock_data = {
        "stop": {
            "gtfsId": "Vaasa:309392",
            "name": "Gerbynmäentie",
            "code": None,
            "lat": 63.14,
            "lon": 21.57,
            "stoptimesForServiceDate": [
                {
                    "pattern": {
                        "route": {"shortName": "3", "longName": "Gerby - Keskusta", "mode": "BUS"},
                        "directionId": 1,
                    },
                    "stoptimes": [
                        {
                            "scheduledArrival": 24000,
                            "scheduledDeparture": 24100,
                            "realtimeArrival": 24000,
                            "realtimeDeparture": 24100,
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
    }

    with patch.object(client.session, "post", return_value=_mock_response(mock_data)):
        from datetime import date

        result = client.fetch_daily_schedule("Vaasa:309392", date(2026, 4, 2))

    assert result is not None
    assert result["gtfsId"] == "Vaasa:309392"
    patterns = result["stoptimesForServiceDate"]
    assert len(patterns) == 1
    assert len(patterns[0]["stoptimes"]) == 1


def test_fetch_realtime():
    client = DigitransitClient("http://test/api", "test-key")

    mock_data = {
        "stop": {
            "gtfsId": "Vaasa:309392",
            "name": "Gerbynmäentie",
            "stoptimesWithoutPatterns": [
                {
                    "serviceDay": 1775170800,
                    "scheduledArrival": 24000,
                    "realtimeArrival": 24120,
                    "arrivalDelay": 120,
                    "scheduledDeparture": 24100,
                    "realtimeDeparture": 24220,
                    "departureDelay": 120,
                    "realtime": True,
                    "realtimeState": "UPDATED",
                    "headsign": "Keskusta",
                    "trip": {
                        "gtfsId": "Vaasa:trip1",
                        "route": {"shortName": "3", "longName": "Gerby - Keskusta"},
                    },
                }
            ],
        }
    }

    with patch.object(client.session, "post", return_value=_mock_response(mock_data)):
        result = client.fetch_realtime("Vaasa:309392")

    assert result is not None
    stoptimes = result["stoptimesWithoutPatterns"]
    assert len(stoptimes) == 1
    assert stoptimes[0]["realtime"] is True
    assert stoptimes[0]["departureDelay"] == 120


def test_search_stops_by_name():
    client = DigitransitClient("http://test/api", "test-key")

    mock_data = {
        "stops": [
            {
                "gtfsId": "Vaasa:309392",
                "name": "Gerbynmäentie",
                "code": None,
                "lat": 63.14,
                "lon": 21.57,
            }
        ]
    }

    with patch.object(client.session, "post", return_value=_mock_response(mock_data)):
        result = client.search_stops_by_name("Gerbyn")

    assert len(result) == 1
    assert result[0]["gtfsId"] == "Vaasa:309392"


def test_fetch_returns_none_for_missing_stop():
    client = DigitransitClient("http://test/api", "test-key")

    mock_data = {"stop": None}

    with patch.object(client.session, "post", return_value=_mock_response(mock_data)):
        result = client.fetch_daily_schedule("Vaasa:999999")

    assert result is None


def test_auth_header_set():
    client = DigitransitClient("http://test/api", "my-secret-key")
    assert client.session.headers["digitransit-subscription-key"] == "my-secret-key"
    assert client.session.headers["Content-Type"] == "application/json"
