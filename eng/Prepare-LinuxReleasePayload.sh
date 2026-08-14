#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 1 ]]; then
    echo "Usage: $0 OUTPUT_PATH" >&2
    exit 2
fi

if [[ "$(uname -s)" != "Linux" || "$(uname -m)" != "x86_64" ]]; then
    echo "The Linux release payload must be built on Linux x86_64." >&2
    exit 2
fi

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
workspace_root="$(cd -- "$script_dir/.." && pwd)"
output_path="$(realpath -m -- "$1")"
build_root="${CLIPEDIT_LINUX_NATIVE_BUILD_ROOT:-${XDG_CACHE_HOME:-$HOME/.cache}/clipedit/native/linux-x64}"
mpv_build_root="$build_root/mpv-build"
venv_root="$build_root/venv"
staging_path="$output_path.staging-$$"
native_dependencies_path="$workspace_root/eng/native/native-dependencies.json"

if [[ -e "$output_path" ]]; then
    echo "The payload output path already exists: $output_path" >&2
    exit 2
fi

required_commands=(
    autoconf automake gcc git make nasm ninja patchelf pkg-config python3
)
missing_commands=()
for command_name in "${required_commands[@]}"; do
    if ! command -v "$command_name" >/dev/null 2>&1; then
        missing_commands+=("$command_name")
    fi
done
if (( ${#missing_commands[@]} > 0 )); then
    echo "Missing Linux build tools: ${missing_commands[*]}" >&2
    echo "Run eng/Install-LinuxBuildDependencies.sh once, then retry." >&2
    exit 2
fi

eval "$(python3 - "$native_dependencies_path" <<'PY'
import json, shlex, sys
with open(sys.argv[1], encoding='utf-8') as stream:
    pins = json.load(stream)
components = pins['components']
values = {
    'mpv_build_revision': components['mpvBuild']['revision'],
    'mpv_revision': components['mpv']['revision'],
    'ffmpeg_revision': components['ffmpeg']['revision'],
    'ffmpeg_version': components['ffmpeg']['version'],
    'libplacebo_revision': components['libplacebo']['revision'],
    'libass_revision': components['libass']['revision'],
    'meson_version': components['meson']['version'],
    'mpv_client_api': pins['linux']['mpvClientApi'],
    'libmpv_build_file': pins['linux']['libmpvBuildFile'],
}
for name, value in values.items():
    print(f'{name}={shlex.quote(value)}')
PY
)"
readonly mpv_build_revision mpv_revision ffmpeg_revision ffmpeg_version
readonly libplacebo_revision libass_revision meson_version mpv_client_api libmpv_build_file

required_packages=(
    alsa fontconfig freetype2 fribidi gnutls harfbuzz libpulse opus vpx x264
)
missing_packages=()
for package_name in "${required_packages[@]}"; do
    if ! pkg-config --exists "$package_name"; then
        missing_packages+=("$package_name")
    fi
done
if (( ${#missing_packages[@]} > 0 )); then
    echo "Missing Linux development packages: ${missing_packages[*]}" >&2
    echo "Run eng/Install-LinuxBuildDependencies.sh once, then retry." >&2
    exit 2
fi

platform_library_names=(
    libfontconfig.so.1 libICE.so.6 libSM.so.6 libX11.so.6
)
declare -A platform_library_paths=()
for library_name in "${platform_library_names[@]}"; do
    library_path="$(ldconfig -p | awk -v name="$library_name" '$1 == name && $NF ~ /^\// { print $NF; exit }')"
    if [[ -z "$library_path" || ! -f "$library_path" ]]; then
        echo "Missing Linux runtime library $library_name." >&2
        echo "Run eng/Install-LinuxBuildDependencies.sh once, then retry." >&2
        exit 2
    fi

    platform_library_paths[$library_name]="$library_path"
done

cleanup() {
    if [[ -d "$staging_path" ]]; then
        rm -rf -- "$staging_path"
    fi
}
trap cleanup EXIT

mkdir -p -- "$build_root"
if [[ ! -d "$mpv_build_root/.git" ]]; then
    git clone https://github.com/mpv-player/mpv-build.git "$mpv_build_root"
fi

git -C "$mpv_build_root" fetch origin "$mpv_build_revision"
git -C "$mpv_build_root" checkout --detach "$mpv_build_revision"
(
    cd -- "$mpv_build_root"
    ./update --skip-selfupdate
)
git -C "$mpv_build_root/ffmpeg" checkout --detach "$ffmpeg_revision"
git -C "$mpv_build_root/libplacebo" checkout --detach "$libplacebo_revision"
git -C "$mpv_build_root/libass" checkout --detach "$libass_revision"
git -C "$mpv_build_root/mpv" checkout --detach "$mpv_revision"

if [[ ! -x "$venv_root/bin/python" ]]; then
    python3 -m venv "$venv_root"
fi
"$venv_root/bin/pip" install --disable-pip-version-check "meson==$meson_version"
export PATH="$venv_root/bin:$PATH"

revision_stamp="$build_root/native-revisions.txt"
expected_revisions="$(printf '%s\n' \
    "mpv-build=$mpv_build_revision" \
    "mpv=$mpv_revision" \
    "ffmpeg=$ffmpeg_revision" \
    "libplacebo=$libplacebo_revision" \
    "libass=$libass_revision" \
    "meson=$meson_version" \
    "recipe=linux-hdr-v1")"

ffmpeg_binary="$mpv_build_root/build_libs/bin/ffmpeg"
ffprobe_binary="$mpv_build_root/build_libs/bin/ffprobe"
libmpv_binary="$mpv_build_root/mpv/build/$libmpv_build_file"
actual_revisions=''
if [[ -f "$revision_stamp" ]]; then
    actual_revisions="$(<"$revision_stamp")"
fi

if [[ "$actual_revisions" != "$expected_revisions" || \
      ! -x "$ffmpeg_binary" || ! -x "$ffprobe_binary" || ! -f "$libmpv_binary" ]]; then
    (
        cd -- "$mpv_build_root"
        scripts/libplacebo-clean || true
        scripts/libass-clean || true
        scripts/ffmpeg-clean || true
        scripts/mpv-clean || true

        scripts/libplacebo-config \
            -Dvulkan=disabled \
            -Dopengl=enabled \
            -Dglslang=disabled \
            -Dshaderc=disabled \
            -Dlcms=disabled \
            -Ddovi=disabled \
            -Dlibdovi=disabled \
            -Dunwind=disabled \
            -Dxxhash=disabled
        scripts/libplacebo-build -j"$(nproc)"

        scripts/libass-config
        scripts/libass-build -j"$(nproc)"

        scripts/ffmpeg-config \
            --enable-libx264 \
            --enable-libvpx \
            --enable-libzimg \
            --enable-libopus
        scripts/ffmpeg-build -j"$(nproc)"

        scripts/mpv-config \
            -Dcplayer=false \
            -Dlibmpv=true \
            -Dbuild-date=false \
            -Dlua=disabled \
            -Djavascript=disabled \
            -Ddvdnav=disabled \
            -Dlibbluray=disabled \
            -Dlibarchive=disabled \
            -Dlibcurl=disabled \
            -Duchardet=disabled \
            -Djpeg=disabled \
            -Dcplugins=disabled \
            -Dalsa=enabled \
            -Dpulse=enabled \
            -Dpipewire=disabled \
            -Dgl=enabled \
            -Dplain-gl=enabled \
            -Dvulkan=disabled \
            -Dx11=disabled \
            -Dwayland=disabled \
            -Ddrm=disabled \
            -Dgbm=disabled \
            -Degl=disabled \
            -Dvaapi=disabled \
            -Dvdpau=disabled \
            -Dmanpage-build=disabled
        scripts/mpv-build -j"$(nproc)"
    )

    printf '%s\n' "$expected_revisions" > "$revision_stamp"
fi

escaped_ffmpeg_version="${ffmpeg_version//./\\.}"
if ! "$ffmpeg_binary" -version | head -n 1 | grep -E "^ffmpeg version n?${escaped_ffmpeg_version}([[:space:]]|$)" >/dev/null; then
    echo "The Linux FFmpeg binary did not report version $ffmpeg_version." >&2
    exit 1
fi
encoders="$($ffmpeg_binary -hide_banner -encoders 2>&1)"
for encoder in libx264 libvpx-vp9 aac libopus; do
    if ! grep -E "[[:space:]]$encoder[[:space:]]" <<<"$encoders" >/dev/null; then
        echo "The Linux FFmpeg binary is missing encoder $encoder." >&2
        exit 1
    fi
done
filters="$($ffmpeg_binary -hide_banner -filters 2>&1)"
for filter in crop scale zscale tonemap format setparams rotate overlay concat; do
    if ! grep -E "[[:space:]]${filter}[[:space:]]" <<<"$filters" >/dev/null; then
        echo "The Linux FFmpeg binary is missing filter $filter." >&2
        exit 1
    fi
done

client_api="$({ python3 - "$libmpv_binary" <<'PY'
import ctypes
import sys
library = ctypes.CDLL(sys.argv[1])
library.mpv_client_api_version.restype = ctypes.c_ulong
version = library.mpv_client_api_version()
print(f"{version >> 16}.{version & 0xffff}")
PY
} 2>/dev/null)"
if [[ "$client_api" != "$mpv_client_api" ]]; then
    echo "The Linux libmpv binary reported client API $client_api; expected $mpv_client_api." >&2
    exit 1
fi

mkdir -p -- "$staging_path/tools/ffmpeg" "$staging_path/native" "$staging_path/licenses"
payload_origin_map="$staging_path/licenses/PAYLOAD-ORIGINS.tsv"
printf 'payloadPath\toriginPath\n' > "$payload_origin_map"
install -m 0755 "$ffmpeg_binary" "$staging_path/tools/ffmpeg/ffmpeg"
install -m 0755 "$ffprobe_binary" "$staging_path/tools/ffmpeg/ffprobe"
install -m 0755 "$libmpv_binary" "$staging_path/libmpv.so.2"
for library_name in "${platform_library_names[@]}"; do
    install -m 0755 "${platform_library_paths[$library_name]}" "$staging_path/$library_name"
    printf '%s\t%s\n' "$library_name" "${platform_library_paths[$library_name]}" >> "$payload_origin_map"
done

declare -a dependency_queue=(
    "$staging_path/tools/ffmpeg/ffmpeg"
    "$staging_path/tools/ffmpeg/ffprobe"
    "$staging_path/libmpv.so.2"
)
for library_name in "${platform_library_names[@]}"; do
    dependency_queue+=("$staging_path/$library_name")
done
declare -A visited_dependencies=()
excluded_dependency_pattern='^(ld-linux-x86-64\.so\.2|libc\.so\.6|libdl\.so\.2|libm\.so\.6|libpthread\.so\.0|libresolv\.so\.2|librt\.so\.1)$'

while (( ${#dependency_queue[@]} > 0 )); do
    current_binary="${dependency_queue[0]}"
    dependency_queue=("${dependency_queue[@]:1}")
    while read -r dependency_path; do
        [[ -n "$dependency_path" && -f "$dependency_path" ]] || continue
        dependency_name="$(basename -- "$dependency_path")"
        [[ "$dependency_name" =~ $excluded_dependency_pattern ]] && continue
        [[ -n "${visited_dependencies[$dependency_name]:-}" ]] && continue
        visited_dependencies[$dependency_name]="$dependency_path"
        install -m 0755 -D "$dependency_path" "$staging_path/native/$dependency_name"
        dependency_queue+=("$staging_path/native/$dependency_name")
    done < <(ldd "$current_binary" | awk '/=> \/[^ ]+/ { print $3 }')
done

for dependency_name in "${!visited_dependencies[@]}"; do
    printf 'native/%s\t%s\n' "$dependency_name" "${visited_dependencies[$dependency_name]}" >> "$payload_origin_map"
done

patchelf --set-rpath '$ORIGIN/../../native' "$staging_path/tools/ffmpeg/ffmpeg"
patchelf --set-rpath '$ORIGIN/../../native' "$staging_path/tools/ffmpeg/ffprobe"
patchelf --set-rpath '$ORIGIN/native' "$staging_path/libmpv.so.2"
for library_name in "${platform_library_names[@]}"; do
    patchelf --set-rpath '$ORIGIN/native' "$staging_path/$library_name"
done
while IFS= read -r native_library; do
    patchelf --set-rpath '$ORIGIN' "$native_library"
done < <(find "$staging_path/native" -maxdepth 1 -type f -name '*.so*' -print)

install -m 0644 "$workspace_root/LICENSE" "$staging_path/LICENSE.txt"
install -m 0644 "$workspace_root/THIRD_PARTY_NOTICES.md" "$staging_path/licenses/THIRD_PARTY_NOTICES.md"
install -m 0644 "$mpv_build_root/ffmpeg/COPYING.GPLv3" "$staging_path/licenses/FFmpeg-GPL-3.0.txt"
install -m 0644 "$mpv_build_root/mpv/Copyright" "$staging_path/licenses/mpv-Copyright.txt"
install -m 0644 "$mpv_build_root/libass/COPYING" "$staging_path/licenses/libass-ISC.txt"
install -m 0644 "$mpv_build_root/libplacebo/LICENSE" "$staging_path/licenses/libplacebo-LGPL-2.1.txt"
printf '%s\n' "$expected_revisions" > "$staging_path/licenses/native-build-revisions.txt"

"$workspace_root/eng/compliance/Collect-LinuxPayloadProvenance.sh" \
    "$staging_path" \
    "$payload_origin_map" \
    "$staging_path/licenses/linux-system-provenance"

(
    cd -- "$staging_path"
    find . -type f ! -path './licenses/PAYLOAD-SHA256SUMS' -print0 |
        sort -z |
        xargs -0 sha256sum > licenses/PAYLOAD-SHA256SUMS
)

mkdir -p -- "$(dirname -- "$output_path")"
mv -- "$staging_path" "$output_path"
trap - EXIT
echo "ClipEdit linux-x64 native payload is ready at $output_path"
