#!/bin/bash

# Focused tests for OrcaSlicer binary-layer identity and cache rejection.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

source "$SCRIPT_DIR/test-framework.sh"
source "$REPO_ROOT/scripts/docker-utils.sh"

TEST_TEMP_DIR=""
MOCK_BIN=""

setup() {
    TEST_TEMP_DIR=$(create_test_temp_dir)
    MOCK_BIN="$TEST_TEMP_DIR/bin"
    mkdir -p "$MOCK_BIN"

    cat > "$MOCK_BIN/docker" <<'EOF'
#!/bin/bash
set -euo pipefail

case "${1:-}" in
    image)
        if [[ "${2:-}" != "inspect" || "${MOCK_IMAGE_EXISTS:-true}" != "true" ]]; then
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
    local worker_compose
    multistage=$(cat "$REPO_ROOT/scripts/docker/dockerfiles/Dockerfile.multistage")
    base_dockerfile=$(cat "$REPO_ROOT/scripts/docker/dockerfiles/Dockerfile.base-orcaslicer-binaries")
    deploy_script=$(cat "$REPO_ROOT/scripts/deploy-docker.sh")
    powershell_deploy_script=$(cat "$REPO_ROOT/scripts/deploy-docker.ps1")
    registry_pull_script=$(cat "$REPO_ROOT/scripts/pull-from-registry.sh")
    publish_workflow=$(cat "$REPO_ROOT/.github/workflows/docker-publish.yml")
    base_workflow=$(cat "$REPO_ROOT/.github/workflows/orcaslicer-base-image.yml")
    preseed_workflow=$(cat "$REPO_ROOT/.github/workflows/appimage-preseed-uploader.yml")
    worker_compose=$(cat "$REPO_ROOT/scripts/docker/compose-templates/docker-compose.orcaslicer-worker.yml")

    assert_contains "$multistage" 'orcaslicer.version="${ORCASLICER_VERSION}"' "Multistage binary layer should label its version"
    assert_contains "$multistage" 'orcaslicer.sha256="${ORCASLICER_SHA256}"' "Multistage binary layer should label its checksum"
    assert_contains "$multistage" 'orcaslicer.allow_stub="${ALLOW_STUB}"' "Multistage binary layer should attest whether stubs were allowed"
    assert_contains "$multistage" '/orcaslicer-dist/orcaslicer.version' "Multistage binary layer should embed its version"
    assert_contains "$multistage" 'cached OrcaSlicer binary version' "Worker build should reject mismatched embedded versions"
    assert_contains "$multistage" 'ln -s /opt/orcaslicer/AppRun /usr/local/bin/orcaslicer' "Worker launcher should preserve AppRun-relative paths"
    assert_contains "$base_dockerfile" 'orcaslicer.version="${ORCASLICER_VERSION}"' "Precache image should label its version"
    assert_contains "$base_dockerfile" 'orcaslicer.sha256="${ORCASLICER_SHA256}"' "Precache image should label its checksum"
    assert_contains "$deploy_script" 'validate_orcaslicer_binary_image "${ORCA_ASSET_IMAGE}"' "Supplied cache images should be validated"
    assert_contains "$deploy_script" 'validate_orcaslicer_binary_image "orcaslicer-binaries:${ORCA_VERSION}"' "Auto-detected cache images should be validated"
    assert_contains "$deploy_script" 'ALLOW_STUB=false" --build-arg "_SKIP_ORCA_BINARY_BUILD=1' "Cached binary builds must keep embedded metadata validation enabled"
    assert_contains "$powershell_deploy_script" "Get-FileHash -Path \$resolvedAppImagePath -Algorithm SHA256" "PowerShell should verify cached AppImages"
    assert_contains "$registry_pull_script" 'validate_orcaslicer_binary_image "$REGISTRY_HOST/orcaslicer-binaries:$ORCASLICER_VERSION"' "Registry cache images should be validated before retagging"
    assert_contains "$publish_workflow" 'ACTUAL_VERSION=$(docker image inspect' "Publishing should inspect cached image metadata"
    assert_contains "$publish_workflow" 'Refusing OrcaSlicer base image' "Publishing should fail closed on stale metadata"
    assert_contains "$publish_workflow" 'Worker__OrcaSlicerPath=/usr/local/bin/orcaslicer' "Published workers should launch through AppRun"
    assert_contains "$publish_workflow" 'Worker__OrcaSlicerAttestationPath=/etc/printfarmer/orcaslicer.sha256' "Published workers should expose binary attestation"
    assert_contains "$base_workflow" 'Rejecting cached image' "Base image workflow should reject stale metadata"
    assert_contains "$base_workflow" 'cat /opt/orcaslicer/orcaslicer.version' "Base image workflow should verify embedded version metadata"
    assert_contains "$base_workflow" 'sha256sum --check --strict' "Base image workflow should verify the pinned AppImage checksum"
    assert_contains "$preseed_workflow" "default: ''" "Shared Prusa/Orca workflow should not apply an Orca version to Prusa"
    assert_contains "$preseed_workflow" 'VERSION_IN="$ORCASLICER_VERSION"' "Default Orca preseed should use the repository-pinned release"
    assert_contains "$worker_compose" 'Worker__OrcaSlicerPath=/usr/local/bin/orcaslicer' "Compose should not bypass the AppRun launcher"

    pass_test
}

test_stable_default_and_checksum_are_pinned() {
    start_test "stable OrcaSlicer default and checksum are pinned"

    local resolved
    resolved=$(
        unset ORCASLICER_VERSION ORCASLICER_SHA256
        source "$REPO_ROOT/scripts/docker/container-versions.conf"
        printf '%s|%s' "$ORCASLICER_VERSION" "$ORCASLICER_SHA256"
    )

    assert_equals \
        "2.4.2|d12fb8c8eac1aecd2dfb6377acd48f994f8fa439ed5292fa532dd82880f029fd" \
        "$resolved"

    pass_test
}

run_tests() {
    test_matching_metadata_is_accepted
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
}

setup
trap teardown EXIT
run_test_suite run_tests "OrcaSlicer binary metadata"
