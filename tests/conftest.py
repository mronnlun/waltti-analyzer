import pytest

from app import create_app
from app.config import TestConfig
from app.db import get_db


@pytest.fixture
def app(tmp_path):
    db_path = str(tmp_path / "test.db")

    class _TestConfig(TestConfig):
        DATABASE_PATH = db_path
        FEED_ID = "Vaasa"
        DEFAULT_STOP_ID = "Vaasa:309392"
        API_RATE_LIMIT_DELAY = 0.0

    app = create_app(_TestConfig)
    yield app


@pytest.fixture
def client(app):
    return app.test_client()


@pytest.fixture
def db(app):
    with app.app_context():
        yield get_db()
