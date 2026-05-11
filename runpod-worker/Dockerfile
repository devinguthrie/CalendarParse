# CalendarParse RunPod Serverless Worker
#
# Bakes the glm-ocr model into the image so cold starts don't download ~4 GB.
# Build:  docker build -t calendarparse-runpod-worker .
# Push:   docker tag calendarparse-runpod-worker <your-registry>/calendarparse-runpod-worker:latest
#         docker push <your-registry>/calendarparse-runpod-worker:latest
#
# On RunPod: create a Serverless endpoint pointing at this image.
# Set OLLAMA_MODEL env var if using a different model (default: glm-ocr).

FROM ollama/ollama:latest

# Install Python 3 + pip
RUN apt-get update && apt-get install -y --no-install-recommends \
        python3 \
        python3-pip \
    && rm -rf /var/lib/apt/lists/*

COPY requirements.txt .
RUN pip3 install --no-cache-dir -r requirements.txt

# Bake glm-ocr weights into the image (~4–5 GB) so the worker is warm immediately.
# This runs Ollama briefly during build to pull the model into /root/.ollama/models.
RUN ollama serve & \
    sleep 5 && \
    ollama pull glm-ocr && \
    pkill ollama || true

COPY handler.py .

CMD ["python3", "handler.py"]
