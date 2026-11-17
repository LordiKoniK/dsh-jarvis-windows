"""Persistent local Kokoro TTS worker.

Speech is synthesized entirely on this machine with `kokoro-onnx` running on
ONNX Runtime's CPU provider — no GPU, no network, no API quota. The model is
loaded ONCE and the process then serves requests until it is killed, because
loading Kokoro per request would dominate the latency.

Protocol: one JSON object per line on stdin, one per line on stdout.

    in : {"id": 1, "text": "Hello there.", "voice": "af_heart", "speed": 1.0}
    out: {"id": 1, "sampleRate": 24000, "seconds": 1.8, "wav": "<base64 WAV>"}
    out: {"id": 1, "error": "..."}

A first line of {"event": "ready"} is written after the model loads.

Everything is overridable from the environment, because the driving host
(synth-kokoro.mjs) sets KOKORO_MODEL_DIR, KOKORO_MODEL_FILE and KOKORO_THREADS
from plugin config rather than trusting these defaults:

    KOKORO_MODEL_DIR    ~/.dsh/models/kokoro
    KOKORO_MODEL_FILE   kokoro-v1.0.onnx   (fp32 — see the note below)
    KOKORO_VOICES_FILE  voices-v1.0.bin
    KOKORO_VOICE        af_heart
    KOKORO_LANG         en-us
    KOKORO_SPEED        1.0
    KOKORO_THREADS      4
"""

from __future__ import annotations

import base64
import io
import json
import os
import sys
import time
import wave

import numpy as np

MODEL_DIR = os.environ.get("KOKORO_MODEL_DIR", os.path.join(os.path.expanduser("~"), ".dsh", "models", "kokoro"))
# fp32, NOT int8: measured on this machine, the quantized model runs at
# real-time factor ~1.15 (slower than playback) while fp32 reaches ~0.32 —
# over three times faster than real time. The int8 kernels are the slow path
# here, so the fp32 weights are the right choice despite being larger.
MODEL_FILE = os.environ.get("KOKORO_MODEL_FILE", "kokoro-v1.0.onnx")
VOICES_FILE = os.environ.get("KOKORO_VOICES_FILE", "voices-v1.0.bin")
DEFAULT_VOICE = os.environ.get("KOKORO_VOICE", "af_heart")
DEFAULT_LANG = os.environ.get("KOKORO_LANG", "en-us")
DEFAULT_SPEED = float(os.environ.get("KOKORO_SPEED", "1.0"))

# Deliberately NOT all cores. Uncapped, fp32 inference pegs every thread and the
# package hits ~89 C and throttles. Four threads measured real-time factor 0.391
# — still 2.5x faster than playback, so streamed chunks stay ahead — while
# dropping average CPU load from ~74% to ~50%. Raise it for more speed, lower it
# for less heat; anything under 1.0 keeps playback gapless.
THREADS = int(os.environ.get("KOKORO_THREADS", "4"))


def install_thread_cap(threads: int) -> None:
    """Force a thread-capped CPU session.

    kokoro-onnx builds its InferenceSession with no SessionOptions, so the only
    way to bound the thread count is to replace the factory it calls. The CPU
    provider is pinned explicitly because DirectML cannot execute this model —
    its encoder ConvTranspose node is rejected by the DML provider.
    """
    import onnxruntime as rt

    def create_session(model_path: str):
        options = rt.SessionOptions()
        options.intra_op_num_threads = threads
        options.inter_op_num_threads = 1
        return rt.InferenceSession(
            model_path, sess_options=options, providers=["CPUExecutionProvider"],
        )

    import kokoro_onnx

    kokoro_onnx.create_session = create_session
    try:
        from kokoro_onnx import session as session_module

        session_module.create_session = create_session
    except Exception:
        pass


def emit(payload: dict) -> None:
    sys.stdout.write(json.dumps(payload) + "\n")
    sys.stdout.flush()


def to_wav(samples, rate: int) -> bytes:
    """Convert Kokoro's float32 samples to canonical mono PCM16 WAV."""
    audio = np.clip(np.asarray(samples, dtype="float32"), -1.0, 1.0)
    pcm = (audio * 32767.0).astype("<i2")
    buffer = io.BytesIO()
    with wave.open(buffer, "wb") as handle:
        handle.setnchannels(1)
        handle.setsampwidth(2)
        handle.setframerate(rate)
        handle.writeframes(pcm.tobytes())
    return buffer.getvalue()


def main() -> int:
    model_path = os.path.join(MODEL_DIR, MODEL_FILE)
    voices_path = os.path.join(MODEL_DIR, VOICES_FILE)
    for path in (model_path, voices_path):
        if not os.path.isfile(path):
            emit({"event": "fatal", "error": f"missing model file: {path}"})
            return 2

    try:
        from kokoro_onnx import Kokoro
    except Exception as exc:  # pragma: no cover - import diagnostics
        emit({"event": "fatal", "error": f"kokoro-onnx import failed: {exc}"})
        return 2

    if THREADS > 0:
        try:
            install_thread_cap(THREADS)
        except Exception as exc:
            emit({"event": "warning", "warning": f"thread cap not applied: {exc}"})

    load_started = time.time()
    try:
        # Kokoro(model_path, voices_path, espeak_config=None, vocab_config=None)
        # It builds its own ONNX Runtime session; espeak-ng comes from
        # espeakng-loader, which bundles the DLL and its data directory.
        kokoro = Kokoro(model_path, voices_path)
    except Exception as exc:
        emit({"event": "fatal", "error": f"model load failed: {exc}"})
        return 2

    # Report the provider actually in use: with ONNX_PROVIDER=DmlExecutionProvider
    # this is the proof the model landed on the GPU rather than the CPU.
    try:
        providers = list(kokoro.sess.get_providers())
    except Exception:
        providers = []

    emit({
        "event": "ready",
        "model": MODEL_FILE,
        "voice": DEFAULT_VOICE,
        "loadSeconds": round(time.time() - load_started, 2),
        "providers": providers,
        "threads": THREADS,
    })

    for line in sys.stdin:
        line = line.strip()
        if not line:
            continue
        try:
            request = json.loads(line)
        except Exception:
            continue

        request_id = request.get("id")
        started = time.time()
        try:
            text = (request.get("text") or "").strip()
            if not text:
                raise ValueError("empty text")
            samples, rate = kokoro.create(
                text,
                voice=request.get("voice") or DEFAULT_VOICE,
                speed=float(request.get("speed") or DEFAULT_SPEED),
                lang=request.get("lang") or DEFAULT_LANG,
            )
            wav = to_wav(samples, rate)
            emit({
                "id": request_id,
                "sampleRate": rate,
                "seconds": round(len(samples) / rate, 2),
                "synthSeconds": round(time.time() - started, 3),
                "chars": len(text),
                "wav": base64.b64encode(wav).decode("ascii"),
            })
        except Exception as exc:
            emit({"id": request_id, "error": str(exc), "synthSeconds": round(time.time() - started, 3)})

    return 0


if __name__ == "__main__":
    sys.exit(main())
