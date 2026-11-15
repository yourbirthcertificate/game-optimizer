"""Export Game Spec Check's graphics card scores to data/gpus.json.

Both tools are by the same author; this keeps one hand-maintained table.
Usage: python tools/export_gpus.py <path to game-spec-check checkout>
"""
import json
import os
import sys

root = os.path.abspath(sys.argv[1] if len(sys.argv) > 1 else "../game-spec-check")
sys.path.insert(0, root)
from specbench import hardware_db  # noqa: E402

out = {
    "source": "Game Spec Check specbench/hardware_db.py (PassMark-like scale, desktop cards)",
    "gpus": [[n, s] for n, s in hardware_db.GPUS],
    "generic": [[p, l, s] for p, l, s in hardware_db.GPU_GENERIC],
}
dest = os.path.join(os.path.dirname(__file__), "..", "data", "gpus.json")
with open(dest, "w", encoding="utf-8", newline="\n") as fh:
    # One card per line keeps diffs readable.
    fh.write('{\n "source": %s,\n "gpus": [\n' % json.dumps(out["source"]))
    fh.write(",\n".join("  " + json.dumps(g, ensure_ascii=False) for g in out["gpus"]))
    fh.write('\n ],\n "generic": [\n')
    fh.write(",\n".join("  " + json.dumps(g, ensure_ascii=False) for g in out["generic"]))
    fh.write("\n ]\n}\n")
print("wrote %d cards to %s" % (len(out["gpus"]), os.path.normpath(dest)))
