# Shared Documentation Context

## What This Project Is Trying To Achieve

- Build a usable historical punctuality dataset for Vaasa Waltti buses.
- Capture whether each bus trip was early, on time, late, or canceled at each stop for each service day.
- Work around the Digitransit limitation that realtime delays disappear after service ends by collecting data while buses are running.
- Keep using the current SQLite schema as the storage boundary unless there is a clear reason to expand it.

## Current Direction

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

- The current Python app config uses `FEED_ID` and `DEFAULT_STOP_ID`, while some older files still refer to `TARGET_STOP_ID`.
- The SQLite schema includes `trips`; schema descriptions should not say there are only three tables.
- Older wording may imply the app only monitors one stop. The current goal is feed-wide coverage with a default stop used only as a convenient UI starting point.
