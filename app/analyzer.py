import sqlite3
from statistics import mean, median

# Delays beyond this threshold (in seconds) are flagged as suspect GPS data
OUTLIER_THRESHOLD = 1800  # 30 minutes


def _is_outlier(delay: int) -> bool:
    return abs(delay) > OUTLIER_THRESHOLD


def parse_time(value: str | None) -> int | None:
    """Parse 'HH:MM' to seconds since midnight. Returns None on bad input."""
    if not value:
        return None
    try:
        h, m = value.split(":")
        return int(h) * 3600 + int(m) * 60
    except (ValueError, AttributeError):
        return None


def _append_filters(query: str, params: list, route: str | None,
                    time_from: int | None, time_to: int | None):
    """Append optional route and time-of-day filters to a query."""
    if route:
        query += " AND t.route_short_name = ?"
        params.append(route)
    if time_from is not None:
        query += " AND o.scheduled_departure >= ?"
        params.append(time_from)
    if time_to is not None:
        query += " AND o.scheduled_departure <= ?"
        params.append(time_to)
    return query


def get_summary(
    db: sqlite3.Connection, stop_id: str, start_date: str, end_date: str,
    route: str | None = None, time_from: int | None = None, time_to: int | None = None,
) -> dict:
    """Compute summary statistics for a date range."""
    query = """SELECT o.departure_delay, o.realtime, o.service_date,
                      rs.name AS realtime_state
               FROM observations o
               JOIN trips t ON o.trip_id = t.id
               JOIN stops s ON o.stop_id = s.id
               LEFT JOIN realtime_states rs ON o.realtime_state_id = rs.id
               WHERE s.gtfs_id = ? AND o.service_date >= ? AND o.service_date <= ?"""
    params: list = [stop_id, start_date, end_date]
    query = _append_filters(query, params, route, time_from, time_to)

    rows = db.execute(query, params).fetchall()

    if not rows:
        return {
            "period": {"start": start_date, "end": end_date},
            "total_departures": 0,
            "message": "No observations found",
        }

    total = len(rows)
    with_realtime = [r for r in rows if r["realtime"]]
    canceled = [r for r in rows if r["realtime_state"] == "CANCELED"]
    static_only = total - len(with_realtime)

    # Delay stats based only on realtime observations
    all_delays = [r["departure_delay"] for r in with_realtime if r["departure_delay"] is not None]
    outliers = [d for d in all_delays if _is_outlier(d)]
    delays = [d for d in all_delays if not _is_outlier(d)]

    on_time = [d for d in delays if 0 <= d <= 180]  # 0–3 min late
    slightly_late = [d for d in delays if 180 < d <= 600]  # 3–10 min late
    very_late = [d for d in delays if d > 600]  # >10 min late
    slightly_early = [d for d in delays if -60 <= d < 0]  # up to 1 min early
    very_early = [d for d in delays if d < -60]  # more than 1 min early

    # Separate late and early for independent averages
    late_delays = [d for d in delays if d > 0]
    early_delays = [d for d in delays if d < 0]

    service_dates = sorted(set(r["service_date"] for r in rows))

    return {
        "period": {"start": start_date, "end": end_date},
        "service_days": len(service_dates),
        "total_departures": total,
        "with_realtime": len(with_realtime),
        "with_realtime_pct": round(len(with_realtime) / total * 100, 1) if total else 0,
        "canceled": len(canceled),
        "static_only": static_only,
        "on_time": len(on_time),
        "on_time_pct": round(len(on_time) / len(delays) * 100, 1) if delays else 0,
        "slightly_late": len(slightly_late),
        "very_late": len(very_late),
        "slightly_early": len(slightly_early),
        "very_early": len(very_early),
        "suspect_gps": len(outliers),
        "avg_late_seconds": round(mean(late_delays), 1) if late_delays else 0,
        "avg_early_seconds": round(mean(early_delays), 1) if early_delays else 0,
        "median_delay_seconds": round(median(delays), 1) if delays else 0,
        "max_late_seconds": max(delays) if delays else 0,
        "max_early_seconds": min(delays) if delays else 0,
    }


def get_route_breakdown(
    db: sqlite3.Connection, stop_id: str, start_date: str, end_date: str,
    route: str | None = None, time_from: int | None = None, time_to: int | None = None,
) -> list[dict]:
    """Per-route statistics."""
    query = """SELECT t.route_short_name, o.departure_delay, o.realtime
           FROM observations o
           JOIN trips t ON o.trip_id = t.id
           JOIN stops s ON o.stop_id = s.id
           WHERE s.gtfs_id = ? AND o.service_date >= ? AND o.service_date <= ?"""
    params: list = [stop_id, start_date, end_date]
    query = _append_filters(query, params, route, time_from, time_to)
    rows = db.execute(query, params).fetchall()

    # Group by route
    from collections import defaultdict

    by_route: dict[str, list] = defaultdict(list)
    for r in rows:
        by_route[r["route_short_name"]].append(r)

    result = []
    for route_name in sorted(by_route.keys()):
        route_rows = by_route[route_name]
        total_count = len(route_rows)
        rt_rows = [r for r in route_rows if r["realtime"]]
        rt_count = len(rt_rows)

        delays = [r["departure_delay"] for r in rt_rows if r["departure_delay"] is not None]
        clean = [d for d in delays if not _is_outlier(d)]
        suspect = len(delays) - len(clean)

        late = [d for d in clean if d > 0]
        early = [d for d in clean if d < 0]
        on_time = sum(1 for d in clean if 0 <= d <= 180)
        on_time_pct = round(on_time / len(clean) * 100, 1) if clean else 0

        result.append(
            {
                "route": route_name,
                "departures": total_count,
                "with_realtime": rt_count,
                "on_time_pct": on_time_pct,
                "avg_late_seconds": round(mean(late), 1) if late else 0,
                "avg_early_seconds": round(mean(early), 1) if early else 0,
                "max_late_seconds": max(clean) if clean else 0,
                "max_early_seconds": min(clean) if clean else 0,
                "suspect_gps": suspect,
            }
        )

    return result


def get_delay_by_hour(
    db: sqlite3.Connection, stop_id: str, start_date: str, end_date: str,
    route: str | None = None, time_from: int | None = None, time_to: int | None = None,
) -> list[dict]:
    """Average delay by hour of day (based on scheduled departure)."""
    query = """SELECT
                 (o.scheduled_departure / 3600) as hour,
                 o.departure_delay, o.realtime
               FROM observations o
               JOIN trips t ON o.trip_id = t.id
               JOIN stops s ON o.stop_id = s.id
               WHERE s.gtfs_id = ? AND o.service_date >= ? AND o.service_date <= ?"""
    params: list = [stop_id, start_date, end_date]
    query = _append_filters(query, params, route, time_from, time_to)

    rows = db.execute(query, params).fetchall()

    from collections import defaultdict

    by_hour: dict[int, list] = defaultdict(list)
    for r in rows:
        by_hour[r["hour"]].append(r)

    result = []
    for hour in sorted(by_hour.keys()):
        hour_rows = by_hour[hour]
        total_count = len(hour_rows)
        rt_rows = [r for r in hour_rows if r["realtime"]]
        delays = [r["departure_delay"] for r in rt_rows if r["departure_delay"] is not None]
        clean = [d for d in delays if not _is_outlier(d)]

        late = [d for d in clean if d > 0]
        early = [d for d in clean if d < 0]

        result.append(
            {
                "hour": hour,
                "departures": total_count,
                "with_realtime": len(rt_rows),
                "avg_late_seconds": round(mean(late), 1) if late else 0,
                "avg_early_seconds": round(mean(early), 1) if early else 0,
            }
        )

    return result


def format_delay(seconds: int | float) -> str:
    """Format delay seconds as human-readable string."""
    if seconds is None:
        return "N/A"
    sign = "+" if seconds >= 0 else "-"
    total = abs(int(seconds))
    minutes = total // 60
    secs = total % 60
    if minutes > 0:
        return f"{sign}{minutes}m {secs:02d}s"
    return f"{sign}{secs}s"
