#!/bin/bash
# Setup EDDA TTS service on dev machine (5070 Ti - Blackwell)
# Run this script once to configure auto-start on boot

set -e

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
PROJECT_DIR="$(dirname "$SCRIPT_DIR")"

echo "=== EDDA TTS Dev Setup (RTX 5070 Ti) ==="
echo ""

# Check for docker-compose
if ! command -v docker-compose &>/dev/null; then
    echo "❌ docker-compose not installed!"
    echo ""
    echo "Run: sudo pacman -S docker-compose"
    exit 1
fi

# Check for NVIDIA Container Toolkit
if ! docker info 2>/dev/null | grep -q "nvidia"; then
    echo "❌ NVIDIA Container Toolkit not configured!"
    echo ""
    echo "Run these commands first:"
    echo "  sudo pacman -S nvidia-container-toolkit"
    echo "  sudo nvidia-ctk runtime configure --runtime=docker"
    echo "  sudo systemctl restart docker"
    echo ""
    echo "Then run this script again."
    exit 1
fi

echo "✓ NVIDIA Container Toolkit detected"

# Install systemd service
echo ""
echo "Installing systemd service..."
sudo cp "$SCRIPT_DIR/edda-tts-dev.service" /etc/systemd/system/
sudo systemctl daemon-reload
sudo systemctl enable edda-tts-dev

echo "✓ Service installed and enabled"

# Build the container (uses Dockerfile.blackwell for RTX 50-series)
echo ""
echo "Building TTS container with CUDA 12.8 for Blackwell..."
echo "(This may take several minutes on first build)"
cd "$PROJECT_DIR/docker"
docker-compose -f docker-compose.dev.yml build

echo "✓ Container built"

# Start the service
echo ""
echo "Starting TTS service..."
sudo systemctl start edda-tts-dev

echo ""
echo "=== Setup Complete ==="
echo ""
echo "TTS service is now running and will start on boot."
echo ""
echo "Useful commands:"
echo "  docker logs -f edda-tts-dev     # View logs"
echo "  sudo systemctl status edda-tts-dev  # Check status"
echo "  sudo systemctl stop edda-tts-dev    # Stop service"
echo "  curl http://localhost:5000/health   # Health check"
echo ""
echo "Basement server will auto-detect this at 10.0.0.210:5000"
