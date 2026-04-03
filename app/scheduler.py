import logging
from datetime import datetime
from zoneinfo import ZoneInfo

from apscheduler.schedulers.background import BackgroundScheduler

from app.collector import collect_daily, discover_stops, poll_realtime_once

logger = logging.getLogger(__name__)

_scheduler: BackgroundScheduler | None = None

HELSINKI_TZ = ZoneInfo("Europe/Helsinki")


def init_scheduler(app):
    global _scheduler

    if _scheduler is not None:
        return

    api_url = app.config["DIGITRANSIT_API_URL"]
    api_key = app.config["DIGITRANSIT_API_KEY"]
    feed_id = app.config["FEED_ID"]
    db_path = app.config["DATABASE_PATH"]
    interval = app.config["POLL_INTERVAL_SECONDS"]
    start_hour = app.config["POLL_START_HOUR"]
    end_hour = app.config["POLL_END_HOUR"]
    rate_limit = app.config["API_RATE_LIMIT_DELAY"]

    if not api_key:
        logger.warning("DIGITRANSIT_API_KEY not set — scheduler disabled")
        return

    _scheduler = BackgroundScheduler(timezone=HELSINKI_TZ)

    # Discover stops on startup and weekly
    def _discover():
        discover_stops(db_path, api_url, api_key, feed_id)

    _scheduler.add_job(
        _discover,
        "cron",
        day_of_week="mon",
        hour=2,
        minute=0,
        id="discover_stops",
        misfire_grace_time=3600,
    )

    # Daily collection at 03:00 Helsinki time — all stops
    def _daily():
        collect_daily(db_path, api_url, api_key, feed_id=feed_id, rate_limit_delay=rate_limit)

    _scheduler.add_job(
        _daily,
        "cron",
        hour=3,
        minute=0,
        id="daily_collection",
        misfire_grace_time=3600,
    )

    # Realtime polling with hour guard — all stops
    def _guarded_poll():
        now = datetime.now(HELSINKI_TZ)
        if start_hour <= now.hour < end_hour:
            poll_realtime_once(
                db_path, api_url, api_key, feed_id=feed_id, rate_limit_delay=rate_limit
            )

    _scheduler.add_job(
        _guarded_poll,
        "interval",
        seconds=interval,
        id="realtime_poll",
        misfire_grace_time=interval,
    )

    _scheduler.start()

    # Discover stops immediately on first startup
    _scheduler.add_job(_discover, id="discover_startup", misfire_grace_time=60)

    logger.info(
        "Scheduler: discover weekly, daily@03:00, realtime every %ds (%d:00–%d:00) feed=%s",
        interval,
        start_hour,
        end_hour,
        feed_id,
    )


def get_scheduler_status() -> dict:
    if _scheduler is None:
        return {"running": False}
    jobs = []
    for job in _scheduler.get_jobs():
        jobs.append(
            {
                "id": job.id,
                "next_run": str(job.next_run_time) if job.next_run_time else None,
            }
        )
    return {"running": _scheduler.running, "jobs": jobs}
