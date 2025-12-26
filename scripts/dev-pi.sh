#!/bin/bash
set -e

PI_HOST="edda"
PI_DIR="/home/blake/edda-voice-client"

echo "[PI] Deploying EDDA client..."

# Create directory
echo "--- Creating directory..."
ssh $PI_HOST "mkdir -p $PI_DIR"

# Create venv if needed
echo "--- Setting up Python environment..."
ssh $PI_HOST "cd $PI_DIR && if [ ! -d venv ]; then python3 -m venv venv; fi && ./venv/bin/pip install -r requirements.txt"

# Sync files
echo "--- Syncing files..."
rsync -avz --delete \
  --exclude 'venv/' \
  --exclude '__pycache__/' \
  --exclude '*.pyc' \
  pi-client/ $PI_HOST:$PI_DIR/

# Kill old process using different method
echo "--- Stopping old process..."
ssh $PI_HOST 'killall python3 2>/dev/null || true'
# Much simpler ^^^^^^

# Start with unbuffered output
echo "--- Starting client and streaming logs..."
echo "------------------------------------------------"
echo ""

ssh $PI_HOST "cd $PI_DIR && ./venv/bin/python3 -u client.py"