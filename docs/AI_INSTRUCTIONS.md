# Waltti Analyzer — AI Instructions

Read [docs/AI_DOCUMENTATION_CONTEXT.md](docs/AI_DOCUMENTATION_CONTEXT.md) before substantial work.
When project direction changes, update existing documentation in the same change instead of creating parallel docs that drift.

## Project Overview

A web application that collects and analyzes the punctuality of buses at stops in Vaasa, Finland. It polls the Digitransit Waltti GraphQL API, stores timetable and realtime delay data in SQLite (locally) or Azure SQL (production), and displays timeliness reports in a single-page web dashboard.

The Digitransit API does **not** retain historical delay data. Realtime delay values are only available while buses are actively running. Once a service day passes, the API reverts to static schedule data. This means the application **must** actively poll during service hours to capture delay data before it disappears.

The current direction is feed-wide coverage for Vaasa. A default stop may still be used in the UI, but collection and reporting should be designed around all discovered stops and routes.

## Tech Stack

- **Python 3.11+** with **Azure Functions** (consumption plan, v2 programming model)
- **SQLite** database locally (stdlib `sqlite3`, WAL mode); **Azure SQL** (via pyodbc/ODBC Driver 18) in production
- Timer-triggered function for background data synchronization (replaces APScheduler)
- HTTP-triggered functions as REST API backend
- **SPA frontend** with vanilla JavaScript, **Chart.js** (CDN) for charts, **Tom-Select** (CDN) for dropdowns
- **zoneinfo** (stdlib) for timezone handling — Europe/Helsinki
- No ORM — plain SQL via `sqlite3` (locally) or `pyodbc` (Azure)
- No frontend build step — all JS via CDN

## Architecture

```
api/
├── function_app.py      # Azure Functions entry point (timer + HTTP triggers)
├── host.json            # Azure Functions host configuration
├── requirements.txt     # Python dependencies for the Function App
└── shared/
    ├── __init__.py
    ├── config.py        # Configuration from environment variables
    ├── db.py            # SQLite schema + data access layer
    ├── digitransit.py   # Digitransit GraphQL API client
    ├── collector.py     # Data collection (discovery, daily schedule, realtime)
    └── analyzer.py      # Statistics and reporting

frontend/
├── index.html           # SPA entry point
├── style.css            # Stylesheet
└── app.js               # Client-side JavaScript (routing, API calls, rendering)

infra/
├── main.bicep           # Azure infrastructure (Function App, Static Web App, SQL)
└── parameters.json      # Deployment parameters
```

## Coding Conventions

- Type hints on function signatures
- Use `zoneinfo.ZoneInfo("Europe/Helsinki")` for all timezone work
- Database connections are created via `shared.db.connect(db_path)` and closed when done
- All times stored in DB as UTC unix timestamps or seconds-since-midnight (as from API)
- All display output uses Europe/Helsinki timezone
- The current code uses upserts keyed on `(stop_gtfs_id, trip_gtfs_id, service_date)`; preserve the meaning of the best known observation when changing collector behavior
- **Always work on a feature branch — never commit directly to `main`.**
- **Always open a pull request** for your branch when the work is ready.
- Run `ruff check` and `ruff format` before committing.
- Tests use `pytest` with in-memory SQLite.

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

**Database per environment:**
- **Local development**: SQLite at `DATABASE_PATH` (default `data/waltti.db`). No extra setup required.
- **Azure (production)**: Azure SQL Server. The connection string is set via the `DATABASE` Azure connection string (injected by Bicep). Do not set `DATABASE_PATH` in production.

## Running Locally

```bash
python -m venv .venv
source .venv/bin/activate
pip install -r api/requirements.txt
pip install -e ".[dev]"
cp api/local.settings.json.example api/local.settings.json
# Edit api/local.settings.json — add your DIGITRANSIT_API_KEY
cd api && func start
```

## Running Tests

```bash
pytest
ruff check .
```

## Database Schema

Five tables: `stops`, `trips`, `realtime_states`, `observations`, `collection_log`. See `api/shared/db.py` for full DDL.
The `observations` table has a `UNIQUE(stop_id, trip_id, service_date)` constraint for upserts.

## Important Edge Cases

- Holiday/no-service detection: all patterns have empty stoptimes → log as no-service, skip
- API returns null for stop → stop ID may be wrong (check GTFS: prefix stripping)
- `realtime=false` means static schedule only — exclude from delay statistics
- Seconds-since-midnight can exceed 86400 for trips past midnight
- Helsinki timezone: UTC+2 in winter (EET), UTC+3 in summer (EEST)
