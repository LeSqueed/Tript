#!/usr/bin/env python3
# SPDX-License-Identifier: GPL-2.0-or-later
# Copyright (c) 2026 LeSqueed and the Tript contributors
#
# Turns failed tests in TRX results into GitHub Actions error annotations, so a failing run names its
# tests on the run summary and through the public checks API rather than only in the job log.

import pathlib
import sys
import xml.etree.ElementTree as ElementTree

NAMESPACE = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}
MAX_MESSAGE = 1500


def escape(text: str) -> str:
    return text.replace("%", "%25").replace("\r", "").replace("\n", "%0A")


def failures(results_root: pathlib.Path):
    for trx in sorted(results_root.rglob("*.trx")):
        for result in ElementTree.parse(trx).getroot().iterfind(".//t:UnitTestResult", NAMESPACE):
            if result.get("outcome") != "Failed":
                continue
            message = result.findtext("t:Output/t:ErrorInfo/t:Message", default="", namespaces=NAMESPACE)
            stack = result.findtext("t:Output/t:ErrorInfo/t:StackTrace", default="", namespaces=NAMESPACE)
            yield result.get("testName", "unknown test"), f"{message.strip()}\n{stack.strip()}"[:MAX_MESSAGE]


def main() -> int:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    root = pathlib.Path(sys.argv[1] if len(sys.argv) > 1 else "TestResults")
    failed = list(failures(root))
    for name, detail in failed:
        print(f"::error title={escape(name)}::{escape(detail)}")
    if failed:
        print(f"::error title={len(failed)} failed tests::{escape(chr(10).join(name for name, _ in failed))}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
