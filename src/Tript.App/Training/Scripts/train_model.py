#!/usr/bin/env python3

"""Train and export one generic Tript YOLO model for a prepared workspace."""

from __future__ import annotations

import argparse
import json
import os
import shutil
import tempfile
from contextlib import nullcontext
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
    if device == "directml":
        configure_directml()
    print(
        f"DEVICE requested={args.device} selected={device} cuda={torch.cuda.is_available()}",
        flush=True,
    )

    workspace = args.workspace.resolve()
    dataset = workspace / "dataset"
    dataset_config = dataset / "dataset.yaml"
    if not dataset_config.is_file():
        raise FileNotFoundError(f"dataset.yaml not found: {dataset_config}")

    run_root = workspace / "runs"
    run_root.mkdir(parents=True, exist_ok=True)
    model = YOLO(args.base_model)

    model.add_callback("on_fit_epoch_end", make_epoch_progress_writer(workspace, args.epochs))
    write_progress(workspace, "starting", epochs=args.epochs)
    print(f"TRAINING epochs={args.epochs} size={args.size} base={args.base_model}", flush=True)
    train_options = {
        "data": str(dataset_config),
        "epochs": args.epochs,
        "imgsz": args.size,
        "batch": 32 if device != "cpu" else 16,

        "patience": 0,
        "device": device,
        "workers": max(1, (os.cpu_count() or 8) // 2),
        "project": str(run_root),
        "name": "latest",
        "exist_ok": True,
        "verbose": True,
    }
    if device == "directml":

        train_options.update(amp=False, workers=0, val=False, batch=16)
    result = model.train(**train_options)
    del result

    best = run_root / "latest" / "weights" / "best.pt"
    if not best.is_file() and device == "directml":

        best = run_root / "latest" / "weights" / "last.pt"
    if not best.is_file():
        raise FileNotFoundError(f"training completed without a model checkpoint: {best}")

    print("EXPORTING format=onnx", flush=True)
    write_progress(workspace, "exporting")
    trained = YOLO(str(best))

    export_device = "cpu" if device == "directml" else device
    exported = Path(trained.export(format="onnx", imgsz=args.size, device=export_device, simplify=False))
    target = dataset / "model.onnx"
    shutil.copy2(exported, target)
    print(f"MODEL {target}", flush=True)
    write_progress(workspace, "done")
    return 0

def write_progress(
    workspace: Path,
    status: str,
    epoch: int = 0,
    epochs: int = 0,
    loss: float | None = None,
    map50: float | None = None,
) -> None:
    """Atomically publish the heartbeat the host polls (dataset/progress.json)."""
    dataset = workspace / "dataset"
    payload = {"status": status, "epoch": epoch, "epochs": epochs, "loss": loss, "map50": map50}
    try:
        descriptor, temporary = tempfile.mkstemp(dir=dataset, prefix=".progress-", suffix=".json")
    except OSError:
        return
    try:
        with os.fdopen(descriptor, "w", encoding="utf-8") as handle:
            json.dump(payload, handle)
        os.replace(temporary, dataset / "progress.json")
    except OSError:

        try:
            os.unlink(temporary)
        except OSError:
            pass

def _epoch_loss(trainer) -> float | None:
    for source in (getattr(trainer, "tloss", None), getattr(trainer, "loss_items", None)):
        if source is None:
            continue
        try:
            terms = source.values() if isinstance(source, dict) else source
            if hasattr(terms, "detach"):
                terms = terms.detach().cpu().flatten().tolist()
            elif not isinstance(terms, (list, tuple)):
                terms = [terms]
            values = [float(term.detach().cpu().item()) if hasattr(term, "detach") else float(term)
                      for term in terms]
            if not values:
                continue
            return sum(values) / len(values)
        except Exception:
            continue
    return None

def _epoch_map50(trainer) -> float | None:
    metrics = getattr(trainer, "metrics", None)
    if metrics is None:
        return None

    if isinstance(metrics, dict):
        for key in ("metrics/mAP50(B)", "val/mAP50", "mAP50"):
            value = metrics.get(key)
            if value is None:
                continue
            try:
                return float(value)
            except (TypeError, ValueError):
                continue
        return None
    box = getattr(metrics, "box", None)
    if box is None:
        return None
    try:
        return float(box.map50)
    except Exception:
        return None

def make_epoch_progress_writer(workspace: Path, epochs: int):
    def on_fit_epoch_end(trainer) -> None:
        write_progress(
            workspace, "training", int(trainer.epoch) + 1, epochs,
            _epoch_loss(trainer), _epoch_map50(trainer),
        )

    return on_fit_epoch_end

def choose_device(torch, requested: str, directml_available: bool | None = None) -> str:
    requested = str(requested).strip().lower()
    if requested in {"directml", "amd"}:
        if directml_available is None:
            directml_available = can_use_directml()
        if not directml_available:
            raise RuntimeError(
                "DirectML was requested but torch-directml is not installed or no DirectX 12 device is available. "
                "Install it with 'python -m pip install torch-directml' and update the AMD graphics driver."
            )
        return "directml"
    if requested == "auto":
        if torch.cuda.is_available():
            return "0"
        if directml_available is None:
            directml_available = can_use_directml()
        if directml_available:
            return "directml"
        return "cpu"
    if requested in {"cuda", "0", "rocm"}:
        if not torch.cuda.is_available():
            raise RuntimeError(
                "ROCm/CUDA was requested but torch.cuda.is_available() is false. "
                "Install the matching AMD ROCm PyTorch package."
            )
        return "0" if requested == "rocm" else requested
    return requested

def can_use_directml() -> bool:
    try:
        import torch_directml

        torch_directml.device()
        return torch_directml.device_count() > 0
    except (ImportError, OSError, RuntimeError, TypeError):
        return False

def configure_directml() -> None:
    """Bridge the PrivateUse1 device used by torch-directml into Ultralytics."""
    import torch
    import torch_directml
    import ultralytics.data.build as data_build
    import ultralytics.engine.exporter as exporter
    import ultralytics.engine.predictor as predictor
    import ultralytics.engine.trainer as trainer
    import ultralytics.engine.validator as validator
    import ultralytics.utils.autobatch as autobatch
    import ultralytics.utils.loss as loss_utils
    import ultralytics.utils.ops as ops
    import ultralytics.utils.tal as tal
    import ultralytics.utils.torch_utils as torch_utils

    directml = torch_directml.device()
    directml_name = torch_directml.device_name(0).rstrip("\x00")
    original_select_device = torch_utils.select_device
    original_get_torch_device_backend = torch_utils.get_torch_device_backend
    original_autocast = torch_utils.autocast
    original_preprocess = loss_utils.v8DetectionLoss.preprocess
    original_assigner_forward = tal.TaskAlignedAssigner.forward
    original_select_topk = tal.TaskAlignedAssigner.select_topk_candidates
    original_validate = trainer.BaseTrainer.validate

    class DirectMLBackend:
        def is_available(self):
            return True

        def device_count(self):
            return 1

        def current_device(self):
            return 0

        def set_device(self, index):
            if index != 0:
                raise ValueError("DirectML training supports one device")

        def get_device_name(self, index):
            return directml_name

        def memory_reserved(self):
            return 0

        def get_device_properties(self, device):
            return type("DirectMLProperties", (), {"total_memory": 0})()

        def empty_cache(self):
            return None

    backend = DirectMLBackend()

    def select_device(device="", newline=False, verbose=True):
        if str(device).lower().startswith("directml") or getattr(device, "type", "") == "privateuseone":
            return directml
        return original_select_device(device, newline=newline, verbose=verbose)

    def get_torch_device_backend(device):
        device_type = getattr(device, "type", str(device).split(":", 1)[0])
        if device_type in {"directml", "privateuseone"}:
            return backend
        return original_get_torch_device_backend(device)

    def autocast(enabled: bool, device: str = "cuda"):
        if device in {"privateuseone", "directml"}:
            return nullcontext()
        return original_autocast(enabled, device)

    def preprocess(self, targets, batch_size, scale_tensor):

        if getattr(targets.device, "type", "") != "privateuseone":
            return original_preprocess(self, targets, batch_size, scale_tensor)
        nl, ne = targets.shape
        if nl == 0:
            return torch.zeros(batch_size, 0, ne - 1, device=self.device)
        batch_idx = targets[:, 0].long()
        _, counts = batch_idx.to("cpu").unique(return_counts=True)
        counts = counts.to(dtype=torch.int32)
        out = torch.zeros(batch_size, counts.max(), ne - 1, device=self.device)
        batch_idx_cpu = batch_idx.to("cpu")
        offsets = torch.zeros(batch_size + 1, dtype=torch.long, device="cpu")
        offsets.scatter_add_(0, batch_idx_cpu + 1, torch.ones_like(batch_idx_cpu))
        offsets = offsets.cumsum(0)
        within_idx = (torch.arange(nl, device="cpu") - offsets[batch_idx_cpu]).to(self.device)
        out[batch_idx, within_idx] = targets[:, 1:]
        out[..., 1:5] = loss_utils.xywh2xyxy(out[..., 1:5].mul_(scale_tensor))
        return out

    def select_topk_candidates(self, metrics, topk_mask=None):
        if getattr(metrics.device, "type", "") != "privateuseone":
            return original_select_topk(self, metrics, topk_mask)
        topk_metrics, topk_idxs = torch.topk(metrics, self.topk, dim=-1, largest=True)
        if topk_mask is None:
            topk_mask = (topk_metrics.max(-1, keepdim=True)[0] > self.eps).expand_as(topk_idxs)
        topk_idxs = topk_idxs.to("cpu")
        topk_idxs.masked_fill_(~topk_mask.to("cpu"), 0)

        count_tensor = torch.zeros(metrics.shape, dtype=torch.int32, device="cpu")
        count_tensor.scatter_add_(-1, topk_idxs, torch.ones_like(topk_idxs, dtype=torch.int32))
        count_tensor.masked_fill_(count_tensor > 1, 0)
        return count_tensor.to(device=metrics.device, dtype=metrics.dtype)

    def assigner_forward(self, pd_scores, pd_bboxes, anc_points, gt_labels, gt_bboxes, mask_gt):
        if getattr(gt_bboxes.device, "type", "") != "privateuseone":
            return original_assigner_forward(
                self, pd_scores, pd_bboxes, anc_points, gt_labels, gt_bboxes, mask_gt
            )

        if gt_bboxes.shape[1] == 0:
            return original_assigner_forward(
                self, pd_scores, pd_bboxes, anc_points, gt_labels, gt_bboxes, mask_gt
            )

        device = gt_bboxes.device
        self.bs = pd_scores.shape[0]
        self.n_max_boxes = gt_bboxes.shape[1]
        cpu_result = self._forward(*(
            tensor.to("cpu")
            for tensor in (pd_scores, pd_bboxes, anc_points, gt_labels, gt_bboxes, mask_gt)
        ))
        return tuple(tensor.to(device) for tensor in cpu_result)

    def validate(self):
        if getattr(self.device, "type", "") == "privateuseone":
            return {}, float(-self.loss.detach().cpu().item())
        return original_validate(self)

    torch_utils.select_device = select_device
    torch_utils.get_torch_device_backend = get_torch_device_backend
    torch_utils.autocast = autocast
    for module in (trainer, validator, exporter, predictor):
        if hasattr(module, "select_device"):
            module.select_device = select_device
        if hasattr(module, "get_torch_device_backend"):
            module.get_torch_device_backend = get_torch_device_backend
        if hasattr(module, "autocast"):
            module.autocast = autocast
    for module in (data_build, autobatch, ops):
        if hasattr(module, "get_torch_device_backend"):
            module.get_torch_device_backend = get_torch_device_backend
    loss_utils.v8DetectionLoss.preprocess = preprocess
    tal.TaskAlignedAssigner.select_topk_candidates = select_topk_candidates
    tal.TaskAlignedAssigner.forward = assigner_forward
    trainer.BaseTrainer.validate = validate

if __name__ == "__main__":
    raise SystemExit(main())
