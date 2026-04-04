import logging
from datetime import datetime
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
    interval = app.config["POLL_INTERVAL_SECONDS"]
    start_hour = app.config["POLL_START_HOUR"]
    end_hour = app.config["POLL_END_HOUR"]
    if not api_key:
        logger.warning("DIGITRANSIT_API_KEY not set — scheduler disabled")
        return

    _scheduler = BackgroundScheduler(timezone=HELSINKI_TZ)

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

    # Realtime polling with hour guard — all stops
    def _guarded_poll():
        now = datetime.now(HELSINKI_TZ)
        logger.debug(
            "Scheduler run starting: realtime_poll feed=%s now=%s window=%02d:00-%02d:00",
            feed_id,
            now.isoformat(),
            start_hour,
            end_hour,
        )
        if start_hour <= now.hour < end_hour:
            result = poll_realtime_once(db_path, api_url, api_key, feed_id=feed_id)
            logger.debug("Scheduler run finished: realtime_poll result=%s", result)
            return

        logger.debug(
            "Scheduler run skipped: realtime_poll outside active hours"
            " now_hour=%02d window=%02d-%02d",
            now.hour,
            start_hour,
            end_hour,
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
        "Scheduler: discover weekly, daily@03:00+23:00, realtime every %ds (%d:00–%d:00) feed=%s",
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
