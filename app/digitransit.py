import logging
import time
from datetime import date, datetime
from zoneinfo import ZoneInfo

import requests

logger = logging.getLogger(__name__)

HELSINKI_TZ = ZoneInfo("Europe/Helsinki")

QUERY_DAILY = """
{
  stop(id: "%s") {
    gtfsId
    name
    code
    lat
    lon
    stoptimesForServiceDate(date: "%s") {
      pattern {
        route { shortName longName mode }
        directionId
      }
      stoptimes {
        scheduledArrival
        scheduledDeparture
        realtimeArrival
        realtimeDeparture
        arrivalDelay
        departureDelay
        realtime
        realtimeState
        headsign
        trip { gtfsId }
      }
    }
  }
}
"""

QUERY_REALTIME = """
{
  stop(id: "%s") {
    gtfsId
    name
    stoptimesWithoutPatterns(
      startTime: %d,
      timeRange: 7200,
      numberOfDepartures: 50
    ) {
      serviceDay
      scheduledArrival
      realtimeArrival
      arrivalDelay
      scheduledDeparture
      realtimeDeparture
      departureDelay
      realtime
      realtimeState
      headsign
      trip {
        gtfsId
        route { shortName longName }
      }
    }
  }
}
"""

QUERY_STOPS_BY_NAME = """
{
  stops(name: "%s") {
    gtfsId
    name
    code
    lat
    lon
  }
}
"""

QUERY_STOPS_BY_RADIUS = """
{
  stopsByRadius(lat: %f, lon: %f, radius: %d) {
    edges {
      node {
        stop { gtfsId name code lat lon }
        distance
      }
    }
  }
}
"""

QUERY_FEED_ROUTES = """
{
  routes {
    gtfsId
    shortName
    longName
    mode
    patterns {
      directionId
      stops {
        gtfsId
        name
        code
        lat
        lon
      }
    }
  }
}
"""


class DigitransitClient:
    def __init__(self, api_url: str, api_key: str):
        self.api_url = api_url
        self.api_key = api_key
        self.session = requests.Session()
        self.session.headers.update(
            {
                "Content-Type": "application/json",
                "digitransit-subscription-key": api_key,
            }
        )

    def _query(self, graphql: str, retries: int = 1) -> dict:
        for attempt in range(retries + 1):
            try:
                resp = self.session.post(
                    self.api_url,
                    json={"query": graphql},
                    timeout=30,
                )
                resp.raise_for_status()
                data = resp.json()
                if "errors" in data:
                    logger.error("GraphQL errors: %s", data["errors"])
                return data.get("data", {})
            except requests.RequestException as e:
                logger.warning("API request failed (attempt %d): %s", attempt + 1, e)
                if attempt < retries:
                    time.sleep(10)
                else:
                    raise

    def fetch_daily_schedule(self, stop_id: str, service_date: date | None = None) -> dict | None:
        if service_date is None:
            service_date = datetime.now(HELSINKI_TZ).date()
        date_str = service_date.isoformat()
        data = self._query(QUERY_DAILY % (stop_id, date_str))
        return data.get("stop")

    def fetch_realtime(self, stop_id: str) -> dict | None:
        now_utc = int(time.time())
        data = self._query(QUERY_REALTIME % (stop_id, now_utc))
        return data.get("stop")

    def search_stops_by_name(self, name: str) -> list[dict]:
        data = self._query(QUERY_STOPS_BY_NAME % name)
        return data.get("stops", [])

    def search_stops_by_radius(self, lat: float, lon: float, radius: int = 500) -> list[dict]:
        data = self._query(QUERY_STOPS_BY_RADIUS % (lat, lon, radius))
        edges = data.get("stopsByRadius", {}).get("edges", [])
        return [{**edge["node"]["stop"], "distance": edge["node"]["distance"]} for edge in edges]

    def discover_feed_stops(self, feed_id: str) -> tuple[list[dict], list[dict]]:
        """Discover all stops and routes for a feed (e.g. 'Vaasa').

        Returns (stops, routes) where stops are unique stop dicts and
        routes are route dicts with their stop IDs.
        """
        data = self._query(QUERY_FEED_ROUTES)
        all_routes = data.get("routes", [])

        stops = {}
        routes = []
        for r in all_routes:
            if not r["gtfsId"].startswith(f"{feed_id}:"):
                continue
            route_stop_ids = set()
            for p in r.get("patterns", []):
                for s in p.get("stops", []):
                    sid = s["gtfsId"]
                    if sid.startswith(f"{feed_id}:"):
                        stops[sid] = {
                            "gtfs_id": sid,
                            "name": s["name"],
                            "code": s.get("code"),
                            "lat": s.get("lat"),
                            "lon": s.get("lon"),
                        }
                        route_stop_ids.add(sid)
            routes.append(
                {
                    "gtfs_id": r["gtfsId"],
                    "short_name": r.get("shortName"),
                    "long_name": r.get("longName"),
                    "mode": r.get("mode"),
                    "stop_ids": list(route_stop_ids),
                }
            )

        return list(stops.values()), routes
