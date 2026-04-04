from unittest.mock import Mock

from flask import Flask

import app.scheduler as scheduler


class FakeScheduler:
    def __init__(self, *args, **kwargs):
        self.jobs = []
        self.running = False

    def add_job(self, func, trigger=None, id=None, **kwargs):
        self.jobs.append({"func": func, "trigger": trigger, "id": id, "kwargs": kwargs})

    def get_jobs(self):
        return []

    def start(self):
        self.running = True


def _make_app():
    app = Flask(__name__)
    app.config.update(
        DIGITRANSIT_API_URL="https://example.test/graphql",
        DIGITRANSIT_API_KEY="test-key",
        FEED_ID="Vaasa",
        DATABASE_PATH="data/test.db",
    )
    return app


def _reset_scheduler_logger_state():
    scheduler._scheduler = None
    scheduler._scheduler_logger_configured = False
    scheduler.logger.handlers.clear()
    scheduler.logger.setLevel(0)
    scheduler.logger.propagate = True


def test_daily_job_emits_debug_output(monkeypatch, capsys):
    _reset_scheduler_logger_state()

    monkeypatch.setattr(scheduler, "BackgroundScheduler", FakeScheduler)
    monkeypatch.setattr(
        scheduler,
        "collect_daily",
        Mock(return_value={"status": "ok", "date": "2026-04-04", "departures": 12}),
    )

    scheduler.init_scheduler(_make_app())

    daily_job = next(job for job in scheduler._scheduler.jobs if job["id"] == "daily_collection")
    daily_job["func"]()

    output = capsys.readouterr().err
    assert "Scheduler run starting: daily_collection" in output
    assert "Scheduler run finished: daily_collection" in output
    assert "'departures': 12" in output


def test_realtime_job_polls_unconditionally(monkeypatch, capsys):
    _reset_scheduler_logger_state()

    poll_mock = Mock(return_value={"status": "ok", "updated": 3})

    monkeypatch.setattr(scheduler, "BackgroundScheduler", FakeScheduler)
    monkeypatch.setattr(scheduler, "poll_realtime_once", poll_mock)

    scheduler.init_scheduler(_make_app())

    realtime_job = next(job for job in scheduler._scheduler.jobs if job["id"] == "realtime_poll")
    realtime_job["func"]()

    output = capsys.readouterr().err
    assert "Scheduler run starting: realtime_poll" in output
    assert "Scheduler run finished: realtime_poll" in output
    poll_mock.assert_called_once()
