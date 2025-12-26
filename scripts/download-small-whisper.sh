#!/bin/bash
set -e

SERVER_HOST="basement"
SERVER_DIR="/home/blake/edda-server"
MODEL_DIR="$SERVER_DIR/models"

echo "Downloading Whisper small.en model to basement server..."
echo "(Best balance: ~6-10x realtime on CPU, 97-98% accuracy vs large models)"
echo ""

ssh $SERVER_HOST "mkdir -p $MODEL_DIR && \
  cd $MODEL_DIR && \
  if [ ! -f ggml-small.en.bin ]; then \
    echo 'Downloading ggml-small.en.bin (~466MB)...'; \
    wget -q --show-progress https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small.en.bin; \
    echo 'Download complete.'; \
  else \
    echo 'ggml-small.en.bin already exists.'; \
  fi"

echo ""
echo "✓ Model ready at: $MODEL_DIR/ggml-small.en.bin"
echo ""
echo "To use this model, run:"
echo "  WHISPER_MODEL_PATH_REMOTE=$MODEL_DIR/ggml-small.en.bin ./scripts/dev-server.sh"
echo ""
echo "Or update scripts/dev-server.sh line 6 to:"
echo "  WHISPER_MODEL_PATH_REMOTE=$MODEL_DIR/ggml-small.en.bin"
echo ""
echo "Expected performance: ~6-10× realtime on CPU (60s audio → 6-10s processing)"

