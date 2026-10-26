FROM mcr.microsoft.com/devcontainers/typescript-node:24-bookworm

# Build with the repository root as context so global.json remains authoritative.
ENV DOTNET_ROOT=/usr/share/dotnet
ENV PATH="$DOTNET_ROOT:$PATH:$HOME/.dotnet"

RUN apt-get update && apt-get install -y --no-install-recommends curl ca-certificates unzip libssl-dev python3 && \
    rm -rf /var/lib/apt/lists/*

COPY global.json /tmp/sdk-policy/global.json
RUN SDK_VERSION="$(node -p "require('/tmp/sdk-policy/global.json').sdk.version")" && \
    curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh && \
    bash /tmp/dotnet-install.sh --version "$SDK_VERSION" --install-dir "$DOTNET_ROOT" && \
    cd /tmp/sdk-policy && dotnet --version && \
    rm -f /tmp/dotnet-install.sh

CMD ["/bin/bash"]
