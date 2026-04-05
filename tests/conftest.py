import sys
from pathlib import Path

import pytest

# Add the api/ directory to sys.path so tests can import shared modules
sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "api"))

from shared.db import connect, init_db, upsert_stop  # noqa: E402


@pytest.fixture
def db(tmp_path):
    db_path = str(tmp_path / "test.db")
    init_db(db_path)
    conn = connect(db_path)
    # Seed default test stop so observation FK lookups succeed
    upsert_stop(conn, "Vaasa:309392", "Gerbynmäentie", None, 63.14, 21.57)
    yield conn
    conn.close()
