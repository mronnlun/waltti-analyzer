import os

from dotenv import load_dotenv

load_dotenv()


class Config:
    DIGITRANSIT_API_KEY = os.environ.get("DIGITRANSIT_API_KEY", "")
    DIGITRANSIT_API_URL = os.environ.get(
        "DIGITRANSIT_API_URL",
        "https://api.digitransit.fi/routing/v2/waltti/gtfs/v1",
    )
    FEED_ID = os.environ.get("FEED_ID", "Vaasa")
    DEFAULT_STOP_ID = os.environ.get("DEFAULT_STOP_ID", "Vaasa:309392")
    DATABASE_PATH = os.environ.get("DATABASE_PATH", "data/waltti.db")
    POLL_INTERVAL_SECONDS = int(os.environ.get("POLL_INTERVAL_SECONDS", "60"))
    POLL_START_HOUR = int(os.environ.get("POLL_START_HOUR", "5"))
    POLL_END_HOUR = int(os.environ.get("POLL_END_HOUR", "24"))
    API_RATE_LIMIT_DELAY = float(os.environ.get("API_RATE_LIMIT_DELAY", "0.1"))


class TestConfig(Config):
    TESTING = True
    DATABASE_PATH = ""  # Set dynamically in conftest
    DIGITRANSIT_API_KEY = "test-key"
