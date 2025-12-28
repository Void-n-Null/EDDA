# EDDA TTS Service - Chatterbox Turbo
# 
# Build: docker build -t edda-tts .
# Run:   docker run --gpus all -p 5000:5000 edda-tts
#
# CUDA 12.1 runtime for PyTorch 2.2+ (works on RTX 20/30/40 series)
# For RTX 50-series (Blackwell), use Dockerfile.blackwell

FROM nvidia/cuda:12.1.1-runtime-ubuntu22.04

# Prevent interactive prompts during build
ENV DEBIAN_FRONTEND=noninteractive

# System dependencies
RUN apt-get update && apt-get install -y \
    python3.11 \
    python3.11-venv \
    python3-pip \
    libsndfile1 \
    ffmpeg \
    curl \
    && rm -rf /var/lib/apt/lists/* \
    && ln -sf /usr/bin/python3.11 /usr/bin/python3 \
    && ln -sf /usr/bin/python3.11 /usr/bin/python

# Create non-root user for security
RUN useradd -m -u 1000 edda
WORKDIR /app

# Install Python dependencies first (cache layer)
COPY requirements.txt .
RUN pip install --no-cache-dir --upgrade pip && \
    pip install --no-cache-dir -r requirements.txt

# Copy application code
COPY server.py .

# Set ownership
RUN chown -R edda:edda /app

# Switch to non-root user
USER edda

# Environment configuration
ENV TTS_DEVICE=cuda \
    TTS_PORT=5000 \
    LOG_LEVEL=INFO \
    PYTHONUNBUFFERED=1

# Expose port
EXPOSE 5000

# Health check
HEALTHCHECK --interval=30s --timeout=10s --start-period=120s --retries=3 \
    CMD curl -f http://localhost:5000/health || exit 1

# Run the server
CMD ["python", "-m", "uvicorn", "server:app", "--host", "0.0.0.0", "--port", "5000"]

