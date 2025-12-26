#!/bin/bash
SERVER_HOST="basement"

echo "Running EDDA server on basement..."
ssh $SERVER_HOST "cd edda-server && dotnet run --project BlakeVoiceAssistant.Server"