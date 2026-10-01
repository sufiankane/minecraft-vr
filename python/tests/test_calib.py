from calib import SCHEMA_VERSION


def test_schema_version() -> None:
    assert SCHEMA_VERSION == 1
