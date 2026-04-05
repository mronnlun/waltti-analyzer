import logging
import sqlite3
import time

from flask import current_app, g

logger = logging.getLogger(__name__)

SCHEMA_SQL = """
CREATE TABLE IF NOT EXISTS stops (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    gtfs_id     TEXT UNIQUE NOT NULL,
    name        TEXT NOT NULL,
    code        TEXT,
    lat         REAL,
    lon         REAL,
    updated_at  INTEGER NOT NULL
);

CREATE TABLE IF NOT EXISTS trips (
    id               INTEGER PRIMARY KEY AUTOINCREMENT,
    gtfs_id          TEXT UNIQUE NOT NULL,
    route_short_name TEXT,
    route_long_name  TEXT,
    mode             TEXT,
    headsign         TEXT,
    direction_id     INTEGER,
    updated_at       INTEGER NOT NULL
);

CREATE TABLE IF NOT EXISTS realtime_states (
    id   INTEGER PRIMARY KEY,
    name TEXT UNIQUE NOT NULL
);

INSERT OR IGNORE INTO realtime_states (id, name) VALUES (0, 'SCHEDULED');
INSERT OR IGNORE INTO realtime_states (id, name) VALUES (1, 'UPDATED');
INSERT OR IGNORE INTO realtime_states (id, name) VALUES (2, 'CANCELED');

CREATE TABLE IF NOT EXISTS observations (
    id                   INTEGER PRIMARY KEY AUTOINCREMENT,
    stop_id              INTEGER NOT NULL REFERENCES stops(id),
    trip_id              INTEGER NOT NULL REFERENCES trips(id),
    service_date         TEXT NOT NULL,
    scheduled_arrival    INTEGER,
    scheduled_departure  INTEGER NOT NULL,
    realtime_arrival     INTEGER,
    realtime_departure   INTEGER,
    arrival_delay        INTEGER,
    departure_delay      INTEGER,
    realtime             INTEGER NOT NULL DEFAULT 0,
    realtime_state_id    INTEGER REFERENCES realtime_states(id),
    queried_at           INTEGER NOT NULL,
    UNIQUE(stop_id, trip_id, service_date)
);

CREATE INDEX IF NOT EXISTS idx_obs_stop_date ON observations(stop_id, service_date);

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

# ---------------------------------------------------------------------------
# Reusable SQL fragments for observation queries.
# These return backward-compatible column names (stop_gtfs_id, trip_gtfs_id,
# realtime_state) so callers don't need to change.
# ---------------------------------------------------------------------------

_OBS_COLUMNS = """\
    o.id, s.gtfs_id AS stop_gtfs_id, t.gtfs_id AS trip_gtfs_id,
    o.service_date, o.scheduled_arrival, o.scheduled_departure,
    o.realtime_arrival, o.realtime_departure,
    o.arrival_delay, o.departure_delay, o.realtime,
    rs.name AS realtime_state, o.queried_at,
    t.route_short_name, t.route_long_name, t.mode, t.headsign, t.direction_id"""

_OBS_JOINS = """\
    FROM observations o
    JOIN stops s ON o.stop_id = s.id
    JOIN trips t ON o.trip_id = t.id
    LEFT JOIN realtime_states rs ON o.realtime_state_id = rs.id"""

_OBS_UPSERT = """\
    INSERT INTO observations (
        stop_id, trip_id, service_date,
        scheduled_arrival, scheduled_departure, realtime_arrival, realtime_departure,
        arrival_delay, departure_delay, realtime, realtime_state_id, queried_at
    ) VALUES (
        (SELECT id FROM stops WHERE gtfs_id = :stop_gtfs_id),
        (SELECT id FROM trips WHERE gtfs_id = :trip_gtfs_id),
        :service_date,
        :scheduled_arrival, :scheduled_departure, :realtime_arrival, :realtime_departure,
        :arrival_delay, :departure_delay, :realtime,
        (SELECT id FROM realtime_states WHERE name = :realtime_state),
        :queried_at
    )
    ON CONFLICT(stop_id, trip_id, service_date) DO UPDATE SET
        scheduled_arrival = excluded.scheduled_arrival,
        scheduled_departure = excluded.scheduled_departure,
        realtime_arrival = excluded.realtime_arrival,
        realtime_departure = excluded.realtime_departure,
        arrival_delay = excluded.arrival_delay,
        departure_delay = excluded.departure_delay,
        realtime = excluded.realtime,
        realtime_state_id = excluded.realtime_state_id,
        queried_at = excluded.queried_at"""


# ---------------------------------------------------------------------------
# Connection helpers
# ---------------------------------------------------------------------------


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
        if _needs_migration(db):
            _migrate_schema(db)
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


# ---------------------------------------------------------------------------
# Schema migration (old text-FK schema → normalized integer-FK schema)
# ---------------------------------------------------------------------------


def _needs_migration(conn: sqlite3.Connection) -> bool:
    """Return True if the database has the old text-PK/FK schema."""
    tables = [
        r[0] for r in conn.execute("SELECT name FROM sqlite_master WHERE type='table'").fetchall()
    ]
    if "stops" not in tables:
        return False  # Fresh database
    cols = [r[1] for r in conn.execute("PRAGMA table_info(stops)").fetchall()]
    return "id" not in cols


def _migrate_schema(conn: sqlite3.Connection):
    """Migrate from text-PK schema to normalized integer-PK schema."""
    logger.info("Migrating database to normalized schema...")
    conn.execute("PRAGMA foreign_keys=OFF")
    conn.executescript("""
        -- Realtime states lookup
        CREATE TABLE IF NOT EXISTS realtime_states (
            id   INTEGER PRIMARY KEY,
            name TEXT UNIQUE NOT NULL
        );
        INSERT OR IGNORE INTO realtime_states (id, name) VALUES (0, 'SCHEDULED');
        INSERT OR IGNORE INTO realtime_states (id, name) VALUES (1, 'UPDATED');
        INSERT OR IGNORE INTO realtime_states (id, name) VALUES (2, 'CANCELED');

        -- Stops: text PK → integer auto-increment PK
        CREATE TABLE stops_new (
            id          INTEGER PRIMARY KEY AUTOINCREMENT,
            gtfs_id     TEXT UNIQUE NOT NULL,
            name        TEXT NOT NULL,
            code        TEXT,
            lat         REAL,
            lon         REAL,
            updated_at  INTEGER NOT NULL
        );
        INSERT INTO stops_new (gtfs_id, name, code, lat, lon, updated_at)
            SELECT gtfs_id, name, code, lat, lon, updated_at FROM stops;
        DROP TABLE stops;
        ALTER TABLE stops_new RENAME TO stops;

        -- Trips: text PK → integer auto-increment PK
        CREATE TABLE trips_new (
            id               INTEGER PRIMARY KEY AUTOINCREMENT,
            gtfs_id          TEXT UNIQUE NOT NULL,
            route_short_name TEXT,
            route_long_name  TEXT,
            mode             TEXT,
            headsign         TEXT,
            direction_id     INTEGER,
            updated_at       INTEGER NOT NULL
        );
        INSERT INTO trips_new (gtfs_id, route_short_name, route_long_name, mode,
                               headsign, direction_id, updated_at)
            SELECT gtfs_id, route_short_name, route_long_name, mode,
                   headsign, direction_id, updated_at FROM trips;
        DROP TABLE trips;
        ALTER TABLE trips_new RENAME TO trips;

        -- Observations: text FKs → integer FKs
        CREATE TABLE observations_new (
            id                   INTEGER PRIMARY KEY AUTOINCREMENT,
            stop_id              INTEGER NOT NULL REFERENCES stops(id),
            trip_id              INTEGER NOT NULL REFERENCES trips(id),
            service_date         TEXT NOT NULL,
            scheduled_arrival    INTEGER,
            scheduled_departure  INTEGER NOT NULL,
            realtime_arrival     INTEGER,
            realtime_departure   INTEGER,
            arrival_delay        INTEGER,
            departure_delay      INTEGER,
            realtime             INTEGER NOT NULL DEFAULT 0,
            realtime_state_id    INTEGER REFERENCES realtime_states(id),
            queried_at           INTEGER NOT NULL,
            UNIQUE(stop_id, trip_id, service_date)
        );
        INSERT INTO observations_new (
            stop_id, trip_id, service_date,
            scheduled_arrival, scheduled_departure,
            realtime_arrival, realtime_departure,
            arrival_delay, departure_delay, realtime, realtime_state_id, queried_at
        )
        SELECT
            s.id, t.id, o.service_date,
            o.scheduled_arrival, o.scheduled_departure,
            o.realtime_arrival, o.realtime_departure,
            o.arrival_delay, o.departure_delay, o.realtime,
            rs.id, o.queried_at
        FROM observations o
        JOIN stops s ON o.stop_gtfs_id = s.gtfs_id
        JOIN trips t ON o.trip_gtfs_id = t.gtfs_id
        LEFT JOIN realtime_states rs ON o.realtime_state = rs.name;
        DROP TABLE observations;
        ALTER TABLE observations_new RENAME TO observations;

        CREATE INDEX IF NOT EXISTS idx_obs_stop_date
            ON observations(stop_id, service_date);
    """)
    conn.execute("PRAGMA foreign_keys=ON")
    logger.info("Migration complete.")


# ---------------------------------------------------------------------------
# Stop operations
# ---------------------------------------------------------------------------


def upsert_stop(
    db: sqlite3.Connection,
    gtfs_id: str,
    name: str,
    code: str | None,
    lat: float | None,
    lon: float | None,
):
    db.execute(
        """INSERT INTO stops (gtfs_id, name, code, lat, lon, updated_at)
        VALUES (?, ?, ?, ?, ?, ?)
        ON CONFLICT(gtfs_id) DO UPDATE SET
            name = excluded.name,
            code = excluded.code,
            lat = excluded.lat,
            lon = excluded.lon,
            updated_at = excluded.updated_at""",
        (gtfs_id, name, code, lat, lon, int(time.time())),
    )
    db.commit()


def upsert_stops_batch(db: sqlite3.Connection, stops: list[dict]):
    db.executemany(
        """INSERT INTO stops (gtfs_id, name, code, lat, lon, updated_at)
        VALUES (:gtfs_id, :name, :code, :lat, :lon, :updated_at)
        ON CONFLICT(gtfs_id) DO UPDATE SET
            name = excluded.name,
            code = excluded.code,
            lat = excluded.lat,
            lon = excluded.lon,
            updated_at = excluded.updated_at""",
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
           FROM observations o
           JOIN trips t ON o.trip_id = t.id
           JOIN stops s ON o.stop_id = s.id
           WHERE s.gtfs_id = ? AND t.route_short_name IS NOT NULL
           ORDER BY t.route_short_name""",
        (stop_id,),
    ).fetchall()
    return [r["route_short_name"] for r in rows]


def get_all_routes(db: sqlite3.Connection, feed_id: str | None = None) -> list[str]:
    query = """SELECT DISTINCT t.route_short_name
               FROM observations o
               JOIN trips t ON o.trip_id = t.id
               WHERE t.route_short_name IS NOT NULL"""
    params: list = []
    if feed_id:
        query += " AND o.stop_id IN (SELECT id FROM stops WHERE gtfs_id LIKE ?)"
        params.append(f"{feed_id}:%")
    query += " ORDER BY t.route_short_name"
    return [r["route_short_name"] for r in db.execute(query, params).fetchall()]


# ---------------------------------------------------------------------------
# Trip operations
# ---------------------------------------------------------------------------


def upsert_trip(db: sqlite3.Connection, **kwargs):
    db.execute(
        """INSERT INTO trips
        (gtfs_id, route_short_name, route_long_name, mode, headsign, direction_id, updated_at)
        VALUES
        (:gtfs_id, :route_short_name, :route_long_name,
         :mode, :headsign, :direction_id, :updated_at)
        ON CONFLICT(gtfs_id) DO UPDATE SET
            route_short_name = excluded.route_short_name,
            route_long_name = excluded.route_long_name,
            mode = excluded.mode,
            headsign = excluded.headsign,
            direction_id = excluded.direction_id,
            updated_at = excluded.updated_at""",
        {**kwargs, "updated_at": int(time.time())},
    )
    db.commit()


def upsert_trips_batch(db: sqlite3.Connection, trips: list[dict]):
    db.executemany(
        """INSERT INTO trips
        (gtfs_id, route_short_name, route_long_name, mode, headsign, direction_id, updated_at)
        VALUES
        (:gtfs_id, :route_short_name, :route_long_name,
         :mode, :headsign, :direction_id, :updated_at)
        ON CONFLICT(gtfs_id) DO UPDATE SET
            route_short_name = excluded.route_short_name,
            route_long_name = excluded.route_long_name,
            mode = excluded.mode,
            headsign = excluded.headsign,
            direction_id = excluded.direction_id,
            updated_at = excluded.updated_at""",
        [{**t, "updated_at": int(time.time())} for t in trips],
    )
    db.commit()


# ---------------------------------------------------------------------------
# Observation operations
# ---------------------------------------------------------------------------


def upsert_observation(db: sqlite3.Connection, **kwargs):
    db.execute(_OBS_UPSERT, kwargs)
    db.commit()


def upsert_observations_batch(db: sqlite3.Connection, observations: list[dict]):
    db.executemany(_OBS_UPSERT, observations)
    db.commit()


def get_observations(
    db: sqlite3.Connection,
    stop_id: str,
    start_date: str,
    end_date: str,
    route: str | None = None,
    time_from: int | None = None,
    time_to: int | None = None,
) -> list[sqlite3.Row]:
    query = f"""SELECT {_OBS_COLUMNS}
               {_OBS_JOINS}
               WHERE s.gtfs_id = ? AND o.service_date >= ? AND o.service_date <= ?"""
    params: list = [stop_id, start_date, end_date]
    if route:
        query += " AND t.route_short_name = ?"
        params.append(route)
    if time_from is not None:
        query += " AND o.scheduled_departure >= ?"
        params.append(time_from)
    if time_to is not None:
        query += " AND o.scheduled_departure <= ?"
        params.append(time_to)
    query += " ORDER BY o.service_date DESC, o.scheduled_departure DESC"
    return db.execute(query, params).fetchall()


def get_recent_observations(
    db: sqlite3.Connection,
    stop_id: str,
    limit: int = 20,
    now_seconds: int | None = None,
    route: str | None = None,
) -> list[sqlite3.Row]:
    """Return recent past observations. Excludes future departures for today."""
    from datetime import datetime
    from zoneinfo import ZoneInfo

    helsinki = ZoneInfo("Europe/Helsinki")
    now = datetime.now(helsinki)
    today = now.strftime("%Y-%m-%d")
    if now_seconds is None:
        now_seconds = now.hour * 3600 + now.minute * 60 + now.second

    query = f"""SELECT {_OBS_COLUMNS}
           {_OBS_JOINS}
           WHERE s.gtfs_id = ?
             AND (o.service_date < ? OR o.scheduled_departure <= ?)"""
    params: list = [stop_id, today, now_seconds]
    if route:
        query += " AND t.route_short_name = ?"
        params.append(route)
    query += " ORDER BY o.service_date DESC, o.scheduled_departure DESC LIMIT ?"
    params.append(limit)
    return db.execute(query, params).fetchall()


def get_latest_observations(
    db: sqlite3.Connection,
    limit: int = 100,
    feed_id: str | None = None,
) -> list[sqlite3.Row]:
    """Return the most recently queried realtime observations (GPS only)."""
    query = f"""SELECT {_OBS_COLUMNS}, s.name AS stop_name
           {_OBS_JOINS}
           WHERE o.realtime = 1"""
    params: list = []
    if feed_id:
        query += " AND s.gtfs_id LIKE ?"
        params.append(f"{feed_id}:%")
    query += " ORDER BY o.queried_at DESC, o.service_date DESC, o.scheduled_departure DESC LIMIT ?"
    params.append(limit)
    return db.execute(query, params).fetchall()


# ---------------------------------------------------------------------------
# Collection log operations
# ---------------------------------------------------------------------------


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
