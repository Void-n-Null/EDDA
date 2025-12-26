#!/bin/bash
set -e

SERVER_HOST="basement"
SERVER_DIR="/home/blake/edda-server"
MODEL_DIR="$SERVER_DIR/models"

echo "Downloading Whisper tiny.en model to basement server..."

ssh $SERVER_HOST "mkdir -p $MODEL_DIR && \
  cd $MODEL_DIR && \
  if [ ! -f ggml-tiny.en.bin ]; then \
    echo 'Downloading ggml-tiny.en.bin...'; \
    wget -q --show-progress https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-tiny.en.bin; \
    echo 'Download complete.'; \
  else \
    echo 'ggml-tiny.en.bin already exists.'; \
  fi"

echo ""
echo "Model ready at: $MODEL_DIR/ggml-tiny.en.bin"
echo ""
echo "To use this model, run:"
echo "  WHISPER_MODEL_PATH_REMOTE=$MODEL_DIR/ggml-tiny.en.bin ./scripts/dev-server.sh"
echo ""
echo "Or update scripts/dev-server.sh line 6 to:"
echo "  WHISPER_MODEL_PATH_REMOTE=$MODEL_DIR/ggml-tiny.en.bin"

