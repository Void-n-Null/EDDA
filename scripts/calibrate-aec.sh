#!/bin/bash
# Calibrate AEC delay on the Raspberry Pi
# This finds the optimal speaker_to_mic_delay_ms value

set -e

PI_HOST="edda"
PI_PATH="/home/blake/edda-voice-client"

echo "[CALIBRATE] Syncing calibration script to Pi..."
rsync -av --progress \
    "$(dirname "$0")/../pi-client/calibrate_aec.py" \
    "${PI_HOST}:${PI_PATH}/"

echo ""
echo "[CALIBRATE] Running calibration on Pi..."
echo "=============================================="
ssh -t "${PI_HOST}" "cd ${PI_PATH} && source venv/bin/activate && python calibrate_aec.py"
