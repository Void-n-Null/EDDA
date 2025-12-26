#!/bin/bash
set -e

SERVER_HOST="basement"
SERVER_DIR="/home/blake/edda-server"
MODEL_DIR="$SERVER_DIR/models"

echo "Downloading Whisper base.en model to basement server..."

ssh $SERVER_HOST "mkdir -p $MODEL_DIR && \
  cd $MODEL_DIR && \
  if [ ! -f ggml-base.en.bin ]; then \
    echo 'Downloading ggml-base.en.bin...'; \
    wget -q --show-progress https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.en.bin; \
    echo 'Download complete.'; \
  else \
    echo 'ggml-base.en.bin already exists.'; \
  fi"

echo ""
echo "Model ready at: $MODEL_DIR/ggml-base.en.bin"
echo ""
echo "To use this model, run:"
echo "  WHISPER_MODEL_PATH_REMOTE=$MODEL_DIR/ggml-base.en.bin ./scripts/dev-server.sh"
echo ""
echo "Or update scripts/dev-server.sh line 6 to:"
echo "  WHISPER_MODEL_PATH_REMOTE=$MODEL_DIR/ggml-base.en.bin"

