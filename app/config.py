import os
import secrets

from dotenv import load_dotenv

load_dotenv()


class Config:
    SECRET_KEY = os.environ.get("SECRET_KEY", secrets.token_hex(32))
    DIGITRANSIT_API_KEY = os.environ.get("DIGITRANSIT_API_KEY", "")
    DIGITRANSIT_API_URL = os.environ.get(
        "DIGITRANSIT_API_URL",
        "https://api.digitransit.fi/routing/v2/waltti/gtfs/v1",
    )
    TARGET_STOP_ID = os.environ.get("TARGET_STOP_ID", "Vaasa:309392")
    DATABASE_PATH = os.environ.get("DATABASE_PATH", "data/waltti.db")
    POLL_INTERVAL_SECONDS = int(os.environ.get("POLL_INTERVAL_SECONDS", "30"))
    POLL_START_HOUR = int(os.environ.get("POLL_START_HOUR", "5"))
    POLL_END_HOUR = int(os.environ.get("POLL_END_HOUR", "24"))


class TestConfig(Config):
    TESTING = True
    DATABASE_PATH = ":memory:"
    DIGITRANSIT_API_KEY = "test-key"
