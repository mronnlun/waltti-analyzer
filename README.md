# Waltti Analyzer

A web application that collects and analyzes the punctuality of buses at stops in Vaasa, Finland. It polls the [Digitransit Waltti GraphQL API](https://digitransit.fi/en/developers/) during service hours, stores timetable and realtime delay data in SQLite, and displays timeliness reports in a dashboard.

## Why a scheduler?

The Digitransit API is a live-data API — realtime delay values only exist while buses are actively running. Once a service day passes, everything reverts to static schedule data. The app must actively poll during service hours to capture actual delay values before they disappear.

## Setup

```bash
# Clone and create virtual environment
git clone https://github.com/mronnlun/waltti-analyzer.git
cd waltti-analyzer
python -m venv .venv
source .venv/bin/activate    # Linux/Mac
# .venv\Scripts\activate     # Windows

# Install dependencies
pip install -r requirements.txt
pip install -e ".[dev]"      # for dev tools (pytest, ruff)

# Configure
cp .env.example .env
# Edit .env and add your Digitransit API key
# Get one at https://portal-api.digitransit.fi
```

## Running

```bash
# Development server
flask run --debug

# Production (Linux)
gunicorn --config gunicorn.conf.py "app:create_app()"

# Docker
docker compose up
```

The dashboard is available at `http://localhost:5000` (dev) or `http://localhost:8000` (production/Docker).

## How it works

1. **Daily collection** (automatic at 03:00): Fetches the full day's scheduled timetable from the API and stores it in the database.
2. **Realtime polling** (automatic every 30s during service hours): Captures actual bus delays as buses run, updating the stored schedule data with real delay values.
3. **Dashboard**: Displays summary statistics, delay charts by hour, per-route breakdowns, and recent observations.

You can also trigger collection manually from the dashboard or via the JSON API.

## Configuration

| Variable | Default | Description |
|---|---|---|
| `DIGITRANSIT_API_KEY` | *(required)* | API key from portal-api.digitransit.fi |
| `TARGET_STOP_ID` | `Vaasa:309392` | GTFS stop ID to monitor |
| `DATABASE_PATH` | `data/waltti.db` | Path to SQLite database file |
| `POLL_INTERVAL_SECONDS` | `30` | Seconds between realtime polls |
| `POLL_START_HOUR` | `5` | Hour (Helsinki time) to start polling |
| `POLL_END_HOUR` | `24` | Hour (Helsinki time) to stop polling |

## Deployment

### Generic (Docker)

```bash
docker build -t waltti-analyzer .
docker run -p 8000:8000 -e DIGITRANSIT_API_KEY=your_key -v waltti-data:/app/data waltti-analyzer
```

### Azure App Service

1. Create a Python 3.11+ App Service
2. Set `DIGITRANSIT_API_KEY` as an App Setting
3. Set startup command: `gunicorn --config gunicorn.conf.py "app:create_app()"`
4. Deploy from GitHub (or use the Docker image)
5. Enable "Always On" (Basic tier+) to keep the scheduler running
6. For persistent SQLite: mount Azure Files to `/app/data`

## API Endpoints

| Endpoint | Method | Description |
|---|---|---|
| `/api/collect/daily` | POST | Trigger daily schedule collection |
| `/api/collect/realtime` | POST | Trigger single realtime poll |
| `/api/status` | GET | Scheduler and collection status |
| `/api/observations?date=YYYY-MM-DD` | GET | Raw observations as JSON |
| `/api/summary?from=YYYY-MM-DD&to=YYYY-MM-DD` | GET | Summary statistics as JSON |

## Notes

- The default stop (Vaasa:309392, Gerbynmäentie / Yttergårdinpolku) serves routes 3 and 9 with ~20 weekday departures.
- Public holidays (e.g. Good Friday) have no bus service — the app detects this and logs it.
- Helsinki timezone is UTC+2 in winter, UTC+3 in summer. DST changes are handled automatically.
