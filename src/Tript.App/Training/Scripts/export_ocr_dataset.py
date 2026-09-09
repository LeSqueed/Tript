#!/usr/bin/env python3
"""Build the OCR recogniser's fine-tune dataset from a Tript workspace.

Training crops come from three sources:

1. Object-detection labels of a pre-conversion sample set (``--object-samples``). Each kill-feed
   row carries an elimination-icon box plus an ability-keyword box whose class *name* matches an
   OCR event, so the feed-line strip and its meaning are both known.
2. ``ocrRegions`` in the workspace samples: a free-form box the user drew on a frame plus the
   exact text they read inside it (direct ground truth).
3. Synthetic feed lines rendered in a condensed bold face over real empty-feed backgrounds, with
   random owner names, italic shear, drop shadow, the red banner, and a faded variant.

Every crop is labelled with the canonical phrase (``ELIMINATED SENTRY TURRET``,
``PLAY OF THE GAME`` …) — the owner name is deliberately dropped, so labels are deterministic and
the recogniser learns to skip the variable span.

Output: ``dataset/ocr/{images/, labels.tsv, character_dict.txt, export.json}``.
"""

from __future__ import annotations

import argparse
import json
import random
import re
import shutil
import unicodedata
from dataclasses import dataclass
from pathlib import Path

from PIL import Image, ImageDraw, ImageEnhance, ImageFilter, ImageFont, ImageOps

# Ability names the OCR template hides inside a {...AndType} capture. Override with --ability-names.
OVERWATCH_ABILITY_NAMES = {
    "Turret": "SENTRY TURRET",
    "Mine": "VENOM MINE",
    "Proximity Mine": "PROXIMITY MINE",
}

CROP_HEIGHT = 48
CROP_WIDTH = 512
PAD_VALUE = 127
WINDOWS_FONTS = Path("C:/Windows/Fonts")
FONT_CANDIDATES = ["impact.ttf", "bahnschrift.ttf", "arialbd.ttf"]

NAME_WORDS = [
    "AMON", "OKASHI", "CASTLER", "STINKIE", "HELOISE", "MAXIMUS", "KENPO", "JOHNWATER",
    "RAREPEPE", "STRYDE", "KYVRAA", "BRKLYN", "CRUMDUDDLER", "PANTSUUU", "CAESURA", "LORIC",
    "SVACINA", "ROXAS", "DAVIDSHO", "FLAMBAYNE", "SYRON", "VALAARK", "DAN", "MOLL", "WIST",
]


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


@dataclass(frozen=True)
class Phrase:
    label: str          # canonical text every crop for this event is labelled with
    kind: str           # "elimination" | "literal"
    region: Region | None
    render_prefix: str  # synthetic: text before the owner name
    render_suffix: str  # synthetic: text after the owner name


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("workspace", type=Path)
    parser.add_argument("--object-samples", type=Path, default=None,
                        help="a pre-conversion sample dir whose object labels name the OCR events")
    parser.add_argument("--synthetic-per-phrase", type=int, default=400)
    parser.add_argument("--validation", type=float, default=0.15)
    parser.add_argument("--ability-names", default="",
                        help='comma list, e.g. "Turret=SENTRY TURRET,Mine=VENOM MINE"')
    parser.add_argument("--seed", type=int, default=1234)
    args = parser.parse_args()

    if not 0 < args.validation < 0.5 or args.synthetic_per_phrase < 0:
        raise ValueError("validation must be in (0,0.5) and synthetic-per-phrase non-negative")

    workspace = args.workspace.resolve()
    rng = random.Random(args.seed)

    events = json.loads((workspace / "events.json").read_text(encoding="utf-8"))
    groups = {g["id"]: g for g in load_json_list(workspace / "regionGroups.json")}

    overrides = dict(OVERWATCH_ABILITY_NAMES)
    for pair in (p.strip() for p in args.ability_names.split(",") if p.strip()):
        name, _, phrase = pair.partition("=")
        overrides[name.strip()] = phrase.strip()

    phrases = build_phrases(events, groups, overrides)
    if not phrases:
        raise ValueError("events.json has no OCR events with a usable template")
    print("PHRASES " + json.dumps({n: p.label for n, p in phrases.items()}), flush=True)

    staging = workspace / f"dataset.ocr-export-{rng.getrandbits(48):012x}"
    images = staging / "images"
    images.mkdir(parents=True)
    records: list[tuple[str, str, str]] = []  # (relative image path, label, "real"|"synthetic")

    try:
        backgrounds: list[Image.Image] = []
        if args.object_samples is not None:
            real, backgrounds = harvest_object_crops(
                args.object_samples.resolve(), events, phrases, images)
            records.extend(real)
            print(f"REAL object-crops={len(real)} backgrounds={len(backgrounds)}", flush=True)

        region_crops = harvest_region_crops(workspace, images)
        records.extend(region_crops)
        if region_crops:
            print(f"REAL region-crops={len(region_crops)}", flush=True)

        if args.synthetic_per_phrase:
            synth = render_synthetic(phrases, backgrounds, args.synthetic_per_phrase, images, rng)
            records.extend(synth)
            print(f"SYNTHETIC crops={len(synth)}", flush=True)

        if not records:
            raise ValueError("no OCR training crops were produced")

        charset = sorted({ch for _, label, _ in records for ch in label if ch != " "})
        (staging / "character_dict.txt").write_text("\n".join(charset) + "\n", encoding="utf-8")

        rng.shuffle(records)
        records.sort(key=lambda r: 0 if r[2] == "real" else 1)  # real first for validation
        val_target = max(1, min(len(records) - 1, round(len(records) * args.validation)))
        lines = [
            f"{'val' if i < val_target else 'train'}\t{rel}\t{label}"
            for i, (rel, label, _) in enumerate(records)
        ]
        rng.shuffle(lines)
        (staging / "labels.tsv").write_text("\n".join(lines) + "\n", encoding="utf-8")

        coverage: dict[str, int] = {}
        for _, label, _ in records:
            coverage[label] = coverage.get(label, 0) + 1
        real_count = sum(1 for _, _, k in records if k == "real")
        (staging / "export.json").write_text(json.dumps({
            "cropHeight": CROP_HEIGHT, "cropWidth": CROP_WIDTH,
            "total": len(records), "real": real_count, "synthetic": len(records) - real_count,
            "validation": val_target, "characters": len(charset), "coverage": coverage,
        }, indent=2) + "\n", encoding="utf-8")

        destination = workspace / "dataset" / "ocr"
        destination.parent.mkdir(parents=True, exist_ok=True)
        if destination.exists():
            shutil.rmtree(destination)
        staging.replace(destination)
        print(f"EXPORTED total={len(records)} real={real_count} "
              f"synthetic={len(records) - real_count} chars={len(charset)}", flush=True)
        return 0
    except Exception:
        if staging.exists():
            shutil.rmtree(staging)
        raise


def load_json_list(path: Path) -> list:
    return json.loads(path.read_text(encoding="utf-8")) if path.is_file() else []


def normalize(text: str) -> str:
    out: list[str] = []
    pending_space = False
    for ch in unicodedata.normalize("NFD", text).upper():
        if unicodedata.category(ch) == "Mn":
            continue
        if ("A" <= ch <= "Z") or ("0" <= ch <= "9") or ch == "'":
            if pending_space and out:
                out.append(" ")
            out.append(ch)
            pending_space = False
        else:
            pending_space = bool(out)
    return "".join(out)


def event_region(event: dict, groups: dict[int, dict]) -> Region | None:
    source = groups.get(event.get("regionGroupId")) or event
    values = [source.get(k) for k in
              ("screenRegionX", "screenRegionY", "screenRegionW", "screenRegionH")]
    if any(v is None for v in values):
        return None
    return Region(*(float(v) for v in values))


def build_phrases(events: list[dict], groups: dict[int, dict],
                  overrides: dict[str, str]) -> dict[str, Phrase]:
    result: dict[str, Phrase] = {}
    for event in events:
        if event.get("detectionKind") != "Ocr":
            continue
        patterns = (event.get("ocr") or {}).get("patterns") or []
        template = next((p["template"] for p in patterns if p.get("template")), "")
        if not template:
            continue
        name = event.get("name", "")
        region = event_region(event, groups)
        parts = re.split(r"\{[^}]*\}", template)
        if len(parts) > 1:
            prefix, suffix = normalize(parts[0]), normalize(parts[-1])
            label = f"{prefix} {overrides.get(name, suffix)}".strip()
            result[name] = Phrase(label, "elimination", region,
                                  render_prefix=f"{prefix} ".lstrip(), render_suffix=f" {suffix}".rstrip())
        else:
            label = normalize(template)
            result[name] = Phrase(label, "literal", region, render_prefix="", render_suffix=label)
    return result


def harvest_object_crops(samples_dir: Path, ocr_events: list[dict], phrases: dict[str, Phrase],
                         images: Path) -> tuple[list, list]:
    object_events = json.loads((samples_dir.parent / "events.json").read_text(encoding="utf-8"))
    class_name = {e["classId"]: e["name"] for e in object_events if e.get("classId", -1) >= 0}
    icon_classes = {c for c, n in class_name.items() if n == "Elimination"}
    ability_classes = {c for c, n in class_name.items()
                       if n in phrases and phrases[n].kind == "elimination"}
    literal_classes = {c for c, n in class_name.items()
                       if n in phrases and phrases[n].kind == "literal"}
    feed_phrase = next((p for p in phrases.values() if p.kind == "elimination"), None)

    records: list[tuple[str, str, str]] = []
    backgrounds: list[Image.Image] = []
    seq = 0
    for meta_path in sorted(samples_dir.glob("*.json")):
        meta = json.loads(meta_path.read_text(encoding="utf-8"))
        image_path = samples_dir / meta["imageFile"]
        if not image_path.is_file():
            continue
        labels = meta.get("labels") or []
        icons = [l for l in labels if l.get("classId") in icon_classes]
        abilities = [l for l in labels if l.get("classId") in ability_classes]
        literals = [l for l in labels if l.get("classId") in literal_classes]

        if not abilities and not literals:
            if feed_phrase and feed_phrase.region and len(backgrounds) < 60:
                with Image.open(image_path) as src:
                    r = feed_phrase.region
                    bg = crop_strip(ImageOps.exif_transpose(src).convert("RGB"),
                                    r.x, r.y, r.right, r.y + r.h * 0.28)
                    if bg is not None:
                        backgrounds.append(bg.copy())
            continue

        with Image.open(image_path) as src:
            image = ImageOps.exif_transpose(src).convert("RGB")
            for ability in abilities:
                icon = nearest_icon(ability, icons)
                if icon is None:
                    continue
                row_cy = (ability["centerY"] + icon["centerY"]) / 2
                line_h = max(ability["height"], icon["height"]) * 1.3
                # Left edge on the icon (the true start of the row), not the ability box, so the
                # full "ELIMINATED <owner> <ability>" strip is captured for narrow ability boxes.
                crop = crop_strip(image,
                                  icon["centerX"] - icon["width"] / 2 - 0.003, row_cy - line_h / 2,
                                  ability["centerX"] + ability["width"] * 0.6, row_cy + line_h / 2)
                if crop is None:
                    continue
                fit_crop(crop).save(images / f"real_{seq:05d}.png")
                records.append((f"images/real_{seq:05d}.png", phrases[class_name[ability["classId"]]].label, "real"))
                seq += 1

            for literal in literals:
                crop = crop_strip(image,
                                  literal["centerX"] - literal["width"] * 0.75,
                                  literal["centerY"] - literal["height"] * 0.9,
                                  literal["centerX"] + literal["width"] * 0.75,
                                  literal["centerY"] + literal["height"] * 0.9)
                if crop is None:
                    continue
                fit_crop(crop).save(images / f"real_{seq:05d}.png")
                records.append((f"images/real_{seq:05d}.png", phrases[class_name[literal["classId"]]].label, "real"))
                seq += 1

    return records, backgrounds


def nearest_icon(ability: dict, icons: list[dict]) -> dict | None:
    best, best_dy = None, 1e9
    limit = max(ability["height"], 0.02) * 1.6
    for icon in icons:
        if icon["centerX"] >= ability["centerX"]:
            continue
        dy = abs(icon["centerY"] - ability["centerY"])
        if dy < best_dy and dy < limit:
            best, best_dy = icon, dy
    return best


def harvest_region_crops(workspace: Path, images: Path) -> list:
    samples_dir = workspace / "samples"
    if not samples_dir.is_dir():
        return []
    records: list[tuple[str, str, str]] = []
    seq = 0
    for meta_path in sorted(samples_dir.glob("*.json")):
        meta = json.loads(meta_path.read_text(encoding="utf-8"))
        regions = meta.get("ocrRegions") or []
        image_path = samples_dir / meta["imageFile"]
        if not regions or not image_path.is_file():
            continue
        with Image.open(image_path) as src:
            image = ImageOps.exif_transpose(src).convert("RGB")
            for region in regions:
                label = normalize(region.get("text", ""))
                if not label:
                    continue
                x, y = float(region.get("x", 0)), float(region.get("y", 0))
                crop = crop_strip(image, x, y, x + float(region.get("width", 0)),
                                  y + float(region.get("height", 0)))
                if crop is None:
                    continue
                fit_crop(crop).save(images / f"region_{seq:05d}.png")
                records.append((f"images/region_{seq:05d}.png", label, "real"))
                seq += 1
    return records


def crop_strip(image: Image.Image, x0: float, y0: float, x1: float, y1: float) -> Image.Image | None:
    iw, ih = image.size
    left, top = max(0, int(x0 * iw)), max(0, int(y0 * ih))
    right, bottom = min(iw, int(x1 * iw)), min(ih, int(y1 * ih))
    if right - left < 12 or bottom - top < 6:
        return None
    return image.crop((left, top, right, bottom))


def fit_crop(crop: Image.Image) -> Image.Image:
    # Match the runtime recogniser (PaddleOcrRecognizer.PrepareInput): keep colour, scale to the
    # input height, squash to the input width if the natural width overruns, gray-pad the rest.
    natural = max(1, round(crop.width * CROP_HEIGHT / crop.height))
    width = min(natural, CROP_WIDTH)
    resized = crop.convert("RGB").resize((width, CROP_HEIGHT), Image.Resampling.LANCZOS)
    canvas = Image.new("RGB", (CROP_WIDTH, CROP_HEIGHT), (PAD_VALUE, PAD_VALUE, PAD_VALUE))
    canvas.paste(resized, (0, 0))
    return canvas


def render_synthetic(phrases: dict[str, Phrase], backgrounds: list[Image.Image], per_phrase: int,
                     images: Path, rng: random.Random) -> list:
    font_path = next((str(WINDOWS_FONTS / f) for f in FONT_CANDIDATES
                      if (WINDOWS_FONTS / f).is_file()), None)
    records: list[tuple[str, str, str]] = []
    seq = 0
    for phrase in phrases.values():
        for _ in range(per_phrase):
            if phrase.kind == "elimination":
                owner = rng.choice(NAME_WORDS) + ("'S" if rng.random() < 0.7 else "")
                visible = f"{phrase.render_prefix}{owner}{phrase.render_suffix}"
            else:
                visible = phrase.label
            render_one(visible, phrase.kind, backgrounds, font_path, rng).save(images / f"synth_{seq:05d}.png")
            records.append((f"images/synth_{seq:05d}.png", phrase.label, "synthetic"))
            seq += 1
    return records


def render_one(text: str, kind: str, backgrounds: list[Image.Image], font_path: str | None,
               rng: random.Random) -> Image.Image:
    size = rng.randint(30, 40)
    font = ImageFont.truetype(font_path, size) if font_path else ImageFont.load_default(size)
    pad = 14
    tw = int(ImageDraw.Draw(Image.new("RGB", (4, 4))).textlength(text, font=font))
    # A trailing score box like the real feed ("… TURRET  100"); the label omits it so the model
    # learns to stop at the phrase.
    score = str(rng.choice([100, 100, 100, 47, 6, 88, 150])) if kind == "elimination" and rng.random() < 0.6 else ""
    score_w = int(ImageDraw.Draw(Image.new("RGB", (4, 4))).textlength(score, font=font)) + 20 if score else 0
    tile_w, tile_h = tw + pad * 2 + score_w, size + 10 + pad

    if backgrounds and rng.random() < 0.8:
        tile = rng.choice(backgrounds).resize((tile_w, tile_h), Image.Resampling.LANCZOS).convert("RGB")
    else:
        base = rng.randint(20, 70)
        tile = Image.new("RGB", (tile_w, tile_h), (base, base, base + rng.randint(0, 20)))

    if kind == "elimination":
        red = (200 + rng.randint(-30, 30), 30 + rng.randint(-15, 25), 45 + rng.randint(-15, 25))
        overlay = Image.new("RGBA", tile.size, (0, 0, 0, 0))
        ImageDraw.Draw(overlay).rounded_rectangle(
            [2, 2, tile_w - 3, tile_h - 3], radius=6, fill=red + (rng.randint(120, 200),))
        tile = Image.alpha_composite(tile.convert("RGBA"), overlay).convert("RGB")

    draw = ImageDraw.Draw(tile)
    x, y = pad, pad // 2
    shadow = rng.randint(1, 2)
    draw.text((x + shadow, y + shadow), text, font=font, fill=(0, 0, 0))
    draw.text((x, y), text, font=font, fill=(235 + rng.randint(-20, 20),) * 3)
    if score:
        sx = pad + tw + 12
        draw.rectangle([sx, y, sx + score_w - 4, y + size + 4], fill=(0, 0, 0, 90))
        draw.text((sx + 8, y), score, font=font, fill=(230,) * 3)

    shear = rng.uniform(0.12, 0.22)
    tile = tile.transform(tile.size, Image.AFFINE,
                          (1, -shear, shear * tile_h / 2, 0, 1, 0), resample=Image.BICUBIC)
    if rng.random() < 0.2:
        tile = ImageEnhance.Color(tile).enhance(0.4)
        tile = ImageEnhance.Contrast(tile).enhance(0.6)
    tile = ImageEnhance.Brightness(tile).enhance(rng.uniform(0.75, 1.15))
    if rng.random() < 0.5:
        tile = tile.filter(ImageFilter.GaussianBlur(rng.uniform(0.3, 1.0)))
    return fit_crop(tile)


if __name__ == "__main__":
    raise SystemExit(main())
