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

    try:
        for split in ("train", "val"):
            (dataset / "images" / split).mkdir(parents=True)
            (dataset / "labels" / split).mkdir(parents=True)

        assignments = split_samples(samples, args.validation)
        exported = 0
        split_crops = {"train": 0, "val": 0}
        for split, split_samples_list in assignments.items():
            for sample in split_samples_list:
                crop_count = export_sample(sample, split, dataset, events, args.size, exported)
                exported += crop_count
                split_crops[split] += crop_count

        if exported == 0:
            raise ValueError("no valid training crops were exported")

        # JSON is also valid YAML, avoiding another parser dependency while preserving the standard
        # Ultralytics dataset contract.
        dataset_config = {
            # Ultralytics resolves `path` from the process working directory rather than from the
            # directory containing dataset.yaml. Use the absolute staging path so the desktop
            # release and a headless launch resolve the same images and labels.
            "path": str((workspace / "dataset").resolve()),
            "train": "images/train",
            "val": "images/val",
            "nc": len(events),
            "names": {str(event["classId"]): event["name"] for event in events},
        }
        (dataset / "dataset.yaml").write_text(
            json.dumps(dataset_config, indent=2) + "\n", encoding="utf-8"
        )
        event_coverage, warnings = summarize_coverage(assignments, events)
        (dataset / "export.json").write_text(
            json.dumps(
                {
                    "size": args.size,
                    "sampleCount": len(samples),
                    "cropCount": exported,
                    "trainingSamples": len(assignments["train"]),
                    "validationSamples": len(assignments["val"]),
                    "trainingCrops": split_crops["train"],
                    "validationCrops": split_crops["val"],
                    "eventCoverage": event_coverage,
                    "warnings": warnings,
                },
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

        coverage = ", ".join(
            f"{item['name']}={item['trainingSamples']}/{item['validationSamples']}"
            for item in event_coverage
        )
        print(f"COVERAGE train/val frames: {coverage}", flush=True)
        for warning in warnings:
            print(f"WARNING {warning}", flush=True)
        print(
            f"EXPORTED samples={len(samples)} train={len(assignments['train'])} "
            f"val={len(assignments['val'])} crops={exported} size={args.size}",
            flush=True,
        )
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
            require_label=True,
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
    shuffled_indices = list(range(len(samples)))
    random.Random(42).shuffle(shuffled_indices)
    target = round(len(samples) * validation_fraction) if len(samples) > 1 else 0
    target = max(1 if len(samples) > 1 else 0, target)
    target = min(len(samples) - 1, target)

    sample_classes = [set(label["classId"] for label in sample["labels"]) for sample in samples]
    class_samples: dict[int, list[int]] = {}
    for sample_index, class_ids in enumerate(sample_classes):
        for class_id in class_ids:
            class_samples.setdefault(class_id, []).append(sample_index)

    singleton_classes = {class_id for class_id, indices in class_samples.items() if len(indices) == 1}
    fixed_train = {
        sample_index
        for sample_index, class_ids in enumerate(sample_classes)
        if class_ids & singleton_classes
    }
    covered_classes = {class_id for class_id, indices in class_samples.items() if len(indices) > 1}
    validation: set[int] = set()
    validation_counts = {class_id: 0 for class_id in covered_classes}
    rank = {sample_index: index for index, sample_index in enumerate(shuffled_indices)}

    def safe_for_validation(sample_index: int) -> bool:
        return sample_index not in fixed_train and all(
            validation_counts[class_id] + 1 < len(class_samples[class_id])
            for class_id in sample_classes[sample_index] & covered_classes
        )

    def cover(uncovered: set[int]) -> bool:
        if not uncovered:
            return True

        choices: dict[int, list[int]] = {}
        for class_id in uncovered:
            candidates = []
            signatures: set[frozenset[int]] = set()
            for sample_index in class_samples[class_id]:
                signature = frozenset(sample_classes[sample_index])
                if sample_index in validation or signature in signatures or not safe_for_validation(sample_index):
                    continue
                signatures.add(signature)
                candidates.append(sample_index)
            choices[class_id] = candidates

        class_id = min(uncovered, key=lambda item: (len(choices[item]), item))
        candidates = sorted(
            choices[class_id],
            key=lambda sample_index: (
                -len(sample_classes[sample_index] & uncovered),
                rank[sample_index],
            ),
        )
        for sample_index in candidates:
            validation.add(sample_index)
            affected = sample_classes[sample_index] & covered_classes
            for affected_class in affected:
                validation_counts[affected_class] += 1
            if cover({item for item in uncovered if validation_counts[item] == 0}):
                return True
            for affected_class in affected:
                validation_counts[affected_class] -= 1
            validation.remove(sample_index)
        return False

    if not cover(set(covered_classes)):
        missing = sorted(class_id for class_id in covered_classes if validation_counts[class_id] == 0)
        raise ValueError(
            "cannot keep every event with multiple samples in both train and validation while "
            f"keeping frames intact; add samples for class IDs: {missing}"
        )

    for sample_index in shuffled_indices:
        if len(validation) >= target:
            break
        if sample_index not in validation and safe_for_validation(sample_index):
            validation.add(sample_index)
            for class_id in sample_classes[sample_index] & covered_classes:
                validation_counts[class_id] += 1

    return {
        "train": [samples[index] for index in shuffled_indices if index not in validation],
        "val": [samples[index] for index in shuffled_indices if index in validation],
    }


def summarize_coverage(assignments: dict[str, list[dict]], events: list[dict]) -> tuple[list[dict], list[str]]:
    counts = {
        split: {
            event["classId"]: sum(
                any(label["classId"] == event["classId"] for label in sample["labels"])
                for sample in samples
            )
            for event in events
        }
        for split, samples in assignments.items()
    }
    coverage = []
    warnings = []
    for event in events:
        class_id = event["classId"]
        training_samples = counts["train"][class_id]
        validation_samples = counts["val"][class_id]
        sample_count = training_samples + validation_samples
        coverage.append(
            {
                "classId": class_id,
                "name": event["name"],
                "sampleCount": sample_count,
                "trainingSamples": training_samples,
                "validationSamples": validation_samples,
            }
        )
        if sample_count == 0:
            warnings.append(f"'{event['name']}' has no labeled frames.")
        elif sample_count == 1:
            warnings.append(
                f"'{event['name']}' has one labeled frame; it is train-only and cannot be validated."
            )
    if not assignments["val"]:
        warnings.append("The dataset has no validation frames.")
    return coverage, warnings


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
