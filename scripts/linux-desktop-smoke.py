#!/usr/bin/env python3
"""Exercise the real GTK UI in a disposable Senda guest, using public WAVs only."""
import argparse
import json
import os
from pathlib import Path
import subprocess
import sys
import time

if (sys.platform != "linux" or os.getuid() != 1000
        or not Path("/var/lib/senda-lab-provisioned").exists()
        or Path("/sys/class/dmi/id/product_name").read_text().strip() != "VirtualBox"):
    raise SystemExit("Run only inside the provisioned disposable Linux guest.")

import pyatspi

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("--wave", type=Path)
parser.add_argument("--cancel", action="store_true")
parser.add_argument("--action")
parser.add_argument("--role", default="button")
parser.add_argument("--app", default="senda", choices=["senda", "xdg-desktop-portal-kde",
    "xdg-desktop-portal-gnome", "gnome-control-center-global-shortcuts-provider"])
parser.add_argument("--report", type=Path)
args = parser.parse_args()
desktop = pyatspi.Registry.getDesktop(0)


def nodes(node, depth=0):
    if node is None or depth > 40:
        return
    yield node
    for child in node:
        yield from nodes(child, depth + 1)


def application():
    return next(a for a in desktop if a.name == args.app)


def find(names, role="button"):
    names = names if isinstance(names, tuple) else (names,)
    deadline = time.monotonic() + 10
    while time.monotonic() < deadline:
        for node in nodes(application()):
            if node.name in names and node.getRoleName() == role:
                return node
        time.sleep(.2)
    raise AssertionError(f"Missing {role}: {names}")


def click(names, role="button"):
    assert find(names, role).queryAction().doAction(0)
    time.sleep(.5)


def transcript():
    return next((n.queryText().getText(0, -1) for n in nodes(application())
                 if n.getRoleName() == "text" and "Text" in n.get_interfaces()), "")


if args.action:
    click(args.action, args.role)
if args.wave or args.cancel:
    assert args.app == "senda"
    if args.wave:
        assert args.wave.name in ("jfk.wav", "es-synthetic.wav")
        assert args.wave.resolve().is_file()
    click(("Inicio", "Home"), "page tab")
    previous = transcript()
    click(("Grabar / terminar", "Record / finish"))
    if args.cancel:
        time.sleep(1)
        click(("Cancelar", "Cancel"))
        assert transcript() == previous, "Cancellation changed the last transcript"
    else:
        subprocess.run(["paplay", "--device=senda_lab", str(args.wave)], check=True)
        click(("Grabar / terminar", "Record / finish"))
        deadline = time.monotonic() + 60
        while time.monotonic() < deadline:
            cancel = find(("Cancelar", "Cancel"))
            if not cancel.getState().contains(pyatspi.STATE_SENSITIVE):
                break
            time.sleep(.25)
        else:
            raise AssertionError("Dictation did not finish")
        text = transcript()
        assert text.strip(), "No transcript reached the GTK interface"
        assert ("country" in text.lower() if args.wave.name == "jfk.wav"
                else "prueba" in text.lower()), text

try:
    remaining = list(nodes(application()))
except StopIteration:
    assert args.action in ("Salir", "Quit"), "The application disappeared unexpectedly"
    remaining = []
result = {"environment": "VirtualBox; Wayland; Flatpak; virtual microphone; host input disabled",
          "application": args.app,
          "labels": list(dict.fromkeys(n.name for n in remaining
                                       if n.name and n.getRoleName() == "label"))}
if args.wave or args.cancel:
    result.update(transcript=transcript(), cancelled=args.cancel,
                  fixture=args.wave.name if args.wave else None)
if args.report:
    root = (Path.home() / "senda-tests").resolve()
    destination = args.report.resolve()
    assert destination.is_relative_to(root), "Keep reports in the disposable guest test folder"
    destination.parent.mkdir(parents=True, exist_ok=True)
    destination.write_text(json.dumps(result, ensure_ascii=False, indent=2))
print(json.dumps(result, ensure_ascii=False))
