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

case "${1:-}" in
    image)
        subcommand="${2:-}"
        if [[ "$subcommand" == "ls" ]]; then
            # Enumerate mocked local orcaslicer-binaries:* tags used by
            # remove_local_orcaslicer_binaries_tags. `MOCK_LOCAL_TAGS` is a
            # newline-separated list; unset/empty means no tags exist.
            # Fall back to `MOCK_IMAGE_LIST` (legacy) or fail if
            # `MOCK_IMAGE_LS_FAIL=true`.
            if [[ "${MOCK_IMAGE_LS_FAIL:-false}" == "true" ]]; then
                exit 1
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
            # `MOCK_RM_FAILS_FOR_TAG` matches the last argument.
            target_tag="${!#}"
            if [[ "${MOCK_RM_FAILS_FOR_TAG:-}" == "$target_tag" ]]; then
                exit 1
            fi
            # Record the removal so tests can assert on it.
            if [[ -n "${MOCK_RM_LOG:-}" ]]; then
                printf '%s\n' "$target_tag" >> "$MOCK_RM_LOG"
            fi
            exit 0
        fi
        if [[ "$subcommand" != "inspect" || "${MOCK_IMAGE_EXISTS:-true}" != "true" ]]; then
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
        if [[ "${MOCK_BUILD_SUCCESS:-true}" != "true" ]]; then
            exit 1
        fi
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
        # decide whether to attempt export. `MOCK_LOCAL_TAGS` (newline-list)
        # defines which references are considered "present"; a match prints a
        # deterministic ID so `docker images --quiet` reports non-empty.
        # `--quiet` is at $2 for `docker images --quiet <ref>`.
        if [[ "${2:-}" == "--quiet" ]]; then
            target_ref="${3:-}"
            if [[ -n "${MOCK_LOCAL_TAGS:-}" ]] && printf '%s\n' "$MOCK_LOCAL_TAGS" | grep -Fxq "$target_ref"; then
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
    assert_contains "$publish_workflow" 'Worker__OrcaSlicerPath=/usr/local/bin/orcaslicer' "Published workers should launch through AppRun"
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
    assert_contains "$worker_compose" 'Worker__OrcaSlicerPath=/usr/local/bin/orcaslicer' "Compose should not bypass the AppRun launcher"
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

    # save_images_to_tar increments fail_count but returns 0 by design so
    # partial-success semantics survive. Exit code alone is therefore not the
    # signal here — the export-suppression assertion is the source of truth.
    unused_exit_code="$exit_code"

    assert_contains "$output" "Refusing to export unattested orcaslicer-binaries:2.4.2" "save boundary refuses unattested OrcaSlicer image"
    assert_contains "$output" "strict OrcaSlicer attestation missing" "save boundary refusal explains the attestation gap"

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
# the combined output and the docker-image-rm log so the caller can assert on
# whether the ORCA_FORCE_REBUILD gate triggered auto-recovery. The image is set
# up as strictly valid so recovery is only triggered by the gate under test,
# not by the validator's own reject path (which has its own dedicated tests).
_run_build_base_images_with_force_rebuild() {
    local force_rebuild_value="$1"
    local rm_log="$2"
    (
        set --
        export PATH="$MOCK_BIN:$PATH"
        export MOCK_IMAGE_EXISTS=true
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
    rm_log="$TEST_TEMP_DIR/rm-env-1.log"
    : > "$rm_log"

    local output
    set +e
    output=$(_run_build_base_images_with_force_rebuild "1" "$rm_log" 2>&1)
    set -e

    # The gate must fire when ORCA_FORCE_REBUILD=1 — this is the exact literal
    # every operator-facing doc prescribes ("rerun with --rebuild-orcaslicer /
    # ORCA_FORCE_REBUILD=1"). Two independent runtime signals prove the gate
    # tripped: (1) the "clearing" info message from deploy-docker.sh itself,
    # and (2) an actual `docker image rm` call recorded by the mock docker
    # shim. Either one alone would be circumstantial; both together lock the
    # behavior in.
    assert_contains "$output" "ORCA_FORCE_REBUILD is set — clearing local orcaslicer-binaries" "clearing message printed when ORCA_FORCE_REBUILD=1"
    local rm_log_contents
    rm_log_contents=$(cat "$rm_log")
    assert_contains "$rm_log_contents" "orcaslicer-binaries:2.4.2" "docker image rm was invoked for the target tag when ORCA_FORCE_REBUILD=1"

    pass_test
}

test_orca_force_rebuild_env_true_triggers_offline_recovery() {
    start_test "ORCA_FORCE_REBUILD=true (CLI-flag path) still triggers offline recovery gate at runtime"

    local rm_log
    rm_log="$TEST_TEMP_DIR/rm-env-true.log"
    : > "$rm_log"

    local output
    set +e
    output=$(_run_build_base_images_with_force_rebuild "true" "$rm_log" 2>&1)
    set -e

    # Regression: --rebuild-orcaslicer sets ORCA_FORCE_REBUILD=true (see
    # scripts/deploy-docker.sh case '--rebuild-orcaslicer'), so the truthy
    # widening must not accidentally break the value the CLI flag itself
    # emits. Same two runtime signals as the =1 test.
    assert_contains "$output" "ORCA_FORCE_REBUILD is set — clearing local orcaslicer-binaries" "clearing message printed when ORCA_FORCE_REBUILD=true"
    local rm_log_contents
    rm_log_contents=$(cat "$rm_log")
    assert_contains "$rm_log_contents" "orcaslicer-binaries:2.4.2" "docker image rm was invoked for the target tag when ORCA_FORCE_REBUILD=true"

    pass_test
}

test_orca_force_rebuild_env_false_leaves_valid_cache_intact() {
    start_test "ORCA_FORCE_REBUILD=false leaves a strictly-valid cached image untouched"

    local rm_log
    rm_log="$TEST_TEMP_DIR/rm-env-false.log"
    : > "$rm_log"

    local output
    set +e
    output=$(_run_build_base_images_with_force_rebuild "false" "$rm_log" 2>&1)
    set -e

    # False must not trip the gate. Three independent signals:
    # 1. No "clearing" message (gate did not print it).
    # 2. MOCK_RM_LOG is empty (remove_local_orcaslicer_binaries_tags was never
    #    invoked from the gate — and since the strictly-valid cache means the
    #    validator's own recovery path also never fires, no other caller
    #    writes to this log either).
    # 3. The "already exists locally (skipping rebuild)" happy-path message
    #    is present, confirming the cache was reused as documented.
    assert_not_contains "$output" "ORCA_FORCE_REBUILD is set — clearing local orcaslicer-binaries" "clearing message MUST NOT print when ORCA_FORCE_REBUILD=false"
    local rm_log_contents
    rm_log_contents=$(cat "$rm_log")
    assert_equals "" "$rm_log_contents" "no docker image rm calls were issued when ORCA_FORCE_REBUILD=false and the cache is strictly valid"
    assert_contains "$output" "already exists locally (skipping rebuild)" "strictly-valid cache is reused when ORCA_FORCE_REBUILD=false"

    pass_test
}

test_orca_force_rebuild_env_unset_leaves_valid_cache_intact() {
    start_test "unset ORCA_FORCE_REBUILD leaves a strictly-valid cached image untouched"

    local rm_log
    rm_log="$TEST_TEMP_DIR/rm-env-unset.log"
    : > "$rm_log"

    local output
    set +e
    output=$(_run_build_base_images_with_force_rebuild "__UNSET__" "$rm_log" 2>&1)
    set -e

    # Default (unset) behavior must equal false — this is what every fresh
    # install experiences, so it's the most important negative case.
    assert_not_contains "$output" "ORCA_FORCE_REBUILD is set — clearing local orcaslicer-binaries" "clearing message MUST NOT print when ORCA_FORCE_REBUILD is unset (default)"
    local rm_log_contents
    rm_log_contents=$(cat "$rm_log")
    assert_equals "" "$rm_log_contents" "no docker image rm calls were issued when ORCA_FORCE_REBUILD is unset"
    assert_contains "$output" "already exists locally (skipping rebuild)" "strictly-valid cache is reused when ORCA_FORCE_REBUILD is unset"

    pass_test
}

test_orca_force_rebuild_env_invalid_is_treated_as_false() {
    start_test "invalid ORCA_FORCE_REBUILD value (e.g. 'yes') is treated as false, not silently truthy"

    local rm_log
    rm_log="$TEST_TEMP_DIR/rm-env-invalid.log"
    : > "$rm_log"

    # `yes` is a common footgun value that some scripts accept but this one
    # deliberately rejects (matches the DISABLE_SLICER_BUILDS convention). If
    # this test starts failing, the truthy set has quietly widened and docs
    # must be re-audited so operators aren't blindsided by partial acceptance.
    local output
    set +e
    output=$(_run_build_base_images_with_force_rebuild "yes" "$rm_log" 2>&1)
    set -e

    assert_not_contains "$output" "ORCA_FORCE_REBUILD is set — clearing local orcaslicer-binaries" "invalid value 'yes' must NOT trip the ORCA_FORCE_REBUILD gate"
    local rm_log_contents
    rm_log_contents=$(cat "$rm_log")
    assert_equals "" "$rm_log_contents" "no docker image rm calls were issued for invalid ORCA_FORCE_REBUILD='yes'"
    assert_contains "$output" "already exists locally (skipping rebuild)" "strictly-valid cache is reused when ORCA_FORCE_REBUILD is invalid (treated as false)"

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
}

setup
trap teardown EXIT
run_test_suite run_tests "OrcaSlicer binary metadata"
