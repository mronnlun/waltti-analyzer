import logging
from datetime import datetime
from zoneinfo import ZoneInfo

from apscheduler.schedulers.background import BackgroundScheduler

from app.collector import collect_daily, poll_realtime_once

logger = logging.getLogger(__name__)

_scheduler: BackgroundScheduler | None = None

HELSINKI_TZ = ZoneInfo("Europe/Helsinki")


def init_scheduler(app):
    global _scheduler

    if _scheduler is not None:
        return

    api_url = app.config["DIGITRANSIT_API_URL"]
    api_key = app.config["DIGITRANSIT_API_KEY"]
    stop_id = app.config["TARGET_STOP_ID"]
    db_path = app.config["DATABASE_PATH"]
    interval = app.config["POLL_INTERVAL_SECONDS"]
    start_hour = app.config["POLL_START_HOUR"]
    end_hour = app.config["POLL_END_HOUR"]

    if not api_key:
        logger.warning("DIGITRANSIT_API_KEY not set — scheduler disabled")
        return

    _scheduler = BackgroundScheduler(timezone=HELSINKI_TZ)

    # Daily collection at 03:00 Helsinki time
    _scheduler.add_job(
        collect_daily,
        "cron",
        hour=3,
        minute=0,
        args=[db_path, api_url, api_key, stop_id],
        id="daily_collection",
        misfire_grace_time=3600,
    )

    # Realtime polling with hour guard
    def _guarded_poll():
        now = datetime.now(HELSINKI_TZ)
        if start_hour <= now.hour < end_hour:
            poll_realtime_once(db_path, api_url, api_key, stop_id)

    _scheduler.add_job(
        _guarded_poll,
        "interval",
        seconds=interval,
        id="realtime_poll",
        misfire_grace_time=interval,
    )

    _scheduler.start()
    logger.info(
        "Scheduler started: daily at 03:00, realtime every %ds (%d:00–%d:00)",
        interval,
        start_hour,
        end_hour,
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
