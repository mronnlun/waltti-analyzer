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

function todayStr() {
  return new Date().toISOString().slice(0, 10);
}

function daysAgo(n) {
  const d = new Date();
  d.setDate(d.getDate() - n);
  return d.toISOString().slice(0, 10);
}

const OUTLIER_THRESHOLD = 1800;

function isDeparturePast(o) {
  const now = new Date();
  const todayHelsinki = now.toLocaleDateString("sv", { timeZone: "Europe/Helsinki" });

  if (o.service_date < todayHelsinki) return true;
  if (o.service_date > todayHelsinki) return false;

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

  const scheduledPast = o.scheduled_departure != null && o.scheduled_departure <= nowSecs;
  const actualPast =
    o.realtime && o.realtime_departure != null && o.realtime_departure <= nowSecs;
  return scheduledPast || actualPast;
}

// ---------------------------------------------------------------------------
// Navigation
// ---------------------------------------------------------------------------

let currentPage = "dashboard";
let hourlyChart = null;
let stopSelect = null;
let routeSelect = null;

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

  container.innerHTML = `
    <h1>Dashboard</h1>
    <p class="subtitle">Bus punctuality analysis for Vaasa</p>
    <div class="controls">
      <form class="filter-form" id="dash-form">
        <div class="filter-row-stop">
          <label for="stop-select">Stop</label>
          <select id="stop-select"><option value="">Loading…</option></select>
        </div>
        <div class="filter-row-options">
          <label>From <input type="date" id="from-date" value="${daysAgo(5)}"></label>
          <label>To <input type="date" id="to-date" value="${todayStr()}"></label>
        </div>
        <div class="filter-row-route">
          <label for="route-select">Route</label>
          <select id="route-select"></select>
        </div>
        <div class="filter-row-time">
          <label>Time from <input type="time" id="time-from"></label>
          <label>Time to <input type="time" id="time-to"></label>
          <span class="filter-hint" title="Filter departures by scheduled time of day">?</span>
        </div>
        <div class="actions">
          <button type="submit">Analyze</button>
          <span id="action-status"></span>
        </div>
      </form>
    </div>
    <div id="dash-results"></div>
  `;

  routeSelect = new TomSelect("#route-select", {
    placeholder: "All routes",
    plugins: ["clear_button"],
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
    const sel = document.getElementById("stop-select");
    sel.innerHTML = stops
      .map(
        (s) =>
          `<option value="${escapeHtml(s.gtfs_id)}">${escapeHtml(s.name)} (${escapeHtml(s.gtfs_id)})</option>`
      )
      .join("");

    stopSelect = new TomSelect("#stop-select", {
      placeholder: "Select a stop…",
      allowEmptyOption: false,
    });

    if (status.default_stop_id) {
      stopSelect.setValue(status.default_stop_id);
    }
  } catch {
    document.getElementById("stop-select").innerHTML =
      '<option value="">Failed to load stops</option>';
  }

  // Form submit
  document.getElementById("dash-form").addEventListener("submit", async (e) => {
    e.preventDefault();
    await loadDashboardData();
  });

  // Auto-load data for the initially selected stop
  await loadDashboardData();
}

async function loadDashboardData() {
  const stopId = document.getElementById("stop-select").value;
  const from = document.getElementById("from-date").value;
  const to = document.getElementById("to-date").value;
  const route = routeSelect ? routeSelect.getValue() : "";
  const timeFrom = document.getElementById("time-from").value;
  const timeTo = document.getElementById("time-to").value;

  if (!stopId || !from || !to) {
    document.getElementById("action-status").textContent = "Select a stop and date range";
    return;
  }

  document.getElementById("action-status").textContent = "Loading…";

  const params = new URLSearchParams({ stop_id: stopId, from, to });
  if (route) params.set("route", route);
  if (timeFrom) params.set("time_from", timeFrom);
  if (timeTo) params.set("time_to", timeTo);

  try {
    const [summary, routes, hourly, observations, stopRoutes] = await Promise.all([
      fetchJSON(`summary?${params}`),
      fetchJSON(`route-breakdown?${params}`),
      fetchJSON(`delay-by-hour?${params}`),
      fetchJSON(`observations?${params}`),
      fetchJSON(`routes-for-stop?stop_id=${encodeURIComponent(stopId)}`),
    ]);

    // Update route selector
    if (routeSelect) {
      const currentRoute = routeSelect.getValue();
      routeSelect.clearOptions();
      routeSelect.addOptions(stopRoutes.map((r) => ({ value: r, text: r })));
      if (stopRoutes.includes(currentRoute)) {
        routeSelect.setValue(currentRoute, true);
      } else {
        routeSelect.clear(true);
      }
    }

    document.getElementById("action-status").textContent = "";
    renderDashboardResults(summary, routes, hourly, observations);
  } catch (err) {
    document.getElementById("action-status").textContent = `Error: ${err.message}`;
  }
}

function renderDashboardResults(summary, routes, hourly, observations) {
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
    <div class="card"><div class="card-value">${summary.with_realtime_pct}%</div><div class="card-label">GPS Tracking</div></div>
  </div>`;

  // Warnings
  if (summary.with_realtime_pct < 50) {
    html += `<div class="warning">⚠ Only ${summary.with_realtime_pct}% of departures have GPS data. Statistics may not be representative.</div>`;
  }
  if (summary.suspect_gps > 0) {
    html += `<div class="warning">⚠ ${summary.suspect_gps} observations have suspect GPS data (>30 min deviation) and are excluded from statistics.</div>`;
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
      <tr><td>Static only (no GPS)</td><td><strong>${summary.static_only}</strong></td></tr>
    </table>
  </div>`;

  // Route breakdown
  if (routes.length > 0) {
    html += `<div class="stats-section">
      <h2>Per-Route Breakdown</h2>
      <div class="table-responsive">
      <table class="data-table" style="width:100%">
        <thead><tr>
          <th>Route</th><th>Deps</th><th>GPS</th><th>On-time %</th>
          <th>Avg Late</th><th>Avg Early</th><th>Max Late</th><th>Suspect</th>
        </tr></thead><tbody>`;
    for (const r of routes) {
      html += `<tr>
        <td>${escapeHtml(r.route)}</td><td>${r.departures}</td><td>${r.with_realtime}</td>
        <td>${r.on_time_pct}%</td><td>${formatDelay(r.avg_late_seconds)}</td>
        <td>${formatDelay(r.avg_early_seconds)}</td><td>${formatDelay(r.max_late_seconds)}</td>
        <td>${r.suspect_gps}</td>
      </tr>`;
    }
    html += "</tbody></table></div></div>";
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
          <th>Date</th><th>Route</th><th>Headsign</th><th>Scheduled</th>
          <th>Actual</th><th>Deviation</th><th>GPS</th>
        </tr></thead><tbody>`;
    for (const o of pastObservations) {
      const isSuspect =
        o.departure_delay != null && Math.abs(o.departure_delay) > OUTLIER_THRESHOLD;
      const cls = isSuspect ? ' class="suspect-row"' : "";
      html += `<tr${cls}>
        <td>${escapeHtml(o.service_date)}</td>
        <td>${escapeHtml(o.route_short_name || "")}</td>
        <td>${escapeHtml(o.headsign || "")}</td>
        <td>${formatTime(o.scheduled_departure)}</td>
        <td>${o.realtime ? formatTime(o.realtime_departure) : ""}</td>
        <td>${o.realtime ? formatDelay(o.departure_delay) : ""}</td>
        <td>${o.realtime ? "✓" : ""}</td>
      </tr>`;
    }
    html += "</tbody></table></div></div>";
  }

  el.innerHTML = html;

  // Render chart
  if (hourly.length > 0) {
    renderHourlyChart(hourly);
  }
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
    <p class="subtitle">100 most recent GPS-tracked departures</p>
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
          <th>Scheduled</th><th>Actual</th><th>Deviation</th>
        </tr></thead><tbody>`;
    for (const o of observations) {
      const isSuspect =
        o.departure_delay != null && Math.abs(o.departure_delay) > OUTLIER_THRESHOLD;
      const cls = isSuspect ? ' class="suspect-row"' : "";
      html += `<tr${cls}>
        <td>${escapeHtml(o.service_date)}</td>
        <td>${escapeHtml(o.stop_name || o.stop_gtfs_id)}</td>
        <td>${escapeHtml(o.route_short_name || "")}</td>
        <td>${escapeHtml(o.headsign || "")}</td>
        <td>${formatTime(o.scheduled_departure)}</td>
        <td>${formatTime(o.realtime_departure)}</td>
        <td>${formatDelay(o.departure_delay)}</td>
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
