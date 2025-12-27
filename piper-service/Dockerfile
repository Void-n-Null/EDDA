# EDDA TTS Service - Piper (Fast Alternative)
#
# Build: docker build -t edda-piper .
# Run:   docker run -p 5001:5001 edda-piper
#
# Piper uses ONNX on CPU - no GPU required, very fast

FROM python:3.11-slim

# Prevent interactive prompts during build
ENV DEBIAN_FRONTEND=noninteractive

# System dependencies - piper binary needs libstdc++ and libgomp
RUN apt-get update && apt-get install -y \
    wget \
    curl \
    libsndfile1 \
    libstdc++6 \
    libgomp1 \
    && rm -rf /var/lib/apt/lists/*

# Install Piper TTS
# The tarball extracts to a 'piper' subdirectory containing the binary and lib/
RUN mkdir -p /opt/piper && \
    cd /opt/piper && \
    wget -q https://github.com/rhasspy/piper/releases/download/2023.11.14-2/piper_linux_x86_64.tar.gz && \
    tar -xzf piper_linux_x86_64.tar.gz && \
    rm piper_linux_x86_64.tar.gz && \
    chmod +x /opt/piper/piper/piper

# Set library path for piper's bundled ONNX runtime
ENV LD_LIBRARY_PATH=/opt/piper/piper/lib:$LD_LIBRARY_PATH
ENV PATH=/opt/piper/piper:$PATH

# Verify binary works
RUN piper --help > /dev/null 2>&1 && echo "Piper binary verified"

# Create app directory
WORKDIR /app

# Install Python dependencies
COPY requirements.txt .
RUN pip install --no-cache-dir --upgrade pip && \
    pip install --no-cache-dir -r requirements.txt

# Create models directory
RUN mkdir -p /app/models

# Download default voice model (lessac medium - good quality, reasonable size)
RUN cd /app/models && \
    wget -q https://huggingface.co/rhasspy/piper-voices/resolve/main/en/en_US/lessac/medium/en_US-lessac-medium.onnx && \
    wget -q https://huggingface.co/rhasspy/piper-voices/resolve/main/en/en_US/lessac/medium/en_US-lessac-medium.onnx.json

# Copy application code
COPY server.py .

# Environment configuration
ENV TTS_PORT=5001 \
    LOG_LEVEL=INFO \
    PIPER_MODEL=en_US-lessac-medium \
    PIPER_MODELS_DIR=/app/models \
    PYTHONUNBUFFERED=1

# Expose port
EXPOSE 5001

# Health check
HEALTHCHECK --interval=30s --timeout=10s --start-period=30s --retries=3 \
    CMD curl -f http://localhost:5001/health || exit 1

# Run the server
CMD ["python", "-m", "uvicorn", "server:app", "--host", "0.0.0.0", "--port", "5001"]

