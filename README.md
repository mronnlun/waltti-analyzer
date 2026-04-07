# Waltti Analyzer

Bus punctuality analysis for Vaasa, Finland. Collects realtime delay data from the [Digitransit Waltti API](https://digitransit.fi/en/developers/) and provides a web dashboard showing how well buses stay on schedule.

## Architecture

- **Backend**: ASP.NET Core (C#/.NET 10) — `src/WalttiAnalyzer.Web/` + `src/WalttiAnalyzer.Core/`
- **Frontend**: Static SPA (HTML/CSS/JS) served by the web app — `frontend/`
- **Database**: SQLite locally (via EF Core), Azure SQL in production (via EF Core)
- **Infrastructure**: Bicep templates in `infra/`

### How it works

1. **`DataSyncBackgroundService`** runs every 10 minutes (starts immediately on startup):
   - **Realtime polling** — every invocation captures current GPS-based delays before they disappear from the API
   - **Daily collection** — every invocation fetches full day schedules for all stops
   - **Stop discovery** — once per hour, or immediately on the first invocation of the day if stops haven't been fetched yet
2. **Minimal API endpoints** serve as the REST API backend for the SPA
3. **SPA frontend** is served as static files from `wwwroot/` and renders the dashboard, observations, and stops pages

## Quick Start (local development)

### Prerequisites

- .NET 10 SDK
- A Digitransit API key (free at https://portal.digitransit.fi)

### Setup

```bash
# Optionally create appsettings.Development.json with your API key:
cat > src/WalttiAnalyzer.Web/appsettings.Development.json << 'JSON'
{
  "Waltti": {
    "DigitransitApiKey": "your_api_key_here"
  }
}
JSON
```

`appsettings.Development.json` is gitignored (it contains secrets).

### Run the app

```bash
cd src/WalttiAnalyzer.Web
dotnet run
```

The app (API + frontend) will be available at `http://localhost:5000`.

## Building and Testing

```bash
# Build the web app
dotnet build src/WalttiAnalyzer.Web/WalttiAnalyzer.Web.csproj

# Run tests
dotnet test tests/WalttiAnalyzer.Tests/WalttiAnalyzer.Tests.csproj
```

## Configuration

Settings are bound from the `"Waltti"` config section (use `Waltti__Key` as environment variable names).

| Setting | Default | Description |
|---|---|---|
| `Waltti__DigitransitApiKey` | — | Required. API key for Digitransit |
| `Waltti__DigitransitApiUrl` | `https://api.digitransit.fi/routing/v2/waltti/gtfs/v1` | Digitransit endpoint |
| `Waltti__FeedId` | `Vaasa` | GTFS feed to collect data for |
| `Waltti__DefaultStopId` | `Vaasa:309392` | Default stop shown in the UI |
| `Waltti__DatabasePath` | `data/waltti.db` | SQLite database path (local dev only) |

In Azure, the `DATABASE` connection string overrides SQLite and uses Azure SQL via EF Core SQL Server provider.

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
| GET | `/health` | Health check (DB connectivity) |
| POST | `/api/collect/daily` | Trigger daily collection |
| POST | `/api/collect/realtime` | Trigger realtime poll |
| POST | `/api/discover` | Trigger stop discovery |

## Deployment

The project deploys to Azure via GitHub Actions (`.github/workflows/cd.yml`):

1. **Infrastructure** — Bicep template creates a B1 Linux App Service Plan, ASP.NET Core web app, Azure SQL, Log Analytics, and Application Insights
2. **App** — Published with `dotnet publish` and deployed via zip deployment to Azure App Service

## Notes

- The Digitransit API only exposes realtime delays while buses are running. Once service ends for the day, delay data disappears. The background service captures this data before it is lost.
- Delays beyond ±30 minutes are flagged as suspect GPS data and excluded from statistics.
- All times are stored in UTC; display output uses Europe/Helsinki timezone.
- Holiday/no-service detection: all patterns return empty stoptimes → logged as no-service.
- OpenTelemetry is configured with Azure Monitor exporter (`APPLICATIONINSIGHTS_CONNECTION_STRING` env var). Custom traces are emitted from `DataSyncBackgroundService` with the `WalttiAnalyzer.Sync` activity source.
