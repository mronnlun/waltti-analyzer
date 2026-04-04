import pytest

from app import create_app
from app.config import TestConfig
from app.db import get_db, upsert_stop


@pytest.fixture
def app(tmp_path):
    db_path = str(tmp_path / "test.db")

    class _TestConfig(TestConfig):
        DATABASE_PATH = db_path
        FEED_ID = "Vaasa"
        DEFAULT_STOP_ID = "Vaasa:309392"

    app = create_app(_TestConfig)
    yield app


@pytest.fixture
def client(app):
    return app.test_client()


@pytest.fixture
def db(app):
    with app.app_context():
        conn = get_db()
        # Seed default test stop so observation FK lookups succeed
        upsert_stop(conn, "Vaasa:309392", "Gerbynmäentie", None, 63.14, 21.57)
        yield conn
