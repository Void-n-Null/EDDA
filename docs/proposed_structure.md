/mnt/dev/EDDA/
├── .git/
├── .vscode/
│   ├── tasks.json          # Deploy commands
│   ├── launch.json         # Debug configs
│   └── settings.json       # Workspace settings
│
├── docs/
│   ├── ARCHITECTURE.md     # The 50-page spec we made
│   ├── deployment.md
│   └── diagrams/
│
├── server/                 # C# Backend (runs on basement server)
│   ├── src/
│   │   ├── BlakeVoiceAssistant.Server/
│   │   │   ├── Program.cs
│   │   │   ├── appsettings.json
│   │   │   ├── appsettings.Development.json
│   │   │   └── BlakeVoiceAssistant.Server.csproj
│   │   └── BlakeVoiceAssistant.sln
│   ├── scripts/
│   │   ├── deploy.sh       # Deploy to basement server
│   │   └── logs.sh         # Tail logs from server
│   └── README.md
│
├── tts-service/            # Python TTS (runs on basement server)
│   ├── server.py
│   ├── requirements.txt
│   ├── Dockerfile
│   ├── .dockerignore
│   └── README.md
│
├── pi-client/              # Python client (runs on Raspberry Pi)
│   ├── client.py
│   ├── config.yaml
│   ├── requirements.txt
│   ├── systemd/
│   │   └── edda-client.service
│   ├── scripts/
│   │   ├── deploy.sh       # Deploy to Pi
│   │   └── logs.sh         # Tail logs from Pi
│   └── README.md
│
├── docker/                 # Docker configs for basement server
│   ├── docker-compose.yml
│   └── docker-compose.dev.yml
│
├── scripts/                # Global deployment scripts
│   ├── deploy-all.sh       # Deploy everything
│   ├── deploy-server.sh    # Server only
│   ├── deploy-pi.sh        # Pi only
│   ├── logs-server.sh      # Server logs
│   └── logs-pi.sh          # Pi logs
│
├── .gitignore
├── README.md
└── LICENSE