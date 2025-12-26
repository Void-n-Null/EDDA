#!/bin/bash
set -e

SERVER_HOST="basement"
SERVER_DIR="/home/blake/edda-server"
MODEL_DIR="$SERVER_DIR/models"

echo "Downloading Whisper small.en quantized models to basement server..."
echo "(Target: ~10-15× realtime on CPU with 97-98% accuracy)"
echo ""

ssh $SERVER_HOST "mkdir -p $MODEL_DIR && cd $MODEL_DIR && \

# Try Q5_0 (best balance: ~same quality, ~2x faster than non-quantized)
if [ ! -f ggml-small.en-q5_0.bin ]; then
  echo 'Attempting to download ggml-small.en-q5_0.bin...';
  if wget -q --show-progress https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small.en-q5_0.bin 2>/dev/null; then
    echo '✓ Q5_0 quantized model downloaded.';
  else
    echo '⚠ Q5_0 not available, trying Q8_0...';
    # Fallback to Q8_0 (higher quality, still faster than non-quantized)
    if wget -q --show-progress https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small.en-q8_0.bin 2>/dev/null; then
      echo '✓ Q8_0 quantized model downloaded.';
    else
      echo '⚠ Quantized models not found. Falling back to non-quantized small.en...';
      if [ ! -f ggml-small.en.bin ]; then
        wget -q --show-progress https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small.en.bin;
        echo '✓ Non-quantized small.en downloaded.';
      fi
    fi
  fi
else
  echo 'ggml-small.en-q5_0.bin already exists.';
fi
"

# Determine which model was downloaded
MODEL_NAME=$(ssh $SERVER_HOST "cd $MODEL_DIR && ls ggml-small.en*.bin 2>/dev/null | head -n1")

echo ""
echo "✓ Model ready: $MODEL_DIR/$MODEL_NAME"
echo ""
echo "To use this model, update scripts/dev-server.sh line 6 to:"
echo "  WHISPER_MODEL_PATH_REMOTE=$MODEL_DIR/$MODEL_NAME"
echo ""
echo "Expected performance:"
echo "  • Q5_0: ~10-15× realtime, 97-98% accuracy"
echo "  • Q8_0: ~8-12× realtime, 98-99% accuracy"
echo "  • Non-quantized: ~6× realtime, 98% accuracy"

