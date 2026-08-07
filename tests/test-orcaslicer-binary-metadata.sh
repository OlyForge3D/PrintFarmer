#!/bin/bash

# Focused tests for OrcaSlicer binary-layer identity and cache rejection.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

source "$SCRIPT_DIR/test-framework.sh"
source "$REPO_ROOT/scripts/docker-utils.sh"

TEST_TEMP_DIR=""
MOCK_BIN=""
DOCKER_COMMAND_LOG=""

setup() {
    TEST_TEMP_DIR=$(create_test_temp_dir)
    MOCK_BIN="$TEST_TEMP_DIR/bin"
    DOCKER_COMMAND_LOG="$TEST_TEMP_DIR/docker-commands.log"
    mkdir -p "$MOCK_BIN"
    : > "$DOCKER_COMMAND_LOG"

    cat > "$MOCK_BIN/docker" <<'EOF'
#!/bin/bash
set -euo pipefail

# ── Stateful mock docker ─────────────────────────────────────────────────────
# The pre-hardening mock returned canned results from environment variables
# without any inter-call memory, so `docker image rm` succeeding did not change
# what a later `docker image inspect` / `docker images --quiet` observed.
# That let production bugs slip through review: a caller could execute the
# "remove stale tag → rebuild → validate" sequence and see all three succeed
# in tests even when the daemon (in reality) had refused the removal or the
# rebuild had never actually run.
#
# The state file below tracks which tags currently exist. `image rm` removes
# them; `build -t <tag>` re-adds them; `image ls`, `images --quiet`, and
# `image inspect` all consult the live state. Callers opt in by exporting
# `MOCK_STATE_DIR`; when unset, the mock keeps the legacy behavior so old
# tests keep working during the migration.
#
# Initialization is lazy: the first mock docker call in a test subshell seeds
# the state file from `MOCK_LOCAL_TAGS` (the canonical "what tags exist at
# test start" input). Subsequent calls in the same subshell see the mutated
# state.
_state_dir="${MOCK_STATE_DIR:-}"
_state_file=""
_removed_file=""
_built_file=""
if [[ -n "$_state_dir" ]]; then
    mkdir -p "$_state_dir"
    _state_file="$_state_dir/present.txt"
    _removed_file="$_state_dir/removed.txt"
    _built_file="$_state_dir/built.txt"
    if [[ ! -f "$_state_dir/.initialized" ]]; then
        : > "$_state_file"
        : > "$_removed_file"
        : > "$_built_file"
        if [[ -n "${MOCK_LOCAL_TAGS:-}" ]]; then
            printf '%s\n' "$MOCK_LOCAL_TAGS" > "$_state_file"
        fi
        : > "$_state_dir/.initialized"
    fi
fi

_tag_present() {
    local tag="$1"
    if [[ -n "$_state_dir" ]]; then
        # Effective present = seed content of $_state_file, which the
        # `image rm` / `build` handlers keep up to date.
        if [[ -s "$_state_file" ]] && grep -Fxq "$tag" "$_state_file"; then
            return 0
        fi
        return 1
    fi
    # Legacy path: MOCK_IMAGE_EXISTS controls presence globally.
    if [[ "${MOCK_IMAGE_EXISTS:-true}" == "true" ]]; then
        return 0
    fi
    return 1
}

_tag_add() {
    local tag="$1"
    if [[ -z "$_state_dir" ]]; then
        return 0
    fi
    if _tag_present "$tag"; then
        return 0
    fi
    printf '%s\n' "$tag" >> "$_state_file"
    if [[ -n "${MOCK_BUILT_LOG:-}" ]]; then
        printf '%s\n' "$tag" >> "$MOCK_BUILT_LOG"
    fi
    printf '%s\n' "$tag" >> "$_built_file"
}

_tag_remove() {
    local tag="$1"
    if [[ -z "$_state_dir" ]]; then
        return 0
    fi
    if [[ ! -s "$_state_file" ]]; then
        return 0
    fi
    local tmp
    tmp="$_state_file.tmp.$$"
    grep -Fxv "$tag" "$_state_file" > "$tmp" || true
    mv "$tmp" "$_state_file"
    printf '%s\n' "$tag" >> "$_removed_file"
}

case "${1:-}" in
    image)
        subcommand="${2:-}"
        if [[ "$subcommand" == "ls" ]]; then
            # Enumerate mocked local orcaslicer-binaries:* tags used by
            # remove_local_orcaslicer_binaries_tags. When `MOCK_STATE_DIR` is
            # set, list the live present-tag state (so removals persist).
            # Otherwise fall back to `MOCK_LOCAL_TAGS` → `MOCK_IMAGE_LIST`.
            if [[ "${MOCK_IMAGE_LS_FAIL:-false}" == "true" ]]; then
                exit 1
            fi
            # Extract the `reference=<pattern>` filter (if any) so we only
            # emit matching tags — production `docker image ls` honors this
            # and callers rely on the filter to avoid enumerating unrelated
            # images. We support the exact filter shape used by the deploy
            # script (`--filter reference=orcaslicer-binaries:*`).
            local_filter=""
            for _arg in "$@"; do
                case "$_arg" in
                    reference=*) local_filter="${_arg#reference=}" ;;
                esac
            done
            if [[ -n "$_state_dir" && -s "$_state_file" ]]; then
                while IFS= read -r _tag; do
                    [[ -z "$_tag" ]] && continue
                    if [[ -n "$local_filter" ]]; then
                        # Only match the `orcaslicer-binaries:*` shape used
                        # by the caller. Fixed-string prefix match keeps the
                        # mock simple and matches production's actual filter.
                        case "$_tag" in
                            ${local_filter}) : ;;
                            *) continue ;;
                        esac
                    fi
                    printf '%s\n' "$_tag"
                done < "$_state_file"
                exit 0
            fi
            if [[ -n "${MOCK_LOCAL_TAGS:-}" ]]; then
                printf '%s\n' "$MOCK_LOCAL_TAGS"
            elif [[ -n "${MOCK_IMAGE_LIST:-}" ]]; then
                printf '%s\n' "$MOCK_IMAGE_LIST"
            fi
            exit 0
        fi
        if [[ "$subcommand" == "rm" ]]; then
            # Simulate `docker image rm -f <tag>`. Success unless
            # `MOCK_RM_FAILS_FOR_TAG` matches the last argument. On success
            # the tag disappears from subsequent `image inspect` / `image ls`
            # / `images --quiet` calls — this is the key statefulness that
            # lets the "rm → rebuild → validate" chain be tested end-to-end.
            target_tag="${!#}"
            if [[ "${MOCK_RM_FAILS_FOR_TAG:-}" == "$target_tag" ]]; then
                exit 1
            fi
            # Record the removal so tests can assert on it.
            if [[ -n "${MOCK_RM_LOG:-}" ]]; then
                printf '%s\n' "$target_tag" >> "$MOCK_RM_LOG"
            fi
            _tag_remove "$target_tag"
            exit 0
        fi
        if [[ "$subcommand" != "inspect" ]]; then
            exit 1
        fi
        # `image inspect <tag>` — consult live state when opted in. In legacy
        # mode fall back to `MOCK_IMAGE_EXISTS`.
        _inspect_tag=""
        # `docker image inspect --format '<fmt>' <tag>` puts the tag last; the
        # bare form is `docker image inspect <tag>`. Grab the last positional.
        _inspect_tag="${!#}"
        if ! _tag_present "$_inspect_tag"; then
            exit 1
        fi
        if [[ "${3:-}" != "--format" ]]; then
            printf '{}\n'
            exit 0
        fi
        case "${4:-}" in
            *orcaslicer.version*)
                if [[ "${MOCK_VERSION_LABEL:-__MISSING__}" == "__MISSING__" ]]; then
                    printf '<no value>\n'
                else
                    printf '%s\n' "$MOCK_VERSION_LABEL"
                fi
                ;;
            *orcaslicer.sha256*)
                if [[ "${MOCK_SHA_LABEL:-__MISSING__}" == "__MISSING__" ]]; then
                    printf '<no value>\n'
                else
                    printf '%s\n' "$MOCK_SHA_LABEL"
                fi
                ;;
            *orcaslicer.allow_stub*)
                if [[ "${MOCK_ALLOW_STUB_LABEL:-__MISSING__}" == "__MISSING__" ]]; then
                    printf '<no value>\n'
                else
                    printf '%s\n' "$MOCK_ALLOW_STUB_LABEL"
                fi
                ;;
            *RepoDigests*)
                if [[ "${MOCK_REPO_DIGEST:-__MISSING__}" == "__MISSING__" ]]; then
                    printf '<none>\n'
                else
                    printf '%s\n' "$MOCK_REPO_DIGEST"
                fi
                ;;
            *)
                exit 2
                ;;
        esac
        ;;
    create)
        printf 'mock-container\n'
        ;;
    cp)
        case "${2:-}" in
            *orcaslicer.version)
                if [[ "${MOCK_EMBEDDED_VERSION:-__MISSING__}" == "__MISSING__" ]]; then
                    exit 1
                fi
                printf '%s' "$MOCK_EMBEDDED_VERSION" > "$3"
                ;;
            *orcaslicer.sha256)
                if [[ "${MOCK_EMBEDDED_SHA:-__MISSING__}" == "__MISSING__" ]]; then
                    exit 1
                fi
                printf '%s' "$MOCK_EMBEDDED_SHA" > "$3"
                ;;
            *)
                exit 2
                ;;
        esac
        ;;
    rm)
        ;;
    rmi)
        # Legacy `docker rmi` handler retained from #1166 for tests that
        # exercise pre-hardening cleanup paths. New tests use `docker image rm`.
        printf '%s\n' "$*" >> "${DOCKER_COMMAND_LOG:?}"
        if [[ "${MOCK_RMI_FAIL:-false}" == "true" ]]; then
            exit 1
        fi
        ;;
    build)
        # Simulate `docker build ...`. Success is the default so tests that
        # exercise build_base_images can focus on the OrcaSlicer recovery path.
        # Set `MOCK_BUILD_SUCCESS=false` to force a build failure.
        #
        # Statefulness: on success, add every `-t <tag>` argument to the
        # present-tag set. This is what makes "rm → rebuild → validate" end-
        # to-end — after the build, `docker image inspect` returns success
        # for the freshly-built tag, and label lookups return whatever the
        # test configured via `MOCK_VERSION_LABEL` etc.
        #
        # Also record the full argv so tests can assert on `--no-cache` and
        # other build-arg presence (the reviewer's B3: "prove `--no-cache`
        # and actual build execution").
        if [[ -n "${MOCK_BUILD_LOG:-}" ]]; then
            printf '%s\n' "$*" >> "$MOCK_BUILD_LOG"
        fi
        if [[ "${MOCK_BUILD_SUCCESS:-true}" != "true" ]]; then
            exit 1
        fi
        # Collect `-t <tag>` pairs. Production callers always pass one, but
        # supporting many keeps the mock general.
        _prev=""
        for _arg in "$@"; do
            if [[ "$_prev" == "-t" ]]; then
                _tag_add "$_arg"
            fi
            _prev="$_arg"
        done
        exit 0
        ;;
    pull)
        # Simulate `docker pull ...`. Default success keeps BuildKit frontend
        # prep from noising up build_base_images tests.
        if [[ "${MOCK_PULL_SUCCESS:-true}" != "true" ]]; then
            exit 1
        fi
        exit 0
        ;;
    images)
        # Simulate `docker images --quiet <ref>` used by save_images_to_tar to
        # decide whether to attempt export. Stateful mode consults the live
        # present-tag file so removals persist. Legacy mode still uses
        # `MOCK_LOCAL_TAGS`.
        # `--quiet` is at $2 for `docker images --quiet <ref>`.
        if [[ "${2:-}" == "--quiet" ]]; then
            target_ref="${3:-}"
            if _tag_present "$target_ref"; then
                printf 'sha256:deadbeef%s\n' "$target_ref"
            fi
            exit 0
        fi
        # Fallback for unexpected `docker images` invocations — succeed
        # silently so unrelated code paths don't fail spuriously.
        exit 0
        ;;
    save)
        # Simulate `docker save -o <path> <ref>`. Record the export so tests
        # can assert whether the stale tag reached the export boundary.
        # Signature: docker save -o <file> <image>
        if [[ "${2:-}" == "-o" ]]; then
            out_path="${3:-}"
            target_ref="${4:-}"
            if [[ -n "${MOCK_SAVE_LOG:-}" ]]; then
                printf '%s -> %s\n' "$target_ref" "$out_path" >> "$MOCK_SAVE_LOG"
            fi
            # Create a tiny placeholder so `stat -c%s "$out_path"` succeeds and
            # save_images_to_tar's success accounting is realistic.
            if [[ -n "$out_path" ]]; then
                printf 'mock-tar\n' > "$out_path"
            fi
            exit 0
        fi
        exit 0
        ;;
    *)
        exit 2
        ;;
esac
EOF
    chmod +x "$MOCK_BIN/docker"
}

teardown() {
    cleanup_test_temp_dir "$TEST_TEMP_DIR"
}

run_validation() {
    local version_label="$1"
    local sha_label="$2"
    local allow_stub_label="${3:-false}"
    local embedded_version="${4:-2.4.2}"
    local embedded_sha="${5:-d12fb8c8eac1aecd2dfb6377acd48f994f8fa439ed5292fa532dd82880f029fd}"
    (
        export PATH="$MOCK_BIN:$PATH"
        export MOCK_EMBEDDED_VERSION="$embedded_version"
        export MOCK_EMBEDDED_SHA="$embedded_sha"
        if [[ "$allow_stub_label" != "__MISSING__" ]]; then
            export MOCK_ALLOW_STUB_LABEL="$allow_stub_label"
        fi
        if [[ "$version_label" != "__MISSING__" ]]; then
            export MOCK_VERSION_LABEL="$version_label"
        fi
        if [[ "$sha_label" != "__MISSING__" ]]; then
            export MOCK_SHA_LABEL="$sha_label"
        fi
        validate_orcaslicer_binary_image \
            "orcaslicer-binaries:2.4.2" \
            "2.4.2" \
            "d12fb8c8eac1aecd2dfb6377acd48f994f8fa439ed5292fa532dd82880f029fd"
    )
}

assert_validation_rejected() {
    local version_label="$1"
    local sha_label="$2"
    local expected_message="$3"
    local output
    local exit_code

    set +e
    output=$(run_validation "$version_label" "$sha_label" 2>&1)
    exit_code=$?
    set -e

    assert_not_equals "0" "$exit_code" "Invalid cached image should be rejected"
    assert_contains "$output" "$expected_message" "Rejection should explain the metadata failure"
}

assert_embedded_validation_rejected() {
    local embedded_version="$1"
    local embedded_sha="$2"
    local expected_message="$3"
    local output
    local exit_code

    set +e
    output=$(run_validation \
        "2.4.2" \
        "d12fb8c8eac1aecd2dfb6377acd48f994f8fa439ed5292fa532dd82880f029fd" \
        "false" \
        "$embedded_version" \
        "$embedded_sha" 2>&1)
    exit_code=$?
    set -e

    assert_not_equals "0" "$exit_code" "Image with invalid embedded metadata should be rejected"
    assert_contains "$output" "$expected_message" "Rejection should explain the embedded metadata failure"
}

test_matching_metadata_is_accepted() {
    start_test "matching OrcaSlicer image metadata is accepted"

    assert_command_success \
        "run_validation '2.4.2' 'd12fb8c8eac1aecd2dfb6377acd48f994f8fa439ed5292fa532dd82880f029fd'"

    pass_test
}

test_immutable_digest_reference_is_required() {
    start_test "validated cache resolves to an immutable digest reference"

    local expected_reference
    local actual_reference
    local output
    local exit_code
    expected_reference="ghcr.io/olyforge3d/orcaslicer-base@sha256:d12fb8c8eac1aecd2dfb6377acd48f994f8fa439ed5292fa532dd82880f029fd"
    actual_reference=$(
        export PATH="$MOCK_BIN:$PATH"
        export MOCK_REPO_DIGEST="$expected_reference"
        docker_image_digest_reference "ghcr.io/olyforge3d/orcaslicer-base:2.4.2"
    )
    assert_equals "$expected_reference" "$actual_reference"

    set +e
    output=$(
        export PATH="$MOCK_BIN:$PATH"
        unset MOCK_REPO_DIGEST
        docker_image_digest_reference "ghcr.io/olyforge3d/orcaslicer-base:2.4.2"
    ) 2>&1
    exit_code=$?
    set -e
    assert_not_equals "0" "$exit_code" "A cache without a repository digest should be rejected"
    assert_contains "$output" "Unable to resolve immutable digest" "Digest rejection should explain the failure"

    pass_test
}

test_missing_version_metadata_is_rejected() {
    start_test "missing OrcaSlicer version metadata is rejected"

    assert_validation_rejected \
        "__MISSING__" \
        "d12fb8c8eac1aecd2dfb6377acd48f994f8fa439ed5292fa532dd82880f029fd" \
        "missing required label 'orcaslicer.version'"

    pass_test
}

test_stale_version_metadata_is_rejected() {
    start_test "stale OrcaSlicer version metadata is rejected"

    assert_validation_rejected \
        "2.3.2" \
        "d12fb8c8eac1aecd2dfb6377acd48f994f8fa439ed5292fa532dd82880f029fd" \
        "does not match requested '2.4.2'"

    pass_test
}

test_missing_checksum_metadata_is_rejected() {
    start_test "missing OrcaSlicer checksum metadata is rejected"

    assert_validation_rejected "2.4.2" "__MISSING__" "missing required label 'orcaslicer.sha256'"

    pass_test
}

test_stale_checksum_metadata_is_rejected() {
    start_test "stale OrcaSlicer checksum metadata is rejected"

    assert_validation_rejected \
        "2.4.2" \
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" \
        "does not match the configured checksum"

    pass_test
}

test_stub_capable_image_is_rejected() {
    start_test "stub-capable OrcaSlicer image is rejected"

    local output
    local exit_code
    set +e
    output=$(run_validation \
        "2.4.2" \
        "d12fb8c8eac1aecd2dfb6377acd48f994f8fa439ed5292fa532dd82880f029fd" \
        "true" 2>&1)
    exit_code=$?
    set -e

    assert_not_equals "0" "$exit_code" "Stub-capable cached image should be rejected"
    assert_contains "$output" "orcaslicer.allow_stub=false" "Rejection should require a strict binary attestation"

    pass_test
}

test_missing_strict_attestation_is_rejected() {
    start_test "missing strict-build attestation is rejected"

    local output
    local exit_code
    set +e
    output=$(run_validation \
        "2.4.2" \
        "d12fb8c8eac1aecd2dfb6377acd48f994f8fa439ed5292fa532dd82880f029fd" \
        "__MISSING__" 2>&1)
    exit_code=$?
    set -e

    assert_not_equals "0" "$exit_code" "Unattested cached image should be rejected"
    assert_contains "$output" "orcaslicer.allow_stub=false" "Rejection should require a strict binary attestation"

    pass_test
}

test_missing_embedded_metadata_is_rejected() {
    start_test "missing embedded OrcaSlicer metadata is rejected"

    assert_embedded_validation_rejected \
        "__MISSING__" \
        "__MISSING__" \
        "missing embedded version/checksum metadata"

    pass_test
}

test_stale_embedded_version_is_rejected() {
    start_test "stale embedded OrcaSlicer version is rejected"

    assert_embedded_validation_rejected \
        "2.3.2" \
        "d12fb8c8eac1aecd2dfb6377acd48f994f8fa439ed5292fa532dd82880f029fd" \
        "embedded version '2.3.2' does not match requested '2.4.2'"

    pass_test
}

test_missing_embedded_checksum_is_rejected() {
    start_test "missing embedded OrcaSlicer checksum is rejected"

    assert_embedded_validation_rejected \
        "2.4.2" \
        "__MISSING__" \
        "missing embedded version/checksum metadata"

    pass_test
}

test_stale_embedded_checksum_is_rejected() {
    start_test "stale embedded OrcaSlicer checksum is rejected"

    assert_embedded_validation_rejected \
        "2.4.2" \
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" \
        "embedded checksum 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa' does not match the configured checksum"

    pass_test
}

test_build_and_deploy_paths_enforce_metadata() {
    start_test "OrcaSlicer build and deploy paths enforce metadata"

    local multistage
    local base_dockerfile
    local deploy_script
    local powershell_deploy_script
    local registry_pull_script
    local publish_workflow
    local base_workflow
    local preseed_workflow
    local strict_workflow
    local worker_compose
    local docker_utils
    local api_docs
    local container_versions
    local ensure_orca_job
    local global_workflow_permissions
    multistage=$(cat "$REPO_ROOT/scripts/docker/dockerfiles/Dockerfile.multistage")
    base_dockerfile=$(cat "$REPO_ROOT/scripts/docker/dockerfiles/Dockerfile.base-orcaslicer-binaries")
    deploy_script=$(cat "$REPO_ROOT/scripts/deploy-docker.sh")
    powershell_deploy_script=$(cat "$REPO_ROOT/scripts/deploy-docker.ps1")
    registry_pull_script=$(cat "$REPO_ROOT/scripts/pull-from-registry.sh")
    publish_workflow=$(cat "$REPO_ROOT/.github/workflows/docker-publish.yml")
    base_workflow=$(cat "$REPO_ROOT/.github/workflows/orcaslicer-base-image.yml")
    preseed_workflow=$(cat "$REPO_ROOT/.github/workflows/appimage-preseed-uploader.yml")
    strict_workflow=$(cat "$REPO_ROOT/.github/workflows/orcaslicer-strict-build.yml")
    worker_compose=$(cat "$REPO_ROOT/scripts/docker/compose-templates/docker-compose.orcaslicer-worker.yml")
    docker_utils=$(cat "$REPO_ROOT/scripts/docker-utils.sh")
    api_docs=$(cat "$REPO_ROOT/docs/API.md")
    container_versions=$(cat "$REPO_ROOT/scripts/docker/container-versions.conf")
    ensure_orca_job=$(sed -n '/^  ensure-orca-base:/,/^  build-containers:/p' "$REPO_ROOT/.github/workflows/docker-publish.yml")
    global_workflow_permissions=$(sed -n '/^permissions:/,/^jobs:/p' "$REPO_ROOT/.github/workflows/docker-publish.yml")

    assert_contains "$multistage" 'orcaslicer.version="${ORCASLICER_VERSION}"' "Multistage binary layer should label its version"
    assert_contains "$multistage" 'orcaslicer.sha256="${ORCASLICER_SHA256}"' "Multistage binary layer should label its checksum"
    assert_contains "$multistage" 'orcaslicer.allow_stub="${ALLOW_STUB}"' "Multistage binary layer should attest whether stubs were allowed"
    assert_contains "$multistage" '/orcaslicer-dist/orcaslicer.version' "Multistage binary layer should embed its version"
    assert_contains "$multistage" 'cached OrcaSlicer binary version' "Worker build should reject mismatched embedded versions"
    assert_contains "$multistage" 'ln -s /opt/orcaslicer/AppRun /usr/local/bin/orcaslicer' "Worker launcher should preserve AppRun-relative paths"
    assert_contains "$multistage" 'ENV Worker__OrcaSlicerPath=/opt/orcaslicer/bin/orca-slicer' "Worker startup should validate the real OrcaSlicer binary"
    assert_contains "$base_dockerfile" 'orcaslicer.version="${ORCASLICER_VERSION}"' "Precache image should label its version"
    assert_contains "$base_dockerfile" 'orcaslicer.sha256="${ORCASLICER_SHA256}"' "Precache image should label its checksum"
    assert_contains "$deploy_script" 'prepare_orcaslicer_binary_cache "${ORCA_ASSET_IMAGE}"' "Supplied cache images should be validated"
    assert_contains "$deploy_script" 'prepare_orcaslicer_binary_cache "orcaslicer-binaries:${ORCA_VERSION}"' "Auto-detected cache images should be validated"
    assert_contains "$docker_utils" 'validate_orcaslicer_binary_image "$image_name"' "Cache preparation should enforce strict attestation"
    assert_contains "$deploy_script" 'ALLOW_STUB=false" --build-arg "_SKIP_ORCA_BINARY_BUILD=1' "Cached binary builds must keep embedded metadata validation enabled"
    assert_contains "$powershell_deploy_script" "Get-FileHash -Path \$resolvedAppImagePath -Algorithm SHA256" "PowerShell should verify cached AppImages"
    assert_not_contains "$deploy_script" "OrcaSlicer version to deploy" "Bash deployment should not offer an OrcaSlicer version selector"
    assert_not_contains "$powershell_deploy_script" "OrcaSlicer version to deploy" "PowerShell deployment should not offer an OrcaSlicer version selector"
    assert_contains "$powershell_deploy_script" '$script:SupportedOrcaSlicerVersion = "2.4.2"' "PowerShell deployment should force the supported version"
    assert_contains "$powershell_deploy_script" '$env:ORCASLICER_VERSION = $script:SupportedOrcaSlicerVersion' "PowerShell compose calls should override inherited versions"
    assert_contains "$powershell_deploy_script" 'Set-SupportedOrcaSlicerEnvFile -Path ".env"' "PowerShell redeploy should migrate stale environment files"
    assert_contains "$powershell_deploy_script" '& pwsh -File (Join-Path $PSScriptRoot "compose-generator.ps1") @generatorArgs' "PowerShell redeploy should regenerate stale compose files"
    assert_contains "$container_versions" 'export SUPPORTED_ORCASLICER_VERSION="2.4.2"' "Container versions should define one supported OrcaSlicer release"
    assert_contains "$container_versions" 'export ORCASLICER_VERSION="$SUPPORTED_ORCASLICER_VERSION"' "Container versions should override inherited OrcaSlicer versions"
    assert_contains "$deploy_script" 'enforce_supported_orcaslicer_release' "Bash deployment should reapply the supported release after loading config"
    assert_contains "$deploy_script" 'Failed to regenerate deployment configuration.' "Bash redeploy should regenerate stale compose files"
    assert_contains "$registry_pull_script" 'validate_orcaslicer_binary_image "$REGISTRY_HOST/orcaslicer-binaries:$ORCASLICER_VERSION"' "Registry cache images should be validated before retagging"
    assert_contains "$publish_workflow" 'source scripts/docker-utils.sh' "Publishing should use shared cache validation"
    assert_contains "$ensure_orca_job" 'actions: write' "Base-image bootstrap should receive workflow-dispatch permission"
    assert_not_contains "$global_workflow_permissions" 'actions: write' "Workflow-dispatch permission should not be granted to unrelated jobs"
    assert_contains "$publish_workflow" 'validate_orcaslicer_binary_image "$BASE_IMAGE" "$ORCA_VERSION" "$ORCA_SHA256"' "Publishing should fail closed on stale metadata"
    assert_contains "$publish_workflow" 'base_image: ${{ steps.verify.outputs.base_image }}' "Publishing should export only the verified base image"
    assert_contains "$publish_workflow" 'echo "base_image=$(docker_image_digest_reference "$BASE_IMAGE")"' "Publishing should resolve the verified image to an immutable digest"
    assert_contains "$publish_workflow" 'FROM ${{ needs.ensure-orca-base.outputs.base_image }}' "Worker builds should consume the verified digest reference"
    assert_not_contains "$publish_workflow" 'EMBEDDED_VERSION=$(docker run' "Publishing must not execute an untrusted cached image"
    assert_not_contains "$publish_workflow" 'execSync(`docker run' "Publishing polls must not execute an untrusted cached image"
    assert_contains "$publish_workflow" 'Worker__OrcaSlicerPath=/opt/orcaslicer/bin/orca-slicer' "Published workers should launch the real ~129MB binary directly (issue #1231)"
    assert_contains "$publish_workflow" 'Worker__OrcaSlicerAttestationPath=/etc/printfarmer/orcaslicer.sha256' "Published workers should expose binary attestation"
    assert_contains "$base_workflow" 'source scripts/docker-utils.sh' "Base image workflow should use shared cache validation"
    assert_contains "$base_workflow" 'validate_orcaslicer_binary_image "$IMAGE" "$ORCA_VERSION" "$ORCA_SHA256"' "Base image workflow should reject stale metadata"
    assert_contains "$base_workflow" 'docker_image_digest_reference "$IMAGE"' "Base image workflow should expose only an immutable cache reference"
    assert_not_contains "$base_workflow" 'EMBEDDED_VERSION=$(docker run' "Base image workflow must not execute an untrusted cached image"
    assert_contains "$docker_utils" 'container_id=$(docker create "$image_name" /printfarmer-metadata-inspection' "Cache attestation should inspect a stopped container"
    assert_contains "$docker_utils" 'docker cp' "Cache attestation should copy metadata without executing the image"
    assert_contains "$base_workflow" 'sha256sum --check --strict' "Base image workflow should verify the pinned AppImage checksum"
    assert_contains "$base_workflow" "ORCA_VERSION: '2.4.2'" "Base image workflow should use the repository-supported release"
    assert_not_contains "$base_workflow" 'orca_version:' "Base image workflow should not expose a version input"
    assert_not_contains "$publish_workflow" 'orca_version:' "Publishing should not dispatch a selectable version"
    assert_contains "$preseed_workflow" "default: ''" "Shared Prusa/Orca workflow should not apply an Orca version to Prusa"
    assert_contains "$preseed_workflow" 'VERSION_IN="$ORCASLICER_VERSION"' "Default Orca preseed should use the repository-pinned release"
    assert_contains "$strict_workflow" 'ORCASLICER_VERSION: 2.4.2' "Calibration publication should track the latest supported worker"
    assert_contains "$strict_workflow" 'ORCASLICER_SHA256: d12fb8c8eac1aecd2dfb6377acd48f994f8fa439ed5292fa532dd82880f029fd' "Calibration publication should pin the official checksum"
    assert_contains "$worker_compose" 'Worker__OrcaSlicerPath=/opt/orcaslicer/bin/orca-slicer' "Compose should point at the real binary, not the AppRun wrapper which is below the 2048-byte stub threshold (issue #1231)"
    assert_not_contains "$worker_compose" 'Worker__OrcaSlicerPath=/usr/local/bin/orcaslicer' "Compose should not resurrect the sub-2048-byte AppRun wrapper path (issue #1231)"
    assert_contains "$api_docs" '"requiredVersion": "2.4.2"' "Calibration API docs should track the latest supported worker"
    assert_not_contains "$api_docs" '2.3.1' "Current calibration API docs should not advertise the previous worker version"

    pass_test
}

test_stable_default_and_checksum_are_pinned() {
    start_test "stable OrcaSlicer default and checksum are pinned"

    local resolved
    resolved=$(
        export ORCASLICER_VERSION=2.4.0
        export ORCASLICER_SHA256=46556197dcc2fb55140e0b1e70c28b4c4da3208f12a4a2522012837c9d77ee10
        source "$REPO_ROOT/scripts/docker/container-versions.conf"
        printf '%s|%s' "$ORCASLICER_VERSION" "$ORCASLICER_SHA256"
    )

    assert_equals \
        "2.4.2|d12fb8c8eac1aecd2dfb6377acd48f994f8fa439ed5292fa532dd82880f029fd" \
        "$resolved"

    pass_test
}

# ── Regression coverage for stale-cache auto-recovery (issue #1164 / PR #1166) ───

test_local_orcaslicer_tags_are_recoverable() {
    start_test "only local OrcaSlicer binary tags are recoverable"

    assert_command_success "is_local_orcaslicer_binaries_image 'orcaslicer-binaries:2.4.2'"
    assert_command_success "is_local_orcaslicer_binaries_image 'orcaslicer-binaries:latest'"
    assert_command_failure "is_local_orcaslicer_binaries_image 'ghcr.io/olyforge3d/orcaslicer-binaries:2.4.2'"
    assert_command_failure "is_local_orcaslicer_binaries_image 'olyforge3d/orcaslicer-binaries:2.4.2'"
    assert_command_failure "is_local_orcaslicer_binaries_image 'orcaslicer-binaries@sha256:d12fb8c8eac1aecd2dfb6377acd48f994f8fa439ed5292fa532dd82880f029fd'"

    pass_test
}

test_local_orcaslicer_binaries_ref_is_recoverable() {
    start_test "local orcaslicer-binaries:X.Y.Z reference is classified as recoverable"

    assert_command_success "is_local_orcaslicer_binaries_ref 'orcaslicer-binaries:2.4.2'"
    assert_command_success "is_local_orcaslicer_binaries_ref 'orcaslicer-binaries:latest'"
    assert_command_success "is_local_orcaslicer_binaries_ref 'orcaslicer-binaries'"

    pass_test
}

test_registry_qualified_ref_is_treated_as_external() {
    start_test "registry-qualified references are treated as external and not auto-removed"

    # Registry host (has `/`).
    assert_command_failure "is_local_orcaslicer_binaries_ref 'ghcr.io/olyforge3d/orcaslicer-binaries:2.4.2'"
    # Localhost registry (has `.`).
    assert_command_failure "is_local_orcaslicer_binaries_ref 'registry.local:5000/orcaslicer-binaries:2.4.2'"
    # Digest reference (has `@`).
    assert_command_failure "is_local_orcaslicer_binaries_ref 'orcaslicer-binaries@sha256:deadbeef'"
    # Different repo name entirely.
    assert_command_failure "is_local_orcaslicer_binaries_ref 'my-custom-orcaslicer:2.4.2'"

    pass_test
}

test_matching_local_orcaslicer_tags_are_removed() {
    start_test "matching local OrcaSlicer binary tags are removed"

    local rm_log
    rm_log="$TEST_TEMP_DIR/rm-legacy.log"
    : > "$rm_log"
    (
        export PATH="$MOCK_BIN:$PATH"
        # #1166's original fixture: mixed local/registry/unrelated tags. The hardened
        # cleanup uses `docker image rm` (per-tag) instead of `docker rmi -f` (batch),
        # so the assertions target MOCK_RM_LOG and are per-tag. The classifier still
        # filters out registry-qualified and unrelated names.
        export MOCK_LOCAL_TAGS=$'orcaslicer-binaries:2.4.2\norcaslicer-binaries:latest\nghcr.io/olyforge3d/orcaslicer-binaries:2.4.2\nprintfarmer-api:latest'
        export MOCK_IMAGE_EXISTS=true
        export MOCK_VERSION_LABEL="stale"
        export MOCK_RM_LOG="$rm_log"
        remove_local_orcaslicer_binaries_tags
    )

    local rm_contents
    rm_contents=$(cat "$rm_log")
    assert_contains "$rm_contents" "orcaslicer-binaries:2.4.2" "Cleanup should force-remove matching local tags"
    assert_contains "$rm_contents" "orcaslicer-binaries:latest" "Cleanup should force-remove local :latest tag"
    assert_not_contains "$rm_contents" "ghcr.io" "Cleanup must not remove registry-qualified images"
    assert_not_contains "$rm_contents" "printfarmer-api" "Cleanup must not remove unrelated images"

    pass_test
}

test_remove_local_orcaslicer_binaries_tags_clears_matching_tags() {
    start_test "remove_local_orcaslicer_binaries_tags clears all local orcaslicer-binaries:* tags"

    local rm_log
    rm_log="$TEST_TEMP_DIR/rm.log"
    : > "$rm_log"

    (
        export PATH="$MOCK_BIN:$PATH"
        # Simulate docker knowing about three local tags — the version-specific tag,
        # `:latest`, and a leftover previous-version tag from an interrupted upgrade.
        export MOCK_LOCAL_TAGS=$'orcaslicer-binaries:2.4.2\norcaslicer-binaries:latest\norcaslicer-binaries:2.4.1'
        export MOCK_IMAGE_EXISTS=true
        # Enough of a label surface to satisfy `docker image inspect` used to check existence.
        export MOCK_VERSION_LABEL="stale"
        export MOCK_RM_LOG="$rm_log"
        remove_local_orcaslicer_binaries_tags "2.4.2"
    )

    local rm_contents
    rm_contents=$(cat "$rm_log")
    assert_contains "$rm_contents" "orcaslicer-binaries:2.4.2" "expected-version tag is removed"
    assert_contains "$rm_contents" "orcaslicer-binaries:latest" "latest alias is removed"
    assert_contains "$rm_contents" "orcaslicer-binaries:2.4.1" "leftover prior-version tag is swept"

    pass_test
}

test_local_cache_recovery_is_behavioral() {
    start_test "stale local cache is removed and requests a rebuild"

    local output
    local exit_code
    local rm_log
    rm_log="$TEST_TEMP_DIR/rm-recovery.log"
    : > "$rm_log"
    set +e
    output=$(
        export PATH="$MOCK_BIN:$PATH"
        export MOCK_VERSION_LABEL="2.4.2"
        unset MOCK_SHA_LABEL
        export MOCK_ALLOW_STUB_LABEL="false"
        export MOCK_LOCAL_TAGS=$'orcaslicer-binaries:2.4.2\norcaslicer-binaries:latest'
        export MOCK_RM_LOG="$rm_log"
        prepare_orcaslicer_binary_cache \
            "orcaslicer-binaries:2.4.2" \
            "2.4.2" \
            "d12fb8c8eac1aecd2dfb6377acd48f994f8fa439ed5292fa532dd82880f029fd"
    ) 2>&1
    exit_code=$?
    set -e

    assert_equals "10" "$exit_code" "Stale local cache should request a rebuild"
    assert_contains "$output" "rebuilding the pinned release" "Local recovery should be explicit"
    local rm_contents
    rm_contents=$(cat "$rm_log")
    assert_contains "$rm_contents" "orcaslicer-binaries:2.4.2" "Local recovery should remove the version tag"
    assert_contains "$rm_contents" "orcaslicer-binaries:latest" "Local recovery should remove the :latest tag"

    pass_test
}

test_remove_local_orcaslicer_binaries_tags_is_noop_when_no_tags() {
    start_test "remove_local_orcaslicer_binaries_tags is a no-op when no local tags exist"

    local output
    output=$(
        export PATH="$MOCK_BIN:$PATH"
        unset MOCK_LOCAL_TAGS
        # Cause `docker image inspect` to fail for every candidate tag.
        export MOCK_IMAGE_EXISTS=false
        remove_local_orcaslicer_binaries_tags "2.4.2" 2>&1
    )
    assert_contains "$output" "No local orcaslicer-binaries tags to remove" "no-op path is signalled clearly"

    pass_test
}

test_external_cache_fails_closed_behaviorally() {
    start_test "stale external cache fails closed without deletion"

    local output
    local exit_code
    local rm_log
    rm_log="$TEST_TEMP_DIR/rm-external.log"
    : > "$rm_log"
    set +e
    output=$(
        export PATH="$MOCK_BIN:$PATH"
        export MOCK_VERSION_LABEL="2.4.2"
        unset MOCK_SHA_LABEL
        export MOCK_ALLOW_STUB_LABEL="false"
        export MOCK_RM_LOG="$rm_log"
        prepare_orcaslicer_binary_cache \
            "ghcr.io/olyforge3d/orcaslicer-binaries:2.4.2" \
            "2.4.2" \
            "d12fb8c8eac1aecd2dfb6377acd48f994f8fa439ed5292fa532dd82880f029fd"
    ) 2>&1
    exit_code=$?
    set -e

    assert_equals "1" "$exit_code" "External cache mismatch should fail closed"
    assert_contains "$output" "Update the pinned image, unset ORCA_ASSET_IMAGE" "External rejection should be actionable"
    assert_equals "" "$(cat "$rm_log")" "External images must never be removed"

    pass_test
}

test_cleanup_failure_is_propagated() {
    start_test "local cache cleanup failure is propagated"

    local exit_code
    set +e
    (
        export PATH="$MOCK_BIN:$PATH"
        # Force `docker image ls` (the enumeration step of the hardened cleanup)
        # to fail; the function must return non-zero so the caller can abort.
        export MOCK_IMAGE_LS_FAIL=true
        remove_local_orcaslicer_binaries_tags
    ) >/dev/null 2>&1
    exit_code=$?
    set -e

    assert_not_equals "0" "$exit_code" "Cleanup failure must stop recovery"

    pass_test
}

test_force_rebuild_requires_supported_worker() {
    start_test "force rebuild requires an enabled supported worker"

    assert_command_success "validate_orcaslicer_rebuild_request '1' 'yes' 'false'"
    assert_command_failure "validate_orcaslicer_rebuild_request '1' 'no' 'false'"
    assert_command_failure "validate_orcaslicer_rebuild_request '1' 'yes' 'true'"
    assert_command_success "validate_orcaslicer_rebuild_request '0' 'no' 'true'"
    # Hardened acceptance: `true` (from --rebuild-orcaslicer) also gates the check.
    assert_command_success "validate_orcaslicer_rebuild_request 'true' 'yes' 'false'"
    assert_command_failure "validate_orcaslicer_rebuild_request 'true' 'no' 'false'"

    pass_test
}

test_deploy_recovery_controls_are_fail_closed() {
    start_test "deploy recovery controls preserve external image attestation"

    local deploy_script
    local docker_utils
    deploy_script=$(cat "$REPO_ROOT/scripts/deploy-docker.sh")
    docker_utils=$(cat "$REPO_ROOT/scripts/docker-utils.sh")

    assert_contains "$deploy_script" 'ORCA_FORCE_REBUILD="${ORCA_FORCE_REBUILD:-0}"' "Environment control should default to disabled"
    assert_contains "$deploy_script" '--rebuild-orcaslicer' "CLI should expose an OrcaSlicer rebuild flag"
    assert_contains "$deploy_script" 'ORCA_BUILD_CMD+=(--no-cache)' "Recovered binary layers should rebuild without cache"
    assert_contains "$deploy_script" 'return 2' "Offline OrcaSlicer failures should use a non-fallback status"
    assert_contains "$deploy_script" 'remove_local_orcaslicer_binaries_tags' "Recovery should remove stale local tags"
    assert_contains "$docker_utils" "Registry-qualified ORCA_ASSET_IMAGE does not attest" "External image mismatches should fail closed"
    assert_contains "$docker_utils" "Update the pinned image, unset ORCA_ASSET_IMAGE" "External failures should provide actionable remediation"

    pass_test
}

test_deploy_docker_defines_orca_force_rebuild() {
    start_test "deploy-docker.sh exposes ORCA_FORCE_REBUILD escape hatch and auto-recovery"

    local deploy_script
    deploy_script=$(cat "$REPO_ROOT/scripts/deploy-docker.sh")

    assert_contains "$deploy_script" 'ORCA_FORCE_REBUILD="${ORCA_FORCE_REBUILD:-0}"' "ORCA_FORCE_REBUILD default is declared"
    assert_contains "$deploy_script" '--rebuild-orcaslicer' "CLI flag is documented"
    assert_contains "$deploy_script" 'remove_local_orcaslicer_binaries_tags' "auto-recovery helper is invoked from deploy-docker.sh"
    assert_contains "$deploy_script" 'is_local_orcaslicer_binaries_image' "ORCA_ASSET_IMAGE is classified as local vs external"
    assert_contains "$deploy_script" 'Update the pinned image' "external ORCA_ASSET_IMAGE rejection carries operator remediation"

    pass_test
}

test_docker_utils_exports_recovery_helpers() {
    start_test "docker-utils.sh exports the reset helpers used by the deploy script"

    local docker_utils
    docker_utils=$(cat "$REPO_ROOT/scripts/docker-utils.sh")

    assert_contains "$docker_utils" 'is_local_orcaslicer_binaries_ref()' "is_local_orcaslicer_binaries_ref helper is defined"
    assert_contains "$docker_utils" 'remove_local_orcaslicer_binaries_tags()' "remove_local_orcaslicer_binaries_tags helper is defined"
    assert_contains "$docker_utils" "reference=orcaslicer-binaries:*" "removal helper sweeps every local orcaslicer-binaries tag"

    pass_test
}

# ── Regression coverage: offline build_base_images must fail loudly when the ──
# ── stale orcaslicer-binaries tag cannot be removed after strict validation. ──
# ── Without this guard, `docker image inspect` still succeeds, the rebuild   ──
# ── branch is skipped, and offline export ships an unattested binary layer. ──

test_offline_build_refuses_when_stale_tag_cannot_be_removed() {
    start_test "offline build_base_images refuses to proceed when stale OrcaSlicer tag cannot be removed"

    local output
    local exit_code

    set +e
    output=$(
        # Clear positional args so deploy-docker.sh's top-level CLI parser is a no-op.
        set --
        export PATH="$MOCK_BIN:$PATH"
        # Stale local image: exists, but only carries the legacy `orcaslicer.version`
        # label (missing `orcaslicer.sha256` / `orcaslicer.allow_stub` / embedded
        # identity), so `validate_orcaslicer_binary_image` will reject it. This
        # mirrors the on-disk state operators hit after PR #1089.
        export MOCK_IMAGE_EXISTS=true
        export MOCK_VERSION_LABEL="2.4.2"
        # MOCK_SHA_LABEL and MOCK_ALLOW_STUB_LABEL intentionally unset.
        export MOCK_LOCAL_TAGS=$'orcaslicer-binaries:2.4.2\norcaslicer-binaries:latest'
        # Simulate the daemon refusing to remove the specific offline-target tag
        # (a running container holds it, the image is layer-shared, etc.). The
        # `:latest` alias in MOCK_LOCAL_TAGS is still allowed to be removed so
        # `remove_local_orcaslicer_binaries_tags` returns 0 as it does in
        # production — the caller cannot rely on that return value.
        export MOCK_RM_FAILS_FOR_TAG="orcaslicer-binaries:2.4.2"
        export MOCK_RM_LOG="$TEST_TEMP_DIR/refuse-rm.log"
        : > "$MOCK_RM_LOG"
        # Base image builds and BuildKit pull are not the focus — let them
        # succeed so we can isolate the OrcaSlicer guard behavior.
        export MOCK_BUILD_SUCCESS=true
        export MOCK_PULL_SUCCESS=true

        # Source deploy-docker.sh purely to obtain `build_base_images`. The top-
        # level CLI parser sees no args (set --) and `main` runs only for
        # direct invocation, so this is a safe no-side-effect source.
        # shellcheck disable=SC1091
        source "$REPO_ROOT/scripts/deploy-docker.sh" >/dev/null 2>&1
        # Suppress `errexit` (turned on by deploy-docker.sh) so the pre-existing
        # `((successful++))` inside build_base_images cannot exit the subshell
        # before the guard fires. In production build_base_images is always
        # called as `if build_base_images ...; then`, which naturally suppresses
        # errexit inside the function; this mirrors that context so the test
        # observes the same guard-vs-fall-through decision as real deployments.
        set +e

        build_base_images 2>&1
    )
    exit_code=$?
    set -e

    assert_not_equals "0" "$exit_code" "build_base_images must return non-zero when stale OrcaSlicer tag cannot be removed"
    assert_contains "$output" "Refusing to proceed with offline preparation" "guard emits explicit refusal message"
    assert_contains "$output" "orcaslicer-binaries:2.4.2 could not be removed" "guard names the specific stale tag"
    # Critical negative assertions: neither the "cache is valid" happy path nor
    # a "rebuild succeeded" claim may appear for the stale tag — either would
    # mean offline export is about to ship an unattested layer.
    assert_not_contains "$output" "already exists locally (skipping rebuild)" "stale tag is not treated as valid cache"
    assert_not_contains "$output" "Build successful: orcaslicer-binaries:2.4.2" "no rebuild success is claimed after failed recovery"

    pass_test
}

test_offline_build_refuses_when_force_rebuild_cannot_remove_stale_tag() {
    start_test "offline build_base_images refuses when ORCA_FORCE_REBUILD cannot remove the target tag"

    local output
    local exit_code

    set +e
    output=$(
        set --
        export PATH="$MOCK_BIN:$PATH"
        # For this scenario the cached image happens to have valid strict labels
        # (so validation itself would succeed), but the operator explicitly
        # requested a forced rebuild. The recovery attempt must still succeed
        # or fail loudly — silently retaining the caller-specified stale tag
        # and skipping the rebuild would defeat the purpose of the flag.
        export MOCK_IMAGE_EXISTS=true
        export MOCK_VERSION_LABEL="2.4.2"
        export MOCK_SHA_LABEL="d12fb8c8eac1aecd2dfb6377acd48f994f8fa439ed5292fa532dd82880f029fd"
        export MOCK_ALLOW_STUB_LABEL="false"
        export MOCK_EMBEDDED_VERSION="2.4.2"
        export MOCK_EMBEDDED_SHA="d12fb8c8eac1aecd2dfb6377acd48f994f8fa439ed5292fa532dd82880f029fd"
        export MOCK_LOCAL_TAGS="orcaslicer-binaries:2.4.2"
        export MOCK_RM_FAILS_FOR_TAG="orcaslicer-binaries:2.4.2"
        export MOCK_BUILD_SUCCESS=true
        export MOCK_PULL_SUCCESS=true
        export ORCA_FORCE_REBUILD=true

        # shellcheck disable=SC1091
        source "$REPO_ROOT/scripts/deploy-docker.sh" >/dev/null 2>&1
        # Same errexit suppression as the sibling regression test — see the
        # comment there for the rationale.
        set +e

        build_base_images 2>&1
    )
    exit_code=$?
    set -e

    assert_not_equals "0" "$exit_code" "ORCA_FORCE_REBUILD path must fail loudly when the target tag cannot be removed"
    assert_contains "$output" "Refusing to proceed with offline preparation" "force-rebuild path shares the same explicit refusal"
    assert_contains "$output" "Remove the reference and rerun with --rebuild-orcaslicer" "operator remediation instructions are surfaced"

    pass_test
}

# ── Boundary regression: prepare_offline_deployment must abort BEFORE calling  ──
# ── save_images_to_tar when the OrcaSlicer strict-attestation guard trips.     ──
# ── This is the exact export-suppression path Bishop's second review flagged:  ──
# ── build_base_images returning non-zero is not sufficient if the caller keeps ──
# ── running and hands the unattested DOCKER_LOCAL_IMAGES entry to docker save. ──

test_prepare_offline_aborts_export_when_stale_tag_cannot_be_removed() {
    start_test "prepare_offline_deployment aborts export path when stale OrcaSlicer tag cannot be removed"

    local output
    local exit_code
    local export_dir
    export_dir=$(create_test_temp_dir)/offline-out
    mkdir -p "$export_dir"

    set +e
    output=$(
        set --
        export PATH="$MOCK_BIN:$PATH"
        # Reproduce the operator's on-disk state from PR #1089:
        # orcaslicer-binaries:2.4.2 present but only carrying the legacy
        # `orcaslicer.version` label, so strict validation rejects it.
        export MOCK_IMAGE_EXISTS=true
        export MOCK_VERSION_LABEL="2.4.2"
        # MOCK_SHA_LABEL and MOCK_ALLOW_STUB_LABEL intentionally unset →
        # validator returns non-zero, build_base_images triggers recovery.
        export MOCK_LOCAL_TAGS=$'orcaslicer-binaries:2.4.2\norcaslicer-binaries:latest'
        # The daemon refuses to remove the version-pinned tag → recovery
        # cannot clear the unattested image; the rebuild branch is skipped.
        # This is precisely the scenario where the previous fix was still
        # allowing save_images_to_tar to export the unattested image.
        export MOCK_RM_FAILS_FOR_TAG="orcaslicer-binaries:2.4.2"
        export MOCK_RM_LOG="$export_dir/rm.log"
        export MOCK_SAVE_LOG="$export_dir/save.log"
        : > "$MOCK_RM_LOG"
        : > "$MOCK_SAVE_LOG"
        export MOCK_BUILD_SUCCESS=true
        export MOCK_PULL_SUCCESS=true

        # shellcheck disable=SC1091
        source "$REPO_ROOT/scripts/deploy-docker.sh" >/dev/null 2>&1
        # Same errexit relaxation as the sibling build_base_images tests — the
        # pre-existing `((successful++))` inside build_base_images would
        # otherwise exit the subshell before we can observe the return path.
        set +e

        prepare_offline_deployment "$export_dir" 2>&1
    )
    exit_code=$?
    set -e

    assert_not_equals "0" "$exit_code" "prepare_offline_deployment must return non-zero when the OrcaSlicer strict-attestation guard trips"
    assert_contains "$output" "Refusing to continue offline preparation" "caller emits explicit refusal referencing the strict-attestation guard"
    assert_contains "$output" "strict OrcaSlicer attestation guard triggered" "caller names the specific failure category"

    # Critical negative assertion: no docker save call may reference the stale
    # tag. This is the actual export-suppression assertion Bishop required —
    # verifying the callee's return code alone is not sufficient because the
    # previous caller (before this fix) demoted rc=1 to a warning and still
    # invoked save_images_to_tar → docker save on the unattested image.
    local save_log_contents=""
    if [ -f "$export_dir/save.log" ]; then
        save_log_contents=$(cat "$export_dir/save.log")
    fi
    assert_not_contains "$save_log_contents" "orcaslicer-binaries:2.4.2" "docker save was never invoked with the stale unattested tag"

    # And no tarball for the stale tag reached the target directory.
    local orca_tars=""
    orca_tars=$(find "$export_dir" -maxdepth 1 -name 'orcaslicer-binaries-2.4.2*.tar' -print 2>/dev/null || true)
    assert_equals "" "$orca_tars" "no orcaslicer-binaries-2.4.2*.tar file was written to the offline output directory"

    # Step 3 header ("Exporting All Images … to TAR Files") must not appear —
    # if it did, we passed the guard and were merely lucky the export failed.
    assert_not_contains "$output" "STEP 3/4: Exporting All Images" "STEP 3/4 export phase was never entered"

    pass_test
}

# ── Boundary regression: even the direct --save-images path must refuse to    ──
# ── export an unattested orcaslicer-binaries:* tag. This defense-in-depth     ──
# ── check protects entrypoints that bypass build_base_images entirely         ──
# ── (e.g. `deploy-docker.sh --save-images` on a host with a pre-existing      ──
# ── unattested cache).                                                        ──

test_save_images_refuses_unattested_orcaslicer_binaries() {
    start_test "save_images_to_tar refuses to export unattested orcaslicer-binaries:* even when called directly"

    local output
    local exit_code
    local export_dir
    export_dir=$(create_test_temp_dir)/direct-save
    mkdir -p "$export_dir"

    set +e
    output=$(
        set --
        export PATH="$MOCK_BIN:$PATH"
        # Same stale-attestation scenario, but this test bypasses
        # build_base_images entirely and calls save_images_to_tar directly.
        export MOCK_IMAGE_EXISTS=true
        export MOCK_VERSION_LABEL="2.4.2"
        # MOCK_SHA_LABEL / MOCK_ALLOW_STUB_LABEL intentionally unset.
        export MOCK_LOCAL_TAGS="orcaslicer-binaries:2.4.2"
        export MOCK_SAVE_LOG="$export_dir/save.log"
        : > "$MOCK_SAVE_LOG"

        # shellcheck disable=SC1091
        source "$REPO_ROOT/scripts/deploy-docker.sh" >/dev/null 2>&1
        set +e

        # Point save_images_to_tar at the DOCKER_LOCAL_IMAGES-only loop by
        # emptying the base/upgraded arrays for this invocation. The
        # orcaslicer-binaries:* entry is the one this defense-in-depth check
        # must reject at the export boundary.
        DOCKER_UPGRADED_IMAGES=()
        DOCKER_BASE_IMAGES=()

        save_images_to_tar "$export_dir" 2>&1
    )
    exit_code=$?
    set -e

    # save_images_to_tar must now propagate a hard non-zero exit code when the
    # OrcaSlicer strict-attestation guard trips at the export boundary. The
    # pre-fix behavior — incrementing `fail_count` internally but still
    # returning 0 and printing "Images exported successfully!" — was the
    # blocker C the pre-PR review flagged: operators and CI could not tell
    # from the exit code that the offline bundle was missing its required
    # OrcaSlicer layer. Return 2 is the documented strict-attestation status
    # (matches build_base_images / prepare_offline_deployment).
    assert_not_equals "0" "$exit_code" "save_images_to_tar must exit non-zero on strict OrcaSlicer attestation refusal"
    assert_equals "2" "$exit_code" "save_images_to_tar must return 2 (strict-attestation status) on OrcaSlicer refusal"

    assert_contains "$output" "Refusing to export unattested orcaslicer-binaries:2.4.2" "save boundary refuses unattested OrcaSlicer image"
    assert_contains "$output" "strict OrcaSlicer attestation missing" "save boundary refusal explains the attestation gap"
    # The success banner must NOT appear alongside a refusal — that was the
    # exact deceptive-status bug the reviewers flagged.
    assert_not_contains "$output" "Images exported successfully!" "success banner must not print when an OrcaSlicer refusal occurred"
    assert_contains "$output" "OrcaSlicer strict-attestation refusal detected" "refusal summary is emitted so operators see the failure category"

    local save_log_contents=""
    if [ -f "$export_dir/save.log" ]; then
        save_log_contents=$(cat "$export_dir/save.log")
    fi
    assert_not_contains "$save_log_contents" "orcaslicer-binaries:2.4.2" "docker save was never invoked with the unattested tag from the direct save path"

    local orca_tars=""
    orca_tars=$(find "$export_dir" -maxdepth 1 -name 'orcaslicer-binaries-2.4.2*.tar' -print 2>/dev/null || true)
    assert_equals "" "$orca_tars" "no orcaslicer-binaries-2.4.2*.tar file was written by the direct save path"

    pass_test
}

# ── Boundary regression: the two ORCA_FORCE_REBUILD gates in deploy-docker.sh  ──
# ── must both accept the documented truthy values (`true` from the CLI flag,   ──
# ── `1` from the env-var form). Operator-facing docs, help text, and the      ──
# ── in-script remediation messages all prescribe `ORCA_FORCE_REBUILD=1`, so   ──
# ── the gates must honor that literal or the documented remediation is a no-  ──
# ── op. `false`/unset/invalid values must remain disabled, matching the        ──
# ── DISABLE_SLICER_BUILDS / COMPOSE_REMOVE_ORPHANS convention in this script.  ──

# Helper — runs build_base_images() in a mocked-docker subshell with the given
# ORCA_FORCE_REBUILD value and a strictly-valid cached image, and captures both
# the combined output, the return code, and the docker command logs so the
# caller can assert on whether the ORCA_FORCE_REBUILD gate triggered auto-
# recovery, whether the rebuild actually executed, and whether the whole path
# completed cleanly (rc 0) or bailed on a strict-attestation refusal (rc 2).
# The image is set up as strictly valid so recovery is only triggered by the
# gate under test, not by the validator's own reject path (which has its own
# dedicated tests).
#
# Writes:
#   $2 rm_log      — one line per `docker image rm` call the mock observed
#   $3 build_log   — full argv per `docker build` call (for `--no-cache` etc.)
#   $4 built_log   — one line per tag `docker build -t <tag>` produced
# Prints the combined stdout+stderr of build_base_images. The caller captures
# the subshell's exit code separately.
_run_build_base_images_with_force_rebuild() {
    local force_rebuild_value="$1"
    local rm_log="$2"
    local build_log="${3:-}"
    local built_log="${4:-}"
    (
        set --
        export PATH="$MOCK_BIN:$PATH"
        # Opt into the stateful mock so `docker image rm` really removes the
        # tag from subsequent `docker image inspect` / `images --quiet` calls.
        # Without this, "removal succeeded → rebuild → validate" is untested
        # end-to-end and the reviewer's B blocker (no proof the mock changes
        # in response to successful rm) applies.
        local state_dir
        state_dir=$(create_test_temp_dir)/mock-state
        mkdir -p "$state_dir"
        export MOCK_STATE_DIR="$state_dir"
        export MOCK_VERSION_LABEL="2.4.2"
        export MOCK_SHA_LABEL="d12fb8c8eac1aecd2dfb6377acd48f994f8fa439ed5292fa532dd82880f029fd"
        export MOCK_ALLOW_STUB_LABEL="false"
        export MOCK_EMBEDDED_VERSION="2.4.2"
        export MOCK_EMBEDDED_SHA="d12fb8c8eac1aecd2dfb6377acd48f994f8fa439ed5292fa532dd82880f029fd"
        export MOCK_LOCAL_TAGS=$'orcaslicer-binaries:2.4.2\norcaslicer-binaries:latest'
        # Do NOT force removal failures — we want to observe whether the gate
        # invoked remove_local_orcaslicer_binaries_tags at all, not what happens
        # when removal itself fails.
        unset MOCK_RM_FAILS_FOR_TAG
        export MOCK_RM_LOG="$rm_log"
        if [ -n "$build_log" ]; then
            export MOCK_BUILD_LOG="$build_log"
        fi
        if [ -n "$built_log" ]; then
            export MOCK_BUILT_LOG="$built_log"
        fi
        export MOCK_BUILD_SUCCESS=true
        export MOCK_PULL_SUCCESS=true

        if [ "$force_rebuild_value" = "__UNSET__" ]; then
            unset ORCA_FORCE_REBUILD
        else
            export ORCA_FORCE_REBUILD="$force_rebuild_value"
        fi

        # shellcheck disable=SC1091
        source "$REPO_ROOT/scripts/deploy-docker.sh" >/dev/null 2>&1
        # Suppress errexit for the same reason as the sibling tests: the
        # pre-existing ((successful++)) inside build_base_images would otherwise
        # exit the subshell before we can capture the output.
        set +e

        build_base_images 2>&1
    )
}

test_orca_force_rebuild_env_1_triggers_offline_recovery() {
    start_test "ORCA_FORCE_REBUILD=1 (documented env value) triggers offline recovery gate at runtime"

    local rm_log
    local build_log
    local built_log
    rm_log="$TEST_TEMP_DIR/rm-env-1.log"
    build_log="$TEST_TEMP_DIR/build-env-1.log"
    built_log="$TEST_TEMP_DIR/built-env-1.log"
    : > "$rm_log"
    : > "$build_log"
    : > "$built_log"

    local output
    local exit_code
    set +e
    output=$(_run_build_base_images_with_force_rebuild "1" "$rm_log" "$build_log" "$built_log" 2>&1)
    exit_code=$?
    set -e

    # The gate must fire when ORCA_FORCE_REBUILD=1 — this is the exact literal
    # every operator-facing doc prescribes ("rerun with --rebuild-orcaslicer /
    # ORCA_FORCE_REBUILD=1"). Assertions layer up from "the gate printed its
    # marker" (weakest) through "docker actually removed and rebuilt the tag"
    # (strongest end-to-end proof).
    assert_contains "$output" "ORCA_FORCE_REBUILD is set — clearing local orcaslicer-binaries" "clearing message printed when ORCA_FORCE_REBUILD=1"
    local rm_log_contents
    rm_log_contents=$(cat "$rm_log")
    assert_contains "$rm_log_contents" "orcaslicer-binaries:2.4.2" "docker image rm was invoked for the target tag when ORCA_FORCE_REBUILD=1"

    # Statefulness proof: the mock actually removed the tag, so the guard
    # (which fires when the tag persists after removal) did NOT trip and the
    # rebuild branch ran. Assert the actual build execution and success
    # message that follows, not just the "would rebuild" intent.
    local build_log_contents
    build_log_contents=$(cat "$build_log")
    assert_contains "$build_log_contents" "-t orcaslicer-binaries:2.4.2" "docker build was invoked with the OrcaSlicer target tag"
    assert_contains "$build_log_contents" "--no-cache" "the rebuild is forced with --no-cache so BuildKit cannot reuse the stale layer"
    assert_contains "$build_log_contents" "Dockerfile.base-orcaslicer-binaries" "the rebuild targets the OrcaSlicer binary Dockerfile"
    local built_log_contents
    built_log_contents=$(cat "$built_log")
    assert_contains "$built_log_contents" "orcaslicer-binaries:2.4.2" "the rebuild produced the target tag (post-build present in mock state)"
    assert_contains "$output" "✓ Build successful: orcaslicer-binaries:2.4.2" "build_base_images reports the OrcaSlicer rebuild as successful"

    # And the whole flow must return rc 0: removal succeeded, rebuild ran,
    # post-build attestation passed. Any non-zero here is a regression.
    assert_equals "0" "$exit_code" "successful removal → rebuild → post-build attestation returns rc 0"

    pass_test
}

test_orca_force_rebuild_env_true_triggers_offline_recovery() {
    start_test "ORCA_FORCE_REBUILD=true (CLI-flag path) still triggers offline recovery gate at runtime"

    local rm_log
    local build_log
    local built_log
    rm_log="$TEST_TEMP_DIR/rm-env-true.log"
    build_log="$TEST_TEMP_DIR/build-env-true.log"
    built_log="$TEST_TEMP_DIR/built-env-true.log"
    : > "$rm_log"
    : > "$build_log"
    : > "$built_log"

    local output
    local exit_code
    set +e
    output=$(_run_build_base_images_with_force_rebuild "true" "$rm_log" "$build_log" "$built_log" 2>&1)
    exit_code=$?
    set -e

    # Regression: --rebuild-orcaslicer sets ORCA_FORCE_REBUILD=true (see
    # scripts/deploy-docker.sh case '--rebuild-orcaslicer'), so the truthy
    # widening must not accidentally break the value the CLI flag itself
    # emits. Same layered assertions as the =1 test.
    assert_contains "$output" "ORCA_FORCE_REBUILD is set — clearing local orcaslicer-binaries" "clearing message printed when ORCA_FORCE_REBUILD=true"
    local rm_log_contents
    rm_log_contents=$(cat "$rm_log")
    assert_contains "$rm_log_contents" "orcaslicer-binaries:2.4.2" "docker image rm was invoked for the target tag when ORCA_FORCE_REBUILD=true"
    local build_log_contents
    build_log_contents=$(cat "$build_log")
    assert_contains "$build_log_contents" "-t orcaslicer-binaries:2.4.2" "docker build was invoked with the OrcaSlicer target tag"
    assert_contains "$build_log_contents" "--no-cache" "the rebuild is forced with --no-cache"
    assert_contains "$output" "✓ Build successful: orcaslicer-binaries:2.4.2" "build_base_images reports the OrcaSlicer rebuild as successful"
    assert_equals "0" "$exit_code" "successful removal → rebuild returns rc 0"

    pass_test
}

test_orca_force_rebuild_env_false_leaves_valid_cache_intact() {
    start_test "ORCA_FORCE_REBUILD=false leaves a strictly-valid cached image untouched"

    local rm_log
    local build_log
    local built_log
    rm_log="$TEST_TEMP_DIR/rm-env-false.log"
    build_log="$TEST_TEMP_DIR/build-env-false.log"
    built_log="$TEST_TEMP_DIR/built-env-false.log"
    : > "$rm_log"
    : > "$build_log"
    : > "$built_log"

    local output
    local exit_code
    set +e
    output=$(_run_build_base_images_with_force_rebuild "false" "$rm_log" "$build_log" "$built_log" 2>&1)
    exit_code=$?
    set -e

    # False must not trip the gate. Layered assertions:
    # 1. No "clearing" message (gate did not print it).
    # 2. MOCK_RM_LOG is empty (remove_local_orcaslicer_binaries_tags was never
    #    invoked from the gate — and since the strictly-valid cache means the
    #    validator's own recovery path also never fires, no other caller
    #    writes to this log either).
    # 3. No OrcaSlicer rebuild happened (the `-t orcaslicer-binaries:2.4.2`
    #    call must not appear in MOCK_BUILD_LOG).
    # 4. The "already exists locally (skipping rebuild)" happy-path message
    #    is present, confirming the cache was reused as documented.
    # 5. rc 0 (happy path).
    assert_not_contains "$output" "ORCA_FORCE_REBUILD is set — clearing local orcaslicer-binaries" "clearing message MUST NOT print when ORCA_FORCE_REBUILD=false"
    local rm_log_contents
    rm_log_contents=$(cat "$rm_log")
    assert_equals "" "$rm_log_contents" "no docker image rm calls were issued when ORCA_FORCE_REBUILD=false and the cache is strictly valid"
    local build_log_contents
    build_log_contents=$(cat "$build_log")
    assert_not_contains "$build_log_contents" "-t orcaslicer-binaries:2.4.2" "no OrcaSlicer rebuild was invoked when the cache is strictly valid"
    assert_contains "$output" "already exists locally (skipping rebuild)" "strictly-valid cache is reused when ORCA_FORCE_REBUILD=false"
    assert_equals "0" "$exit_code" "strictly-valid cache reuse returns rc 0"

    pass_test
}

test_orca_force_rebuild_env_unset_leaves_valid_cache_intact() {
    start_test "unset ORCA_FORCE_REBUILD leaves a strictly-valid cached image untouched"

    local rm_log
    local build_log
    local built_log
    rm_log="$TEST_TEMP_DIR/rm-env-unset.log"
    build_log="$TEST_TEMP_DIR/build-env-unset.log"
    built_log="$TEST_TEMP_DIR/built-env-unset.log"
    : > "$rm_log"
    : > "$build_log"
    : > "$built_log"

    local output
    local exit_code
    set +e
    output=$(_run_build_base_images_with_force_rebuild "__UNSET__" "$rm_log" "$build_log" "$built_log" 2>&1)
    exit_code=$?
    set -e

    # Default (unset) behavior must equal false — this is what every fresh
    # install experiences, so it's the most important negative case.
    assert_not_contains "$output" "ORCA_FORCE_REBUILD is set — clearing local orcaslicer-binaries" "clearing message MUST NOT print when ORCA_FORCE_REBUILD is unset (default)"
    local rm_log_contents
    rm_log_contents=$(cat "$rm_log")
    assert_equals "" "$rm_log_contents" "no docker image rm calls were issued when ORCA_FORCE_REBUILD is unset"
    local build_log_contents
    build_log_contents=$(cat "$build_log")
    assert_not_contains "$build_log_contents" "-t orcaslicer-binaries:2.4.2" "no OrcaSlicer rebuild was invoked when ORCA_FORCE_REBUILD is unset"
    assert_contains "$output" "already exists locally (skipping rebuild)" "strictly-valid cache is reused when ORCA_FORCE_REBUILD is unset"
    assert_equals "0" "$exit_code" "strictly-valid cache reuse (default) returns rc 0"

    pass_test
}

test_orca_force_rebuild_env_invalid_is_treated_as_false() {
    start_test "invalid ORCA_FORCE_REBUILD value (e.g. 'yes') is treated as false, not silently truthy"

    local rm_log
    local build_log
    local built_log
    rm_log="$TEST_TEMP_DIR/rm-env-invalid.log"
    build_log="$TEST_TEMP_DIR/build-env-invalid.log"
    built_log="$TEST_TEMP_DIR/built-env-invalid.log"
    : > "$rm_log"
    : > "$build_log"
    : > "$built_log"

    # `yes` is a common footgun value that some scripts accept but this one
    # deliberately rejects (matches the DISABLE_SLICER_BUILDS convention). If
    # this test starts failing, the truthy set has quietly widened and docs
    # must be re-audited so operators aren't blindsided by partial acceptance.
    local output
    local exit_code
    set +e
    output=$(_run_build_base_images_with_force_rebuild "yes" "$rm_log" "$build_log" "$built_log" 2>&1)
    exit_code=$?
    set -e

    assert_not_contains "$output" "ORCA_FORCE_REBUILD is set — clearing local orcaslicer-binaries" "invalid value 'yes' must NOT trip the ORCA_FORCE_REBUILD gate"
    local rm_log_contents
    rm_log_contents=$(cat "$rm_log")
    assert_equals "" "$rm_log_contents" "no docker image rm calls were issued for invalid ORCA_FORCE_REBUILD='yes'"
    local build_log_contents
    build_log_contents=$(cat "$build_log")
    assert_not_contains "$build_log_contents" "-t orcaslicer-binaries:2.4.2" "no OrcaSlicer rebuild was invoked for invalid ORCA_FORCE_REBUILD='yes'"
    assert_contains "$output" "already exists locally (skipping rebuild)" "strictly-valid cache is reused when ORCA_FORCE_REBUILD is invalid (treated as false)"
    assert_equals "0" "$exit_code" "invalid value is falsy → happy-path rc 0"

    pass_test
}

test_orca_force_rebuild_deploy_containers_gate_matches_offline_gate() {
    start_test "deploy_containers ORCA_FORCE_REBUILD gate accepts the same truthy set as the offline gate"

    # This test doesn't exercise deploy_containers() end-to-end (it has an
    # enormous setup surface); instead it extracts the exact gate expressions
    # from every call site and runtime-evaluates each against every documented
    # truthy/falsy value. This is genuine shell-expression evaluation, not
    # text matching — the assertion is on the actual `[[ ... ]]` exit code, so
    # if any gate ever drifts from the shared truthy set this test breaks
    # loudly rather than silently pinning one path only.

    # Extract every ORCA_FORCE_REBUILD gate expression from deploy-docker.sh.
    # All gates must share the same truthy set: `1` OR `true`.
    local gate_lines
    gate_lines=$(grep -nE '^\s*if \[\[ "\$ORCA_FORCE_REBUILD" == "1" \|\| "\$ORCA_FORCE_REBUILD" == "true" \]\]; then' "$REPO_ROOT/scripts/deploy-docker.sh" || true)

    # Expect at least two gate lines (offline build_base_images + main deploy
    # path). Any additional call sites must match the same expression style so
    # they are captured by the runtime-evaluation loop below.
    local gate_count
    gate_count=$(printf '%s\n' "$gate_lines" | grep -c '^' || true)
    if [ "$gate_count" -lt 2 ]; then
        fail_test "at least two ORCA_FORCE_REBUILD gates must exist and share truthy set (found $gate_count)"
        return
    fi

    # Evaluate each gate expression against every value the fix must handle.
    # `1` and `true` must be truthy; `false`, unset, and `yes` (invalid) must be falsy.
    local expr
    local line
    while IFS= read -r line; do
        [ -z "$line" ] && continue
        # Strip the leading "NNN:" prefix and any leading whitespace.
        expr="${line#*:}"
        expr="${expr#"${expr%%[![:space:]]*}"}"
        # Strip the leading "if " keyword and the trailing "; then".
        expr="${expr#if }"
        expr="${expr%; then}"
        expr="${expr% then}"

        # Truthy: 1 and true.
        if ! ORCA_FORCE_REBUILD=1 bash -c "$expr"; then
            fail_test "gate expression should be truthy for ORCA_FORCE_REBUILD=1: $expr"
            return
        fi
        if ! ORCA_FORCE_REBUILD=true bash -c "$expr"; then
            fail_test "gate expression should be truthy for ORCA_FORCE_REBUILD=true: $expr"
            return
        fi

        # Falsy: false, unset, and invalid values.
        if ORCA_FORCE_REBUILD=false bash -c "$expr"; then
            fail_test "gate expression should be falsy for ORCA_FORCE_REBUILD=false: $expr"
            return
        fi
        if (unset ORCA_FORCE_REBUILD && bash -c "$expr"); then
            fail_test "gate expression should be falsy when ORCA_FORCE_REBUILD is unset: $expr"
            return
        fi
        if ORCA_FORCE_REBUILD=yes bash -c "$expr"; then
            fail_test "gate expression should be falsy for invalid ORCA_FORCE_REBUILD=yes: $expr"
            return
        fi
    done <<< "$gate_lines"

    pass_test
}

# ── Bash 3.2 compatibility: `remove_local_orcaslicer_binaries_tags` must   ──
# ── work under macOS's system /bin/bash (3.2.57) which never gained          ──
# ── associative arrays. Previous versions used `local -A seen=()`; this     ──
# ── test guards against reintroducing any Bash-4-only syntax.               ──

test_remove_local_orcaslicer_binaries_tags_uses_no_bash4_only_features() {
    start_test "remove_local_orcaslicer_binaries_tags avoids Bash-4-only syntax (macOS /bin/bash compat)"

    local docker_utils
    docker_utils=$(cat "$REPO_ROOT/scripts/docker-utils.sh")

    # Extract the function body and confirm it contains no `local -A` /
    # `declare -A` / `typeset -A` (associative arrays) — those are the
    # concrete Bash-4-only features that broke this helper for the operator
    # who ran the deploy on macOS.
    local fn_body
    fn_body=$(sed -n '/^remove_local_orcaslicer_binaries_tags()/,/^}/p' "$REPO_ROOT/scripts/docker-utils.sh")

    if [ -z "$fn_body" ]; then
        fail_test "unable to extract remove_local_orcaslicer_binaries_tags function body from docker-utils.sh"
        return
    fi

    # Precise negative assertions — any of these would break Bash 3.2.
    if echo "$fn_body" | grep -qE '(^|\s)(local|declare|typeset)\s+-A(\s|$)'; then
        fail_test "remove_local_orcaslicer_binaries_tags must not declare associative arrays (Bash 4+ only)"
        return
    fi
    # `${!var@P}` prompt-transformation is also Bash-4-only; guard against it
    # in case anyone tries to build a set-emulation via indirection later.
    if echo "$fn_body" | grep -qE '\$\{![^}]*@P\}'; then
        fail_test "remove_local_orcaslicer_binaries_tags must not use \${!var@P} (Bash 4+ only)"
        return
    fi

    pass_test
}

# ── Statefulness proof: `docker image rm` failing to actually remove the tag  ──
# ── from the daemon (e.g. a running container is holding it) must cause      ──
# ── build_base_images to bail with rc 2, NOT falsely report success. The     ──
# ── pre-fix mock returned canned answers so this was untestable end-to-end.  ──

test_orca_force_rebuild_persistent_stale_tag_returns_rc_2() {
    start_test "ORCA_FORCE_REBUILD=1 with a genuinely persistent stale tag returns strict rc 2"

    local output
    local exit_code
    local rm_log
    local build_log
    local built_log
    rm_log="$TEST_TEMP_DIR/rm-persistent.log"
    build_log="$TEST_TEMP_DIR/build-persistent.log"
    built_log="$TEST_TEMP_DIR/built-persistent.log"
    : > "$rm_log"
    : > "$build_log"
    : > "$built_log"

    set +e
    output=$(
        set --
        export PATH="$MOCK_BIN:$PATH"
        # Stateful mock: the tag is present, ORCA_FORCE_REBUILD=1 triggers
        # `docker image rm`, but the mock refuses removal for the version tag
        # (simulating the "running container is holding the reference" case).
        # `remove_local_orcaslicer_binaries_tags` returns 0 because it treats
        # per-tag rm failures as warnings; the guard in build_base_images
        # must catch this — the tag is still present and a subsequent build
        # would silently reuse the stale layer.
        local state_dir
        state_dir=$(create_test_temp_dir)/mock-state
        mkdir -p "$state_dir"
        export MOCK_STATE_DIR="$state_dir"
        export MOCK_VERSION_LABEL="2.4.2"
        export MOCK_SHA_LABEL="d12fb8c8eac1aecd2dfb6377acd48f994f8fa439ed5292fa532dd82880f029fd"
        export MOCK_ALLOW_STUB_LABEL="false"
        export MOCK_EMBEDDED_VERSION="2.4.2"
        export MOCK_EMBEDDED_SHA="d12fb8c8eac1aecd2dfb6377acd48f994f8fa439ed5292fa532dd82880f029fd"
        export MOCK_LOCAL_TAGS=$'orcaslicer-binaries:2.4.2\norcaslicer-binaries:latest'
        export MOCK_RM_FAILS_FOR_TAG="orcaslicer-binaries:2.4.2"
        export MOCK_RM_LOG="$rm_log"
        export MOCK_BUILD_LOG="$build_log"
        export MOCK_BUILT_LOG="$built_log"
        export MOCK_BUILD_SUCCESS=true
        export MOCK_PULL_SUCCESS=true
        export ORCA_FORCE_REBUILD=1

        # shellcheck disable=SC1091
        source "$REPO_ROOT/scripts/deploy-docker.sh" >/dev/null 2>&1
        set +e

        build_base_images 2>&1
    )
    exit_code=$?
    set -e

    # Strict rc: this is the strict-attestation status code (2), not a
    # generic non-zero. The reviewer's blocker demanded that both the
    # successful-recovery path (rc 0) and the genuine persistence failure
    # (rc 2) be tested end-to-end with a stateful mock. rc 1 would mean a
    # non-Orca failure and would trigger the documented soft fallback — but
    # OrcaSlicer strict-attestation refusals MUST NOT fall back, so rc 2 is
    # the required signal.
    assert_equals "2" "$exit_code" "persistent stale tag must return the strict-attestation status code (2)"
    assert_contains "$output" "Refusing to proceed with offline preparation" "guard emits explicit refusal message"
    assert_contains "$output" "orcaslicer-binaries:2.4.2 could not be removed" "guard names the specific stale tag"
    # The rebuild must NOT have executed — the guard must have short-circuited
    # before docker build ran with the OrcaSlicer target.
    local build_log_contents
    build_log_contents=$(cat "$build_log")
    assert_not_contains "$build_log_contents" "-t orcaslicer-binaries:2.4.2" "no OrcaSlicer rebuild is executed when the stale tag persists"
    assert_not_contains "$output" "✓ Build successful: orcaslicer-binaries:2.4.2" "no rebuild success is claimed after failed recovery"

    pass_test
}

# ── CLI-path exit propagation: `--save-images` and `--pull-images --save-  ──
# ── images` must return non-zero when save_images_to_tar refuses to export   ──
# ── an unattested orcaslicer-binaries:* tag. Pre-fix both paths `exit 0`.   ──

_orca_test_run_cli_direct_save() {
    # Exec the deploy-docker.sh CLI with `--save-images` in a subshell where
    # the mock docker knows about a stale unattested orcaslicer-binaries:2.4.2
    # tag. Prints "RC=<code>" on the first line followed by combined output;
    # callers parse both from the returned string. Using output-only return
    # avoids nameref pitfalls where `local output` inside the helper would
    # shadow the caller's variable of the same name.
    local export_dir
    export_dir=$(create_test_temp_dir)/cli-direct-save
    mkdir -p "$export_dir"

    local combined
    local rc
    set +e
    combined=$(
        set --
        export PATH="$MOCK_BIN:$PATH"
        export MOCK_VERSION_LABEL="2.4.2"
        # MOCK_SHA_LABEL / MOCK_ALLOW_STUB_LABEL intentionally unset →
        # validator rejects the tag at the save boundary.
        export MOCK_LOCAL_TAGS="orcaslicer-binaries:2.4.2"
        export MOCK_SAVE_LOG="$export_dir/save.log"
        : > "$MOCK_SAVE_LOG"
        # Suppress interactive prompts. The mock is a no-op anyway, but be
        # explicit.
        export SKIP_UI_TESTS=1
        bash "$REPO_ROOT/scripts/deploy-docker.sh" --save-images --images-dir "$export_dir" 2>&1
    )
    rc=$?
    set -e
    printf 'RC=%s\n%s' "$rc" "$combined"
}

test_cli_save_images_propagates_orca_attestation_refusal() {
    start_test "CLI --save-images propagates non-zero exit on OrcaSlicer strict-attestation refusal"

    local combined
    local rc
    local output
    combined=$(_orca_test_run_cli_direct_save)
    rc="${combined#RC=}"
    rc="${rc%%$'\n'*}"
    output="${combined#*$'\n'}"

    # The CLI must exit non-zero when save_images_to_tar refuses the
    # unattested OrcaSlicer tag. Previously both `--save-images` and
    # `--pull-images --save-images` `exit 0` unconditionally regardless of
    # save_images_to_tar's return, hiding a security-critical refusal.
    assert_not_equals "0" "$rc" "CLI --save-images must exit non-zero when the OrcaSlicer save is refused"
    assert_equals "2" "$rc" "CLI --save-images must propagate the strict-attestation status code (2)"
    assert_contains "$output" "Refusing to export unattested orcaslicer-binaries:2.4.2" "CLI --save-images surfaces the refusal to operators"
    assert_not_contains "$output" "Images exported successfully!" "success banner must not print when the CLI is about to exit non-zero"

    pass_test
}

test_cli_pull_and_save_images_propagates_orca_attestation_refusal() {
    start_test "CLI --pull-images --save-images propagates non-zero exit on OrcaSlicer refusal"

    local export_dir
    export_dir=$(create_test_temp_dir)/cli-pull-save
    mkdir -p "$export_dir"

    local combined
    local rc
    local output
    set +e
    combined=$(
        set --
        export PATH="$MOCK_BIN:$PATH"
        export MOCK_VERSION_LABEL="2.4.2"
        export MOCK_LOCAL_TAGS="orcaslicer-binaries:2.4.2"
        export MOCK_SAVE_LOG="$export_dir/save.log"
        : > "$MOCK_SAVE_LOG"
        export MOCK_PULL_SUCCESS=true
        export SKIP_UI_TESTS=1
        bash "$REPO_ROOT/scripts/deploy-docker.sh" --pull-images --save-images --images-dir "$export_dir" 2>&1
        printf 'MARKER_RC=%s\n' "$?"
    )
    set -e
    # Extract the CLI's own rc from the marker line — the outer $? here is
    # the rc of the printf, which is always 0.
    rc=$(printf '%s\n' "$combined" | awk -F= '/^MARKER_RC=/ {print $2; exit}')
    output=$(printf '%s\n' "$combined" | grep -v '^MARKER_RC=')

    assert_not_equals "0" "$rc" "CLI --pull-images --save-images must exit non-zero when the OrcaSlicer save is refused"
    assert_equals "2" "$rc" "CLI --pull-images --save-images must propagate the strict-attestation status code (2)"
    assert_contains "$output" "Refusing to export unattested orcaslicer-binaries:2.4.2" "combined pull+save CLI surfaces the refusal"

    pass_test
}

# ── save_images_to_tar function-level exit-code coverage: happy path       ──
# ── (nothing to export, no Orca refusal) still returns 0 so the "no local  ──
# ── OrcaSlicer image present" and "attested OrcaSlicer image present"      ──
# ── flows don't accidentally start failing.                                 ──

test_save_images_returns_zero_when_no_orca_image_present() {
    start_test "save_images_to_tar returns 0 when orcaslicer-binaries:* tag is absent from local Docker state"

    local output
    local exit_code
    local export_dir
    export_dir=$(create_test_temp_dir)/no-orca-save
    mkdir -p "$export_dir"

    set +e
    output=$(
        set --
        export PATH="$MOCK_BIN:$PATH"
        # Production-faithful regression for blocker F: on a fresh host where
        # DOCKER_LOCAL_IMAGES still lists orcaslicer-binaries:${ORCASLICER_VERSION}
        # (as it always does — this is the config production ships), but the
        # tag has never been built locally, save_images_to_tar MUST detect
        # the absent image and skip it with rc 0 partial-success. The
        # previous test evaded production by setting `DOCKER_LOCAL_IMAGES=()`,
        # which bypassed the presence check entirely — that hid the bug
        # where `docker images --quiet` returns rc 0 for missing refs and
        # let the strict-attestation guard misclassify absence as refusal.
        #
        # Model "tag absent" by NOT seeding it into the mock's present-tag
        # state. In legacy mode, `MOCK_IMAGE_EXISTS=false` makes both
        # `docker images --quiet` return empty output AND `docker image
        # inspect` return non-zero — matching real Docker's behavior for a
        # missing reference.
        export MOCK_IMAGE_EXISTS=false
        unset MOCK_LOCAL_TAGS
        export MOCK_SAVE_LOG="$export_dir/save.log"
        : > "$MOCK_SAVE_LOG"

        # shellcheck disable=SC1091
        source "$REPO_ROOT/scripts/deploy-docker.sh" >/dev/null 2>&1
        set +e

        DOCKER_UPGRADED_IMAGES=()
        DOCKER_BASE_IMAGES=()
        # Critical: leave DOCKER_LOCAL_IMAGES populated so the loop iterates
        # over the orcaslicer-binaries:${ORCASLICER_VERSION} entry exactly
        # as it does in production. The presence check is what must correctly
        # identify the image as absent and skip it.
        DOCKER_LOCAL_IMAGES=("orcaslicer-binaries:2.4.2")

        save_images_to_tar "$export_dir" 2>&1
    )
    exit_code=$?
    set -e

    assert_equals "0" "$exit_code" "absent orcaslicer-binaries:* tag → happy-path rc 0 (partial-success)"
    assert_contains "$output" "Skipping orcaslicer-binaries:2.4.2 (not built locally)" \
        "presence check emits the skip message for the absent tag"
    assert_not_contains "$output" "Refusing to export unattested" \
        "no attestation refusal is triggered when the image is simply not present"
    assert_contains "$output" "Images exported successfully!" \
        "success banner is present because absence is not a failure"

    # The presence check must have prevented any docker save invocation on
    # the absent orcaslicer-binaries tag — no tarball may have been written.
    local save_log_contents=""
    if [ -f "$export_dir/save.log" ]; then
        save_log_contents=$(cat "$export_dir/save.log")
    fi
    assert_not_contains "$save_log_contents" "orcaslicer-binaries:2.4.2" \
        "docker save must not be invoked on the absent orcaslicer-binaries tag"

    local orca_tars=""
    orca_tars=$(find "$export_dir" -maxdepth 1 -name 'orcaslicer-binaries-2.4.2*.tar' -print 2>/dev/null || true)
    assert_equals "" "$orca_tars" \
        "no orcaslicer-binaries-2.4.2*.tar file is written when the image is absent"

    pass_test
}

test_save_images_returns_zero_when_orca_image_is_attested() {
    start_test "save_images_to_tar returns 0 when orcaslicer-binaries:* image is fully attested"

    local output
    local exit_code
    local export_dir
    export_dir=$(create_test_temp_dir)/attested-orca-save
    mkdir -p "$export_dir"

    set +e
    output=$(
        set --
        export PATH="$MOCK_BIN:$PATH"
        # Fully attested OrcaSlicer image: all labels present and matching.
        export MOCK_VERSION_LABEL="2.4.2"
        export MOCK_SHA_LABEL="d12fb8c8eac1aecd2dfb6377acd48f994f8fa439ed5292fa532dd82880f029fd"
        export MOCK_ALLOW_STUB_LABEL="false"
        export MOCK_EMBEDDED_VERSION="2.4.2"
        export MOCK_EMBEDDED_SHA="d12fb8c8eac1aecd2dfb6377acd48f994f8fa439ed5292fa532dd82880f029fd"
        export MOCK_LOCAL_TAGS="orcaslicer-binaries:2.4.2"
        export MOCK_SAVE_LOG="$export_dir/save.log"
        : > "$MOCK_SAVE_LOG"

        # shellcheck disable=SC1091
        source "$REPO_ROOT/scripts/deploy-docker.sh" >/dev/null 2>&1
        set +e

        DOCKER_UPGRADED_IMAGES=()
        DOCKER_BASE_IMAGES=()

        save_images_to_tar "$export_dir" 2>&1
    )
    exit_code=$?
    set -e

    assert_equals "0" "$exit_code" "attested OrcaSlicer image exports cleanly → rc 0"
    assert_contains "$output" "Images exported successfully!" "success banner is present on the attested happy path"
    local save_log_contents=""
    if [ -f "$export_dir/save.log" ]; then
        save_log_contents=$(cat "$export_dir/save.log")
    fi
    assert_contains "$save_log_contents" "orcaslicer-binaries:2.4.2" "attested OrcaSlicer image is passed to docker save"

    pass_test
}

run_tests() {
    test_matching_metadata_is_accepted
    test_immutable_digest_reference_is_required
    test_missing_version_metadata_is_rejected
    test_stale_version_metadata_is_rejected
    test_missing_checksum_metadata_is_rejected
    test_stale_checksum_metadata_is_rejected
    test_stub_capable_image_is_rejected
    test_missing_strict_attestation_is_rejected
    test_missing_embedded_metadata_is_rejected
    test_stale_embedded_version_is_rejected
    test_missing_embedded_checksum_is_rejected
    test_stale_embedded_checksum_is_rejected
    test_build_and_deploy_paths_enforce_metadata
    test_stable_default_and_checksum_are_pinned
    # PR #1166 upstream test coverage (adapted to the hardened helper contract).
    test_local_orcaslicer_tags_are_recoverable
    test_matching_local_orcaslicer_tags_are_removed
    test_local_cache_recovery_is_behavioral
    test_external_cache_fails_closed_behaviorally
    test_cleanup_failure_is_propagated
    test_force_rebuild_requires_supported_worker
    test_deploy_recovery_controls_are_fail_closed
    # Additional hardening (offline-export guard, force-rebuild normalization,
    # stricter local/external ref classification, gate parity) introduced by
    # the branch. These layer on top of #1166's upstream architecture.
    test_local_orcaslicer_binaries_ref_is_recoverable
    test_registry_qualified_ref_is_treated_as_external
    test_remove_local_orcaslicer_binaries_tags_clears_matching_tags
    test_remove_local_orcaslicer_binaries_tags_is_noop_when_no_tags
    test_deploy_docker_defines_orca_force_rebuild
    test_docker_utils_exports_recovery_helpers
    test_offline_build_refuses_when_stale_tag_cannot_be_removed
    test_offline_build_refuses_when_force_rebuild_cannot_remove_stale_tag
    test_prepare_offline_aborts_export_when_stale_tag_cannot_be_removed
    test_save_images_refuses_unattested_orcaslicer_binaries
    test_orca_force_rebuild_env_1_triggers_offline_recovery
    test_orca_force_rebuild_env_true_triggers_offline_recovery
    test_orca_force_rebuild_env_false_leaves_valid_cache_intact
    test_orca_force_rebuild_env_unset_leaves_valid_cache_intact
    test_orca_force_rebuild_env_invalid_is_treated_as_false
    test_orca_force_rebuild_deploy_containers_gate_matches_offline_gate
    # Bash 3.2 compat (macOS system /bin/bash) — blocker A.
    test_remove_local_orcaslicer_binaries_tags_uses_no_bash4_only_features
    # Stateful mock end-to-end coverage — blocker B: proves successful rm →
    # rebuild → attestation returns rc 0, and genuine persistence failure
    # returns strict rc 2.
    test_orca_force_rebuild_persistent_stale_tag_returns_rc_2
    # save_images_to_tar exit-code coverage — blocker C: refusal → rc 2,
    # happy paths → rc 0.
    test_save_images_returns_zero_when_no_orca_image_present
    test_save_images_returns_zero_when_orca_image_is_attested
    # CLI-path exit-code propagation — blocker C, second half: both
    # `--save-images` and `--pull-images --save-images` CLI paths must
    # propagate the non-zero refusal instead of `exit 0`.
    test_cli_save_images_propagates_orca_attestation_refusal
    test_cli_pull_and_save_images_propagates_orca_attestation_refusal
}

setup
trap teardown EXIT
run_test_suite run_tests "OrcaSlicer binary metadata"
