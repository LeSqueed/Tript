#!/usr/bin/env python3
"""Fine-tune a small CRNN recogniser on the OCR dataset built by export_ocr_dataset.py.

Reads ``dataset/ocr/{images,labels.tsv,character_dict.txt}``; writes ``dataset/ocr_model.onnx`` and
``dataset/ocr_dict.txt``. The ONNX graph matches the runtime contract in
``Tript.Detection/PaddleOcrRecognizer.cs``:

* input  ``x`` shape ``[1, 3, 48, 512]``, value ``pixel / 127.5 - 1``
* output ``logits`` shape ``[1, T, C]`` **softmax over C**, where ``C = len(dict) + 2``
  (the recogniser appends a space class and CTC blank is index 0).

Progress is written to ``dataset/progress.json`` on the same heartbeat schema as train_model.py.
"""

from __future__ import annotations

import argparse
import json
import shutil
from pathlib import Path

import numpy as np
import torch
import torch.nn as nn
from PIL import Image
from torch.utils.data import DataLoader, Dataset

CROP_H, CROP_W = 48, 512
BLANK = 0


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("workspace", type=Path)
    parser.add_argument("--epochs", type=int, required=True)
    parser.add_argument("--device", default="auto")
    parser.add_argument("--batch", type=int, default=32)
    parser.add_argument("--lr", type=float, default=1e-3)
    args = parser.parse_args()
    if args.epochs <= 0:
        raise ValueError("epochs must be positive")

    workspace = args.workspace.resolve()
    ocr_dir = workspace / "dataset" / "ocr"
    progress_path = workspace / "dataset" / "progress.json"
    write_progress(progress_path, "starting", 0, args.epochs)

    device = pick_device(args.device)
    chars = [c for c in (ocr_dir / "character_dict.txt").read_text(encoding="utf-8").splitlines() if c]
    # Runtime: _characters = file lines + " "; classCount = len(_characters) + 1.
    vocab = chars + [" "]
    num_classes = len(vocab) + 1
    char_to_index = {c: i + 1 for i, c in enumerate(vocab)}  # 1-based; 0 is CTC blank

    records = read_labels(ocr_dir / "labels.tsv")
    train_records = [r for r in records if r[0] == "train"]
    # Real crops are a small fraction of the set; weight them up so synthetic does not dominate.
    real = [r for r in train_records
            if "/real_" in r[1] or "/trans_" in r[1] or "/region_" in r[1]]
    train = OcrDataset(train_records + real * 4, ocr_dir, char_to_index)
    val = OcrDataset([r for r in records if r[0] == "val"], ocr_dir, char_to_index)
    if len(train) == 0:
        raise ValueError("the OCR dataset has no training crops")
    train_loader = DataLoader(train, batch_size=args.batch, shuffle=True, collate_fn=collate, drop_last=False)
    val_loader = DataLoader(val, batch_size=args.batch, shuffle=False, collate_fn=collate) if len(val) else None

    model = Crnn(num_classes).to(device)
    optimizer = torch.optim.Adam(model.parameters(), lr=args.lr)
    scheduler = torch.optim.lr_scheduler.CosineAnnealingLR(optimizer, T_max=args.epochs)
    ctc = nn.CTCLoss(blank=BLANK, zero_infinity=True)

    print(f"TRAIN device={device} train={len(train)} val={len(val)} classes={num_classes} "
          f"epochs={args.epochs}", flush=True)

    for epoch in range(1, args.epochs + 1):
        model.train()
        running = 0.0
        for images, targets, target_lengths in train_loader:
            images = images.to(device)
            logits = model(images)                      # [B, T, C]
            log_probs = logits.log_softmax(2).permute(1, 0, 2)  # [T, B, C]
            input_lengths = torch.full((images.size(0),), logits.size(1), dtype=torch.long)
            loss = ctc(log_probs, targets, input_lengths, target_lengths)
            optimizer.zero_grad()
            loss.backward()
            nn.utils.clip_grad_norm_(model.parameters(), 5.0)
            optimizer.step()
            running += loss.item() * images.size(0)
        scheduler.step()

        mean_loss = running / len(train)
        accuracy = evaluate(model, val_loader, vocab, device) if val_loader else 0.0
        write_progress(progress_path, "training", epoch, args.epochs, mean_loss, accuracy)
        print(f"EPOCH {epoch}/{args.epochs} loss={mean_loss:.4f} val_exact={accuracy:.3f}", flush=True)

    write_progress(progress_path, "exporting", args.epochs, args.epochs)
    torch.save({"state_dict": model.state_dict(), "num_classes": num_classes},
               ocr_dir / "checkpoint.pt")  # so a failed export can be retried without retraining
    export_onnx(model, ocr_dir.parent / "ocr_model.onnx", device)
    shutil.copyfile(ocr_dir / "character_dict.txt", ocr_dir.parent / "ocr_dict.txt")
    write_progress(progress_path, "completed", args.epochs, args.epochs)
    print(f"TRAINED_OCR {ocr_dir.parent / 'ocr_model.onnx'}", flush=True)
    return 0


class Crnn(nn.Module):
    def __init__(self, num_classes: int):
        super().__init__()

        def block(cin, cout, pool):
            return nn.Sequential(
                nn.Conv2d(cin, cout, 3, padding=1), nn.BatchNorm2d(cout), nn.ReLU(inplace=True),
                nn.MaxPool2d(pool),
            )

        # H 48 -> 3, W 512 -> 128.
        self.cnn = nn.Sequential(
            block(3, 32, (2, 2)), block(32, 64, (2, 2)),
            block(64, 128, (2, 1)), block(128, 128, (2, 1)),
        )
        self.rnn = nn.LSTM(128 * 3, 192, num_layers=2, batch_first=True, bidirectional=True, dropout=0.1)
        self.head = nn.Linear(192 * 2, num_classes)

    def forward(self, x):
        f = self.cnn(x)                       # [B, 128, 3, W']
        b, c, h, w = f.shape
        f = f.permute(0, 3, 1, 2).reshape(b, w, c * h)  # [B, W', 384]
        f, _ = self.rnn(f)
        return self.head(f)                   # [B, W', num_classes]


class OcrDataset(Dataset):
    def __init__(self, records, root: Path, char_to_index: dict[str, int]):
        self.records = records
        self.root = root
        self.char_to_index = char_to_index

    def __len__(self):
        return len(self.records)

    def __getitem__(self, i):
        _, rel, label = self.records[i]
        image = Image.open(self.root / rel).convert("RGB").resize((CROP_W, CROP_H))
        # Runtime PrepareInput reads BGRA channel 0/1/2 = B/G/R into planes 0/1/2; match that order.
        array = (np.asarray(image, dtype=np.float32)[:, :, ::-1].copy() / 127.5) - 1.0  # HWC, BGR
        tensor = torch.from_numpy(array).permute(2, 0, 1)             # CHW
        target = torch.tensor([self.char_to_index[c] for c in label if c in self.char_to_index],
                              dtype=torch.long)
        return tensor, target


def collate(batch):
    images = torch.stack([b[0] for b in batch])
    targets = torch.cat([b[1] for b in batch])
    lengths = torch.tensor([len(b[1]) for b in batch], dtype=torch.long)
    return images, targets, lengths


@torch.no_grad()
def evaluate(model, loader, vocab, device) -> float:
    model.eval()
    correct = total = 0
    for images, targets, target_lengths in loader:
        logits = model(images.to(device))
        preds = greedy_decode(logits.cpu(), vocab)
        offset = 0
        for length, text in zip(target_lengths.tolist(), preds):
            gold = "".join(vocab[t - 1] for t in targets[offset:offset + length].tolist())
            offset += length
            correct += int(text.strip() == gold.strip())
            total += 1
    return correct / max(1, total)


def greedy_decode(logits: torch.Tensor, vocab: list[str]) -> list[str]:
    indices = logits.argmax(2)  # [B, T]
    out = []
    for row in indices.tolist():
        chars = []
        previous = -1
        for index in row:
            if index != BLANK and index != previous:
                chars.append(vocab[index - 1])
            previous = index
        out.append("".join(chars))
    return out


def export_onnx(model: nn.Module, path: Path, device) -> None:
    model.eval()

    class Exported(nn.Module):
        def __init__(self, inner):
            super().__init__()
            self.inner = inner

        def forward(self, x):
            return self.inner(x).softmax(2)

    dummy = torch.zeros(1, 3, CROP_H, CROP_W)
    exported = Exported(model).to("cpu").eval()
    try:
        # Legacy TorchScript exporter: a plain static graph, and no onnxscript dependency.
        torch.onnx.export(exported, dummy, str(path), input_names=["x"], output_names=["logits"],
                          opset_version=17, dynamo=False)
    except TypeError:
        torch.onnx.export(exported, dummy, str(path), input_names=["x"], output_names=["logits"],
                          opset_version=17)


def read_labels(path: Path):
    rows = []
    for line in path.read_text(encoding="utf-8").splitlines():
        if not line.strip():
            continue
        split, rel, label = line.split("\t", 2)
        rows.append((split, rel, label))
    return rows


def pick_device(requested: str) -> str:
    if requested in ("cpu", "cuda"):
        return requested
    return "cuda" if torch.cuda.is_available() else "cpu"


def write_progress(path: Path, status: str, epoch: int, epochs: int,
                   loss: float | None = None, accuracy: float | None = None) -> None:
    payload = {"status": status, "epoch": epoch, "epochs": epochs}
    if loss is not None:
        payload["loss"] = round(loss, 5)
    if accuracy is not None:
        payload["map50"] = round(accuracy, 4)  # reuse the object heartbeat's accuracy slot
    temporary = path.with_suffix(".tmp")
    temporary.write_text(json.dumps(payload), encoding="utf-8")
    temporary.replace(path)


if __name__ == "__main__":
    raise SystemExit(main())
