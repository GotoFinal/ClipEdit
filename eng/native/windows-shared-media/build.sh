#!/usr/bin/env bash
set -euo pipefail

readonly TOOLCHAIN_REVISION="cd1edc11dc6887a50f705717619d879f5a93a488"
readonly TOOLCHAIN_REPOSITORY="https://github.com/zhongfly/mpv-winbuild-cmake.git"
readonly CACHE_ROOT="/cache"
readonly TOOLCHAIN_ROOT="${CACHE_ROOT}/mpv-winbuild-cmake"
readonly BUILD_ROOT="${TOOLCHAIN_ROOT}/build64"
readonly PREFIX="${BUILD_ROOT}/install/x86_64-w64-mingw32"
readonly OUTPUT_ROOT="/output"
readonly JOBS="${CLIPEDIT_NATIVE_JOBS:-8}"

if [[ ! "${JOBS}" =~ ^[1-9][0-9]*$ ]] || (( JOBS > 32 )); then
    echo "CLIPEDIT_NATIVE_JOBS must be an integer from 1 through 32." >&2
    exit 2
fi
if [[ ! -d "${OUTPUT_ROOT}" ]] || find "${OUTPUT_ROOT}" -mindepth 1 -print -quit | grep -q .; then
    echo "The mounted output directory must exist and be empty: ${OUTPUT_ROOT}" >&2
    exit 2
fi

mkdir -p "${CACHE_ROOT}"
if [[ ! -d "${TOOLCHAIN_ROOT}/.git" ]]; then
    git clone --filter=blob:none "${TOOLCHAIN_REPOSITORY}" "${TOOLCHAIN_ROOT}"
fi

git -C "${TOOLCHAIN_ROOT}" restore --staged --worktree -- .
rm -f "${TOOLCHAIN_ROOT}/cmake/copy_ffmpeg_dlls.cmake"
git -C "${TOOLCHAIN_ROOT}" fetch origin "${TOOLCHAIN_REVISION}"
git -C "${TOOLCHAIN_ROOT}" checkout --detach "${TOOLCHAIN_REVISION}"
git -C "${TOOLCHAIN_ROOT}" apply /opt/clipedit/mpv-winbuild-cmake.patch
git config --global user.email build@clipedit.local
git config --global user.name ClipEdit-Native-Builder

cmake -Wno-dev --fresh \
    -DTARGET_ARCH=x86_64-w64-mingw32 \
    -DCOMPILER_TOOLCHAIN=gcc \
    -DENABLE_CCACHE=ON \
    -DSINGLE_SOURCE_LOCATION="${TOOLCHAIN_ROOT}/src_packages" \
    -DRUSTUP_LOCATION="${TOOLCHAIN_ROOT}/install_rustup" \
    -G Ninja \
    -H"${TOOLCHAIN_ROOT}" \
    -B"${BUILD_ROOT}"

ninja -j "${JOBS}" -C "${BUILD_ROOT}" download
if [[ ! -f "${BUILD_ROOT}/install/bin/cross-gcc" ]]; then
    ninja -j "${JOBS}" -C "${BUILD_ROOT}" gcc
    rm -rf "${BUILD_ROOT}/toolchain"
fi
if [[ ! -x "${TOOLCHAIN_ROOT}/install_rustup/.cargo/bin/rustc" ]]; then
    ninja -j "${JOBS}" -C "${BUILD_ROOT}" rustup-fullclean
    ninja -j "${JOBS}" -C "${BUILD_ROOT}" rustup
fi

# The download target materializes every source repository. Do not run the
# toolchain's force-update target afterward: it follows branch tips and cannot
# be allowed to replace the reviewed revisions below. Local branches preserve
# the upstream build's reset semantics without tracking a network branch.
while IFS=$'\t' read -r name revision; do
    [[ -z "${name}" || "${name}" == \#* ]] && continue
    source_directory="${TOOLCHAIN_ROOT}/src_packages/${name}"
    top_level="$(git -C "${source_directory}" rev-parse --show-toplevel 2>/dev/null || true)"
    if [[ "${top_level}" != "${source_directory}" ]]; then
        ninja -j "${JOBS}" -C "${BUILD_ROOT}" "${name}-download"
        top_level="$(git -C "${source_directory}" rev-parse --show-toplevel 2>/dev/null || true)"
    fi
    if [[ "${top_level}" != "${source_directory}" ]]; then
        echo "Locked Git source is missing: ${name}" >&2
        exit 3
    fi
    if ! git -C "${source_directory}" cat-file -e "${revision}^{commit}" 2>/dev/null; then
        git -C "${source_directory}" fetch --force --tags origin \
            '+refs/heads/*:refs/remotes/origin/*'
    fi
    if ! git -C "${source_directory}" cat-file -e "${revision}^{commit}" 2>/dev/null; then
        echo "Locked Git revision is unavailable for ${name}: ${revision}" >&2
        exit 3
    fi
    git -C "${source_directory}" reset --hard "${revision}"
    git -C "${source_directory}" clean -df
    git -C "${source_directory}" update-ref refs/heads/clipedit-locked-upstream "${revision}"
    git -C "${source_directory}" checkout -B clipedit-build "${revision}"
    git -C "${source_directory}" config branch.clipedit-build.remote .
    git -C "${source_directory}" config branch.clipedit-build.merge refs/heads/clipedit-locked-upstream
    actual_revision="$(git -C "${source_directory}" rev-parse HEAD)"
    if [[ "${actual_revision}" != "${revision}" ]]; then
        echo "Source lock mismatch for ${name}: ${actual_revision}" >&2
        exit 3
    fi
done < /opt/clipedit/source-lock.tsv

# mpv's package rename target leaves dated output directories outside its
# fullclean target. Remove only those generated packages before a cached retry.
find "${BUILD_ROOT}" -maxdepth 1 -type d -name 'mpv-*' -exec rm -rf -- {} +
ninja -j "${JOBS}" -C "${BUILD_ROOT}" mpv-fullclean
ninja -j "${JOBS}" -C "${BUILD_ROOT}" mpv

bin_output="${OUTPUT_ROOT}/bin"
license_output="${OUTPUT_ROOT}/licenses"
mkdir -p "${bin_output}" "${license_output}"

readonly native_files=(
    ffmpeg.exe
    ffprobe.exe
    avcodec-63.dll
    avdevice-63.dll
    avfilter-12.dll
    avformat-63.dll
    avutil-61.dll
    swresample-7.dll
    swscale-10.dll
    vulkan-1.dll
)
for name in "${native_files[@]}"; do
    install -m 0755 "${PREFIX}/bin/${name}" "${bin_output}/${name}"
done

libmpv_path="$(find "${BUILD_ROOT}" -maxdepth 2 -type f -path '*/mpv-dev-x86_64-*/libmpv-2.dll' -print -quit)"
if [[ -z "${libmpv_path}" ]]; then
    echo "The libmpv package was not produced." >&2
    exit 4
fi
install -m 0755 "${libmpv_path}" "${bin_output}/libmpv-2.dll"

install -m 0644 "${TOOLCHAIN_ROOT}/src_packages/ffmpeg/COPYING.GPLv3" "${license_output}/FFmpeg-GPL-3.0.txt"
install -m 0644 "${TOOLCHAIN_ROOT}/src_packages/mpv/LICENSE.GPL" "${license_output}/mpv-GPL.txt"
install -m 0644 "${TOOLCHAIN_ROOT}/src_packages/vulkan/LICENSE.txt" "${license_output}/Vulkan-Loader-Apache-2.0.txt"
install -m 0644 /opt/clipedit/source-lock.tsv "${OUTPUT_ROOT}/SOURCE-LOCK.tsv"
pacman -Q > "${OUTPUT_ROOT}/BUILDER-PACKAGES.txt"

objdump_path="${BUILD_ROOT}/install/bin/cross-objdump"
readonly shared_imports=(
    avcodec-63.dll
    avdevice-63.dll
    avfilter-12.dll
    avformat-63.dll
    avutil-61.dll
    swresample-7.dll
    swscale-10.dll
)
for program in libmpv-2.dll ffmpeg.exe ffprobe.exe; do
    imports="$(${objdump_path} -p "${bin_output}/${program}")"
    for library in "${shared_imports[@]}"; do
        if ! grep -Fqi "DLL Name: ${library}" <<< "${imports}"; then
            echo "${program} does not import the shared ${library}." >&2
            exit 5
        fi
    done
done

cat > "${OUTPUT_ROOT}/NATIVE-STACK.txt" <<EOF
mpv_revision=f4d13e1c2c91f3a56e589aef9cb44cbc02e26e47
ffmpeg_revision=bf1b838f2ab88b4f8fd83443325c782ea0e0f7fa
ffmpeg_version=9.0.1
openssl_revision=aae016bfd52fcad2bc9657c2c782cfdf73b1ed5f
mpv_winbuild_cmake_revision=${TOOLCHAIN_REVISION}
shared_libav_imports=${shared_imports[*]}
EOF

(
    cd "${OUTPUT_ROOT}"
    find . -type f ! -name SHA256SUMS -print0 |
        sort -z | xargs -0 sha256sum
) > "${OUTPUT_ROOT}/SHA256SUMS"
echo "ClipEdit Windows shared media stack is ready at ${OUTPUT_ROOT}."
