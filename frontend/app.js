/* Waltti Analyzer SPA */

const API_BASE = window.WALTTI_API_BASE || "/api";

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

async function fetchJSON(path, options) {
  const res = await fetch(`${API_BASE}/${path}`, options);
  if (!res.ok) throw new Error(`API ${res.status}: ${res.statusText}`);
  return res.json();
}

function formatDelay(seconds) {
  if (seconds == null || seconds === 0) return "0s";
  const sign = seconds >= 0 ? "+" : "-";
  const total = Math.abs(Math.round(seconds));
  const m = Math.floor(total / 60);
  const s = total % 60;
  if (m > 0) return `${sign}${m}m ${String(s).padStart(2, "0")}s`;
  return `${sign}${s}s`;
}

function formatTime(secondsSinceMidnight) {
  if (secondsSinceMidnight == null) return "";
  const h = Math.floor(secondsSinceMidnight / 3600);
  const m = Math.floor((secondsSinceMidnight % 3600) / 60);
  return `${String(h).padStart(2, "0")}:${String(m).padStart(2, "0")}`;
}

function escapeHtml(str) {
  if (!str) return "";
  const el = document.createElement("span");
  el.textContent = str;
  return el.innerHTML;
}

/** Format int YYYYMMDD as "YYYY-MM-DD" for display. */
function formatServiceDate(dateInt) {
  if (dateInt == null) return "";
  const s = String(dateInt);
  if (s.length !== 8) return s;
  return `${s.slice(0, 4)}-${s.slice(4, 6)}-${s.slice(6, 8)}`;
}

function todayStr() {
  return new Date().toISOString().slice(0, 10);
}

function daysAgo(n) {
  const d = new Date();
  d.setDate(d.getDate() - n);
  return d.toISOString().slice(0, 10);
}

const OUTLIER_THRESHOLD = 1800;

/** Delay source labels: 0=SCHEDULED, 1=PROPAGATED, 2=MEASURED */
function delaySourceLabel(ds) {
  if (ds === 2) return "M";
  if (ds === 1) return "P";
  return "";
}

function delaySourceTitle(ds) {
  if (ds === 2) return "Measured";
  if (ds === 1) return "Propagated";
  return "Scheduled";
}

function isDeparturePast(o) {
  const now = new Date();
  const todayHelsinki = now.toLocaleDateString("sv", { timeZone: "Europe/Helsinki" });
  // Convert "YYYY-MM-DD" to YYYYMMDD int for comparison with service_date int
  const todayInt = parseInt(todayHelsinki.replace(/-/g, ""), 10);

  if (o.service_date < todayInt) return true;
  if (o.service_date > todayInt) return false;

  // Same date as today — compare seconds since midnight in Helsinki time
  const parts = new Intl.DateTimeFormat("en", {
    timeZone: "Europe/Helsinki",
    hour: "2-digit",
    minute: "2-digit",
    second: "2-digit",
    hour12: false,
  }).formatToParts(now);
  const nowSecs =
    parseInt(parts.find((p) => p.type === "hour").value) * 3600 +
    parseInt(parts.find((p) => p.type === "minute").value) * 60 +
    parseInt(parts.find((p) => p.type === "second").value);

  return o.scheduled_departure != null && o.scheduled_departure <= nowSecs;
}

// ---------------------------------------------------------------------------
// Navigation
// ---------------------------------------------------------------------------

let currentPage = "dashboard";
let hourlyChart = null;
let stopSelect = null;
let routeSelect = null;
let headsignSelect = null;
let currentAbortController = null;
let _updatingDropdowns = false;

// Per-Route Breakdown sort state
let routeSortCol = null;
let routeSortDir = "asc";

const VALID_PAGES = ["dashboard", "observations", "stops"];

function pageFromHash() {
  const hash = window.location.hash.slice(1);
  return VALID_PAGES.includes(hash) ? hash : "dashboard";
}

function navigate(page, updateHash = true) {
  currentPage = page;
  if (updateHash && window.location.hash.slice(1) !== page) {
    window.location.hash = page;
  }
  document.querySelectorAll(".nav-links a").forEach((a) => {
    a.classList.toggle("active", a.dataset.page === page);
  });
  const app = document.getElementById("app");
  switch (page) {
    case "dashboard":
      renderDashboard(app);
      break;
    case "observations":
      renderObservations(app);
      break;
    case "stops":
      renderStops(app);
      break;
    default:
      renderDashboard(app);
  }
}

document.addEventListener("DOMContentLoaded", () => {
  document.querySelectorAll("[data-page]").forEach((el) => {
    el.addEventListener("click", (e) => {
      e.preventDefault();
      navigate(el.dataset.page);
    });
  });
  navigate(pageFromHash());
});

window.addEventListener("hashchange", () => {
  navigate(pageFromHash(), false);
});

// ---------------------------------------------------------------------------
// Dashboard page
// ---------------------------------------------------------------------------

async function renderDashboard(container) {
  if (hourlyChart) {
    hourlyChart.destroy();
    hourlyChart = null;
  }
  if (stopSelect) {
    stopSelect.destroy();
    stopSelect = null;
  }
  if (routeSelect) {
    routeSelect.destroy();
    routeSelect = null;
  }
  if (headsignSelect) {
    headsignSelect.destroy();
    headsignSelect = null;
  }

  container.innerHTML = `
    <h1>Dashboard</h1>
    <p class="subtitle">Bus punctuality analysis for Vaasa</p>
    <div class="controls">
      <div class="filter-form" id="dash-form">
        <div class="filter-row-stop">
          <label for="stop-select">Stop</label>
          <select id="stop-select"><option value="">Loading…</option></select>
          <button type="button" class="filter-clear-btn" id="stop-clear" title="Show all stops" aria-label="Show all stops">×</button>
        </div>
        <div class="filter-row-options">
          <label>From <input type="date" id="from-date" value="${daysAgo(5)}"></label>
          <label>To <input type="date" id="to-date" value="${todayStr()}"></label>
        </div>
        <div class="filter-row-route">
          <label for="route-select">Route</label>
          <select id="route-select"></select>
        </div>
        <div class="filter-row-headsign">
          <label for="headsign-select">Headsign</label>
          <select id="headsign-select"></select>
        </div>
        <div class="filter-row-time">
          <label>Time from <input type="time" id="time-from"></label>
          <button type="button" class="filter-clear-btn" id="time-from-clear" title="Clear time from" aria-label="Clear time from">×</button>
          <label>Time to <input type="time" id="time-to"></label>
          <button type="button" class="filter-clear-btn" id="time-to-clear" title="Clear time to" aria-label="Clear time to">×</button>
          <span class="filter-hint" title="Filter departures by scheduled time of day">?</span>
        </div>
        <div class="filter-status">
          <span id="action-status"></span>
        </div>
      </div>
    </div>
    <div id="dash-results"></div>
  `;

  _updatingDropdowns = true;

  routeSelect = new TomSelect("#route-select", {
    placeholder: "All routes",
    plugins: ["clear_button"],
    onChange: () => { if (!_updatingDropdowns) loadDashboardData(); },
    render: {
      option: function (data, escape) {
        return `<div class="option" title="${escape(data.text)}">${escape(data.text)}</div>`;
      },
    },
  });

  headsignSelect = new TomSelect("#headsign-select", {
    placeholder: "All headsigns",
    plugins: ["clear_button"],
    onChange: () => { if (!_updatingDropdowns) loadDashboardData(); },
    render: {
      option: function (data, escape) {
        return `<div class="option" title="${escape(data.text)}">${escape(data.text)}</div>`;
      },
    },
  });

  // Load stops and settings in parallel
  try {
    const [stops, status] = await Promise.all([
      fetchJSON("stops"),
      fetchJSON("status"),
    ]);
    const allStopsOption = { value: "", name: "All stops", id: "" };
    stopSelect = new TomSelect("#stop-select", {
      placeholder: "Select a stop…",
      valueField: "value",
      labelField: "name",
      searchField: ["name", "id"],
      options: [allStopsOption, ...stops.map((s) => ({ value: s.gtfs_id, name: s.name, id: s.gtfs_id }))],
      items: status.default_stop_id ? [status.default_stop_id] : [""],
      onChange: () => { if (!_updatingDropdowns) loadDashboardData(); },
      render: {
        option: function (data, escape) {
          if (data.value === "") {
            return `<div class="option ts-stop-option ts-stop-all"><span class="ts-stop-name">${escape(data.name)}</span></div>`;
          }
          return `<div class="option ts-stop-option">
            <span class="ts-stop-name">${escape(data.name)}</span>
            <span class="ts-stop-id">${escape(data.id)}</span>
          </div>`;
        },
        item: function (data, escape) {
          if (data.value === "") {
            return `<div class="item">${escape(data.name)}</div>`;
          }
          return `<div class="item" title="${escape(data.name)} (${escape(data.id)})">${escape(data.name)}</div>`;
        },
      },
    });
  } catch {
    document.getElementById("stop-select").innerHTML =
      '<option value="">Failed to load stops</option>';
  }

  _updatingDropdowns = false;

  // Wire up date / time change listeners
  document.getElementById("from-date").addEventListener("change", () => loadDashboardData());
  document.getElementById("to-date").addEventListener("change", () => loadDashboardData());
  document.getElementById("time-from").addEventListener("change", () => loadDashboardData());
  document.getElementById("time-to").addEventListener("change", () => loadDashboardData());

  // Clear buttons
  document.getElementById("stop-clear").addEventListener("click", () => {
    if (stopSelect) stopSelect.setValue(""); // onChange will trigger loadDashboardData
  });
  document.getElementById("time-from-clear").addEventListener("click", () => {
    document.getElementById("time-from").value = "";
    loadDashboardData();
  });
  document.getElementById("time-to-clear").addEventListener("click", () => {
    document.getElementById("time-to").value = "";
    loadDashboardData();
  });

  // Initial data load
  await loadDashboardData();
}

async function loadDashboardData() {
  // Cancel any in-flight request
  if (currentAbortController) currentAbortController.abort();
  currentAbortController = new AbortController();
  const signal = currentAbortController.signal;

  const stopId = document.getElementById("stop-select").value;
  const from = document.getElementById("from-date").value;
  const to = document.getElementById("to-date").value;
  const route = routeSelect ? routeSelect.getValue() : "";
  const headsign = headsignSelect ? headsignSelect.getValue() : "";
  const timeFrom = document.getElementById("time-from").value;
  const timeTo = document.getElementById("time-to").value;

  if (!from || !to) {
    document.getElementById("action-status").textContent = "Select a date range";
    return;
  }

  document.getElementById("action-status").textContent = "Loading…";

  const allStops = stopId === "";
  const params = new URLSearchParams({ from, to });
  if (!allStops) params.set("stop_id", stopId);
  if (route) params.set("route", route);
  if (headsign) params.set("headsign", headsign);
  if (timeFrom) params.set("time_from", timeFrom);
  if (timeTo) params.set("time_to", timeTo);

  try {
    const [summary, routes, hourly, observations, facets] = await Promise.all([
      fetchJSON(`summary?${params}`, { signal }),
      fetchJSON(`route-breakdown?${params}`, { signal }),
      fetchJSON(`delay-by-hour?${params}`, { signal }),
      fetchJSON(`observations?${params}`, { signal }),
      fetchJSON(`facets?${params}`, { signal }),
    ]);

    // Update all dropdowns from facets (suppress onChange to avoid recursive calls)
    _updatingDropdowns = true;
    let needsFollowUpLoad = false;
    try {
      // Update stop dropdown
      if (stopSelect) {
        const currentStop = stopSelect.getValue();
        const allStopsOption = { value: "", name: "All stops", id: "" };
        stopSelect.clearOptions();
        stopSelect.addOptions([allStopsOption, ...facets.stops.map((s) => ({ value: s.value, name: s.name, id: s.value }))]);
        const stopStillValid = currentStop === "" || facets.stops.some((s) => s.value === currentStop);
        stopSelect.setValue(stopStillValid ? currentStop : "", true);
        if (!stopStillValid && currentStop !== "") needsFollowUpLoad = true;
      }

      // Update route selector
      if (routeSelect) {
        const currentRoute = routeSelect.getValue();
        routeSelect.clearOptions();
        routeSelect.addOptions(facets.routes.map((r) => ({ value: r, text: r })));
        if (facets.routes.includes(currentRoute)) {
          routeSelect.setValue(currentRoute, true);
        } else {
          routeSelect.clear(true);
          if (currentRoute) needsFollowUpLoad = true;
        }
      }

      // Update headsign selector
      if (headsignSelect) {
        const currentHeadsign = headsignSelect.getValue();
        headsignSelect.clearOptions();
        headsignSelect.addOptions(facets.headsigns.map((h) => ({ value: h, text: h })));
        if (facets.headsigns.includes(currentHeadsign)) {
          headsignSelect.setValue(currentHeadsign, true);
        } else {
          headsignSelect.clear(true);
          if (currentHeadsign) needsFollowUpLoad = true;
        }
      }
    } finally {
      _updatingDropdowns = false;
    }

    // If any filter was auto-cleared (stale selection no longer valid), skip
    // rendering stale results and immediately reload with the corrected state.
    if (needsFollowUpLoad) {
      loadDashboardData();
      return;
    }

    document.getElementById("action-status").textContent = "";
    renderDashboardResults(summary, routes, hourly, observations, allStops);
  } catch (err) {
    if (err.name === "AbortError") return;
    document.getElementById("action-status").textContent = `Error: ${err.message}`;
  }
}

function renderDashboardResults(summary, routes, hourly, observations, allStops = false) {
  const el = document.getElementById("dash-results");

  if (!summary || summary.total_departures === 0) {
    el.innerHTML = '<p class="warning">No observations found for the selected criteria.</p>';
    return;
  }

  let html = "";

  // Summary cards
  html += `<div class="cards">
    <div class="card"><div class="card-value">${summary.total_departures}</div><div class="card-label">Total Departures</div></div>
    <div class="card"><div class="card-value">${summary.on_time_pct}%</div><div class="card-label">On Time</div></div>
    <div class="card"><div class="card-value">${formatDelay(summary.avg_late_seconds)}</div><div class="card-label">Avg Late</div></div>
    <div class="card"><div class="card-value">${formatDelay(summary.avg_early_seconds)}</div><div class="card-label">Avg Early</div></div>
    <div class="card"><div class="card-value">${summary.measured_pct}%</div><div class="card-label">Measured</div></div>
  </div>`;

  // Warnings
  if (summary.measured_pct < 50) {
    html += `<div class="warning">Only ${summary.measured_pct}% of departures have measured GPS data. Statistics may not be representative.</div>`;
  }
  if (summary.suspect_gps > 0) {
    html += `<div class="warning">${summary.suspect_gps} observations have suspect GPS data (>30 min deviation) and are excluded from statistics.</div>`;
  }

  // Timeliness breakdown
  html += `<div class="stats-section">
    <h2>Timeliness Breakdown</h2>
    <table>
      <tr><td>On time (0–3 min late)</td><td><strong>${summary.on_time}</strong></td></tr>
      <tr><td>Slightly late (3–10 min)</td><td><strong>${summary.slightly_late}</strong></td></tr>
      <tr><td>Very late (&gt;10 min)</td><td><strong>${summary.very_late}</strong></td></tr>
      <tr><td>Slightly early (&lt;1 min)</td><td><strong>${summary.slightly_early}</strong></td></tr>
      <tr><td>Very early (&gt;1 min early)</td><td><strong>${summary.very_early}</strong></td></tr>
      <tr><td>Canceled</td><td><strong>${summary.canceled}</strong></td></tr>
      <tr><td>Skipped</td><td><strong>${summary.skipped || 0}</strong></td></tr>
      <tr><td>Propagated (estimated)</td><td><strong>${summary.propagated || 0}</strong></td></tr>
      <tr><td>Static only (no GPS)</td><td><strong>${summary.static_only}</strong></td></tr>
    </table>
  </div>`;

  // Route breakdown
  if (routes.length > 0) {
    html += `<div class="stats-section" id="route-breakdown-section">
      <h2>Per-Route Breakdown</h2>
      <div class="table-responsive">
      <table class="data-table" id="route-breakdown-table" style="width:100%">
        <thead><tr>
          <th class="sortable" data-col="route">Route</th>
          <th class="sortable" data-col="departures">Deps</th>
          <th class="sortable" data-col="measured">Measured</th>
          <th class="sortable" data-col="on_time_pct">On-time %</th>
          <th class="sortable" data-col="avg_late_seconds">Avg Late</th>
          <th class="sortable" data-col="avg_early_seconds">Avg Early</th>
          <th class="sortable" data-col="max_late_seconds">Max Late</th>
          <th class="sortable" data-col="suspect_gps">Suspect</th>
        </tr></thead>
        <tbody id="route-breakdown-body"></tbody>
      </table></div></div>`;
  }

  // Hourly chart
  html += `<div class="stats-section">
    <h2>Delay by Hour of Day</h2>
    <div class="chart-container"><canvas id="hourly-chart"></canvas></div>
  </div>`;

  // Observations table — only show departures that have already occurred
  const pastObservations = observations.filter(isDeparturePast);
  if (pastObservations.length > 0) {
    html += `<div class="stats-section">
      <h2>All Departures (${pastObservations.length})</h2>
      <div class="table-responsive">
      <table class="data-table" style="width:100%">
        <thead><tr>
          <th>Date</th>${allStops ? "<th>Stop</th>" : ""}<th>Route</th><th>Headsign</th><th>Scheduled</th>
          <th>Actual</th><th>Deviation</th><th>State</th><th>Source</th>
        </tr></thead><tbody>`;
    for (const o of pastObservations) {
      const isSuspect =
        o.departure_delay != null && Math.abs(o.departure_delay) > OUTLIER_THRESHOLD;
      const isSkipped = o.realtime_state === "SKIPPED" || o.realtime_state === "CANCELED";
      const hasMeasurement = o.delay_source >= 1;
      const cls = isSuspect || isSkipped ? ' class="suspect-row"' : "";
      const showDelay = hasMeasurement && !isSkipped;
      const actualTime = showDelay && o.departure_delay != null
        ? o.scheduled_departure + o.departure_delay
        : null;
      html += `<tr${cls}>
        <td>${formatServiceDate(o.service_date)}</td>
        ${allStops ? `<td>${escapeHtml(o.stop_name || o.stop_gtfs_id)}</td>` : ""}
        <td>${escapeHtml(o.route_short_name || "")}</td>
        <td>${escapeHtml(o.headsign || "")}</td>
        <td>${formatTime(o.scheduled_departure)}</td>
        <td>${showDelay ? formatTime(actualTime) : ""}</td>
        <td>${showDelay ? formatDelay(o.departure_delay) : ""}</td>
        <td>${escapeHtml(o.realtime_state || "")}</td>
        <td title="${delaySourceTitle(o.delay_source)}">${delaySourceLabel(o.delay_source)}</td>
      </tr>`;
    }
    html += "</tbody></table></div></div>";
  }

  el.innerHTML = html;

  // Wire up Per-Route Breakdown sorting
  if (routes.length > 0) {
    renderRouteTableBody(routes);
    document.querySelectorAll("#route-breakdown-table th.sortable").forEach((th) => {
      th.addEventListener("click", () => {
        const col = th.dataset.col;
        if (routeSortCol === col) {
          routeSortDir = routeSortDir === "asc" ? "desc" : "asc";
        } else {
          routeSortCol = col;
          routeSortDir = "asc";
        }
        renderRouteTableBody(routes);
      });
    });
  }

  // Render chart
  if (hourly.length > 0) {
    renderHourlyChart(hourly);
  }
}

function renderRouteTableBody(routes) {
  const sorted = [...routes].sort((a, b) => {
    if (routeSortCol === null) return 0;
    const av = a[routeSortCol];
    const bv = b[routeSortCol];
    if (av == null && bv == null) return 0;
    if (av == null) return 1;
    if (bv == null) return -1;
    const cmp = typeof av === "string" ? av.localeCompare(bv) : av - bv;
    return routeSortDir === "asc" ? cmp : -cmp;
  });

  const tbody = document.getElementById("route-breakdown-body");
  if (!tbody) return;

  let html = "";
  for (const r of sorted) {
    html += `<tr>
      <td>${escapeHtml(r.route)}</td><td>${r.departures}</td><td>${r.measured}</td>
      <td>${r.on_time_pct}%</td><td>${formatDelay(r.avg_late_seconds)}</td>
      <td>${formatDelay(r.avg_early_seconds)}</td><td>${formatDelay(r.max_late_seconds)}</td>
      <td>${r.suspect_gps}</td>
    </tr>`;
  }
  tbody.innerHTML = html;

  // Update header sort indicators
  document.querySelectorAll("#route-breakdown-table th.sortable").forEach((th) => {
    th.classList.remove("sort-asc", "sort-desc");
    if (th.dataset.col === routeSortCol) {
      th.classList.add(routeSortDir === "asc" ? "sort-asc" : "sort-desc");
    }
  });
}

function renderHourlyChart(data) {
  if (hourlyChart) {
    hourlyChart.destroy();
    hourlyChart = null;
  }
  const canvas = document.getElementById("hourly-chart");
  if (!canvas) return;
  hourlyChart = new Chart(canvas, {
    type: "bar",
    data: {
      labels: data.map((d) => `${d.hour}:00`),
      datasets: [
        {
          label: "Avg Late (s)",
          data: data.map((d) => d.avg_late_seconds),
          backgroundColor: "rgba(231,76,60,0.7)",
        },
        {
          label: "Avg Early (s)",
          data: data.map((d) => d.avg_early_seconds),
          backgroundColor: "rgba(46,204,113,0.7)",
        },
        {
          label: "Average (s)",
          data: data.map((d) => d.avg_delay_seconds),
          type: "line",
          borderColor: "black",
          backgroundColor: "black",
          borderWidth: 2,
          pointRadius: 3,
          fill: false,
          order: 0,
        },
      ],
    },
    options: {
      responsive: true,
      scales: { y: { title: { display: true, text: "Seconds" } } },
    },
  });
}

// ---------------------------------------------------------------------------
// Observations page
// ---------------------------------------------------------------------------

async function renderObservations(container) {
  if (hourlyChart) {
    hourlyChart.destroy();
    hourlyChart = null;
  }
  if (stopSelect) {
    stopSelect.destroy();
    stopSelect = null;
  }

  container.innerHTML = `
    <h1>Latest GPS Observations</h1>
    <p class="subtitle">300 most recent GPS-tracked departures</p>
    <div id="obs-content"><p>Loading…</p></div>
  `;

  try {
    const observations = await fetchJSON("latest-observations");
    const el = document.getElementById("obs-content");
    if (observations.length === 0) {
      el.innerHTML = "<p>No GPS observations recorded yet.</p>";
      return;
    }

    let html = `<div class="table-responsive">
      <table class="data-table" style="width:100%">
        <thead><tr>
          <th>Date</th><th>Stop</th><th>Route</th><th>Headsign</th>
          <th>Scheduled</th><th>Actual</th><th>Deviation</th><th>State</th><th>Source</th>
        </tr></thead><tbody>`;
    for (const o of observations) {
      const isSuspect =
        o.departure_delay != null && Math.abs(o.departure_delay) > OUTLIER_THRESHOLD;
      const isSkipped = o.realtime_state === "SKIPPED" || o.realtime_state === "CANCELED";
      const hasMeasurement = o.delay_source >= 1;
      const cls = isSuspect || isSkipped ? ' class="suspect-row"' : "";
      const showDelay = hasMeasurement && !isSkipped;
      const actualTime = showDelay && o.departure_delay != null
        ? o.scheduled_departure + o.departure_delay
        : null;
      html += `<tr${cls}>
        <td>${formatServiceDate(o.service_date)}</td>
        <td>${escapeHtml(o.stop_name || o.stop_gtfs_id)}</td>
        <td>${escapeHtml(o.route_short_name || "")}</td>
        <td>${escapeHtml(o.headsign || "")}</td>
        <td>${formatTime(o.scheduled_departure)}</td>
        <td>${showDelay ? formatTime(actualTime) : ""}</td>
        <td>${showDelay ? formatDelay(o.departure_delay) : ""}</td>
        <td>${escapeHtml(o.realtime_state || "")}</td>
        <td title="${delaySourceTitle(o.delay_source)}">${delaySourceLabel(o.delay_source)}</td>
      </tr>`;
    }
    html += "</tbody></table></div>";
    el.innerHTML = html;
  } catch (err) {
    document.getElementById("obs-content").innerHTML = `<p class="warning">Error: ${escapeHtml(err.message)}</p>`;
  }
}

// ---------------------------------------------------------------------------
// Stops page
// ---------------------------------------------------------------------------

async function renderStops(container) {
  if (hourlyChart) {
    hourlyChart.destroy();
    hourlyChart = null;
  }
  if (stopSelect) {
    stopSelect.destroy();
    stopSelect = null;
  }

  container.innerHTML = `
    <h1>Bus Stops</h1>
    <p class="subtitle" id="stop-count">Loading…</p>
    <div class="controls">
      <input type="text" id="stop-filter" placeholder="Filter by name…"
             style="padding:0.4rem;border:1px solid #ddd;border-radius:4px;flex:1;min-width:200px;">
    </div>
    <div id="stops-content"><p>Loading…</p></div>
  `;

  try {
    const stops = await fetchJSON("stops");
    document.getElementById("stop-count").textContent = `${stops.length} stops discovered`;

    const renderTable = (filtered) => {
      let html = `<div class="table-responsive">
        <table class="data-table" style="width:100%">
          <thead><tr><th>Name</th><th>GTFS ID</th><th>Lat</th><th>Lon</th></tr></thead><tbody>`;
      for (const s of filtered) {
        html += `<tr>
          <td>${escapeHtml(s.name)}</td>
          <td>${escapeHtml(s.gtfs_id)}</td>
          <td>${s.lat != null ? s.lat.toFixed(4) : ""}</td>
          <td>${s.lon != null ? s.lon.toFixed(4) : ""}</td>
        </tr>`;
      }
      html += "</tbody></table></div>";
      document.getElementById("stops-content").innerHTML = html;
    };

    renderTable(stops);

    document.getElementById("stop-filter").addEventListener("input", (e) => {
      const q = e.target.value.toLowerCase();
      renderTable(stops.filter((s) => s.name.toLowerCase().includes(q)));
    });
  } catch (err) {
    document.getElementById("stops-content").innerHTML = `<p class="warning">Error: ${escapeHtml(err.message)}</p>`;
  }
}
