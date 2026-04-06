# Waltti Analyzer — AI Instructions

Read [docs/AI_DOCUMENTATION_CONTEXT.md](docs/AI_DOCUMENTATION_CONTEXT.md) before substantial work.
When project direction changes, update existing documentation in the same change instead of creating parallel docs that drift.

## Project Overview

A web application that collects and analyzes the punctuality of buses at stops in Vaasa, Finland. It polls the Digitransit Waltti GraphQL API, stores timetable and realtime delay data in SQLite (locally) or Azure SQL (production), and displays timeliness reports in a single-page web dashboard.

The Digitransit API does **not** retain historical delay data. Realtime delay values are only available while buses are actively running. Once a service day passes, the API reverts to static schedule data. This means the application **must** actively poll during service hours to capture delay data before it disappears.

The current direction is feed-wide coverage for Vaasa. A default stop may still be used in the UI, but collection and reporting should be designed around all discovered stops and routes.

## Tech Stack

- **C# / .NET 8** with **Azure Functions** (Flex Consumption plan, isolated worker model)
- **SQLite** database locally (Microsoft.Data.Sqlite); **Azure SQL** in production
- Timer-triggered function for background data synchronization
- HTTP-triggered functions as REST API backend
- **SPA frontend** with vanilla JavaScript, **Chart.js** (CDN) for charts, **Tom-Select** (CDN) for dropdowns
- **xUnit** for testing
- No frontend build step — all JS via CDN

## Architecture

```
api/WalttiAnalyzer.Functions/
├── Program.cs                       # Host builder and DI setup
├── WalttiAnalyzer.Functions.csproj  # Project file (.NET 8, isolated worker)
├── host.json                        # Azure Functions host configuration
├── local.settings.json.example      # Dev settings template
├── Functions/
│   ├── SyncBusDataFunction.cs       # Timer trigger (every 5 min)
│   └── ApiFunctions.cs              # HTTP triggers (REST API)
├── Services/
│   ├── DatabaseService.cs           # SQLite schema + data access
│   ├── DigitransitClient.cs         # GraphQL API client
│   ├── CollectorService.cs          # Data collection orchestration
│   └── AnalyzerService.cs           # Statistics and reporting
└── Models/
    ├── Stop.cs
    ├── Trip.cs
    ├── Observation.cs
    └── CollectionLogEntry.cs

frontend/
├── index.html           # SPA entry point
├── style.css            # Stylesheet
└── app.js               # Client-side JavaScript

tests/WalttiAnalyzer.Tests/
├── WalttiAnalyzer.Tests.csproj
├── TestDbFixture.cs     # Shared test DB setup
├── DatabaseTests.cs     # DB layer tests
└── AnalyzerTests.cs     # Statistics tests

infra/
├── main.bicep           # Azure infrastructure (Function App, Static Web App, SQL)
└── parameters.json      # Deployment parameters
```

## Coding Conventions

- Use dependency injection for services (registered in Program.cs)
- Database connections via `DatabaseService.Connect(dbPath)` — close when done
- All times stored in DB as UTC unix timestamps or seconds-since-midnight (as from API)
- All display output uses Europe/Helsinki timezone
- The code uses upserts keyed on `(stop_gtfs_id, trip_gtfs_id, service_date)`
- **Always work on a feature branch — never commit directly to `main`.**
- **Always open a pull request** for your branch when the work is ready.

## Key API Details

- **Endpoint**: `POST https://api.digitransit.fi/routing/v2/waltti/gtfs/v1`
- **Auth header**: `digitransit-subscription-key: <key>`
- **Content-Type**: `application/json`
- **Body**: `{"query": "<graphql>"}`
- **Stop ID format**: `Vaasa:309392` (no `GTFS:` prefix)
- **Realtime fields**: `realtime` (bool), `departureDelay` (seconds, positive=late), `realtimeState` (SCHEDULED/UPDATED/CANCELED)
- **No-service days**: All patterns return empty `stoptimes` arrays (not an error)

## Environment Variables

| Variable | Default | Required |
|---|---|---|
| `DIGITRANSIT_API_KEY` | — | Yes |
| `DIGITRANSIT_API_URL` | `https://api.digitransit.fi/routing/v2/waltti/gtfs/v1` | No |
| `FEED_ID` | `Vaasa` | No |
| `DEFAULT_STOP_ID` | `Vaasa:309392` | No |
| `DATABASE_PATH` | `data/waltti.db` | No (local SQLite only — irrelevant in Azure) |

**Database per environment — never confuse these:**
- **Local development**: SQLite only. `DatabaseService` uses `Microsoft.Data.Sqlite` via `DATABASE_PATH` / `Waltti__DatabasePath`. No extra setup required.
- **Azure (production)**: Azure SQL only. The `DATABASE` connection string is injected by Bicep (ODBC, SQL Server). `Waltti__DatabasePath` and SQLite are **not used** in Azure. Do not add `Waltti__DatabasePath` to Bicep or any production config.

## Building and Testing

```bash
dotnet build api/WalttiAnalyzer.Functions/WalttiAnalyzer.Functions.csproj
dotnet test tests/WalttiAnalyzer.Tests/WalttiAnalyzer.Tests.csproj
```

## Database Schema

Five tables: `stops`, `trips`, `realtime_states`, `observations`, `collection_log`. See `DatabaseService.cs` for full DDL.
The `observations` table has a `UNIQUE(stop_id, trip_id, service_date)` constraint for upserts.

## Important Edge Cases

- Holiday/no-service detection: all patterns have empty stoptimes → log as no-service, skip
- API returns null for stop → stop ID may be wrong (check GTFS: prefix stripping)
- `realtime=false` means static schedule only — exclude from delay statistics
- Seconds-since-midnight can exceed 86400 for trips past midnight
- Helsinki timezone: UTC+2 in winter (EET), UTC+3 in summer (EEST)
