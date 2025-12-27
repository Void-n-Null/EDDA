#!/bin/bash
# Start EDDA Docker services (TTS + Qdrant)
set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(dirname "$SCRIPT_DIR")"
DOCKER_DIR="$PROJECT_ROOT/docker"

cd "$DOCKER_DIR"

echo "Starting EDDA Docker services..."
echo "  - TTS Service (Chatterbox Turbo)"
echo "  - Qdrant Vector Database"
echo ""

# Build and start services
docker compose up -d --build

echo ""
echo "Waiting for services to be healthy..."

# Wait for TTS service (can take a while for model loading)
echo -n "TTS Service: "
for i in {1..60}; do
    if curl -sf http://localhost:5000/health > /dev/null 2>&1; then
        echo "✓ Ready"
        break
    fi
    if [ $i -eq 60 ]; then
        echo "✗ Timeout (check logs: docker compose logs tts)"
        exit 1
    fi
    echo -n "."
    sleep 2
done

# Wait for Qdrant
echo -n "Qdrant:      "
for i in {1..30}; do
    if curl -sf http://localhost:6333/readiness > /dev/null 2>&1; then
        echo "✓ Ready"
        break
    fi
    if [ $i -eq 30 ]; then
        echo "✗ Timeout (check logs: docker compose logs qdrant)"
        exit 1
    fi
    echo -n "."
    sleep 1
done

echo ""
echo "All services running!"
echo ""
echo "Service URLs:"
echo "  TTS:    http://localhost:5000"
echo "  Qdrant: http://localhost:6333"
echo ""
echo "View logs: cd docker && docker compose logs -f"

