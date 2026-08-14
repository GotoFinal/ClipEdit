#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 3 ]]; then
    echo "Usage: $0 LINUX_PAYLOAD RECIPE_DIRECTORY OUTPUT_DIRECTORY" >&2
    exit 2
fi

readonly payload_root="$(realpath -- "$1")"
readonly recipe_directory="$(realpath -- "$2")"
readonly output_directory="$(realpath -m -- "$3")"
readonly cache_root="${XDG_CACHE_HOME:-$HOME/.cache}/clipedit/compliance/linux-source"
readonly native_build_root="${CLIPEDIT_LINUX_NATIVE_BUILD_ROOT:-${XDG_CACHE_HOME:-$HOME/.cache}/clipedit/native/linux-x64}/mpv-build"
readonly provenance_root="${payload_root}/licenses/linux-system-provenance"
readonly debian_provenance="${provenance_root}/linux-debian-binary-provenance.tsv"
readonly native_dependencies_path="${recipe_directory}/native/native-dependencies.json"

if [[ ! -f "$debian_provenance" ]]; then
    echo "The Linux payload has no build-time Debian provenance: ${debian_provenance}" >&2
    echo 'Rebuild it with eng/Prepare-ReleasePayload.ps1 before exporting source.' >&2
    exit 3
fi
if ! command -v zstd >/dev/null 2>&1; then
    echo 'zstd is required to create the corresponding-source archive.' >&2
    exit 2
fi

mkdir -p -- "$output_directory" "$cache_root"
staging_directory="$(mktemp -d "${TMPDIR:-/tmp}/clipedit-linux-source.XXXXXXXX")"
cleanup() {
    rm -rf -- "$staging_directory"
}
trap cleanup EXIT

archive_root="${staging_directory}/ClipEdit-linux-native-corresponding-source"
source_root="${archive_root}/sources"
license_root="${staging_directory}/ClipEdit-linux-native-third-party-licenses"
mkdir -p -- "$source_root" "$license_root" "${archive_root}/build-recipe"
provenance_path="${archive_root}/SOURCE-PROVENANCE.tsv"
license_manifest_path="${license_root}/LICENSE-MANIFEST.tsv"
printf 'component\trevision\torigin\n' > "$provenance_path"
printf 'component\trevision\tpath\n' > "$license_manifest_path"

mapfile -t source_components < <(python3 - "$native_dependencies_path" <<'PY'
import json, sys
with open(sys.argv[1], encoding='utf-8') as stream:
    components = json.load(stream)['components']
for key, name in (
    ('mpvBuild', 'mpv-build'),
    ('mpv', 'mpv'),
    ('ffmpeg', 'ffmpeg'),
    ('libplacebo', 'libplacebo'),
    ('libass', 'libass'),
):
    component = components[key]
    print(f"{name}|{component['revision']}|{component['repository']}")
PY
)
readonly -a source_components

for entry in "${source_components[@]}"; do
    IFS='|' read -r name revision origin <<< "$entry"
    if [[ "$name" == 'mpv-build' && -d "${native_build_root}/.git" ]]; then
        repository="$native_build_root"
    elif [[ -d "${native_build_root}/${name}/.git" ]]; then
        repository="${native_build_root}/${name}"
    else
        repository="${cache_root}/${name}"
    fi
    if [[ ! -d "${repository}/.git" ]]; then
        git clone --filter=blob:none "$origin" "$repository"
    fi
    if ! git -C "$repository" cat-file -e "${revision}^{commit}" 2>/dev/null; then
        git -C "$repository" fetch --force origin "$revision"
    fi
    if ! git -C "$repository" cat-file -e "${revision}^{commit}" 2>/dev/null; then
        echo "Pinned source revision is unavailable for ${name}: ${revision}" >&2
        exit 3
    fi

    mkdir -p -- "${source_root}/${name}" "${license_root}/${name}"
    git -C "$repository" archive "$revision" | tar -xf - -C "${source_root}/${name}"
    printf '%s\t%s\t%s\n' "$name" "$revision" "$origin" >> "$provenance_path"

    license_count=0
    while IFS= read -r -d '' license_path; do
        relative_path="${license_path#${repository}/}"
        destination="${license_root}/${name}/${relative_path}"
        mkdir -p -- "$(dirname -- "$destination")"
        cp -a -- "$license_path" "$destination"
        printf '%s\t%s\t%s\n' "$name" "$revision" "${name}/${relative_path}" >> "$license_manifest_path"
        license_count=$((license_count + 1))
    done < <(find "$repository" -maxdepth 3 -type f \
        \( -iname 'COPYING*' -o -iname 'LICENSE*' -o -iname 'LICENCE*' -o \
           -iname 'NOTICE*' -o -iname 'COPYRIGHT*' -o -iname 'AUTHORS*' \) \
        -print0 | sort -z)
    if (( license_count == 0 )); then
        echo "No license or notice file was found for source-built component ${name}." >&2
        exit 4
    fi
done

debian_source_root="${source_root}/ubuntu-22.04-source-packages"
mkdir -p -- "$debian_source_root" "${license_root}/ubuntu-22.04"
declare -A downloaded_sources=()
while IFS=$'\t' read -r payload_path payload_hash origin_path binary_package binary_version \
    source_package source_version copyright_file; do
    [[ "$payload_path" == 'payloadPath' ]] && continue
    source_key="${source_package}|${source_version}"
    if [[ -n "${downloaded_sources[$source_key]:-}" ]]; then
        continue
    fi
    downloaded_sources[$source_key]=1

    if ! apt-cache showsrc "$source_package" >/dev/null 2>&1; then
        echo "Ubuntu source indexes are unavailable for ${source_package}." >&2
        echo 'Enable Ubuntu 22.04 deb-src repositories and run apt-get update, then retry.' >&2
        exit 5
    fi

    safe_name="${source_package}_${source_version//[:\/+~]/_}"
    package_output="${debian_source_root}/${safe_name}"
    mkdir -p -- "$package_output"
    (
        cd -- "$package_output"
        apt-get source --download-only "${source_package}=${source_version}"
    )
    if ! find "$package_output" -maxdepth 1 -type f -name '*.dsc' -print -quit | grep -q .; then
        echo "Ubuntu source download produced no .dsc for ${source_package} ${source_version}." >&2
        exit 5
    fi

    origin="https://packages.ubuntu.com/source/jammy/${source_package}"
    printf 'ubuntu:%s\t%s\t%s\n' "$source_package" "$source_version" "$origin" >> "$provenance_path"
    copyright_source="${provenance_root}/${copyright_file}"
    if [[ ! -f "$copyright_source" ]]; then
        echo "Recorded Ubuntu copyright file is missing: ${copyright_source}" >&2
        exit 4
    fi
    copyright_destination="${license_root}/ubuntu-22.04/${safe_name}.copyright"
    cp -a -- "$copyright_source" "$copyright_destination"
    printf 'ubuntu:%s\t%s\t%s\n' \
        "$source_package" "$source_version" "ubuntu-22.04/${safe_name}.copyright" \
        >> "$license_manifest_path"
done < "$debian_provenance"

cp -a -- "$recipe_directory/Prepare-LinuxReleasePayload.sh" "${archive_root}/build-recipe/"
cp -a -- "$recipe_directory/Install-LinuxBuildDependencies.sh" "${archive_root}/build-recipe/"
cp -a -- "$native_dependencies_path" "${archive_root}/build-recipe/"
cp -a -- "$recipe_directory/compliance/Collect-LinuxPayloadProvenance.sh" \
    "${archive_root}/build-recipe/"
cp -a -- "$debian_provenance" "${archive_root}/build-recipe/"
cat > "${archive_root}/README.txt" <<'EOF'
This archive contains the exact source-built Git revisions, Ubuntu 22.04
source packages for every bundled distribution library, and ClipEdit's build
recipe. SOURCE-PROVENANCE.tsv records every source origin and revision.
EOF

source_archive="${output_directory}/linux-native-corresponding-source.tar.zst"
license_archive="${output_directory}/linux-native-third-party-licenses.tar.zst"
tar --sort=name --mtime='UTC 1970-01-01' --owner=0 --group=0 --numeric-owner \
    -I 'zstd -T0 -6' -cf "$source_archive" -C "$staging_directory" \
    'ClipEdit-linux-native-corresponding-source'
tar --sort=name --mtime='UTC 1970-01-01' --owner=0 --group=0 --numeric-owner \
    -I 'zstd -T0 -6' -cf "$license_archive" -C "$staging_directory" \
    'ClipEdit-linux-native-third-party-licenses'
cp -a -- "$provenance_path" "${output_directory}/linux-native-source-provenance.tsv"
cp -a -- "$license_manifest_path" "${output_directory}/linux-native-license-manifest.tsv"
(
    cd -- "$output_directory"
    sha256sum \
        linux-native-corresponding-source.tar.zst \
        linux-native-third-party-licenses.tar.zst \
        linux-native-source-provenance.tsv \
        linux-native-license-manifest.tsv > SHA256SUMS
)
echo "Linux corresponding-source export is ready at ${output_directory}."
