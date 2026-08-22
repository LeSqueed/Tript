#!/usr/bin/env python3
"""Train and export one generic Tript YOLO model for a prepared workspace."""

from __future__ import annotations

import argparse
import shutil
from pathlib import Path


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("workspace", type=Path)
    parser.add_argument("--size", type=int, required=True)
    parser.add_argument("--epochs", type=int, default=100)
    parser.add_argument("--device", default="auto")
    parser.add_argument("--base-model", default="yolo11n.pt")
    args = parser.parse_args()

    if args.size <= 0 or args.epochs <= 0:
        raise ValueError("size and epochs must be positive")

    import torch
    from ultralytics import YOLO

    device = choose_device(torch, args.device)
    print(f"DEVICE requested={args.device} selected={device} cuda={torch.cuda.is_available()}", flush=True)

    workspace = args.workspace.resolve()
    dataset = workspace / "dataset"
    dataset_config = dataset / "dataset.yaml"
    if not dataset_config.is_file():
        raise FileNotFoundError(f"dataset.yaml not found: {dataset_config}")

    run_root = workspace / "runs"
    run_root.mkdir(parents=True, exist_ok=True)
    model = YOLO(args.base_model)
    print(f"TRAINING epochs={args.epochs} size={args.size} base={args.base_model}", flush=True)
    result = model.train(
        data=str(dataset_config),
        epochs=args.epochs,
        imgsz=args.size,
        device=device,
        project=str(run_root),
        name="latest",
        exist_ok=True,
        verbose=True,
    )
    del result

    best = run_root / "latest" / "weights" / "best.pt"
    if not best.is_file():
        raise FileNotFoundError(f"training completed without a best model: {best}")

    print("EXPORTING format=onnx", flush=True)
    trained = YOLO(str(best))
    exported = Path(trained.export(format="onnx", imgsz=args.size, device=device, simplify=False))
    target = dataset / "model.onnx"
    shutil.copy2(exported, target)
    print(f"MODEL {target}", flush=True)
    return 0


def choose_device(torch, requested: str) -> str:
    if requested == "auto":
        return "0" if torch.cuda.is_available() else "cpu"
    if requested in {"cuda", "0"} and not torch.cuda.is_available():
        raise RuntimeError("CUDA was requested but torch.cuda.is_available() is false")
    return requested


if __name__ == "__main__":
    raise SystemExit(main())
