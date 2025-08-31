#!/bin/bash

# Automated deployment script with sudo password handling
# Usage: ./deploy-with-password.sh <remote-host> <remote-user> <password>

set -e

REMOTE_HOST="${1}"
REMOTE_USER="${2}"
PASSWORD="${3}"

if [ -z "$REMOTE_HOST" ] || [ -z "$REMOTE_USER" ] || [ -z "$PASSWORD" ]; then
    echo "Usage: $0 <remote-host> <remote-user> <password>"
    echo "Example: $0 10.0.0.75 pi mypassword"
    exit 1
fi

echo "🚀 Deploying PrintFarmer to ${REMOTE_USER}@${REMOTE_HOST}"

# Function to run commands with password
run_remote_sudo() {
    local cmd="$1"
    echo "Executing: $cmd"
    sshpass -p "$PASSWORD" ssh "$REMOTE_USER@$REMOTE_HOST" "echo '$PASSWORD' | sudo -S $cmd"
}

run_remote() {
    local cmd="$1"
    echo "Executing: $cmd"
    sshpass -p "$PASSWORD" ssh "$REMOTE_USER@$REMOTE_HOST" "$cmd"
}

echo "� Checking system and Docker installation..."
# Check system info and Docker installation
run_remote "uname -a"
run_remote "which docker || echo 'Docker command not found'"
run_remote "docker --version || echo 'Docker not working'"

echo "�📝 Extracting clean deployment package..."
run_remote "cd /home/$REMOTE_USER && tar -xzf printfarmer-deployment-clean-10.0.0.75.tar.gz"

echo "🧹 Cleaning up macOS extended attributes..."
# Clean up any macOS extended attribute files that might cause issues
run_remote "find /home/$REMOTE_USER/src -name '._*' -delete 2>/dev/null || true"
run_remote "find /home/$REMOTE_USER/src -name '.DS_Store' -delete 2>/dev/null || true"

echo "🔧 Setting up Docker permissions..."
run_remote_sudo "usermod -aG docker $REMOTE_USER || echo 'User already in docker group'"

# Check if Docker service exists and handle different installation types
echo "🐳 Checking Docker service..."
run_remote "sudo systemctl status docker 2>/dev/null || sudo service docker status 2>/dev/null || echo 'Docker service check failed'"

# Try different ways to start Docker
echo "🔄 Starting Docker service..."
run_remote_sudo "systemctl start docker 2>/dev/null || service docker start 2>/dev/null || echo 'Docker service start - trying direct'"

# Enable Docker if systemctl is available
echo "⚙️ Enabling Docker service..."
run_remote_sudo "systemctl enable docker 2>/dev/null || echo 'Systemctl not available or Docker already enabled'"

# Verify Docker is working
echo "✅ Verifying Docker installation..."
run_remote "docker --version"
run_remote "docker info >/dev/null 2>&1 && echo 'Docker daemon is running' || echo 'Docker daemon not accessible - trying with sudo'"

echo "🐳 Deploying application..."
run_remote "cd /home/$REMOTE_USER/src"

# Try without sudo first, then with sudo
echo "🚀 Starting containers..."
if run_remote "cd /home/$REMOTE_USER/src && docker compose up --build -d" 2>/dev/null; then
    echo "✅ Deployed without sudo"
else
    echo "🔐 Trying with sudo..."
    run_remote "cd /home/$REMOTE_USER/src && echo '$PASSWORD' | sudo -S docker compose up --build -d"
fi

echo "📊 Checking deployment status..."
sleep 5
if run_remote "cd /home/$REMOTE_USER/src && docker compose ps" 2>/dev/null; then
    echo "✅ Container status retrieved without sudo"
else
    echo "🔐 Getting container status with sudo..."
    run_remote "cd /home/$REMOTE_USER/src && echo '$PASSWORD' | sudo -S docker compose ps"
fi

echo "✅ Deployment complete!"
echo "🌐 Application available at: http://${REMOTE_HOST}:8081"
echo "📊 Health check: http://${REMOTE_HOST}:8081/healthz"

# Test the deployment
echo "🧪 Testing deployment..."
sleep 10
if curl -s --connect-timeout 5 "http://${REMOTE_HOST}:8081/healthz" | grep -q "ok"; then
    echo "✅ Health check passed!"
    echo "🎉 Deployment successful!"
else
    echo "⚠️ Health check failed - checking logs..."
    run_remote "cd /home/$REMOTE_USER/src && (docker compose logs 2>/dev/null || echo '$PASSWORD' | sudo -S docker compose logs)"
fi
