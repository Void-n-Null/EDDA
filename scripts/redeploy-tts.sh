#!/bin/bash
# Redeploy all TTS services (dev machine + basement server)
#
# Usage: ./scripts/redeploy-tts.sh [--build]
#   --build    Force rebuild of containers (default: just restart)

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(dirname "$SCRIPT_DIR")"
ENV_FILE="$PROJECT_ROOT/.env"

# Colors
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
NC='\033[0m' # No Color

log_info() { echo -e "${BLUE}[INFO]${NC} $1"; }
log_success() { echo -e "${GREEN}[OK]${NC} $1"; }
log_warn() { echo -e "${YELLOW}[WARN]${NC} $1"; }
log_error() { echo -e "${RED}[ERROR]${NC} $1"; }

# Parse args
BUILD_FLAG=""
if [[ "${1:-}" == "--build" ]]; then
    BUILD_FLAG="--build"
    log_info "Build mode enabled - will rebuild containers"
fi

# Load .env file
if [[ ! -f "$ENV_FILE" ]]; then
    log_error ".env file not found at $ENV_FILE"
    log_error "Create it with: echo 'HF_TOKEN=your_token_here' > $ENV_FILE"
    exit 1
fi

source "$ENV_FILE"

if [[ -z "${HF_TOKEN:-}" || "$HF_TOKEN" == "your_token_here" ]]; then
    log_error "HF_TOKEN not set in $ENV_FILE"
    log_error "Get your token at: https://huggingface.co/settings/tokens"
    exit 1
fi

export HF_TOKEN
log_success "HF_TOKEN loaded from .env"

echo ""
echo "=============================================="
echo "  EDDA TTS Redeployment"
echo "=============================================="
echo ""

# ============================================================================
# Dev Machine (RTX 5070 Ti)
# ============================================================================
log_info "Deploying to DEV MACHINE (RTX 5070 Ti)..."

cd "$PROJECT_ROOT/docker"

# Stop existing
docker-compose -f docker-compose.dev.yml down 2>/dev/null || true

# Start (with optional build)
if [[ -n "$BUILD_FLAG" ]]; then
    docker-compose -f docker-compose.dev.yml up -d --build
else
    docker-compose -f docker-compose.dev.yml up -d
fi

log_success "Dev machine TTS container started"

# ============================================================================
# Basement Server (RTX 2070 Super)
# ============================================================================
log_info "Deploying to BASEMENT SERVER (RTX 2070 Super)..."

# Sync TTS service files
log_info "Syncing tts-service to basement..."
rsync -avz --delete "$PROJECT_ROOT/tts-service/" basement:/home/blake/edda-server/tts-service/ >/dev/null

# Sync docker-compose
rsync -avz "$PROJECT_ROOT/docker/docker-compose.yml" basement:/home/blake/edda-server/docker/ >/dev/null

# Deploy on basement
ssh basement bash -s "$HF_TOKEN" "$BUILD_FLAG" << 'REMOTE_SCRIPT'
    HF_TOKEN="$1"
    BUILD_FLAG="$2"
    export HF_TOKEN
    
    cd /home/blake/edda-server/docker
    
    # Stop existing
    docker compose down tts 2>/dev/null || true
    
    # Start (with optional build)
    if [[ -n "$BUILD_FLAG" ]]; then
        docker compose up -d --build tts
    else
        docker compose up -d tts
    fi
REMOTE_SCRIPT

log_success "Basement server TTS container started"

# ============================================================================
# Wait for health checks
# ============================================================================
echo ""
log_info "Waiting for services to become healthy..."
echo ""

wait_for_health() {
    local name="$1"
    local url="$2"
    local max_wait=120
    local waited=0
    
    while [[ $waited -lt $max_wait ]]; do
        if curl -sf "$url/health" >/dev/null 2>&1; then
            local status=$(curl -s "$url/health" | grep -o '"status":"[^"]*"' | cut -d'"' -f4)
            if [[ "$status" == "healthy" ]]; then
                log_success "$name is healthy"
                return 0
            fi
        fi
        sleep 5
        waited=$((waited + 5))
        echo -ne "\r  Waiting for $name... ${waited}s"
    done
    
    echo ""
    log_warn "$name did not become healthy within ${max_wait}s"
    return 1
}

# Check dev machine
wait_for_health "Dev-5070Ti" "http://localhost:5000" &
DEV_PID=$!

# Check basement (via SSH tunnel or direct if accessible)
# For now, we trust the remote deployment
log_info "Basement deployment initiated (check with: ssh basement 'docker logs edda-tts')"

# Wait for dev
wait $DEV_PID || true

echo ""
echo "=============================================="
log_success "TTS Redeployment Complete!"
echo "=============================================="
echo ""
echo "Endpoints:"
echo "  Dev (5070 Ti):     http://localhost:5000"
echo "  Basement (2070S):  http://10.0.0.176:5000"
echo ""
echo "Check logs:"
echo "  Dev:      docker logs -f edda-tts-dev"
echo "  Basement: ssh basement 'docker logs -f edda-tts'"
echo ""
