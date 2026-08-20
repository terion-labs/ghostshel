#!/usr/bin/env python3
"""Generate the reviewed Maven byte lock from an isolated local repository."""

from __future__ import annotations

import hashlib
import json
import os
import pathlib
import sys


CENTRAL_REPOSITORY = "https://repo.maven.apache.org/maven2/"
LOCK_FORMAT_VERSION = 1


def sha256_file(path: pathlib.Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def main() -> int:
    if len(sys.argv) != 3:
        print(f"Usage: {sys.argv[0]} REPOSITORY OUTPUT", file=sys.stderr)
        return 64

    repository = pathlib.Path(sys.argv[1]).resolve(strict=True)
    output = pathlib.Path(sys.argv[2])
    artifacts: list[dict[str, object]] = []
    for path in sorted(repository.rglob("*")):
        if path.is_symlink():
            raise ValueError(f"Maven repository contains a symlink: {path}")
        if not path.is_file() or path.suffix not in {".jar", ".pom"}:
            continue
        relative = path.relative_to(repository).as_posix()
        artifacts.append(
            {
                "path": relative,
                "size": path.stat().st_size,
                "sha256": sha256_file(path),
            }
        )

    artifacts.sort(key=lambda artifact: str(artifact["path"]))

    if not artifacts:
        raise ValueError("Maven repository contains no lockable content")

    document = {
        "formatVersion": LOCK_FORMAT_VERSION,
        "repository": CENTRAL_REPOSITORY,
        "artifacts": artifacts,
    }
    output.parent.mkdir(parents=True, exist_ok=True)
    temporary = output.with_name(f".{output.name}.{os.getpid()}.tmp")
    temporary.write_text(
        json.dumps(document, indent=2, ensure_ascii=True) + "\n",
        encoding="utf-8",
    )
    os.replace(temporary, output)
    print(f"Locked {len(artifacts)} Maven files in {output}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
