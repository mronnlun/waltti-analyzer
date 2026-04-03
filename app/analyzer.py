import sqlite3
from statistics import mean, median


def get_summary(db: sqlite3.Connection, stop_id: str, start_date: str,
                end_date: str, route: str | None = None) -> dict:
    """Compute summary statistics for a date range."""
    query = """SELECT * FROM observations
               WHERE stop_gtfs_id = ? AND service_date >= ? AND service_date <= ?"""
    params: list = [stop_id, start_date, end_date]
    if route:
        query += " AND route_short_name = ?"
        params.append(route)

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
    delays = [r["departure_delay"] for r in with_realtime if r["departure_delay"] is not None]

    on_time = [d for d in delays if d <= 180]       # <= 3 min
    slightly_late = [d for d in delays if 180 < d <= 600]  # 3-10 min
    very_late = [d for d in delays if d > 600]       # > 10 min
    early = [d for d in delays if d < 0]

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
        "early": len(early),
        "avg_delay_seconds": round(mean(delays), 1) if delays else 0,
        "median_delay_seconds": round(median(delays), 1) if delays else 0,
        "max_delay_seconds": max(delays) if delays else 0,
        "min_delay_seconds": min(delays) if delays else 0,
    }


def get_route_breakdown(db: sqlite3.Connection, stop_id: str,
                        start_date: str, end_date: str) -> list[dict]:
    """Per-route statistics."""
    rows = db.execute(
        """SELECT route_short_name, COUNT(*) as total,
                  SUM(CASE WHEN realtime = 1 THEN 1 ELSE 0 END) as rt_count,
                  AVG(CASE WHEN realtime = 1 THEN departure_delay END) as avg_delay,
                  MAX(CASE WHEN realtime = 1 THEN departure_delay END) as max_delay
           FROM observations
           WHERE stop_gtfs_id = ? AND service_date >= ? AND service_date <= ?
           GROUP BY route_short_name
           ORDER BY route_short_name""",
        (stop_id, start_date, end_date),
    ).fetchall()

    result = []
    for r in rows:
        rt_count = r["rt_count"] or 0
        # Calculate on-time % from realtime observations
        if rt_count > 0:
            on_time = db.execute(
                """SELECT COUNT(*) as cnt FROM observations
                   WHERE stop_gtfs_id = ? AND service_date >= ? AND service_date <= ?
                     AND route_short_name = ? AND realtime = 1
                     AND departure_delay <= 180""",
                (stop_id, start_date, end_date, r["route_short_name"]),
            ).fetchone()["cnt"]
            on_time_pct = round(on_time / rt_count * 100, 1)
        else:
            on_time_pct = 0

        result.append({
            "route": r["route_short_name"],
            "departures": r["total"],
            "with_realtime": rt_count,
            "on_time_pct": on_time_pct,
            "avg_delay_seconds": round(r["avg_delay"] or 0, 1),
            "max_delay_seconds": r["max_delay"] or 0,
        })

    return result


def get_delay_by_hour(db: sqlite3.Connection, stop_id: str, start_date: str,
                      end_date: str, route: str | None = None) -> list[dict]:
    """Average delay by hour of day (based on scheduled departure)."""
    query = """SELECT
                 (scheduled_departure / 3600) as hour,
                 COUNT(*) as total,
                 SUM(CASE WHEN realtime = 1 THEN 1 ELSE 0 END) as rt_count,
                 AVG(CASE WHEN realtime = 1 THEN departure_delay END) as avg_delay
               FROM observations
               WHERE stop_gtfs_id = ? AND service_date >= ? AND service_date <= ?"""
    params: list = [stop_id, start_date, end_date]
    if route:
        query += " AND route_short_name = ?"
        params.append(route)
    query += " GROUP BY hour ORDER BY hour"

    rows = db.execute(query, params).fetchall()

    return [
        {
            "hour": r["hour"],
            "departures": r["total"],
            "with_realtime": r["rt_count"] or 0,
            "avg_delay_seconds": round(r["avg_delay"] or 0, 1),
        }
        for r in rows
    ]


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
