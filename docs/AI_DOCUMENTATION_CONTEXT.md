# Shared Documentation Context

## What This Project Is Trying To Achieve

- Build a usable historical punctuality dataset for Vaasa Waltti buses.
- Capture whether each bus trip was early, on time, late, or canceled at each stop for each service day.
- Work around the Digitransit limitation that realtime delays disappear after service ends by collecting data while buses are running.
- Keep using SQLite locally; Azure SQL is used in the production Azure deployment.

## Current Direction

- The backend runs on Azure Functions (Flex Consumption plan, C#/.NET 8 isolated worker) with a timer-triggered sync function and HTTP-triggered API functions.
- The frontend is a static SPA (vanilla JS, no build step) deployed to Azure Static Web Apps.
- Treat the app as a network-wide collector for the Vaasa feed, not only as a single-stop demo.
- Discover all stops for the configured `FEED_ID`.
- Seed daily scheduled stop-times before service starts.
- Poll realtime during service hours and upgrade scheduled rows into observed rows.
- Treat each `observations` row as the best known result for one `(stop_gtfs_id, trip_gtfs_id, service_date)` combination.
- Distinguish clearly between scheduled-only rows and rows backed by realtime observations when reporting punctuality.

## Documentation Maintenance Rules

- Search existing docs before creating a new document.
- Update the existing source-of-truth document when the topic already has a home.
- When direction changes, update old documentation in the same change instead of leaving stale guidance behind.
- Remove or rewrite outdated guidance instead of adding a second, conflicting explanation elsewhere.
- If a new document is necessary, keep its purpose narrow and link to it from the existing source-of-truth document.

## Documentation Ownership By File

- `README.md`: human-facing onboarding, setup, operations, and high-level behavior
- `docs/AI_INSTRUCTIONS.md`: agent-facing repo instructions, architecture, and implementation caveats
- `docs/AI_DOCUMENTATION_CONTEXT.md`: shared product intent, current direction, and documentation maintenance rules for all agents

## Known Drift To Watch For

- The database backend differs by environment: SQLite (`sqlite3`) locally and Azure SQL (ODBC) in production. Do not assume SQLite-only when working on deployment or database connection code.
- The SQLite schema includes `realtime_states`; schema descriptions should list all five tables (stops, trips, realtime_states, observations, collection_log).
