#!/bin/bash
set -e

SERVER_HOST="basement"
SERVER_DIR="/home/blake/edda-server"
WHISPER_MODEL_PATH_REMOTE=/home/blake/edda-server/models/ggml-small.en-q8_0.bin

echo "Deploying EDDA server..."

# Sync files
rsync -avz --delete \
  --exclude 'bin/' \
  --exclude 'obj/' \
  --exclude '.git/' \
  --exclude 'models/' \
  server/src/ $SERVER_HOST:$SERVER_DIR/ 2>&1 | grep -v "WARNING: connection"

# Build
echo "Building..."
ssh $SERVER_HOST "cd $SERVER_DIR && dotnet build EDDA.sln --configuration Release" 2>&1 | grep -v "WARNING: connection"

# Kill old process
echo "Stopping old process..."
ssh $SERVER_HOST "pkill -f 'dotnet.*EDDA.Server' || true; pkill -f 'EDDA\\.Server' || true" 2>&1 | grep -v "WARNING: connection"

# Clear old log
ssh $SERVER_HOST "rm -f /tmp/edda.log" 2>&1 | grep -v "WARNING: connection"

# Start and immediately tail logs
echo "Starting server and streaming logs..."
echo "------------------------------------------------"
echo ""

# Run with CUDA GPU acceleration. Keep current dir on the loader path for native whisper libs.
ssh -t $SERVER_HOST "bash -l -c 'cd $SERVER_DIR/EDDA.Server/bin/Release/net8.0 && \
export PATH=/usr/local/cuda/bin:\$PATH && \
export LD_LIBRARY_PATH=.:/usr/local/cuda/lib64:/usr/local/cuda/targets/x86_64-linux/lib:\$LD_LIBRARY_PATH && \
export WHISPER_MODEL_PATH=$WHISPER_MODEL_PATH_REMOTE && \
export WHISPER_MAX_AUDIO_SECONDS=60 && \
export WHISPER_SILENCE_SECONDS=1.5 && \
export WHISPER_THREADS=4 && \
./EDDA.Server'"
