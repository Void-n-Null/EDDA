#!/bin/bash
set -e

PI_HOST="edda"
PI_DIR="/home/blake/edda-voice-client"

echo "[PI] Deploying EDDA client..."

# Create directory
echo "--- Creating directory..."
ssh $PI_HOST "mkdir -p $PI_DIR"

# Recreate venv if --fresh flag is passed, otherwise create if missing
if [ "$1" = "--fresh" ]; then
    echo "--- Recreating Python environment (fresh install)..."
    ssh $PI_HOST "cd $PI_DIR && rm -rf venv && python3 -m venv venv"
else
    echo "--- Creating Python environment if needed..."
    ssh $PI_HOST "cd $PI_DIR && if [ ! -d venv ]; then python3 -m venv venv; fi"
fi

# Sync files FIRST (so requirements.txt is up to date)
echo "--- Syncing files..."
rsync -avz --delete \
  --exclude 'venv/' \
  --exclude '__pycache__/' \
  --exclude '*.pyc' \
  pi-client/ $PI_HOST:$PI_DIR/

# THEN install requirements (use --force-reinstall for numpy if needed)
echo "--- Installing Python dependencies..."
ssh $PI_HOST "cd $PI_DIR && ./venv/bin/pip install --upgrade -r requirements.txt"

# Kill old process using different method
echo "--- Stopping old process..."
ssh $PI_HOST 'killall python3 2>/dev/null || true'
# Much simpler ^^^^^^

# Start with unbuffered output
echo "--- Starting client and streaming logs..."
echo "------------------------------------------------"
echo ""

ssh $PI_HOST "cd $PI_DIR && ./venv/bin/python3 -u client.py"