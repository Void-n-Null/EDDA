#!/bin/bash
# Deploy Docker services to basement server and stream logs
# Usage: ./scripts/dev-docker.sh [service]
#   service: optional, "tts" or "qdrant" to only show that service's logs
set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(dirname "$SCRIPT_DIR")"

SERVER_HOST="basement"
SERVER_DIR="/home/blake/edda-server"
SERVICE="${1:-}"

echo "=========================================="
echo "EDDA Docker - Deploy & Run"
echo "=========================================="
echo ""

# Sync Docker directory
echo "[1/4] Syncing docker compose configuration..."
ssh $SERVER_HOST "mkdir -p $SERVER_DIR/docker"
rsync -av --delete \
    "$PROJECT_ROOT/docker/" \
    "$SERVER_HOST:$SERVER_DIR/docker/" 2>&1 | grep -v "^$"

# Sync Chatterbox TTS service
echo "[2/4] Syncing Chatterbox TTS service..."
ssh $SERVER_HOST "mkdir -p $SERVER_DIR/tts-service"
rsync -av --delete \
    "$PROJECT_ROOT/tts-service/" \
    "$SERVER_HOST:$SERVER_DIR/tts-service/" 2>&1 | grep -v "^$"

# Sync Piper TTS service
echo "[3/4] Syncing Piper TTS service..."
ssh $SERVER_HOST "mkdir -p $SERVER_DIR/piper-service"
rsync -av --delete \
    "$PROJECT_ROOT/piper-service/" \
    "$SERVER_HOST:$SERVER_DIR/piper-service/" 2>&1 | grep -v "^$"

# Sync voices directory (if exists)
if [ -d "$PROJECT_ROOT/voices" ]; then
    echo "       Syncing voices..."
    ssh $SERVER_HOST "mkdir -p $SERVER_DIR/voices"
    rsync -av \
        "$PROJECT_ROOT/voices/" \
        "$SERVER_HOST:$SERVER_DIR/voices/" 2>&1 | grep -v "^$"
fi

# Build and start containers
echo "[4/4] Building and starting Docker containers..."
ssh $SERVER_HOST "cd $SERVER_DIR/docker && docker compose up -d --build"

echo ""
echo "Waiting for services to initialize..."
sleep 3

# Show status
echo ""
ssh $SERVER_HOST "cd $SERVER_DIR/docker && docker compose ps"
echo ""

echo "=========================================="
echo "Following logs (Ctrl+C to stop)..."
echo "=========================================="
echo ""

# Follow logs via SSH
if [ -z "$SERVICE" ]; then
    ssh $SERVER_HOST "cd $SERVER_DIR/docker && docker compose logs -f"
else
    ssh $SERVER_HOST "cd $SERVER_DIR/docker && docker compose logs -f $SERVICE"
fi
