#!/bin/bash
# View EDDA Docker service logs
set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(dirname "$SCRIPT_DIR")"
DOCKER_DIR="$PROJECT_ROOT/docker"

cd "$DOCKER_DIR"

# Default to following all logs
SERVICE="${1:-}"

if [ -z "$SERVICE" ]; then
    echo "Following all service logs (Ctrl+C to stop)..."
    docker compose logs -f
else
    echo "Following $SERVICE logs (Ctrl+C to stop)..."
    docker compose logs -f "$SERVICE"
fi

