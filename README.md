# Waltti Analyzer

Bus punctuality analysis for Vaasa, Finland. Collects realtime delay data from the [Digitransit Waltti API](https://digitransit.fi/en/developers/) and provides a web dashboard showing how well buses stay on schedule.

## Architecture

- **Backend**: Azure Functions (Python, consumption plan) — `api/`
- **Frontend**: Static SPA (HTML/CSS/JS) — `frontend/`
- **Database**: SQLite locally, Azure SQL in production
- **Infrastructure**: Bicep templates in `infra/`

### How it works

1. **Timer-triggered function** (`sync_bus_data`) runs every 3 minutes:
   - **Realtime polling** — every invocation captures current GPS-based delays before they disappear from the API
   - **Daily collection** — at 03:00 and 23:00 Helsinki time, fetches full day schedules
   - **Weekly discovery** — Monday 02:00 Helsinki time, discovers all stops/routes for the feed
2. **HTTP-triggered functions** serve as the REST API backend for the SPA
3. **SPA frontend** fetches data from the API and renders the dashboard, observations, and stops pages

## Quick Start (local development)

### Prerequisites

- Python 3.11+
- [Azure Functions Core Tools](https://learn.microsoft.com/en-us/azure/azure-functions/functions-run-local) (v4)
- A Digitransit API key (free at https://portal.digitransit.fi)

### Setup

```bash
# Create and activate a virtual environment
python -m venv .venv
source .venv/bin/activate    # or .venv\Scripts\activate on Windows

# Install dependencies
pip install -r api/requirements.txt
pip install -e ".[dev]"

# Configure environment
cp api/local.settings.json.example api/local.settings.json
# Edit api/local.settings.json and add your DIGITRANSIT_API_KEY
```

### Run the backend

```bash
cd api
func start
```

The API will be available at `http://localhost:7071/api/`.

### Run the frontend

Open `frontend/index.html` in a browser, or serve it with any static file server:

```bash
cd frontend
python -m http.server 8080
```

Then open `http://localhost:8080`. By default the SPA calls `/api/*` — configure `window.WALTTI_API_BASE` in the browser console if the Function App runs on a different port.

## Running Tests

```bash
pytest
ruff check .
```

## Configuration

| Variable | Default | Description |
|---|---|---|
| `DIGITRANSIT_API_KEY` | — | Required. API key for Digitransit |
| `FEED_ID` | `Vaasa` | GTFS feed to collect data for |
| `DEFAULT_STOP_ID` | `Vaasa:309392` | Default stop shown in the UI |
| `DATABASE_PATH` | `data/waltti.db` | SQLite database path (local only) |

In Azure, the database connection string is injected via the `DATABASE` connection string setting.

## API Endpoints

| Method | Endpoint | Description |
|---|---|---|
| GET | `/api/status` | Collection status and feed info |
| GET | `/api/stops` | List all discovered stops |
| GET | `/api/routes` | List all routes with observations |
| GET | `/api/routes-for-stop?stop_id=X` | Routes for a specific stop |
| GET | `/api/observations?stop_id=X&from=Y&to=Z` | Observations for a stop and date range |
| GET | `/api/latest-observations` | 100 most recent GPS observations |
| GET | `/api/summary?stop_id=X&from=Y&to=Z` | Summary statistics |
| GET | `/api/route-breakdown?stop_id=X&from=Y&to=Z` | Per-route statistics |
| GET | `/api/delay-by-hour?stop_id=X&from=Y&to=Z` | Average delay by hour |
| POST | `/api/collect/daily` | Trigger daily collection |
| POST | `/api/collect/realtime` | Trigger realtime poll |
| POST | `/api/discover` | Trigger stop discovery |

## Deployment

The project deploys to Azure via GitHub Actions (`.github/workflows/cd.yml`):

1. **Infrastructure** — Bicep template creates a consumption-plan Function App, Static Web App, Azure SQL, and supporting resources
2. **API** — Function App code is deployed via zip deployment
3. **Frontend** — Static files are deployed to Azure Static Web Apps

## Notes

- The Digitransit API only exposes realtime delays while buses are running. Once service ends for the day, delay data disappears. The timer function captures this data before it is lost.
- Delays beyond ±30 minutes are flagged as suspect GPS data and excluded from statistics.
- All times are stored in UTC; display output uses Europe/Helsinki timezone.
- Holiday/no-service detection: all patterns return empty stoptimes → logged as no-service.
