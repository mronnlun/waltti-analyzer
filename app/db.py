import sqlite3
import time

from flask import current_app, g

SCHEMA_SQL = """
CREATE TABLE IF NOT EXISTS stops (
    gtfs_id     TEXT PRIMARY KEY,
    name        TEXT NOT NULL,
    code        TEXT,
    lat         REAL,
    lon         REAL,
    updated_at  INTEGER NOT NULL
);

CREATE TABLE IF NOT EXISTS trips (
    gtfs_id          TEXT PRIMARY KEY,
    route_short_name TEXT,
    route_long_name  TEXT,
    mode             TEXT,
    headsign         TEXT,
    direction_id     INTEGER,
    updated_at       INTEGER NOT NULL
);

CREATE TABLE IF NOT EXISTS observations (
    id                   INTEGER PRIMARY KEY AUTOINCREMENT,
    stop_gtfs_id         TEXT NOT NULL,
    trip_gtfs_id         TEXT NOT NULL REFERENCES trips(gtfs_id),
    service_date         TEXT NOT NULL,
    scheduled_arrival    INTEGER,
    scheduled_departure  INTEGER NOT NULL,
    realtime_arrival     INTEGER,
    realtime_departure   INTEGER,
    arrival_delay        INTEGER,
    departure_delay      INTEGER,
    realtime             INTEGER NOT NULL DEFAULT 0,
    realtime_state       TEXT,
    queried_at           INTEGER NOT NULL,
    UNIQUE(stop_gtfs_id, trip_gtfs_id, service_date)
);

CREATE TABLE IF NOT EXISTS collection_log (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    queried_at      INTEGER NOT NULL,
    stop_gtfs_id    TEXT NOT NULL,
    query_type      TEXT NOT NULL,
    service_date    TEXT,
    departures_found INTEGER,
    no_service      INTEGER DEFAULT 0,
    error           TEXT
);
"""


def get_db() -> sqlite3.Connection:
    if "db" not in g:
        g.db = _connect(current_app.config["DATABASE_PATH"])
    return g.db


def close_db(exc=None):
    db = g.pop("db", None)
    if db is not None:
        db.close()


def init_db(app):
    with app.app_context():
        db = _connect(app.config["DATABASE_PATH"])
        db.executescript(SCHEMA_SQL)
        db.close()


def _connect(db_path: str) -> sqlite3.Connection:
    if db_path == ":memory:" or db_path.startswith("file:"):
        conn = sqlite3.connect(db_path, uri=db_path.startswith("file:"))
    else:
        conn = sqlite3.connect(db_path)
    conn.row_factory = sqlite3.Row
    if not db_path.startswith("file:") and db_path != ":memory:":
        conn.execute("PRAGMA journal_mode=WAL")
    conn.execute("PRAGMA foreign_keys=ON")
    return conn


def connect_direct(db_path: str) -> sqlite3.Connection:
    """Direct connection for use outside Flask request context (e.g. scheduler)."""
    return _connect(db_path)


# --- Stop operations ---


def upsert_stop(
    db: sqlite3.Connection,
    gtfs_id: str,
    name: str,
    code: str | None,
    lat: float | None,
    lon: float | None,
):
    db.execute(
        "INSERT OR REPLACE INTO stops (gtfs_id, name, code, lat, lon, updated_at) "
        "VALUES (?, ?, ?, ?, ?, ?)",
        (gtfs_id, name, code, lat, lon, int(time.time())),
    )
    db.commit()


def upsert_stops_batch(db: sqlite3.Connection, stops: list[dict]):
    db.executemany(
        "INSERT OR REPLACE INTO stops (gtfs_id, name, code, lat, lon, updated_at) "
        "VALUES (:gtfs_id, :name, :code, :lat, :lon, :updated_at)",
        [{**s, "updated_at": int(time.time())} for s in stops],
    )
    db.commit()


def get_stop(db: sqlite3.Connection, gtfs_id: str) -> sqlite3.Row | None:
    return db.execute("SELECT * FROM stops WHERE gtfs_id = ?", (gtfs_id,)).fetchone()


def get_all_stops(db: sqlite3.Connection, feed_id: str | None = None) -> list[sqlite3.Row]:
    if feed_id:
        return db.execute(
            "SELECT * FROM stops WHERE gtfs_id LIKE ? ORDER BY name",
            (f"{feed_id}:%",),
        ).fetchall()
    return db.execute("SELECT * FROM stops ORDER BY name").fetchall()


def get_all_stop_ids(db: sqlite3.Connection, feed_id: str | None = None) -> list[str]:
    rows = get_all_stops(db, feed_id)
    return [r["gtfs_id"] for r in rows]


def get_routes_for_stop(db: sqlite3.Connection, stop_id: str) -> list[str]:
    rows = db.execute(
        """SELECT DISTINCT t.route_short_name
           FROM observations o JOIN trips t ON o.trip_gtfs_id = t.gtfs_id
           WHERE o.stop_gtfs_id = ? AND t.route_short_name IS NOT NULL
           ORDER BY t.route_short_name""",
        (stop_id,),
    ).fetchall()
    return [r["route_short_name"] for r in rows]


def get_all_routes(db: sqlite3.Connection, feed_id: str | None = None) -> list[str]:
    query = """SELECT DISTINCT t.route_short_name
               FROM observations o JOIN trips t ON o.trip_gtfs_id = t.gtfs_id
               WHERE t.route_short_name IS NOT NULL"""
    params: list = []
    if feed_id:
        query += " AND o.stop_gtfs_id LIKE ?"
        params.append(f"{feed_id}:%")
    query += " ORDER BY t.route_short_name"
    return [r["route_short_name"] for r in db.execute(query, params).fetchall()]


# --- Trip operations ---


def upsert_trip(db: sqlite3.Connection, **kwargs):
    db.execute(
        """INSERT OR REPLACE INTO trips
        (gtfs_id, route_short_name, route_long_name, mode, headsign, direction_id, updated_at)
        VALUES
        (:gtfs_id, :route_short_name, :route_long_name, :mode, :headsign, :direction_id, :updated_at)""",
        {**kwargs, "updated_at": int(time.time())},
    )
    db.commit()


def upsert_trips_batch(db: sqlite3.Connection, trips: list[dict]):
    db.executemany(
        """INSERT OR REPLACE INTO trips
        (gtfs_id, route_short_name, route_long_name, mode, headsign, direction_id, updated_at)
        VALUES
        (:gtfs_id, :route_short_name, :route_long_name, :mode, :headsign, :direction_id, :updated_at)""",
        [{**t, "updated_at": int(time.time())} for t in trips],
    )
    db.commit()


# --- Observation operations ---


def upsert_observation(db: sqlite3.Connection, **kwargs):
    db.execute(
        """INSERT OR REPLACE INTO observations
        (stop_gtfs_id, trip_gtfs_id, service_date,
         scheduled_arrival, scheduled_departure, realtime_arrival, realtime_departure,
         arrival_delay, departure_delay, realtime, realtime_state, queried_at)
        VALUES
        (:stop_gtfs_id, :trip_gtfs_id, :service_date,
         :scheduled_arrival, :scheduled_departure, :realtime_arrival, :realtime_departure,
         :arrival_delay, :departure_delay, :realtime, :realtime_state, :queried_at)""",
        kwargs,
    )
    db.commit()


def upsert_observations_batch(db: sqlite3.Connection, observations: list[dict]):
    db.executemany(
        """INSERT OR REPLACE INTO observations
        (stop_gtfs_id, trip_gtfs_id, service_date,
         scheduled_arrival, scheduled_departure, realtime_arrival, realtime_departure,
         arrival_delay, departure_delay, realtime, realtime_state, queried_at)
        VALUES
        (:stop_gtfs_id, :trip_gtfs_id, :service_date,
         :scheduled_arrival, :scheduled_departure, :realtime_arrival, :realtime_departure,
         :arrival_delay, :departure_delay, :realtime, :realtime_state, :queried_at)""",
        observations,
    )
    db.commit()


def get_observations(
    db: sqlite3.Connection, stop_id: str, start_date: str, end_date: str, route: str | None = None
) -> list[sqlite3.Row]:
    query = """SELECT o.*, t.route_short_name, t.route_long_name, t.mode, t.headsign, t.direction_id
               FROM observations o JOIN trips t ON o.trip_gtfs_id = t.gtfs_id
               WHERE o.stop_gtfs_id = ? AND o.service_date >= ? AND o.service_date <= ?"""
    params: list = [stop_id, start_date, end_date]
    if route:
        query += " AND t.route_short_name = ?"
        params.append(route)
    query += " ORDER BY o.service_date, o.scheduled_departure"
    return db.execute(query, params).fetchall()


def get_recent_observations(
    db: sqlite3.Connection, stop_id: str, limit: int = 20
) -> list[sqlite3.Row]:
    return db.execute(
        """SELECT o.*, t.route_short_name, t.route_long_name, t.mode, t.headsign, t.direction_id
           FROM observations o JOIN trips t ON o.trip_gtfs_id = t.gtfs_id
           WHERE o.stop_gtfs_id = ?
           ORDER BY o.service_date DESC, o.scheduled_departure DESC
           LIMIT ?""",
        (stop_id, limit),
    ).fetchall()


# --- Collection log operations ---


def log_collection(
    db: sqlite3.Connection,
    stop_gtfs_id: str,
    query_type: str,
    service_date: str | None = None,
    departures_found: int = 0,
    no_service: int = 0,
    error: str | None = None,
):
    db.execute(
        """INSERT INTO collection_log
        (queried_at, stop_gtfs_id, query_type, service_date, departures_found, no_service, error)
        VALUES (?, ?, ?, ?, ?, ?, ?)""",
        (
            int(time.time()),
            stop_gtfs_id,
            query_type,
            service_date,
            departures_found,
            no_service,
            error,
        ),
    )
    db.commit()


def get_latest_collection(
    db: sqlite3.Connection, stop_id: str, query_type: str
) -> sqlite3.Row | None:
    return db.execute(
        """SELECT * FROM collection_log
           WHERE stop_gtfs_id = ? AND query_type = ?
           ORDER BY queried_at DESC LIMIT 1""",
        (stop_id, query_type),
    ).fetchone()


def get_collection_log(db: sqlite3.Connection, stop_id: str, limit: int = 50) -> list[sqlite3.Row]:
    return db.execute(
        "SELECT * FROM collection_log WHERE stop_gtfs_id = ? ORDER BY queried_at DESC LIMIT ?",
        (stop_id, limit),
    ).fetchall()
