# Waltti Analyzer — AI Instructions

Read [docs/AI_DOCUMENTATION_CONTEXT.md](docs/AI_DOCUMENTATION_CONTEXT.md) before substantial work.
When project direction changes, update existing documentation in the same change instead of creating parallel docs that drift.

## Project Overview

A web application that collects and analyzes the punctuality of buses at stops in Vaasa, Finland. It polls the Digitransit Waltti GraphQL API, stores timetable and realtime delay data in SQLite (locally) or Azure SQL (production), and displays timeliness reports in a single-page web dashboard.

The Digitransit API does **not** retain historical delay data. Realtime delay values are only available while buses are actively running. Once a service day passes, the API reverts to static schedule data. This means the application **must** actively poll during service hours to capture delay data before it disappears.

The current direction is feed-wide coverage for Vaasa. A default stop may still be used in the UI, but collection and reporting should be designed around all discovered stops and routes.

## Tech Stack

- **C# / .NET 10** with **ASP.NET Core** (minimal APIs + BackgroundService)
- **SQLite** database locally (via EF Core); **Azure SQL** in production (via EF Core)
- `BackgroundService` for periodic data synchronization (every 10 minutes)
- Minimal API endpoints as REST API backend
- **SPA frontend** with vanilla JavaScript, **Chart.js** (CDN) for charts, **Tom-Select** (CDN) for dropdowns
- **xUnit** for testing
- No frontend build step — all JS via CDN

## Architecture

```
src/WalttiAnalyzer.Core/              # Shared library
├── WalttiAnalyzer.Core.csproj
├── Data/
│   └── WalttiDbContext.cs            # EF Core DbContext (schema + model config)
├── Models/
│   ├── WalttiSettings.cs             # Configuration (bound from "Waltti:" config section)
│   ├── Stop.cs                       # EF entity
│   ├── Trip.cs                       # EF entity
│   ├── RealtimeState.cs              # EF entity (seeded: SCHEDULED/UPDATED/CANCELED)
│   ├── ObservationRecord.cs          # EF entity (observations table)
│   ├── Observation.cs                # Read DTO (denormalized, joins stops+trips+states)
│   └── CollectionLogEntry.cs         # EF entity
└── Services/
    ├── DatabaseService.cs            # Data access (upserts via raw SQL, reads via LINQ)
    ├── DigitransitClient.cs          # GraphQL API client (typed HttpClient)
    ├── CollectorService.cs           # Data collection orchestration
    └── AnalyzerService.cs            # Statistics and reporting

src/WalttiAnalyzer.Web/               # ASP.NET Core host
├── WalttiAnalyzer.Web.csproj
├── Program.cs                        # DI setup, EF Core config, minimal API routes
├── appsettings.json
└── Services/
    └── DataSyncBackgroundService.cs  # Periodic sync (10 min, IHostedService)

frontend/
├── index.html           # SPA entry point (served as static files by the web app)
├── style.css            # Stylesheet
└── app.js               # Client-side JavaScript

tests/WalttiAnalyzer.Tests/
├── WalttiAnalyzer.Tests.csproj      # References WalttiAnalyzer.Core
├── TestDbFixture.cs                 # Shared in-memory SQLite WalttiDbContext setup
├── DatabaseTests.cs                 # DB layer tests (async)
└── AnalyzerTests.cs                 # Statistics tests (async)

infra/
├── main.bicep           # Azure infrastructure (App Service, Azure SQL, App Insights)
└── parameters.json      # Deployment parameters
```

## Coding Conventions

- Use dependency injection for services (registered in Program.cs)
- `WalttiDbContext` is scoped; `DatabaseService` and `AnalyzerService` are scoped; `DigitransitClient` is a typed HttpClient (scoped by AddHttpClient)
- `DataSyncBackgroundService` uses `IServiceScopeFactory` to create a new scope per sync cycle
- All times stored in DB as UTC unix timestamps or seconds-since-midnight (as from API)
- All display output uses Europe/Helsinki timezone
- The code uses upserts keyed on `(stop_id, trip_id, service_date)` via `ON CONFLICT` (SQLite) or `MERGE` (SQL Server)
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
| `DATABASE_PATH` | `data/waltti.db` | No (local SQLite only) |

**Database per environment — never confuse these:**
- **Local development**: SQLite only. `WalttiDbContext` uses `UseSqlite(DatabasePath)`. No extra setup required.
- **Azure (production)**: Azure SQL only. The `DATABASE` connection string is injected by Bicep (ADO.NET format for EF Core SQL Server provider). `Waltti__DatabasePath` and SQLite are **not used** in Azure. Do not add `Waltti__DatabasePath` to Bicep or any production config.

## Building and Testing

```bash
dotnet build src/WalttiAnalyzer.Web/WalttiAnalyzer.Web.csproj
dotnet test tests/WalttiAnalyzer.Tests/WalttiAnalyzer.Tests.csproj
```

## Database Schema

Five tables: `stops`, `trips`, `realtime_states`, `observations`, `collection_log`. See `WalttiDbContext.cs` for full model configuration and `DatabaseService.cs` for upsert SQL.
The `observations` table has a `UNIQUE(stop_id, trip_id, service_date)` constraint for upserts.

## Important Edge Cases

- Holiday/no-service detection: all patterns have empty stoptimes → log as no-service, skip
- API returns null for stop → stop ID may be wrong (check GTFS: prefix stripping)
- `realtime=false` means static schedule only — exclude from delay statistics
- Seconds-since-midnight can exceed 86400 for trips past midnight
- Helsinki timezone: UTC+2 in winter (EET), UTC+3 in summer (EEST)

Read [docs/AI_DOCUMENTATION_CONTEXT.md](docs/AI_DOCUMENTATION_CONTEXT.md) before substantial work.
