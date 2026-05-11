"""
CalendarParse RunPod Serverless Worker

Accepts the standard Ollama /api/generate JSON body as `job["input"]` and
forwards it to a local Ollama instance running glm-ocr.

Ollama is started once at module load so it is warm before the first request.
This avoids burning the /runsync wait window on cold-start model loading.
"""

import runpod
import requests
import subprocess
import time
import os

OLLAMA_HOST = "http://localhost:11434"
MODEL_NAME  = os.getenv("OLLAMA_MODEL", "glm-ocr")


def _start_ollama() -> None:
    """Launch Ollama server and wait for it to become ready."""
    subprocess.Popen(["ollama", "serve"], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    # Poll until the API responds — typically < 3 seconds on a warm GPU node.
    for _ in range(30):
        try:
            resp = requests.get(f"{OLLAMA_HOST}/api/tags", timeout=2)
            if resp.status_code == 200:
                return
        except requests.ConnectionError:
            pass
        time.sleep(1)
    raise RuntimeError("Ollama failed to start within 30 seconds.")


def _ensure_model() -> None:
    """Pull the model if it is not already present (warm workers skip this)."""
    resp = requests.get(f"{OLLAMA_HOST}/api/tags", timeout=5)
    models = [m["name"] for m in resp.json().get("models", [])]
    if not any(MODEL_NAME in m for m in models):
        print(f"[worker] Pulling {MODEL_NAME} — this may take several minutes on first run.")
        subprocess.run(["ollama", "pull", MODEL_NAME], check=True)
        print(f"[worker] {MODEL_NAME} ready.")


# Initialize once at module load (warm on first request, instant on reuse)
_start_ollama()
_ensure_model()


def handler(job: dict) -> dict:
    """
    Entry point for each RunPod job.

    Input:  job["input"] — standard Ollama /api/generate JSON body
    Output: the Ollama /api/generate JSON response (forwarded as-is)
    """
    payload = job.get("input", {})
    if not payload:
        return {"error": "Missing 'input' in job payload."}

    try:
        resp = requests.post(
            f"{OLLAMA_HOST}/api/generate",
            json=payload,
            timeout=600,  # 10-minute hard cap; GLM-OCR inference is typically <90s
        )
        resp.raise_for_status()
        return resp.json()
    except requests.HTTPError as exc:
        return {"error": f"Ollama HTTP error: {exc.response.status_code} — {exc.response.text[:200]}"}
    except requests.Timeout:
        return {"error": "Ollama inference timed out after 600 seconds."}
    except Exception as exc:  # noqa: BLE001
        return {"error": f"Unexpected error: {exc}"}


if __name__ == "__main__":
    runpod.serverless.start({"handler": handler})
