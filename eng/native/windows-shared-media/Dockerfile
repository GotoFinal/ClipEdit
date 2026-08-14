FROM ghcr.io/archlinux/archlinux:base-devel@sha256:e2b7ba4b2c6efc63059c20cc506eb2f583c63af812cb1ab13f75e26a2165c390

RUN printf 'Server = https://archive.archlinux.org/repos/2026/08/12/$repo/os/$arch\n' > /etc/pacman.d/mirrorlist \
    && printf '\n[multilib]\nInclude = /etc/pacman.d/mirrorlist\n' >> /etc/pacman.conf \
    && pacman-key --init \
    && pacman-key --populate archlinux \
    && pacman -Syu --noconfirm \
    && pacman -S --noconfirm --needed \
        git ninja cmake meson clang lld libc++ unzip ragel yasm nasm gperf \
        rst2pdf lib32-gcc-libs lib32-glib2 python-cairo curl wget mimalloc \
        ccache python-pip 7zip shaderc \
    && python -m pip install --break-system-packages 'roman==5.2' \
    && pacman -Scc --noconfirm

COPY native/windows-shared-media/build.sh /opt/clipedit/build.sh
COPY native/windows-shared-media/mpv-winbuild-cmake.patch /opt/clipedit/mpv-winbuild-cmake.patch
COPY native/windows-shared-media/source-lock.tsv /opt/clipedit/source-lock.tsv
COPY native/native-dependencies.json /opt/clipedit/native-dependencies.json

RUN chmod 0755 /opt/clipedit/build.sh

ENTRYPOINT ["/opt/clipedit/build.sh"]
