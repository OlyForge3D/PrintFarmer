#!/bin/bash
# Registry management and status script

set -e

REGISTRY_HOST=${REGISTRY_HOST:-localhost:5000}
COMMAND=${1:-status}

case "$COMMAND" in
  "status"|"check")
    echo "=== Registry Status Check ==="
    echo "Registry: $REGISTRY_HOST"
    echo ""
    
    if curl -f "http://${REGISTRY_HOST}/v2/" >/dev/null 2>&1; then
      echo "✅ Registry is accessible"
      echo ""
      echo "📦 Available repositories:"
      curl -s "http://${REGISTRY_HOST}/v2/_catalog" | jq -r '.repositories[]' | sort | sed 's/^/  • /'
      echo ""
      echo "💾 Registry container status:"
      docker ps | grep registry || echo "  No registry containers found"
    else
      echo "❌ Registry at $REGISTRY_HOST is not accessible"
      echo ""
      echo "🔍 Checking local registry container..."
      if docker ps | grep -q local-registry; then
        echo "✅ Local registry container is running"
        echo "   Check firewall or network configuration"
      else
        echo "❌ No local registry container found"
        echo "   Run: ./setup-local-registry.sh"
      fi
    fi
    ;;
    
  "list"|"ls")
    echo "=== Registry Contents ==="
    echo "Registry: $REGISTRY_HOST"
    echo ""
    
    if ! curl -f "http://${REGISTRY_HOST}/v2/" >/dev/null 2>&1; then
      echo "❌ Registry not accessible"
      exit 1
    fi
    
    repos=$(curl -s "http://${REGISTRY_HOST}/v2/_catalog" | jq -r '.repositories[]' | sort)
    
    for repo in $repos; do
      echo "📦 $repo"
      tags=$(curl -s "http://${REGISTRY_HOST}/v2/$repo/tags/list" | jq -r '.tags[]' 2>/dev/null | sort)
      for tag in $tags; do
        echo "   • $tag"
      done
      echo ""
    done
    ;;
    
  "tags")
    if [ -z "$2" ]; then
      echo "Usage: $0 tags <repository-name>"
      echo "Example: $0 tags orcaslicer-binaries"
      exit 1
    fi
    
    repo="$2"
    echo "=== Tags for $repo ==="
    echo "Registry: $REGISTRY_HOST"
    echo ""
    
    if curl -s "http://${REGISTRY_HOST}/v2/$repo/tags/list" | jq -e '.tags' >/dev/null 2>&1; then
      curl -s "http://${REGISTRY_HOST}/v2/$repo/tags/list" | jq -r '.tags[]' | sort | sed 's/^/  • /'
    else
      echo "❌ Repository '$repo' not found or no tags available"
    fi
    ;;
    
  "size"|"disk")
    echo "=== Registry Storage Usage ==="
    
    # Check local storage if registry is local
    if docker ps --format "table {{.Names}}\t{{.Mounts}}" | grep -q local-registry; then
      mount_path=$(docker inspect local-registry | jq -r '.[0].Mounts[] | select(.Destination=="/var/lib/registry") | .Source')
      if [ -n "$mount_path" ] && [ -d "$mount_path" ]; then
        echo "📁 Storage location: $mount_path"
        echo "💾 Disk usage:"
        du -sh "$mount_path" 2>/dev/null || echo "  Unable to check disk usage"
        echo ""
        echo "📊 Top repositories by size:"
        if [ -d "$mount_path/docker/registry/v2/repositories" ]; then
          du -sh "$mount_path/docker/registry/v2/repositories"/* 2>/dev/null | sort -hr | head -10 || echo "  No repositories found"
        fi
      fi
    else
      echo "❌ Local registry container not found"
    fi
    ;;
    
  "logs")
    echo "=== Registry Logs ==="
    if docker ps | grep -q local-registry; then
      docker logs --tail=50 local-registry
    else
      echo "❌ Local registry container not found"
    fi
    ;;
    
  "restart")
    echo "=== Restarting Registry ==="
    if docker ps | grep -q local-registry; then
      docker restart local-registry
      echo "✅ Registry restarted"
    else
      echo "❌ Local registry container not found"
      echo "Run: ./setup-local-registry.sh"
    fi
    ;;
    
  "stop")
    echo "=== Stopping Registry ==="
    if docker ps | grep -q local-registry; then
      docker stop local-registry
      echo "✅ Registry stopped"
    else
      echo "ℹ️  Registry is not running"
    fi
    ;;
    
  "remove"|"rm")
    echo "=== Removing Registry ==="
    if docker ps -a | grep -q local-registry; then
      docker stop local-registry 2>/dev/null || true
      docker rm local-registry
      echo "✅ Registry container removed"
      echo "💾 Registry data preserved in ~/docker-registry-data"
    else
      echo "ℹ️  No registry container found"
    fi
    ;;
    
  "help"|"-h"|"--help")
    echo "Registry Management Script"
    echo ""
    echo "Usage: $0 <command>"
    echo ""
    echo "Commands:"
    echo "  status    Check registry accessibility and show basic info"
    echo "  list      List all repositories and their tags"
    echo "  tags      Show tags for a specific repository"
    echo "  size      Show registry storage usage"
    echo "  logs      Show registry container logs"
    echo "  restart   Restart the registry container"
    echo "  stop      Stop the registry container"
    echo "  remove    Remove the registry container (keeps data)"
    echo "  help      Show this help message"
    echo ""
    echo "Environment Variables:"
    echo "  REGISTRY_HOST   Registry host:port (default: localhost:5000)"
    echo ""
    echo "Examples:"
    echo "  $0 status"
    echo "  $0 list"
    echo "  $0 tags orcaslicer-binaries"
    echo "  REGISTRY_HOST=192.168.1.100:5000 $0 status"
    ;;
    
  *)
    echo "❌ Unknown command: $COMMAND"
    echo "Run '$0 help' for usage information"
    exit 1
    ;;
esac