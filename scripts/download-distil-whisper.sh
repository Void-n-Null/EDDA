#!/bin/bash
set -euo pipefail

SERVER_HOST="${SERVER_HOST:-basement}"
SERVER_DIR="${SERVER_DIR:-/home/blake/edda-server}"

# Default filename from the model card:
# https://huggingface.co/distil-whisper/distil-large-v3-ggml
MODEL_FILENAME="${MODEL_FILENAME:-ggml-distil-large-v3.bin}"
MODEL_URL="${MODEL_URL:-https://huggingface.co/distil-whisper/distil-large-v3-ggml/resolve/main/${MODEL_FILENAME}}"

echo "[DL] Downloading distil-whisper model to ${SERVER_HOST}:${SERVER_DIR}/models/${MODEL_FILENAME}"

ssh "${SERVER_HOST}" "mkdir -p '${SERVER_DIR}/models' && \
  command -v wget >/dev/null 2>&1 || { echo '[DL] ERROR: wget not installed on server'; exit 2; } && \
  wget -O '${SERVER_DIR}/models/${MODEL_FILENAME}' '${MODEL_URL}'"

echo ""
echo "[DL] Done."
echo "[DL] To use it for dev runs:"
echo "     WHISPER_MODEL_PATH_REMOTE=${SERVER_DIR}/models/${MODEL_FILENAME} ./scripts/dev-server.sh"


