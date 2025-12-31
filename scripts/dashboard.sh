#!/bin/bash
# Start the EDDA Log Dashboard
set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(dirname "$SCRIPT_DIR")"
DASHBOARD_DIR="$PROJECT_ROOT/dashboard"

cd "$DASHBOARD_DIR"

echo "Starting EDDA Log Dashboard..."
echo "  Frontend: http://localhost:3000"
echo "  Backend:  ws://localhost:3001"
echo ""

bun run dev
