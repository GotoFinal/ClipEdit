#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 5 ]]; then
    echo "Usage: $0 SOURCE_LOCK SOURCE_EXCLUSIONS RECIPE_DIRECTORY NATIVE_DEPENDENCIES OUTPUT_DIRECTORY" >&2
    exit 2
fi

readonly source_lock="$1"
readonly source_exclusions="$2"
readonly recipe_directory="$3"
readonly native_dependencies="$4"
readonly output_directory="$5"
toolchain_revision="$(python - "$native_dependencies" <<'PY'
import json, sys
with open(sys.argv[1], encoding='utf-8') as stream:
    print(json.load(stream)['windows']['toolchainRevision'])
PY
)"
readonly toolchain_revision
readonly cache_root='/cache'

toolchain_root="${cache_root}/mpv-winbuild-cmake"
if [[ ! -d "${toolchain_root}/.git" ]]; then
    echo "The pinned Windows source cache is missing: ${toolchain_root}" >&2
    echo 'Build the native stack with eng/Build-WindowsSharedMediaStack.ps1 first.' >&2
    exit 3
fi

mkdir -p -- "$output_directory"
staging_directory="$(mktemp -d "${cache_root}/clipedit-source-export.XXXXXXXX")"
cleanup() {
    rm -rf -- "$staging_directory"
}
trap cleanup EXIT

archive_root="${staging_directory}/ClipEdit-windows-native-corresponding-source"
source_root="${archive_root}/sources"
license_root="${staging_directory}/ClipEdit-windows-native-third-party-licenses"
mkdir -p -- "$source_root" "$license_root" "${archive_root}/build-recipe"

provenance_path="${archive_root}/SOURCE-PROVENANCE.tsv"
license_manifest_path="${license_root}/LICENSE-MANIFEST.tsv"
printf 'component\trevision\torigin\n' > "$provenance_path"
printf 'component\trevision\tpath\n' > "$license_manifest_path"

missing_licenses=()
while IFS=$'\t' read -r name revision; do
    [[ -z "$name" || "$name" == \#* ]] && continue

    if awk -F '\t' -v component="$name" \
        '$1 == component { found = 1 } END { exit !found }' "$source_exclusions"; then
        continue
    fi

    source_directory="${toolchain_root}/src_packages/${name}"
    if [[ ! -d "${source_directory}/.git" ]]; then
        echo "Locked source is missing from the build cache: ${name}" >&2
        exit 3
    fi

    actual_revision="$(git -C "$source_directory" rev-parse HEAD)"
    if [[ "$actual_revision" != "$revision" ]]; then
        echo "Source cache revision mismatch for ${name}: ${actual_revision}; expected ${revision}" >&2
        exit 3
    fi

    origin="$(git -C "$source_directory" config --get remote.origin.url || true)"
    [[ -n "$origin" ]] || origin='NOASSERTION'
    printf '%s\t%s\t%s\n' "$name" "$revision" "$origin" >> "$provenance_path"

    mkdir -p -- "${source_root}/${name}" "${license_root}/${name}"
    git -C "$source_directory" archive "$revision" |
        tar -xf - -C "${source_root}/${name}"

    license_count=0
    while IFS= read -r -d '' license_path; do
        relative_path="${license_path#"${source_directory}"/}"
        destination="${license_root}/${name}/${relative_path}"
        mkdir -p -- "$(dirname -- "$destination")"
        cp -a -- "$license_path" "$destination"
        printf '%s\t%s\t%s\n' "$name" "$revision" "${name}/${relative_path}" >> "$license_manifest_path"
        license_count=$((license_count + 1))
    done < <(find "$source_directory" -maxdepth 3 -type f \
        \( -iname 'COPYING*' -o -iname 'LICENSE*' -o -iname 'LICENCE*' -o \
           -iname 'NOTICE*' -o -iname 'COPYRIGHT*' -o -iname 'AUTHORS*' \) \
        -print0 | sort -z)

    # Header-only SDKs sometimes carry the complete notice in each header but
    # do not ship a standalone LICENSE file. Preserve representative source
    # files verbatim in the notice bundle instead of guessing a license.
    if (( license_count == 0 )); then
        while IFS= read -r -d '' notice_path; do
            relative_path="${notice_path#"${source_directory}"/}"
            destination="${license_root}/${name}/source-notices/${relative_path}"
            mkdir -p -- "$(dirname -- "$destination")"
            cp -a -- "$notice_path" "$destination"
            printf '%s\t%s\t%s\n' \
                "$name" "$revision" "${name}/source-notices/${relative_path}" >> "$license_manifest_path"
            license_count=$((license_count + 1))
        done < <(grep -RIlZ -m1 \
            -E 'Copyright|Permission is hereby granted|Redistribution and use' \
            --exclude-dir=.git "$source_directory" | sort -z | head -z -n 20)
    fi

    if (( license_count == 0 )); then
        missing_licenses+=("$name")
    fi
done < "$source_lock"

if (( ${#missing_licenses[@]} > 0 )); then
    printf 'No license/notice file was found for: %s\n' "${missing_licenses[*]}" >&2
    exit 4
fi

actual_toolchain_revision="$(git -C "$toolchain_root" rev-parse HEAD)"
if [[ "$actual_toolchain_revision" != "$toolchain_revision" ]]; then
    echo "Toolchain source mismatch: ${actual_toolchain_revision}; expected ${toolchain_revision}" >&2
    exit 3
fi
mkdir -p -- "${source_root}/mpv-winbuild-cmake"
git -C "$toolchain_root" archive "$toolchain_revision" |
    tar -xf - -C "${source_root}/mpv-winbuild-cmake"
printf '%s\t%s\t%s\n' \
    'mpv-winbuild-cmake' \
    "$toolchain_revision" \
    "$(git -C "$toolchain_root" config --get remote.origin.url)" >> "$provenance_path"

cp -a -- "$recipe_directory/build.sh" "${archive_root}/build-recipe/"
cp -a -- "$recipe_directory/Dockerfile" "${archive_root}/build-recipe/"
cp -a -- "$recipe_directory/mpv-winbuild-cmake.patch" "${archive_root}/build-recipe/"
cp -a -- "$source_lock" "${archive_root}/build-recipe/source-lock.tsv"
cp -a -- "$source_exclusions" "${archive_root}/build-recipe/source-exclusions.tsv"
cp -a -- "$native_dependencies" "${archive_root}/build-recipe/native-dependencies.json"
cat > "${archive_root}/README.txt" <<'EOF'
This archive contains the exact upstream source revisions and the complete
ClipEdit build recipe used to produce the Windows x64 native media payload.
The source trees intentionally omit Git history; SOURCE-PROVENANCE.tsv records
the upstream origin and exact commit for every tree.
EOF

source_archive="${output_directory}/windows-native-corresponding-source.tar.zst"
license_archive="${output_directory}/windows-native-third-party-licenses.tar.zst"
tar --sort=name --mtime='UTC 1970-01-01' --owner=0 --group=0 --numeric-owner \
    --zstd -cf "$source_archive" -C "$staging_directory" \
    'ClipEdit-windows-native-corresponding-source'
tar --sort=name --mtime='UTC 1970-01-01' --owner=0 --group=0 --numeric-owner \
    --zstd -cf "$license_archive" -C "$staging_directory" \
    'ClipEdit-windows-native-third-party-licenses'

cp -a -- "$provenance_path" "${output_directory}/windows-native-source-provenance.tsv"
cp -a -- "$license_manifest_path" "${output_directory}/windows-native-license-manifest.tsv"
(
    cd -- "$output_directory"
    sha256sum \
        windows-native-corresponding-source.tar.zst \
        windows-native-third-party-licenses.tar.zst \
        windows-native-source-provenance.tsv \
        windows-native-license-manifest.tsv > SHA256SUMS
)

echo "Windows corresponding-source export is ready at ${output_directory}"
