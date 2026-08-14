#!/usr/bin/env bash
set -euo pipefail

if (( $# < 2 )); then
    echo "Usage: $0 <tag> <asset> [<asset> ...]" >&2
    exit 2
fi

readonly tag="$1"
shift
: "${GITHUB_REPOSITORY:?GITHUB_REPOSITORY must identify the release repository.}"

release_json="$(gh api "repos/${GITHUB_REPOSITORY}/releases/tags/${tag}")"
readonly release_json
is_draft="$(jq -r '.draft' <<<"$release_json")"
readonly is_draft
if [[ "$is_draft" != 'true' && "$is_draft" != 'false' ]]; then
    echo "Could not determine whether release ${tag} is a draft." >&2
    exit 3
fi

download_directory="$(mktemp -d "${RUNNER_TEMP:-${TMPDIR:-/tmp}}/clipedit-release-assets.XXXXXXXX")"
readonly download_directory
cleanup() {
    rm -rf -- "$download_directory"
}
trap cleanup EXIT

for asset_path in "$@"; do
    if [[ ! -f "$asset_path" ]]; then
        echo "Release asset does not exist: $asset_path" >&2
        exit 4
    fi

    asset_name="$(basename -- "$asset_path")"
    existing_asset="$(jq -c --arg name "$asset_name" '.assets[]? | select(.name == $name)' <<<"$release_json")"
    if [[ -z "$existing_asset" ]]; then
        echo "Uploading missing release asset ${asset_name}."
        gh release upload "$tag" "$asset_path" --repo "$GITHUB_REPOSITORY"
        continue
    fi

    if [[ "$is_draft" == 'true' ]]; then
        echo "Replacing existing draft asset ${asset_name}."
        gh release upload "$tag" --clobber "$asset_path" --repo "$GITHUB_REPOSITORY"
        continue
    fi

    local_digest="sha256:$(sha256sum "$asset_path" | awk '{ print $1 }')"
    remote_digest="$(jq -r '.digest // empty' <<<"$existing_asset")"
    if [[ "$remote_digest" == "$local_digest" ]]; then
        echo "Published asset ${asset_name} is already present with the expected digest; skipping it."
        continue
    fi

    if [[ -z "$remote_digest" ]]; then
        downloaded_asset="${download_directory}/${asset_name}"
        gh release download "$tag" --pattern "$asset_name" --output "$downloaded_asset" --repo "$GITHUB_REPOSITORY"
        downloaded_digest="sha256:$(sha256sum "$downloaded_asset" | awk '{ print $1 }')"
        if [[ "$downloaded_digest" == "$local_digest" ]]; then
            echo "Published asset ${asset_name} is already present with identical content; skipping it."
            continue
        fi
        remote_digest="$downloaded_digest"
    fi

    echo "Published asset ${asset_name} differs from the newly built file." >&2
    echo "Existing digest: ${remote_digest}" >&2
    echo "New digest:      ${local_digest}" >&2
    echo "Use a new release version, or explicitly remove the existing asset before retrying." >&2
    exit 5
done
