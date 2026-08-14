#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 3 ]]; then
    echo "Usage: $0 LINUX_PAYLOAD PAYLOAD_ORIGIN_MAP OUTPUT_DIRECTORY" >&2
    exit 2
fi

readonly payload_root="$(realpath -- "$1")"
readonly origin_map="$(realpath -- "$2")"
readonly output_root="$(realpath -m -- "$3")"

if [[ ! -x "${payload_root}/tools/ffmpeg/ffmpeg" ||
      ! -x "${payload_root}/tools/ffmpeg/ffprobe" ||
      ! -f "${payload_root}/libmpv.so.2" ]]; then
    echo "The Linux payload is incomplete: ${payload_root}" >&2
    exit 2
fi

mkdir -p -- "$output_root/debian-copyright"
provenance_path="${output_root}/linux-debian-binary-provenance.tsv"
printf 'payloadPath\tsha256\toriginPath\tbinaryPackage\tbinaryVersion\tsourcePackage\tsourceVersion\tcopyrightFile\n' \
    > "$provenance_path"

declare -A copied_copyrights=()
declare -A origin_paths=()
while IFS=$'\t' read -r mapped_payload_path mapped_origin_path; do
    [[ "$mapped_payload_path" == 'payloadPath' ]] && continue
    [[ -n "$mapped_payload_path" && -n "$mapped_origin_path" ]] || continue
    origin_paths[$mapped_payload_path]="$mapped_origin_path"
done < "$origin_map"

while IFS= read -r -d '' payload_path; do
    relative_path="${payload_path#${payload_root}/}"
    case "$relative_path" in
        tools/ffmpeg/ffmpeg|tools/ffmpeg/ffprobe|libmpv.so.2)
            continue
            ;;
    esac

    payload_name="$(basename -- "$payload_path")"
    payload_hash="$(sha256sum "$payload_path" | awk '{ print $1 }')"
    matched_path="${origin_paths[$relative_path]:-}"
    if [[ -z "$matched_path" || ! -f "$matched_path" ]]; then
        echo "The build-time origin map has no installed library for ${relative_path}." >&2
        exit 3
    fi

    owner_line="$(dpkg-query -S "$matched_path" 2>/dev/null | head -n 1 || true)"
    if [[ -z "$owner_line" ]]; then
        resolved_path="$(readlink -f -- "$matched_path")"
        owner_line="$(dpkg-query -S "$resolved_path" 2>/dev/null | head -n 1 || true)"
    fi
    if [[ -z "$owner_line" ]]; then
        echo "No Debian package owns the matching library ${matched_path}." >&2
        exit 3
    fi

    binary_package="${owner_line%%: /*}"
    if [[ "$binary_package" == "$owner_line" ]]; then
        binary_package="${owner_line%%:*}"
    fi
    package_fields="$(dpkg-query -W \
        -f='${binary:Package}\t${Version}\t${source:Package}\t${source:Version}\n' \
        "$binary_package")"
    IFS=$'\t' read -r binary_package binary_version source_package source_version \
        <<< "$package_fields"
    [[ -n "$source_package" ]] || source_package="${binary_package%%:*}"
    [[ -n "$source_version" ]] || source_version="$binary_version"

    copyright_source="/usr/share/doc/${binary_package%%:*}/copyright"
    if [[ ! -f "$copyright_source" ]]; then
        echo "Debian copyright metadata is missing for ${binary_package}: ${copyright_source}" >&2
        exit 4
    fi
    copyright_name="${source_package}_${source_version//[:\/+~]/_}.copyright"
    copyright_relative="debian-copyright/${copyright_name}"
    if [[ -z "${copied_copyrights[$copyright_name]:-}" ]]; then
        cp -L -- "$copyright_source" "${output_root}/${copyright_relative}"
        copied_copyrights[$copyright_name]=1
    fi

    printf '%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\n' \
        "$relative_path" "$payload_hash" "$matched_path" "$binary_package" "$binary_version" \
        "$source_package" "$source_version" "$copyright_relative" >> "$provenance_path"
done < <(
    find "$payload_root" -maxdepth 1 -type f -name 'lib*.so*' -print0
    find "$payload_root/native" -maxdepth 1 -type f -name 'lib*.so*' -print0
)

sort -t $'\t' -k1,1 -o "$provenance_path" "$provenance_path"
# Restore the header after sorting; it sorts after some lowercase payload paths.
sed -i '/^payloadPath\tsha256\t/d' "$provenance_path"
sed -i '1i payloadPath\tsha256\toriginPath\tbinaryPackage\tbinaryVersion\tsourcePackage\tsourceVersion\tcopyrightFile' \
    "$provenance_path"

record_count="$(($(wc -l < "$provenance_path") - 1))"
if (( record_count == 0 )); then
    echo 'No distributable Linux libraries were inventoried.' >&2
    exit 4
fi

(
    cd -- "$output_root"
    find . -type f ! -name SHA256SUMS -print0 | sort -z | xargs -0 sha256sum > SHA256SUMS
)
echo "Inventoried ${record_count} Linux payload libraries at ${output_root}."
