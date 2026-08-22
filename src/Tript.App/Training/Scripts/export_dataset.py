#!/usr/bin/env python3
"""Export full-frame Tript samples into the detector's cropped YOLO dataset format."""

from __future__ import annotations

import argparse
import json
import random
import shutil
import uuid
from dataclasses import dataclass
from pathlib import Path

from PIL import Image, ImageOps


@dataclass(frozen=True)
class Region:
    x: float
    y: float
    w: float
    h: float

    @property
    def right(self) -> float:
        return self.x + self.w

    @property
    def bottom(self) -> float:
        return self.y + self.h


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("workspace", type=Path)
    parser.add_argument("--size", type=int, default=640)
    parser.add_argument("--validation", type=float, default=0.2)
    args = parser.parse_args()

    if args.size <= 0 or not 0 < args.validation < 1:
        raise ValueError("size must be positive and validation must be between 0 and 1")

    workspace = args.workspace.resolve()
    events = load_events(workspace / "events.json")
    samples = load_samples(workspace, events)
    dataset = workspace / f"dataset.export-{uuid.uuid4().hex}"
    existing_dataset = workspace / "dataset"

    try:
        if existing_dataset.exists():
            shutil.copytree(existing_dataset, dataset)
        else:
            for split in ("train", "val"):
                (dataset / "images" / split).mkdir(parents=True)
                (dataset / "labels" / split).mkdir(parents=True)

        captured_samples = [sample for sample in samples if not sample.get("datasetImagePath")]
        assignments = split_samples(captured_samples, args.validation) if captured_samples else {"train": [], "val": []}
        exported = 0
        for split, split_samples_list in assignments.items():
            for sample in split_samples_list:
                exported += export_sample(sample, split, dataset, events, args.size, exported)

        if exported == 0 and not existing_dataset.exists():
            raise ValueError("no valid training crops were exported")

        # JSON is also valid YAML, avoiding another parser dependency while preserving the standard
        # Ultralytics dataset contract.
        dataset_config = {
            "path": ".",
            "train": "images/train",
            "val": "images/val",
            "nc": len(events),
            "names": {str(event["classId"]): event["name"] for event in events},
        }
        (dataset / "dataset.yaml").write_text(
            json.dumps(dataset_config, indent=2) + "\n", encoding="utf-8"
        )
        (dataset / "export.json").write_text(
            json.dumps(
                {"size": args.size, "sampleCount": len(samples), "cropCount": exported},
                indent=2,
            )
            + "\n",
            encoding="utf-8",
        )

        destination = workspace / "dataset"
        backup = workspace / f"dataset.previous-{uuid.uuid4().hex}"
        if destination.exists():
            destination.replace(backup)
        try:
            dataset.replace(destination)
        except Exception:
            if backup.exists() and not destination.exists():
                backup.replace(destination)
            raise
        if backup.exists():
            shutil.rmtree(backup)

        print(f"EXPORTED samples={len(samples)} crops={exported} size={args.size}", flush=True)
        return 0
    except Exception:
        if dataset.exists():
            shutil.rmtree(dataset)
        raise


def load_events(path: Path) -> list[dict]:
    events = json.loads(path.read_text(encoding="utf-8"))
    if not isinstance(events, list) or not events:
        raise ValueError("events.json must contain at least one event")

    ordered = sorted(events, key=lambda event: event["classId"])
    expected = list(range(len(ordered)))
    actual = [event["classId"] for event in ordered]
    if actual != expected:
        raise ValueError(f"events.json class IDs must be contiguous from zero: {actual}")
    if any(not isinstance(event.get("name"), str) or not event["name"] for event in ordered):
        raise ValueError("every event must have a non-empty name")
    return ordered


def load_samples(workspace: Path, events: list[dict]) -> list[dict]:
    samples_dir = workspace / "samples"
    samples = []
    for metadata_path in sorted(samples_dir.glob("*.json")):
        sample = json.loads(metadata_path.read_text(encoding="utf-8"))
        image_path = samples_dir / sample["imageFile"]
        if not image_path.is_file():
            raise ValueError(f"sample image is missing: {image_path}")
        validate_labels(
            sample["labels"],
            sample["imageFile"],
            require_label=not sample.get("datasetImagePath"),
            class_ids={event["classId"] for event in events},
        )
        samples.append(sample)
    if not samples:
        raise ValueError("workspace contains no samples")
    return samples


def validate_labels(
    labels: list[dict],
    image_name: str,
    require_label: bool = True,
    class_ids: set[int] | None = None,
) -> None:
    if require_label and not labels:
        raise ValueError(f"sample has no labels: {image_name}")
    for label in labels:
        if class_ids is not None and label["classId"] not in class_ids:
            raise ValueError(f"sample uses an unknown class ID: {image_name}")
        values = [label[key] for key in ("centerX", "centerY", "width", "height")]
        if any(not isinstance(value, (int, float)) for value in values):
            raise ValueError(f"sample has non-numeric label coordinates: {image_name}")
        left = label["centerX"] - label["width"] / 2
        top = label["centerY"] - label["height"] / 2
        right = label["centerX"] + label["width"] / 2
        bottom = label["centerY"] + label["height"] / 2
        if label["width"] <= 0 or label["height"] <= 0 or min(left, top) < 0 or max(right, bottom) > 1:
            raise ValueError(f"sample label is outside the image: {image_name}")


def split_samples(samples: list[dict], validation_fraction: float) -> dict[str, list[dict]]:
    shuffled = list(samples)
    random.Random(42).shuffle(shuffled)
    validation_count = round(len(shuffled) * validation_fraction) if len(shuffled) > 1 else 0
    validation_count = max(1 if len(shuffled) > 1 else 0, validation_count)
    validation_count = min(len(shuffled) - 1, validation_count)
    return {"train": shuffled[validation_count:], "val": shuffled[:validation_count]}


def export_sample(sample: dict, split: str, dataset: Path, events: list[dict], size: int, sequence: int) -> int:
    samples_dir = dataset.parent / "samples"
    image_path = samples_dir / sample["imageFile"]
    with Image.open(image_path) as source:
        image = ImageOps.exif_transpose(source).convert("RGB")
        groups = crop_groups(sample["labels"], events)
        for group_index, (crop_region, labels) in enumerate(groups):
            crop = crop_image(image, crop_region)
            if crop_region is not None:
                left, top, right, bottom = crop_bounds(image.size, crop_region)
                crop_region = Region(
                    left / image.width,
                    top / image.height,
                    (right - left) / image.width,
                    (bottom - top) / image.height,
                )
            crop = ImageOps.grayscale(crop).convert("RGB").resize((size, size), Image.Resampling.LANCZOS)
            stem = f"{sequence:06d}_{group_index:02d}"
            crop.save(dataset / "images" / split / f"{stem}.png", format="PNG")
            write_labels(dataset / "labels" / split / f"{stem}.txt", labels, crop_region)
    return len(groups)


def crop_groups(labels: list[dict], events: list[dict]) -> list[tuple[Region | None, list[dict]]]:
    by_class = {event["classId"]: event for event in events}
    groups: list[tuple[Region | None, list[dict]]] = []
    for label in labels:
        event = by_class.get(label["classId"])
        region = event_region(event)
        groups.append((region, [label]))

    # Merge until stable. A merged union can overlap a group that was checked earlier, so a single
    # first-match pass does not match the detector's order-independent grouping.
    changed = True
    while changed:
        changed = False
        for index, (left, left_labels) in enumerate(groups):
            for other in range(index + 1, len(groups)):
                right, right_labels = groups[other]
                if left is None or right is None:
                    if left is not None or right is not None:
                        continue
                elif not overlaps(left, right):
                    continue
                groups[index] = (merge_regions(left, right), left_labels + right_labels)
                groups.pop(other)
                changed = True
                break
            if changed:
                break
    return groups


def event_region(event: dict | None) -> Region | None:
    if event is None:
        raise ValueError("sample references an unknown class")
    keys = ("screenRegionX", "screenRegionY", "screenRegionW", "screenRegionH")
    values = [event.get(key) for key in keys]
    if any(value is None for value in values):
        return None
    region = Region(*values)
    if region.w <= 0 or region.h <= 0 or region.x < 0 or region.y < 0 or region.right > 1 or region.bottom > 1:
        raise ValueError("event screen region must be inside the normalized frame")
    return region


def overlaps(left: Region, right: Region) -> bool:
    return left.x < right.right and right.x < left.right and left.y < right.bottom and right.y < left.bottom


def merge_regions(left: Region | None, right: Region | None) -> Region | None:
    if left is None or right is None:
        return None
    x = min(left.x, right.x)
    y = min(left.y, right.y)
    return Region(x, y, max(left.right, right.right) - x, max(left.bottom, right.bottom) - y)


def crop_image(image: Image.Image, region: Region | None) -> Image.Image:
    if region is None:
        return image
    left, top, right, bottom = crop_bounds(image.size, region)
    return image.crop((left, top, right, bottom))


def crop_bounds(size: tuple[int, int], region: Region) -> tuple[int, int, int, int]:
    width, height = size
    left = max(0, min(width - 1, int(region.x * width)))
    top = max(0, min(height - 1, int(region.y * height)))
    right = min(width, left + int(region.w * width))
    bottom = min(height, top + int(region.h * height))
    if right <= left or bottom <= top:
        raise ValueError("event screen region is smaller than one source pixel")
    return left, top, right, bottom


def write_labels(path: Path, labels: list[dict], region: Region | None) -> None:
    lines = []
    for label in labels:
        if region is None:
            center_x, center_y = label["centerX"], label["centerY"]
            width, height = label["width"], label["height"]
        else:
            center_x = (label["centerX"] - region.x) / region.w
            center_y = (label["centerY"] - region.y) / region.h
            width = label["width"] / region.w
            height = label["height"] / region.h
        lines.append(f"{label['classId']} {center_x:.6f} {center_y:.6f} {width:.6f} {height:.6f}")
    path.write_text("\n".join(lines) + "\n", encoding="utf-8")


if __name__ == "__main__":
    raise SystemExit(main())
