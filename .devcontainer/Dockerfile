FROM mcr.microsoft.com/devcontainers/typescript-node:20-bookworm

# Install dotnet 9.0.302 using the official dotnet-install script
ARG DOTNET_VERSION=9.0.302
ENV DOTNET_ROOT=/usr/share/dotnet
ENV PATH="$DOTNET_ROOT:$PATH:$HOME/.dotnet"

RUN apt-get update && apt-get install -y --no-install-recommends curl ca-certificates unzip libssl-dev && \
    rm -rf /var/lib/apt/lists/*

RUN curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh && \
    bash /tmp/dotnet-install.sh --version ${DOTNET_VERSION} --install-dir $HOME/.dotnet && \
    rm -f /tmp/dotnet-install.sh

CMD ["/bin/bash"]
