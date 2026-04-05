# Waltti Analyzer — AI Instructions

Read [docs/AI_DOCUMENTATION_CONTEXT.md](docs/AI_DOCUMENTATION_CONTEXT.md) before substantial work.
When project direction changes, update existing documentation in the same change instead of creating parallel docs that drift.

## Project Overview

A Flask web application that collects and analyzes the punctuality of buses at stops in Vaasa, Finland. It polls the Digitransit Waltti GraphQL API, stores timetable and realtime delay data in SQLite, and displays timeliness reports in a web dashboard.

The Digitransit API does **not** retain historical delay data. Realtime delay values are only available while buses are actively running. Once a service day passes, the API reverts to static schedule data. This means the application **must** actively poll during service hours to capture delay data before it disappears.

The current direction is feed-wide coverage for Vaasa. A default stop may still be used in the UI, but collection and reporting should be designed around all discovered stops and routes.

## Tech Stack

- **Python 3.11+** with **Flask** web framework
- **SQLite** database locally (stdlib `sqlite3`, WAL mode); **Azure SQL** (via pyodbc/ODBC Driver 18) in production on Azure App Service
- **APScheduler** for background polling (in-process, no external broker)
- **Jinja2** server-rendered templates with **Chart.js** (CDN) for charts
- **Gunicorn** + **Docker** for deployment
- **zoneinfo** (stdlib) for timezone handling — Europe/Helsinki
- No ORM — plain SQL via `sqlite3` (locally) or `pyodbc` (Azure)
- No frontend build step — all JS via CDN

## Architecture

```
app/
├── __init__.py          # Flask app factory (create_app)
├── config.py            # Configuration from env vars
├── db.py                # SQLite schema + data access layer
├── digitransit.py       # Digitransit GraphQL API client
├── collector.py         # Data collection (daily schedule + realtime polling)
├── analyzer.py          # Statistics and reporting
├── scheduler.py         # APScheduler background job setup
├── routes/
│   ├── dashboard.py     # Server-rendered HTML pages
│   └── api.py           # JSON API endpoints
├── templates/           # Jinja2 templates
└── static/              # CSS
```

## Coding Conventions

- Type hints on function signatures
- Use `zoneinfo.ZoneInfo("Europe/Helsinki")` for all timezone work
- SQLite connections via `flask.g` in request context; direct `sqlite3.connect()` in scheduler
- All times stored in DB as UTC unix timestamps or seconds-since-midnight (as from API)
- All display output uses Europe/Helsinki timezone
- The current code uses upserts keyed on `(stop_gtfs_id, trip_gtfs_id, service_date)`; preserve the meaning of the best known observation when changing collector behavior
- **Always work on a feature branch — never commit directly to `main`.**
- **Always open a pull request** for your branch when the work is ready.
- Run `ruff check` and `ruff format` before committing; a `PreToolUse` hook enforces this automatically before every `git push`.
- Tests use `pytest` with `TestConfig` (in-memory SQLite)
## Key API Details

- **Endpoint**: `POST https://api.digitransit.fi/routing/v2/waltti/gtfs/v1`
- **Auth header**: `digitransit-subscription-key: <key>`
- **Content-Type**: `application/json`
- **Body**: `{"query": "<graphql>"}`
- **Stop ID format**: `Vaasa:309392` (no `GTFS:` prefix — that prefix causes silent null returns)
- **Realtime fields**: `realtime` (bool), `departureDelay` (seconds, positive=late), `realtimeState` (SCHEDULED/UPDATED/CANCELED/ADDED/MODIFIED)
- **No-service days**: All patterns return empty `stoptimes` arrays (not an error)

## Environment Variables

| Variable | Default | Required |
|---|---|---|
| `DIGITRANSIT_API_KEY` | — | Yes |
| `FEED_ID` | `Vaasa` | No |
| `DEFAULT_STOP_ID` | `Vaasa:309392` | No |
| `DATABASE_PATH` | `data/waltti.db` | No (local only) |
| `POLL_INTERVAL_SECONDS` | `300` | No |
| `POLL_START_HOUR` | `5` | No |
| `POLL_END_HOUR` | `24` | No |

**Database per environment:**
- **Local development**: SQLite at `DATABASE_PATH` (default `data/waltti.db`). No extra setup required.
- **Azure (production)**: Azure SQL Server. The connection string is set via the `DATABASE` Azure App Service connection string (injected by Bicep). Do not set `DATABASE_PATH` in production.

Some older deployment-facing files still refer to `TARGET_STOP_ID`; prefer `DEFAULT_STOP_ID` in Python app code and update stale references when you touch them.

## Running Locally

```bash
python -m venv .venv
source .venv/bin/activate    # or .venv\Scripts\activate on Windows
pip install -r requirements.txt
pip install -e ".[dev]"
cp .env.example .env         # add your API key
flask run --debug
```

## Running Tests

```bash
pytest
ruff check .
```

## Database Schema

Four tables: `stops`, `trips`, `observations`, `collection_log`. See `app/db.py` for full DDL.
The `observations` table has a `UNIQUE(stop_gtfs_id, trip_gtfs_id, service_date)` constraint for upserts.

## Important Edge Cases

- Holiday/no-service detection: all patterns have empty stoptimes → log as no-service, skip
- API returns null for stop → stop ID may be wrong (check GTFS: prefix stripping)
- `realtime=false` means static schedule only — exclude from delay statistics
- Seconds-since-midnight can exceed 86400 for trips past midnight
- Helsinki timezone: UTC+2 in winter (EET), UTC+3 in summer (EEST)
