## .NET SDK selection and installation

Root [`global.json`](../global.json) is the only approved SDK version.
`rollForward: disable` and `allowPrerelease: false` make a missing exact SDK
an error, even when a newer SDK is installed. Run verification from this
checkout, not a different working directory.

The approved SDK must be .NET 10, feature band 200 or newer because the older
formatter ignores diagnostic suppressors. SDK versions are not target-framework
versions (`net10.0`) or runtime image versions (`ASPNET_TAG`); those have
different release schedules and are intentionally not synchronized.

### Linux and macOS

The platform bootstrap scripts read `sdk.version`, install that exact SDK if
necessary, and verify it before restoring/building. They use the bundled
Microsoft installer rather than floating apt/Homebrew SDK packages.
Python 3 is required for JSON parsing; bootstrap provisions it first.

For SDK-only installation, from the repository root:

```bash
bash scripts/dotnet-install.sh \
  --version "$(python3 scripts/dotnet-sdk.py version)" \
  --install-dir "$HOME/.dotnet"
export DOTNET_ROOT="$HOME/.dotnet"
export PATH="$DOTNET_ROOT:$PATH"
python3 scripts/dotnet-sdk.py verify
```

Add the two exports to your shell profile for future terminals. When invoking
bootstrap via sudo, the SDK is installed for the invoking user, not root.
`DOTNET_VERSION` is no longer an override.

### Windows

`scripts/bootstrap-windows.ps1` reads `global.json` through
`scripts/dotnet-sdk.ps1`, checks the selected SDK rather than just the presence
of `dotnet`, and uses Microsoft's installer with an exact `-Version`.
It no longer installs an unrelated SDK via winget.

Alternatively download the exact approved SDK from the
[official .NET downloads](https://dotnet.microsoft.com/download/dotnet/10.0),
then verify from the repository root:

```powershell
. .\scripts\dotnet-sdk.ps1
Assert-ApprovedDotnetSdk
```

A user-local installation requires `$HOME\.dotnet` on `PATH` and `DOTNET_ROOT`
set to that directory. The bootstrap updates these for its own process;
configure them for future shells if using that installation.

### Docker and devcontainers

Docker-only hosts do not need a host SDK. The optional SDK check in the deploy
script warns about a missing/mismatched SDK without preventing a container build.
Its optional installer also uses the exact root version.

Existing container build tooling derives `SDK_TAG` as `<sdk.version>-noble`
through `scripts/docker/container-versions.conf` (PowerShell uses
`Get-ApprovedDotnetSdkTag`). Old environment/configuration SDK tags cannot select
another SDK. The pre-upgraded offline image variant is permitted only for the
same approved version. Runtime tags remain independently configurable.

SDK Dockerfile templates deliberately have **no default SDK tag**. For direct
builds, pass the derived argument explicitly and use the repository root context:

```bash
docker build -f scripts/docker/dockerfiles/Dockerfile.multistage \
  --build-arg "SDK_TAG=$(python3 scripts/dotnet-sdk.py tag)" \
  --target api-runtime .
```

Each SDK build stage copies `global.json` and logs the selected SDK before
restoring/building. Release and daily-image builds pass the same derived tag.
Build the devcontainer with `docker build -f .devcontainer/Dockerfile .`;
its installer reads the copied `global.json`, and post-create provisioning
repairs stale cached devcontainer SDKs before any restore.
The devcontainer provisions the node user's SDK directory and persists its
`DOTNET_ROOT` and `PATH` through `remoteEnv`, so new terminals and editor processes
select the repaired SDK too.

### CI and updates

`actions/setup-dotnet` steps use `global-json-file: global.json` and immediately
run `python3 scripts/dotnet-sdk.py verify`. The release build uses
`source/global.json` and verification with `--root source`, because its selected
application revision may differ from the release-tooling checkout.
The unconditional SDK consistency gate
rejects conflicting selectors. See [CI SDK controls](./CI.md#sdk-consistency-gate).

Dependabot uses the supported
[`dotnet-sdk` ecosystem](https://github.blog/changelog/2024-11-19-dependabot-can-now-perform-version-updates-for-the-net-sdk/)
to propose weekly SDK patch/feature-band updates to root `global.json`. These
are reviewable updates, not automatic approval or merge.

Before approving a version, verify it in Microsoft's
[.NET 10 release metadata](https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json)
and verify the SDK container tag supports the required architectures.
The initial exact pin, 10.0.403, was verified there on 2026-10-25; its Windows
archive checksum and Noble amd64/arm64 manifests were also verified.

### Troubleshooting

- **A compatible SDK was not found:** install the exact `sdk.version`; a newer
  installed SDK is not a substitute. Check `dotnet --list-sdks`, `PATH` and
  `DOTNET_ROOT`.
- **HTTP 409/404 or interrupted download:** distinguish transport/feed failure
  from version existence using official release metadata. Do not silently
  change the pin or enable roll-forward to mask a failed download.
- **SDK policy gate fails:** remove the independent selector and consume
  `global.json`; do not add an exemption for another literal.
- **An SDK update breaks compilation or formatting:** revert the `global.json`
  update and requalify the proposed SDK. Contributors and CI are blocked until
  the exact approved SDK is available; no application data migration is involved.
