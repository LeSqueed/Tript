#!/usr/bin/env python3
"""Validate and package a Tript detection model for release publication."""

import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import sys
import zipfile


SAFE_ID = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$")
LFS_HEADER = b"version https://git-lfs.github.com/spec/v1"


def fail(message: str) -> None:
    raise ValueError(message)


def file_metadata(path: Path) -> dict[str, object]:
    digest = hashlib.sha256()
    size = 0
    with path.open("rb") as source:
        for chunk in iter(lambda: source.read(1024 * 1024), b""):
            size += len(chunk)
            digest.update(chunk)
    return {"sizeBytes": size, "sha256": digest.hexdigest()}


def checked_id(value: str, label: str) -> str:
    if not SAFE_ID.fullmatch(value):
        fail(f"{label} must use only letters, digits, '.', '_' or '-' and be at most 64 characters")
    return value


def append_github_outputs(values: dict[str, object]) -> None:
    output_path = os.environ.get("GITHUB_OUTPUT")
    if not output_path:
        return
    with open(output_path, "a", encoding="utf-8") as output:
        for key, value in values.items():
            print(f"{key}={value}", file=output)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--source-dir", required=True, type=Path)
    parser.add_argument("--output-dir", required=True, type=Path)
    parser.add_argument("--game-id", required=True)
    parser.add_argument("--model-api-version", required=True, type=int)
    parser.add_argument("--revision", required=True, type=int)
    parser.add_argument("--catalog-manifest", required=True, type=Path)
    parser.add_argument("--repository", required=True)
    args = parser.parse_args()

    game_id = checked_id(args.game_id, "game ID")
    revision = args.revision
    if revision < 1:
        fail("revision must be a positive integer")
    if args.model_api_version < 1:
        fail("model API version must be a positive integer")

    source_dir = args.source_dir.resolve()
    model_path = source_dir / "model.onnx"
    events_path = source_dir / "events.json"
    manifest_path = source_dir / "manifest.json"
    ocr_detector_path = source_dir / "ocr_detector.onnx"
    ocr_model_path = source_dir / "ocr_model.onnx"
    ocr_dict_path = source_dir / "ocr_dict.txt"
    for path in (events_path, manifest_path):
        if not path.is_file():
            fail(f"required input does not exist: {path}")
    if not model_path.is_file() and not ocr_model_path.is_file():
        fail("at least one of model.onnx or ocr_model.onnx is required")

    if model_path.is_file():
        with model_path.open("rb") as model:
            header = model.read(len(LFS_HEADER))
        if not header:
            fail("model.onnx is empty")
        if header == LFS_HEADER:
            fail("model.onnx is a Git LFS pointer; fetch LFS content before packaging")
    if ocr_model_path.is_file() and not ocr_dict_path.is_file():
        fail("ocr_dict.txt is required when ocr_model.onnx is present")
    if ocr_detector_path.is_file() and not ocr_model_path.is_file():
        fail("ocr_model.onnx is required when ocr_detector.onnx is present")
    for path in (ocr_detector_path, ocr_model_path):
        if not path.is_file():
            continue
        with path.open("rb") as model:
            header = model.read(len(LFS_HEADER))
        if not header:
            fail(f"{path.name} is empty")
        if header == LFS_HEADER:
            fail(f"{path.name} is a Git LFS pointer; fetch LFS content before packaging")

    with events_path.open(encoding="utf-8") as events_file:
        events = json.load(events_file)
    if not isinstance(events, list) or not events:
        fail("events.json must be a non-empty JSON array")
    if any(not isinstance(event, dict) for event in events):
        fail("every events.json entry must be an object")

    with manifest_path.open(encoding="utf-8") as manifest_file:
        manifest = json.load(manifest_file)
    if manifest.get("schemaVersion") != 1:
        fail("manifest schemaVersion must be 1")
    if manifest.get("gameId") != game_id:
        fail("manifest gameId does not match --game-id")
    if manifest.get("modelApiVersion") != args.model_api_version:
        fail("manifest modelApiVersion does not match --model-api-version")

    files = {
        "events.json": file_metadata(events_path),
    }
    if model_path.is_file():
        files["model.onnx"] = file_metadata(model_path)
    if ocr_model_path.is_file() and ocr_dict_path.is_file():
        if ocr_detector_path.is_file():
            files["ocr_detector.onnx"] = file_metadata(ocr_detector_path)
        files["ocr_model.onnx"] = file_metadata(ocr_model_path)
        files["ocr_dict.txt"] = file_metadata(ocr_dict_path)
    package_metadata = {
        "packageFormatVersion": 1,
        "gameId": game_id,
        "modelApiVersion": args.model_api_version,
        "revision": revision,
        "files": files,
    }
    metadata_bytes = (json.dumps(package_metadata, indent=2) + "\n").encode("utf-8")

    args.output_dir.mkdir(parents=True, exist_ok=True)
    asset_name = f"{game_id}-model-api-{args.model_api_version}-revision-{revision}.zip"
    asset_path = (args.output_dir / asset_name).resolve()
    with zipfile.ZipFile(asset_path, "x", compression=zipfile.ZIP_DEFLATED) as package:
        if model_path.is_file():
            package.write(model_path, "model.onnx")
        package.write(events_path, "events.json")
        if ocr_model_path.is_file():
            if ocr_detector_path.is_file():
                package.write(ocr_detector_path, "ocr_detector.onnx")
            package.write(ocr_model_path, "ocr_model.onnx")
            package.write(ocr_dict_path, "ocr_dict.txt")
        package.writestr("package.json", metadata_bytes)

    asset = file_metadata(asset_path)
    with args.catalog_manifest.open(encoding="utf-8") as manifest_file:
        catalog = json.load(manifest_file)
    if catalog.get("schemaVersion") != 1 or not isinstance(catalog.get("games"), list):
        fail("catalog manifest must use schemaVersion 1 and contain a games array")
    game = next((entry for entry in catalog["games"] if entry.get("gameId") == game_id), None)
    if game is None:
        game = {"gameId": game_id, "releases": []}
        catalog["games"].append(game)
    releases = game.get("releases")
    if not isinstance(releases, list):
        fail(f"catalog manifest releases for {game_id} must be an array")
    if any(entry.get("modelApiVersion") == args.model_api_version and entry.get("revision") == revision
           for entry in releases):
        fail(f"model API {args.model_api_version} revision {revision} already exists for {game_id}")
    releases.append({
        "modelApiVersion": args.model_api_version,
        "revision": revision,
        "url": (
            f"https://github.com/{args.repository}/releases/download/model-assets-v1/{asset_name}"
        ),
        "sizeBytes": asset["sizeBytes"],
        "sha256": asset["sha256"],
    })
    releases.sort(key=lambda entry: (entry["modelApiVersion"], entry["revision"]))
    catalog["games"].sort(key=lambda entry: entry["gameId"].casefold())
    args.catalog_manifest.write_text(json.dumps(catalog, indent=2) + "\n", encoding="utf-8")

    result = {
        "asset_name": asset_name,
        "asset_path": asset_path,
        "asset_size_bytes": asset["sizeBytes"],
        "asset_sha256": asset["sha256"],
        "manifest_path": args.catalog_manifest.resolve(),
    }
    append_github_outputs(result)
    print(json.dumps(result, indent=2, default=str))
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except (OSError, ValueError, json.JSONDecodeError, zipfile.BadZipFile) as error:
        print(f"error: {error}", file=sys.stderr)
        sys.exit(1)
