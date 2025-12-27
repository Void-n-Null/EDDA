#!/bin/bash
# View EDDA TTS Docker service logs from basement server
set -e

SERVER_HOST="basement"
SERVER_DIR="/home/blake/edda-server"

echo "Following TTS service logs on $SERVER_HOST (Ctrl+C to stop)..."
echo ""

ssh $SERVER_HOST "cd $SERVER_DIR/docker && docker compose logs -f tts"
