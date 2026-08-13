#!/usr/bin/env bash
set -euo pipefail

if [[ "$(uname -s)" != "Linux" ]]; then
    echo 'Linux build dependencies can only be installed on Linux.' >&2
    exit 2
fi

if ! command -v apt-get >/dev/null 2>&1; then
    echo 'This bootstrapper currently supports Debian/Ubuntu through apt-get.' >&2
    exit 2
fi

if [[ "$EUID" -eq 0 ]]; then
    sudo_command=()
elif command -v sudo >/dev/null 2>&1; then
    sudo_command=(sudo)
else
    echo 'Run this script as root or install sudo.' >&2
    exit 2
fi

export DEBIAN_FRONTEND=noninteractive
"${sudo_command[@]}" apt-get update
"${sudo_command[@]}" apt-get install -y \
    autoconf \
    automake \
    build-essential \
    ca-certificates \
    dpkg-dev \
    git \
    libasound2-dev \
    libfontconfig1-dev \
    libfreetype6-dev \
    libfribidi-dev \
    libgnutls28-dev \
    libharfbuzz-dev \
    libice6 \
    libopus-dev \
    libpulse-dev \
    libsm6 \
    libvpx-dev \
    libx264-dev \
    libtool \
    nasm \
    ninja-build \
    patchelf \
    pkg-config \
    python3-venv \
    yasm \
    zlib1g-dev \
    zstd
