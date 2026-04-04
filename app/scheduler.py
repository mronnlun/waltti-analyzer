import logging
from zoneinfo import ZoneInfo

from apscheduler.schedulers.background import BackgroundScheduler

from app.collector import collect_daily, discover_stops, poll_realtime_once

logger = logging.getLogger(__name__)
_scheduler_logger_configured = False

_scheduler: BackgroundScheduler | None = None

HELSINKI_TZ = ZoneInfo("Europe/Helsinki")


def _ensure_scheduler_debug_logging() -> None:
    global _scheduler_logger_configured

    if _scheduler_logger_configured:
        return

    handler = logging.StreamHandler()
    handler.setLevel(logging.DEBUG)
    handler.setFormatter(logging.Formatter("%(asctime)s %(levelname)s [%(name)s] %(message)s"))
    logger.addHandler(handler)
    logger.setLevel(logging.DEBUG)
    logger.propagate = False
    _scheduler_logger_configured = True


def init_scheduler(app):
    global _scheduler

    if _scheduler is not None:
        return

    _ensure_scheduler_debug_logging()

    api_url = app.config["DIGITRANSIT_API_URL"]
    api_key = app.config["DIGITRANSIT_API_KEY"]
    feed_id = app.config["FEED_ID"]
    db_path = app.config["DATABASE_PATH"]
    if not api_key:
        logger.warning("DIGITRANSIT_API_KEY not set — scheduler disabled")
        return

    _scheduler = BackgroundScheduler(
        timezone=HELSINKI_TZ,
        job_defaults={"coalesce": True, "max_instances": 1},
    )

    # Discover stops on startup and weekly
    def _discover():
        logger.debug("Scheduler run starting: discover_stops feed=%s", feed_id)
        result = discover_stops(db_path, api_url, api_key, feed_id)
        logger.debug("Scheduler run finished: discover_stops result=%s", result)

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
        logger.debug(
            "Scheduler run starting: daily_collection feed=%s",
            feed_id,
        )
        result = collect_daily(
            db_path,
            api_url,
            api_key,
            feed_id=feed_id,
        )
        logger.debug("Scheduler run finished: daily_collection result=%s", result)

    _scheduler.add_job(
        _daily,
        "cron",
        hour=3,
        minute=0,
        id="daily_collection",
        misfire_grace_time=3600,
    )

    # Evening collection at 23:00 — captures accumulated realtime delay data
    _scheduler.add_job(
        _daily,
        "cron",
        hour=23,
        minute=0,
        id="evening_collection",
        misfire_grace_time=3600,
    )

    # Realtime polling every 10 minutes (interval, not clock-aligned)
    def _realtime_poll():
        logger.debug(
            "Scheduler run starting: realtime_poll feed=%s",
            feed_id,
        )
        result = poll_realtime_once(db_path, api_url, api_key, feed_id=feed_id)
        logger.debug("Scheduler run finished: realtime_poll result=%s", result)

    _scheduler.add_job(
        _realtime_poll,
        "interval",
        minutes=3,
        id="realtime_poll",
        misfire_grace_time=120,
    )

    _scheduler.start()

    # Run discover + first realtime poll immediately on startup
    _scheduler.add_job(_discover, id="discover_startup", misfire_grace_time=60)
    _scheduler.add_job(_realtime_poll, id="realtime_startup", misfire_grace_time=60)

    logger.info(
        "Scheduler: discover weekly, daily@03:00+23:00,"
        " realtime every 3min around the clock, feed=%s",
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
