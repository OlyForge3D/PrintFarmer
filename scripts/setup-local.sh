#!/bin/bash

# PrintFarmer Local Development Setup Script
# Quick setup for local development without Docker containers

set -euo pipefail

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
NC='\033[0m' # No Color

# Print colored output
print_info() { echo -e "${BLUE}ℹ️  $1${NC}"; }
print_success() { echo -e "${GREEN}✅ $1${NC}"; }
print_warning() { echo -e "${YELLOW}⚠️  $1${NC}"; }
print_error() { echo -e "${RED}❌ $1${NC}"; }

# Print section headers
print_header() {
    echo
    echo -e "${BLUE}================================================${NC}"
    echo -e "${BLUE}$1${NC}"
    echo -e "${BLUE}================================================${NC}"
    echo
}

# Check prerequisites
check_prerequisites() {
    print_header "🔍 Checking Prerequisites"
    
    local missing_deps=()
    
    # Check .NET SDK
    if command -v dotnet &> /dev/null; then
        local dotnet_version
        dotnet_version=$(dotnet --version)
        
        # Check if it's .NET 9.x
        if [[ $dotnet_version =~ ^9\. ]]; then
            print_success ".NET SDK found: $dotnet_version"
        else
            print_warning ".NET SDK found but version $dotnet_version is not .NET 9.x"
            print_info "PrintFarmer requires .NET 9.0.302 as specified in global.json"
            missing_deps+=("dotnet9")
        fi
    else
        print_error ".NET SDK not found"
        missing_deps+=("dotnet")
    fi
    
    # Check Node.js
    if command -v node &> /dev/null; then
        local node_version
        node_version=$(node --version | sed 's/v//')
        local major_version
        major_version=$(echo "$node_version" | cut -d. -f1)
        
        if [ "$major_version" -ge 18 ]; then
            print_success "Node.js found: v$node_version"
        else
            print_warning "Node.js found but version $node_version is below 18"
            print_info "PrintFarmer requires Node.js 18+ for the React frontend"
            missing_deps+=("node18")
        fi
    else
        print_error "Node.js not found"
        missing_deps+=("node")
    fi
    
    # Check npm
    if command -v npm &> /dev/null; then
        local npm_version
        npm_version=$(npm --version)
        print_success "npm found: $npm_version"
    else
        print_error "npm not found"
        missing_deps+=("npm")
    fi
    
    # Check git
    if command -v git &> /dev/null; then
        local git_version
        git_version=$(git --version | cut -d' ' -f3)
        print_success "Git found: $git_version"
    else
        print_error "Git not found"
        missing_deps+=("git")
    fi
    
    # Show installation instructions if dependencies are missing
    if [ ${#missing_deps[@]} -ne 0 ]; then
        print_error "Missing dependencies detected!"
        echo
        print_info "Installation instructions:"
        
        for dep in "${missing_deps[@]}"; do
            case $dep in
                dotnet|dotnet9)
                    echo -e "${YELLOW}• .NET 9.0.302 SDK:${NC}"
                    echo "  - Download: https://dotnet.microsoft.com/download/dotnet/9.0"
                    echo "  - Or use included script: ./dotnet-install.sh --version 9.0.302"
                    if [[ "$OSTYPE" == "darwin"* ]]; then
                        echo "  - Or with Homebrew: brew install --cask dotnet-sdk"
                    fi
                    echo
                    ;;
                node|node18)
                    echo -e "${YELLOW}• Node.js 18+:${NC}"
                    echo "  - Download: https://nodejs.org/"
                    if [[ "$OSTYPE" == "darwin"* ]]; then
                        echo "  - Or with Homebrew: brew install node@18"
                    elif [[ "$OSTYPE" == "linux-gnu"* ]]; then
                        echo "  - Or with package manager: apt install nodejs npm"
                    fi
                    echo
                    ;;
                npm)
                    echo -e "${YELLOW}• npm: Usually comes with Node.js${NC}"
                    echo
                    ;;
                git)
                    echo -e "${YELLOW}• Git:${NC}"
                    echo "  - Download: https://git-scm.com/"
                    if [[ "$OSTYPE" == "darwin"* ]]; then
                        echo "  - Or with Homebrew: brew install git"
                    elif [[ "$OSTYPE" == "linux-gnu"* ]]; then
                        echo "  - Or with package manager: apt install git"
                    fi
                    echo
                    ;;
            esac
        done
        
        print_info "Please install missing dependencies and run this script again."
        exit 1
    fi
}

# Verify we're in the right directory
check_directory() {
    print_header "📂 Verifying Directory"
    
    if [ ! -f "global.json" ] || [ ! -f "src/farm-web.sln" ]; then
        print_error "Please run this script from the PrintFarmer root directory"
        print_info "Expected files: global.json, src/farm-web.sln"
        echo
        print_info "If you haven't cloned the repository yet:"
        print_info "  git clone https://github.com/jpapiez/PrintFarmer.git"
        print_info "  cd PrintFarmer"
        exit 1
    fi
    
    print_success "Found PrintFarmer project structure"
    
    # Check if we have the React app
    if [ ! -d "src/Web/ReactApp" ]; then
        print_error "React app directory not found: src/Web/ReactApp"
        print_info "This might be an older version of PrintFarmer"
        exit 1
    fi
    
    print_success "React application directory found"
}

# Setup function
setup_project() {
    print_header "⚙️  Setting Up Development Environment"
    
    # Navigate to src directory
    cd src
    
    print_info "Restoring .NET dependencies..."
    print_warning "This may take 30-60 seconds on first run..."
    
    if timeout 120 dotnet restore ./farm-web.sln; then
        print_success ".NET dependencies restored"
    else
        print_error "Failed to restore .NET dependencies"
        print_info "This might be due to network issues or version conflicts"
        exit 1
    fi
    
    print_info "Installing React dependencies..."
    print_warning "This may take 30-60 seconds on first run..."
    
    cd Web/ReactApp
    if timeout 120 npm install; then
        print_success "React dependencies installed"
    else
        print_error "Failed to install React dependencies"
        print_info "Try running 'npm install' manually in src/Web/ReactApp"
        exit 1
    fi
    
    cd ../../  # Back to src directory
}

# Build projects
build_projects() {
    print_header "🔨 Building Projects"
    
    print_info "Building .NET solution..."
    print_warning "This may take 60-90 seconds on first build..."
    
    if timeout 150 dotnet build ./farm-web.sln -c Debug; then
        print_success ".NET solution built successfully"
    else
        print_error "Failed to build .NET solution"
        print_info "Check for compilation errors in the output above"
        exit 1
    fi
    
    print_info "Building React application..."
    print_warning "This may take 20-40 seconds..."
    
    cd Web/ReactApp
    if timeout 90 npm run build; then
        print_success "React application built successfully"
    else
        print_error "Failed to build React application"
        exit 1
    fi
    
    cd ../../  # Back to src directory
}

# Run tests
run_tests() {
    print_header "🧪 Running Tests"
    
    print_info "Running .NET API tests..."
    if timeout 60 dotnet test ./farm-web.sln -c Debug --logger "console;verbosity=minimal"; then
        print_success "API tests passed"
    else
        print_warning "Some API tests failed - check output above"
        print_info "This might be OK for development, but should be investigated"
    fi
    
    print_info "Running React tests..."
    cd Web/ReactApp
    if timeout 30 npm test -- --run; then
        print_success "React tests passed"
    else
        print_warning "Some React tests failed - check output above"
    fi
    
    cd ../../  # Back to src directory
}

# Create startup scripts
create_startup_scripts() {
    print_header "📜 Creating Startup Scripts"
    
    # Create API startup script
    cat > start-api.sh << 'EOF'
#!/bin/bash
echo "🚀 Starting PrintFarmer API Server..."
echo "Press Ctrl+C to stop"
echo
cd "$(dirname "$0")"
cd api
dotnet run --project Farm.Web.Api.csproj
EOF
    
    chmod +x start-api.sh
    print_success "Created: start-api.sh"
    
    # Create React startup script
    cat > start-react.sh << 'EOF'
#!/bin/bash
echo "🚀 Starting PrintFarmer React Client..."  
echo "Press Ctrl+C to stop"
echo
cd "$(dirname "$0")"
cd Web/ReactApp
npm run dev
EOF
    
    chmod +x start-react.sh
    print_success "Created: start-react.sh"
    
    # Create combined startup script
    cat > start-dev.sh << 'EOF'
#!/bin/bash
echo "🚀 Starting PrintFarmer Development Environment..."
echo "This will start both API and React in the background"
echo "Press Ctrl+C to stop all services"
echo

cd "$(dirname "$0")"

# Function to cleanup background processes
cleanup() {
    echo
    echo "🛑 Shutting down services..."
    kill $(jobs -p) 2>/dev/null || true
    exit
}

# Set trap to cleanup on script exit
trap cleanup SIGINT SIGTERM

# Start API server in background
echo "📡 Starting API server (background)..."
cd api
dotnet run --project Farm.Web.Api.csproj &
API_PID=$!
cd ..

# Wait a moment for API to start
sleep 5

# Start React dev server in background
echo "⚛️  Starting React client (background)..."
cd Web/ReactApp
npm run dev &
REACT_PID=$!
cd ../..

echo
echo "✅ Services started!"
echo "   📡 API: http://localhost:5245"
echo "   ⚛️  React: http://localhost:3000"
echo
echo "Press Ctrl+C to stop all services"

# Wait for processes to complete
wait $API_PID $REACT_PID
EOF
    
    chmod +x start-dev.sh
    print_success "Created: start-dev.sh"
}

# Display final instructions
display_instructions() {
    print_header "🎉 Setup Complete!"
    
    print_success "PrintFarmer development environment is ready!"
    echo
    
    echo -e "${GREEN}🚀 Starting the Application:${NC}"
    echo
    echo -e "${BLUE}Option 1: Manual startup (recommended for development)${NC}"
    echo -e "${YELLOW}  Terminal 1 - API Server:${NC}"
    echo -e "${BLUE}    cd src && ./start-api.sh${NC}"
    echo -e "${YELLOW}  Terminal 2 - React Client:${NC}" 
    echo -e "${BLUE}    cd src && ./start-react.sh${NC}"
    echo
    echo -e "${BLUE}Option 2: Automatic startup (both services)${NC}"
    echo -e "${BLUE}    cd src && ./start-dev.sh${NC}"
    echo
    echo -e "${GREEN}📱 Access Points:${NC}"
    echo -e "${BLUE}  • React App: http://localhost:3000${NC}"
    echo -e "${BLUE}  • API Server: http://localhost:5245${NC}"
    echo -e "${BLUE}  • API Health: http://localhost:5245/healthz${NC}"
    echo
    echo -e "${GREEN}🛠️  Development Commands:${NC}"
    echo -e "${BLUE}  • Run tests: dotnet test ./farm-web.sln${NC}"
    echo -e "${BLUE}  • Format code: dotnet format ./farm-web.sln${NC}"
    echo -e "${BLUE}  • React tests: cd Web/ReactApp && npm test${NC}"
    echo -e "${BLUE}  • React lint: cd Web/ReactApp && npm run lint${NC}"
    echo
    echo -e "${GREEN}📚 Documentation:${NC}"
    echo -e "${BLUE}  • Local Development: LOCAL_DEVELOPMENT.md${NC}"
    echo -e "${BLUE}  • Docker Deployment: DOCKER_DEPLOYMENT.md${NC}"
    echo -e "${BLUE}  • Contributing: CONTRIBUTING.md${NC}"
    echo
    
    if [[ "$OSTYPE" == "darwin"* ]]; then
        print_info "macOS detected: Local development provides full WiFi device access"
        print_info "This is better than Docker on macOS for network discovery"
    fi
    
    print_success "Happy coding! 🎉"
}

# Main execution
main() {
    print_header "🚀 PrintFarmer Local Development Setup"
    
    print_info "This script will set up PrintFarmer for local development"
    print_info "Local development provides the best experience for active development"
    echo
    
    check_prerequisites
    check_directory
    setup_project
    build_projects
    run_tests
    create_startup_scripts
    display_instructions
}

# Run main function
main "$@"
