#!/bin/bash
# Deploy and start Docker services on the basement server
# This syncs the TTS service and docker compose files, then starts containers.
set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(dirname "$SCRIPT_DIR")"

SERVER_HOST="basement"
SERVER_DIR="/home/blake/edda-server"

echo "Deploying Docker services to basement server..."
echo ""

# Sync Docker directory
echo "[1/5] Syncing docker compose configuration..."
ssh $SERVER_HOST "mkdir -p $SERVER_DIR/docker"
rsync -av --delete \
    "$PROJECT_ROOT/docker/" \
    "$SERVER_HOST:$SERVER_DIR/docker/"

# Sync Chatterbox TTS service
echo "[2/5] Syncing Chatterbox TTS service..."
ssh $SERVER_HOST "mkdir -p $SERVER_DIR/tts-service"
rsync -av --delete \
    "$PROJECT_ROOT/tts-service/" \
    "$SERVER_HOST:$SERVER_DIR/tts-service/"

# Sync Piper TTS service
echo "[3/5] Syncing Piper TTS service..."
ssh $SERVER_HOST "mkdir -p $SERVER_DIR/piper-service"
rsync -av --delete \
    "$PROJECT_ROOT/piper-service/" \
    "$SERVER_HOST:$SERVER_DIR/piper-service/"

# Sync voices directory
echo "[4/5] Syncing voices directory..."
ssh $SERVER_HOST "mkdir -p $SERVER_DIR/voices"
rsync -av \
    "$PROJECT_ROOT/voices/" \
    "$SERVER_HOST:$SERVER_DIR/voices/"

# Start Docker services
echo "[5/5] Starting Docker containers..."
ssh $SERVER_HOST "cd $SERVER_DIR/docker && docker compose up -d --build"

echo ""
echo "Waiting for services to be ready..."

# Wait for TTS service
echo -n "TTS Service: "
for i in {1..90}; do
    if ssh $SERVER_HOST "curl -sf http://localhost:5000/health" > /dev/null 2>&1; then
        echo "✓ Ready"
        break
    fi
    if [ $i -eq 90 ]; then
        echo ""
        echo "⚠ TTS service taking longer than expected. Check logs:"
        echo "  ssh basement 'cd $SERVER_DIR/docker && docker compose logs tts'"
        break
    fi
    echo -n "."
    sleep 2
done

# Wait for Qdrant
echo -n "Qdrant:      "
for i in {1..30}; do
    if ssh $SERVER_HOST "curl -sf http://localhost:6333/readiness" > /dev/null 2>&1; then
        echo "✓ Ready"
        break
    fi
    if [ $i -eq 30 ]; then
        echo ""
        echo "⚠ Qdrant taking longer than expected. Check logs:"
        echo "  ssh basement 'cd $SERVER_DIR/docker && docker compose logs qdrant'"
        break
    fi
    echo -n "."
    sleep 1
done

echo ""
echo "Docker services deployed!"
echo ""
echo "To view logs:  ssh basement 'cd $SERVER_DIR/docker && docker compose logs -f'"
echo "To stop:       ssh basement 'cd $SERVER_DIR/docker && docker compose down'"

