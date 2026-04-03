import os

from flask import Flask

from app.config import Config
from app.db import close_db, init_db


def create_app(config_class=None):
    app = Flask(__name__)
    app.config.from_object(config_class or Config)

    os.makedirs(os.path.dirname(app.config["DATABASE_PATH"]) or ".", exist_ok=True)

    init_db(app)
    app.teardown_appcontext(close_db)

    from app.routes.api import api_bp
    from app.routes.dashboard import dashboard_bp

    app.register_blueprint(dashboard_bp)
    app.register_blueprint(api_bp, url_prefix="/api")

    if not app.config.get("TESTING"):
        from app.scheduler import init_scheduler

        init_scheduler(app)

    return app
