#!/bin/bash
set -e

echo "🚀 Setting up PrintFarmer React Development Environment..."

# Support an optional --verify flag or DEVCONTAINER_VERIFY env var to run smoke-tests after provisioning
DEVCONTAINER_VERIFY=${DEVCONTAINER_VERIFY:-0}
if [ "$#" -gt 0 ]; then
    while [ "$#" -gt 0 ]; do
        case "$1" in
            --verify)
                DEVCONTAINER_VERIFY=1
                shift
                ;;
            *) shift ;;
        esac
    done
fi

if [ "${SKIP_APT:-0}" != "1" ]; then
    # Update system packages
    echo "📦 Updating system packages..."
    sudo apt-get update

    # Install additional tools
    echo "🛠️  Installing additional development tools..."
    sudo apt-get install -y curl wget git jq
else
    echo "⏭️  Skipping apt-get operations (SKIP_APT=${SKIP_APT})"
fi

if [ "${SKIP_GLOBAL_NPM:-0}" != "1" ]; then
    # Ensure latest npm and install global packages
    echo "📦 Setting up Node.js global packages..."
    sudo npm install -g npm@latest
    sudo npm install -g @vitejs/create-vite
    sudo npm install -g typescript
    sudo npm install -g eslint
    sudo npm install -g prettier
else
    echo "⏭️  Skipping global npm installs (SKIP_GLOBAL_NPM=${SKIP_GLOBAL_NPM})"
fi

# Install .NET global tools
echo "🔧 Installing .NET global tools..."
dotnet tool install --global dotnet-ef
dotnet tool install --global dotnet-watch
dotnet tool install --global dotnet-user-secrets

# Restore .NET solution
echo "🔄 Restoring .NET solution..."
if [ -f "src/farm-web.sln" ]; then
    dotnet restore ./src/farm-web.sln
else
    echo "⚠️  Solution file not found, skipping .NET restore"
fi

# Install React app dependencies if the app exists
echo "📱 Setting up React application..."
if [ -d "src/Web/ReactApp" ]; then
    cd src/Web/ReactApp
    # Fix ownership of node_modules volume mount (may be created as root)
    if [ -d "node_modules" ]; then
        echo "🔧 Fixing node_modules ownership..."
        sudo chown -R $(id -u):$(id -g) node_modules 2>/dev/null || true
    fi
    echo "📦 Installing React dependencies..."
    npm ci
    cd ../../../
else
    echo "ℹ️  React app not found - will be created during Phase 1 implementation"
fi

# Set up environment file
echo "⚙️  Setting up environment configuration..."
if [ ! -f ".env" ]; then
    cp .env.template .env
    echo "📝 Created .env file from template - please configure it for your environment"
fi

# Create useful aliases
echo "🔗 Setting up development aliases..."
cat >> ~/.bashrc << 'EOF'

# PrintFarmer development aliases (use DEVCONTAINER_WORKSPACE_FOLDER if available)
WORKSPACE_DIR=${DEVCONTAINER_WORKSPACE_FOLDER:-/workspaces/$(basename "$(git rev-parse --show-toplevel 2>/dev/null || echo PrintFarmer)")}
alias pf-api='cd "$WORKSPACE_DIR"/src && dotnet watch --project api/Farm.Web.Api.csproj run'
alias pf-react='cd "$WORKSPACE_DIR"/src/Web/ReactApp && npm run dev'
alias pf-build='cd "$WORKSPACE_DIR" && cd ./src && dotnet build ./farm-web.sln -c Debug && cd ./Web/ReactApp && npm ci && npm run build'
alias pf-deploy='cd "$WORKSPACE_DIR" && ./scripts/deploy-docker.sh'
alias pf-dev='cd "$WORKSPACE_DIR" && ./scripts/pf-dev.sh start'
alias pf-logs='docker-compose logs -f'
alias pf-ps='docker-compose ps'
EOF

# Make scripts executable
echo "🔐 Making scripts executable..."
chmod +x scripts/*.sh

# Initialize git hooks (if available)
if [ -d ".git" ]; then
    echo "🎣 Setting up git hooks..."
    git config core.hooksPath .githooks
    mkdir -p .githooks
fi

# Create development workspace file
echo "🏠 Setting up VS Code workspace..."
cat > PrintFarmer.code-workspace << 'EOF'
{
    "folders": [
        {
            "name": "PrintFarmer",
            "path": "."
        },
        {
            "name": "React App",
            "path": "./src/Web/ReactApp"
        },
        {
            "name": "API",
            "path": "./src/api"
        },
        {
            "name": "Shared",
            "path": "./src/shared"
        }
    ],
    "settings": {
        "typescript.preferences.importModuleSpecifier": "relative",
        "eslint.workingDirectories": [
            "./src/Web/ReactApp"
        ]
    },
    "extensions": {
        "recommendations": [
            "ms-dotnettools.csharp",
            "ms-dotnettools.csdevkit",
            "bradlc.vscode-tailwindcss",
            "esbenp.prettier-vscode",
            "dbaeumer.vscode-eslint"
        ]
    }
}
EOF

# Display setup completion info
echo ""
echo "✅ PrintFarmer React development environment setup complete!"
echo ""
echo "🎯 Next steps:"
echo "   1. Configure .env file with your settings"
echo "   2. Review GitHub issues for React migration phases"
echo "   3. Start Phase 1: React Foundation setup"
echo ""
echo "🚀 Quick start commands:"
echo "   • pf-dev    - Start development environment"
echo "   • pf-api    - Start API only"
echo "   • pf-react  - Start React dev server (after Phase 1)"
echo "   • pf-build  - Build Docker images"
echo ""
echo "🔗 Useful URLs (after starting services):"
echo "   • React App: http://localhost:3000"
echo "   • API: http://localhost:5245"
echo "   • Health Check: http://localhost:5245/health"
echo ""
echo "📚 Documentation:"
echo "   • React Migration: ./REACT_MIGRATION_README.md"
echo "   • Docker Deployment: ./DOCKER_DEPLOYMENT_README.md"
echo "   • GitHub Issues: ./.github/issues/"
echo ""

# Optional verification step: lightweight smoke tests to confirm runtime tooling
if [ "${DEVCONTAINER_VERIFY}" = "1" ] || [ "${DEVCONTAINER_VERIFY,,}" = "true" ]; then
    echo "🔍 Running optional devcontainer verification (DEVCONTAINER_VERIFY=${DEVCONTAINER_VERIFY})..."
    echo "• dotnet --info"
    dotnet --info || true
    echo "• node --version"
    node --version || true
    echo "• npm --version"
    npm --version || true
    echo "• git --version"
    git --version || true

    # Small API build smoke test if solution present
    if [ -f "src/farm-web.sln" ]; then
        echo "🔨 Running small dotnet build smoke test (API project)"
        pushd src >/dev/null
        dotnet restore ./farm-web.sln || true
        dotnet build ./api/Farm.Web.Api.csproj -c Debug --no-restore || true
        popd >/dev/null
    else
        echo "ℹ️  Solution file not found; skipping build smoke test"
    fi
    echo "✅ Devcontainer verification complete"
fi