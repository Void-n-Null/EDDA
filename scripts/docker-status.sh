#!/bin/bash
# Check status of Docker services on basement server
set -e

SERVER_HOST="basement"
SERVER_DIR="/home/blake/edda-server"

echo "EDDA Docker Service Status"
echo "=========================="
echo ""

# Check if containers are running
echo "Containers:"
ssh $SERVER_HOST "cd $SERVER_DIR/docker && docker compose ps" 2>/dev/null || echo "  Docker Compose not running"

echo ""
echo "Health Checks:"

# TTS Health
echo -n "  TTS Service: "
TTS_HEALTH=$(ssh $SERVER_HOST "curl -sf http://localhost:5000/health" 2>/dev/null)
if [ -n "$TTS_HEALTH" ]; then
    STATUS=$(echo "$TTS_HEALTH" | grep -o '"status":"[^"]*"' | cut -d'"' -f4)
    VRAM=$(echo "$TTS_HEALTH" | grep -o '"vram_used_gb":[0-9.]*' | cut -d':' -f2)
    echo "$STATUS (VRAM: ${VRAM}GB)"
else
    echo "unavailable"
fi

# Qdrant Health
echo -n "  Qdrant:      "
if ssh $SERVER_HOST "curl -sf http://localhost:6333/readiness" > /dev/null 2>&1; then
    echo "ready"
else
    echo "unavailable"
fi

echo ""

