#!/usr/bin/env python3
"""Exercise the real Linux worker over private pipes, optionally inside Flatpak.

Use public/synthetic fixtures only. This does not validate microphone or desktop input.
"""
import argparse
import json
import os
from pathlib import Path
import selectors
import shutil
import struct
import subprocess
import sys
import time
import wave

APP = "io.github.AndrewQuest.Senda"
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("--worker", help="Native worker path; omit with --flatpak")
parser.add_argument("--flatpak", action="store_true")
parser.add_argument("--protocol-only", action="store_true", help="Exercise sequential requests without model weights or microphone")
parser.add_argument("--model", type=Path)
parser.add_argument("--wave", type=Path)
parser.add_argument("--language", choices=["es", "en"], default="en")
parser.add_argument("--output", required=True, type=Path)
args = parser.parse_args()
if sys.platform != "linux":
    parser.error("Run in the disposable Linux guest, not on the Windows host.")
if not args.flatpak and not args.worker:
    parser.error("Specify --worker or --flatpak.")
if not args.protocol_only and (not args.model or not args.wave):
    parser.error("Inference requires --model and --wave.")
catalog = ("ggml-tiny-q5_1.bin", "ggml-base-q5_1.bin", "ggml-small-q5_1.bin", "ggml-medium-q5_0.bin", "ggml-large-v3-turbo-q5_0.bin")
if args.model and args.model.name not in catalog:
    parser.error("Use a supported multilingual Whisper quantization.")
if args.protocol_only:
    pcm = bytes(16000)
else:
    with wave.open(str(args.wave), "rb") as sample:
        assert (sample.getnchannels(), sample.getsampwidth(), sample.getframerate()) == (1, 2, 16000)
        pcm = sample.readframes(sample.getnframes())
assert 8000 <= len(pcm) <= 3840000
audio = (b"RIFF" + struct.pack("<I", len(pcm) + 36) + b"WAVEfmt "
         + struct.pack("<IHHIIHH", 16, 1, 1, 16000, 32000, 2, 16)
         + b"data" + struct.pack("<I", len(pcm)) + pcm)
base = (Path.home() / ".var/app" / APP / "data") if args.flatpak else Path(os.environ.get("XDG_DATA_HOME", str(Path.home() / ".local/share")))
directory = base / ("senda-worker-smoke-" + str(time.time_ns()))
(directory / "models").mkdir(parents=True, mode=0o700)
if args.model:
    shutil.copyfile(args.model, directory / "models" / args.model.name)
command = (["flatpak", "run", "--command=/app/lib/senda/worker/Senda.Worker", APP]
           if args.flatpak else [str(Path(args.worker).resolve())])
command += ["--data-directory", str(directory)]
process = subprocess.Popen(command, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL)
deadline = time.monotonic() + 600
selector = selectors.DefaultSelector()
selector.register(process.stdout, selectors.EVENT_READ)

def exact(count):
    result = bytearray()
    while len(result) < count:
        if not selector.select(max(0, deadline - time.monotonic())):
            raise TimeoutError("Local worker timed out")
        block = os.read(process.stdout.fileno(), count - len(result))
        if not block:
            raise RuntimeError("Worker disconnected")
        result.extend(block)
    return result

def receive():
    length, = struct.unpack("<I", exact(4))
    assert 2 <= length <= 65536
    message = json.loads(exact(length))
    assert message["version"] == 1
    return message

def send(identifier, operation, data=None, payload=b""):
    data = dict(data or {}, version=1, id=identifier, operation=operation, audioBytes=len(payload))
    header = json.dumps(data).encode()
    process.stdin.write(struct.pack("<I", len(header)) + header + payload)
    process.stdin.flush()

try:
    assert receive()["kind"] == "ready"
    for index, model in enumerate(catalog):
        identifier = "catalog-" + str(index)
        send(identifier, "configure", {"model": model})
        reply = receive()
        assert reply["id"] == identifier and reply["kind"] == "configured" and reply["data"]["model"] == model, reply
    send("unsupported", "configure", {"model": "../unsupported.bin"})
    assert receive()["kind"] == "error"
    send("configure", "configure", {"model": args.model.name if args.model else "ggml-base-q5_1.bin", "settings": {"language": args.language, "cpuThreads": 4, "acceleration": "cpu"}})
    assert receive()["kind"] == "configured"
    for sequence in range(20):
        identifier = "silence-" + str(sequence)
        send(identifier, "transcribe", payload=audio[:44] + bytes(len(pcm)))
        reply = receive()
        assert reply["id"] == identifier and reply["kind"] == "error" and reply["error"] == "No speech detected.", reply
    if args.protocol_only:
        result = {"protocol": 1, "sequentialRequests": 20, "result": "passed"}
    else:
        start = time.monotonic()
        send("voice", "transcribe", payload=audio)
        result = receive()
        assert result["id"] == "voice" and result["kind"] == "transcript", result
        assert result["data"]["text"].strip()
        assert result["data"]["model"] == args.model.name
        assert "CPU" in result["data"]["engine"]
        result["wallSeconds"] = round(time.monotonic() - start, 3)
    result["environment"] = "VirtualBox VM; CPU; Flatpak" if args.flatpak else "VirtualBox VM; CPU; native worker"
    result["fixture"] = args.wave.name if args.wave else "generated silence in RAM"
    args.output.write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8")
    send("shutdown", "shutdown")
    process.stdin.close()
    assert process.wait(timeout=20) == 0
    print("PASS: framed protocol, 20 sequential requests and clean shutdown" + ("" if args.protocol_only else "; CPU inference and model identity"))
finally:
    if process.poll() is None:
        process.kill()
        process.wait(timeout=10)
    selector.close()
    # Leave only a report, not extra copies of model weights, in the test guest.
    owned = directory.resolve()
    assert owned.parent == base.resolve() and owned.name.startswith("senda-worker-smoke-")
    shutil.rmtree(owned)
