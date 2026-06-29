#!/usr/bin/env python3

import argparse
import requests
import os
import re  # Hyperion: engine version parsed from Version.props (see get_engine_version)
from typing import Iterable

PUBLISH_TOKEN = os.environ["PUBLISH_TOKEN"]
VERSION = os.environ["GITHUB_SHA"]

RELEASE_DIR = "release"

#
# CONFIGURATION PARAMETERS
# Forks should change these to publish to their own infrastructure.
#
# Hyperion: point at our own Robust.Cdn edge (was cdn.goobstation.com / "Monolith").
# Overridable via env so a LAN publish can target the CDN directly (no edge round-trip).
ROBUST_CDN_URL = os.environ.get("ROBUST_CDN_URL", "https://cdn.hyperionsector.com/")
FORK_ID = os.environ.get("FORK_ID", "hyperion")

def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--fork-id", default=FORK_ID)

    args = parser.parse_args()
    fork_id = args.fork_id

    session = requests.Session()
    session.headers = {
        "Authorization": f"Bearer {PUBLISH_TOKEN}",
    }

    print(f"Starting publish on Robust.Cdn for version {VERSION}")

    data = {
        "version": VERSION,
        "engineVersion": get_engine_version(),
    }
    headers = {
        "Content-Type": "application/json"
    }
    resp = session.post(f"{ROBUST_CDN_URL}fork/{fork_id}/publish/start", json=data, headers=headers)
    resp.raise_for_status()
    print("Publish successfully started, adding files...")

    for file in get_files_to_publish():
        print(f"Publishing {file}")
        with open(file, "rb") as f:
            headers = {
                "Content-Type": "application/octet-stream",
                "Robust-Cdn-Publish-File": os.path.basename(file),
                "Robust-Cdn-Publish-Version": VERSION
            }
            resp = session.post(f"{ROBUST_CDN_URL}fork/{fork_id}/publish/file", data=f, headers=headers)

        resp.raise_for_status()

    print("Successfully pushed files, finishing publish...")

    data = {
        "version": VERSION
    }
    headers = {
        "Content-Type": "application/json"
    }
    resp = session.post(f"{ROBUST_CDN_URL}fork/{fork_id}/publish/finish", json=data, headers=headers)
    resp.raise_for_status()

    print("SUCCESS!")


def get_files_to_publish() -> Iterable[str]:
    for file in os.listdir(RELEASE_DIR):
        yield os.path.join(RELEASE_DIR, file)


# Hyperion: read the engine version from Version.props instead of `git describe --tags`.
# CI does a shallow submodule checkout with no tags, so git-describe fails; the props
# file is the authoritative version anyway (Tools/version.py writes it).
def get_engine_version() -> str:
    props = os.path.join("RobustToolbox", "MSBuild", "Robust.Engine.Version.props")
    with open(props, encoding="UTF-8") as f:
        match = re.search(r"<Version>(.*?)</Version>", f.read())
    assert match is not None, f"no <Version> in {props}"
    return match.group(1).strip()


if __name__ == '__main__':
    main()
