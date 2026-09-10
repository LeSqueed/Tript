#!/usr/bin/env python3

"""Fine-tune PP-OCRv4-English on a workspace's OCR crops and export the result to ONNX.

Reads ``dataset/ocr/{images,labels.tsv}`` built by export_ocr_dataset.py, drives the vendored
PaddleOCR ``tools/train.py`` + ``tools/export_model.py`` from the pretrained checkpoint, converts
with paddle2onnx, and writes ``dataset/ocr_model.onnx`` + ``dataset/ocr_dict.txt`` on the runtime
contract (input [1,3,48,W] BGR, output softmax [1,T,len(dict)+2], CTC blank at 0).

Progress goes to ``dataset/progress.json`` on train_model.py's heartbeat schema. CPU only -
PaddlePaddle has no Windows GPU and the fine-tune is small.
"""
from __future__ import annotations

import argparse
import json
import os
import random
import re
import shutil
import subprocess
import sys
from pathlib import Path

MAX_SYNTHETIC = 200

EPOCH_LINE = re.compile(r"epoch:\s*\[\s*(\d+)\s*/\s*(\d+)\s*\].*?loss:\s*([0-9.eE+-]+)")
EVAL_ACC = re.compile(r"cur metric.*?\bacc:\s*([0-9.eE+-]+)")

def write_progress(path: Path, status: str, epoch: int, epochs: int,
                   loss: float | None = None, acc: float | None = None) -> None:
    payload = {"status": status, "epoch": epoch, "epochs": epochs}
    if loss is not None:
        payload["loss"] = round(loss, 5)
    if acc is not None:
        payload["map50"] = round(acc, 4)
    tmp = path.with_suffix(".tmp")
    tmp.write_text(json.dumps(payload), encoding="utf-8")
    tmp.replace(path)

def build_label_files(ocr_dir: Path, staging: Path) -> tuple[Path, Path, int]:
    rows = [ln.split("\t", 2) for ln in (ocr_dir / "labels.tsv").read_text(encoding="utf-8").splitlines()
            if ln.strip()]
    is_real = lambda rel: "/synth_" not in rel
    real_train = [f"{rel}\t{label}" for split, rel, label in rows if split == "train" and is_real(rel)]
    synthetic = [f"{rel}\t{label}" for split, rel, label in rows if split == "train" and not is_real(rel)]
    if len(synthetic) > MAX_SYNTHETIC:
        synthetic = random.Random(0).sample(synthetic, MAX_SYNTHETIC)
    train = real_train + synthetic

    val = [f"{rel}\t{label}" for split, rel, label in rows if split == "val" and is_real(rel)]
    if not train:
        raise SystemExit("the OCR dataset has no training crops")
    if not val:
        val = [f"{rel}\t{label}" for split, rel, label in rows if split == "val"][:16] or train[:8]

    if len(val) < 64:
        val = (val * (64 // len(val) + 1))[:64]
    (staging / "train_list.txt").write_text("\n".join(train) + "\n", encoding="utf-8")
    (staging / "val_list.txt").write_text("\n".join(val) + "\n", encoding="utf-8")
    longest = max(len(r.split("\t", 1)[1]) for r in train + val)
    return staging / "train_list.txt", staging / "val_list.txt", min(60, max(25, longest + 5))

def render_config(base_config: Path, staging: Path, max_text_length: int) -> Path:
    text = re.sub(r"(max_text_length:\s*&max_text_length\s*)\d+",
                  rf"\g<1>{max_text_length}", base_config.read_text(encoding="utf-8"))
    run_config = staging / "run.yml"
    run_config.write_text(text, encoding="utf-8")
    return run_config

def stream(cmd: list[str], cwd: Path, env: dict,
           progress_path: Path | None, epochs: int) -> None:
    proc = subprocess.Popen([str(c) for c in cmd], cwd=str(cwd), env=env,
                            stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True,
                            encoding="utf-8", errors="replace", bufsize=1)
    epoch = 0
    loss: float | None = None
    for line in proc.stdout:
        sys.stdout.write(line)
        sys.stdout.flush()
        if progress_path is None:
            continue
        m = EPOCH_LINE.search(line)
        if m:
            epoch = int(m.group(1))
            loss = float(m.group(3))
            write_progress(progress_path, "training", epoch, epochs, loss)
        a = EVAL_ACC.search(line)
        if a:
            write_progress(progress_path, "training", epoch, epochs, loss, float(a.group(1)))
    if proc.wait() != 0:
        raise SystemExit(f"{Path(cmd[1]).name} exited {proc.returncode}")

def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("workspace", type=Path)
    parser.add_argument("--epochs", type=int, required=True)
    parser.add_argument("--device", default="auto")
    parser.add_argument("--paddle-root", type=Path, required=True)
    parser.add_argument("--config", type=Path, required=True)
    parser.add_argument("--pretrained", type=Path, required=True, help="checkpoint prefix, no extension")
    parser.add_argument("--dict", type=Path, required=True,
                        help="character dict matching the pretrained classifier (the shipped en dict)")
    args = parser.parse_args()
    if args.epochs <= 0:
        raise SystemExit("epochs must be positive")
    for path in (args.paddle_root / "tools" / "train.py", args.config, args.dict):
        if not path.exists():
            raise SystemExit(f"missing {path}")
    if not any(args.pretrained.parent.glob(args.pretrained.name + ".pdparams")):
        raise SystemExit(f"missing pretrained checkpoint {args.pretrained}.pdparams")

    workspace = args.workspace.resolve()
    ocr_dir = workspace / "dataset" / "ocr"
    progress_path = workspace / "dataset" / "progress.json"
    if not (ocr_dir / "labels.tsv").is_file():
        raise SystemExit(f"missing {ocr_dir / 'labels.tsv'} - run export_ocr_dataset.py first")
    write_progress(progress_path, "starting", 0, args.epochs)

    env = {**os.environ, "PYTHONUNBUFFERED": "1", "PYTHONIOENCODING": "utf-8"}
    paddle2onnx = Path(sys.executable).parent / "paddle2onnx"
    staging = workspace / "dataset" / "ocr-finetune"
    if staging.exists():
        shutil.rmtree(staging)
    staging.mkdir(parents=True)
    try:
        train_list, val_list, max_text_length = build_label_files(ocr_dir, staging)
        run_config = render_config(args.config, staging, max_text_length)
        out_dir = staging / "out"
        train_opts = [
            f"Global.pretrained_model={args.pretrained}",
            f"Global.character_dict_path={args.dict}",
            "Global.use_gpu=false",
            f"Global.epoch_num={args.epochs}",
            f"Global.save_model_dir={out_dir}",
            f"Train.dataset.data_dir={ocr_dir}",
            f"Train.dataset.label_file_list=[{train_list}]",
            f"Eval.dataset.data_dir={ocr_dir}",
            f"Eval.dataset.label_file_list=[{val_list}]",
        ]
        stream([sys.executable, args.paddle_root / "tools" / "train.py", "-c", run_config, "-o",
                *train_opts], args.paddle_root, env, progress_path, args.epochs)

        write_progress(progress_path, "exporting", args.epochs, args.epochs)

        trained = out_dir / "best_accuracy"
        if not (out_dir / "best_accuracy.pdparams").exists():
            trained = out_dir / "latest"
        if not trained.with_suffix(".pdparams").exists():
            raise SystemExit(f"training produced no checkpoint in {out_dir}")
        inference_dir = staging / "inference"
        stream([sys.executable, args.paddle_root / "tools" / "export_model.py", "-c", run_config, "-o",
                f"Global.pretrained_model={trained}",
                f"Global.character_dict_path={args.dict}",
                f"Global.save_inference_dir={inference_dir}"], args.paddle_root, env, None, args.epochs)

        model_out = workspace / "dataset" / "ocr_model.onnx"
        subprocess.run([str(paddle2onnx), "--model_dir", str(inference_dir),
                        "--model_filename", "inference.pdmodel",
                        "--params_filename", "inference.pdiparams",
                        "--save_file", str(model_out), "--opset_version", "14"],
                       check=True, env=env)
        shutil.copyfile(args.dict, workspace / "dataset" / "ocr_dict.txt")
        write_progress(progress_path, "completed", args.epochs, args.epochs)
        print(f"TRAINED_OCR {model_out}", flush=True)
        return 0
    finally:
        shutil.rmtree(staging, ignore_errors=True)

if __name__ == "__main__":
    raise SystemExit(main())
