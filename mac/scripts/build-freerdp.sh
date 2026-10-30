#!/bin/bash
# Buduje statyczne FreeRDP 3 (+ OpenSSL) dla wbudowanego klienta RDP Waypointa.
#
#   mac/scripts/build-freerdp.sh <katalog-wyjściowy>
#
# macOS: osobno arm64 i x86_64 (OpenSSL i FreeRDP mają kod zależny od architektury), potem lipo do
# bibliotek uniwersalnych. Linux (sprawdzenie skryptu / testy): jedna architektura gospodarza.
# Wynik: <wyjście>/include/{freerdp3,winpr3,openssl}, <wyjście>/lib/*.a — do WAYPOINT_FREERDP
# (Package.swift). Minimalny zestaw: biblioteki klienta i kanały, bez serwera,
# ffmpeg, dźwięku systemowego, kart inteligentnych i USB.
set -euo pipefail

OUT="$(mkdir -p "$1" && cd "$1" && pwd)"
FREERDP_VERSION="${FREERDP_VERSION:-3.32.1}"
OPENSSL_VERSION="${OPENSSL_VERSION:-3.5.0}"
WORK="${WORK:-$(mktemp -d)}"
GEN="$(command -v ninja >/dev/null && echo Ninja || echo "Unix Makefiles")"
JOBS="$(getconf _NPROCESSORS_ONLN 2>/dev/null || echo 4)"
mkdir -p "$WORK"
cd "$WORK"

[ -d "FreeRDP-$FREERDP_VERSION" ] || git clone -q --depth 1 --branch "$FREERDP_VERSION" \
    https://github.com/FreeRDP/FreeRDP.git "FreeRDP-$FREERDP_VERSION"
[ -d "openssl-$OPENSSL_VERSION" ] || curl -fsSL \
    "https://github.com/openssl/openssl/releases/download/openssl-$OPENSSL_VERSION/openssl-$OPENSSL_VERSION.tar.gz" | tar xz

if [ "$(uname)" = "Darwin" ]; then ARCHS="${ARCHS:-arm64 x86_64}"; else ARCHS="$(uname -m)"; fi

build_arch() {
    local arch="$1" prefix="$WORK/prefix-$1"
    local cc_flags="" ossl_target cmake_arch=()
    if [ "$(uname)" = "Darwin" ]; then
        export MACOSX_DEPLOYMENT_TARGET=14.0
        cc_flags="-arch $arch -mmacosx-version-min=14.0"
        ossl_target="darwin64-$arch-cc"
        cmake_arch=(-DCMAKE_OSX_ARCHITECTURES="$arch" -DCMAKE_OSX_DEPLOYMENT_TARGET=14.0)
    else
        ossl_target="linux-$arch"
    fi

    if [ ! -f "$prefix/lib/libssl.a" ]; then
        rm -rf "ossl-$arch" && cp -R "openssl-$OPENSSL_VERSION" "ossl-$arch"
        (cd "ossl-$arch" && ./Configure "$ossl_target" no-shared no-tests no-apps no-docs \
            --prefix="$prefix" --libdir=lib CFLAGS="$cc_flags -O2" >/dev/null 2>&1 \
            && make -j"$JOBS" >/dev/null 2>&1 && make install_sw >/dev/null 2>&1)
    fi

    cmake -S "FreeRDP-$FREERDP_VERSION" -B "build-$arch" -G "$GEN" \
        -DCMAKE_BUILD_TYPE=Release -DCMAKE_INSTALL_PREFIX="$prefix" -DCMAKE_INSTALL_LIBDIR=lib \
        "${cmake_arch[@]}" -DCMAKE_C_FLAGS="$cc_flags" -DCMAKE_POSITION_INDEPENDENT_CODE=ON \
        -DBUILD_SHARED_LIBS=OFF -DBUILD_TESTING=OFF \
        -DOPENSSL_ROOT_DIR="$prefix" -DOPENSSL_USE_STATIC_LIBS=ON \
        -DWITH_CLIENT_COMMON=ON -DWITH_CLIENT=OFF -DWITH_CLIENT_CHANNELS=ON -DWITH_CHANNELS=ON \
        -DWITH_SERVER=OFF -DWITH_SAMPLE=OFF -DWITH_PROXY=OFF -DWITH_SHADOW=OFF -DWITH_PLATFORM_SERVER=OFF \
        -DWITH_MANPAGES=OFF -DWITH_WINPR_TOOLS=OFF -DWITH_FFMPEG=OFF -DWITH_SWSCALE=OFF -DWITH_CAIRO=OFF \
        -DWITH_FUSE=OFF -DWITH_JSON_DISABLED=ON -DWITH_AAD=OFF -DWITH_KRB5=OFF -DWITH_PKCS11=OFF \
        -DWITH_PCSC=OFF -DWITH_SMARTCARD_EMULATE=OFF -DWITH_CUPS=OFF -DWITH_OPUS=OFF -DWITH_FAAD2=OFF \
        -DWITH_URIPARSER=OFF -DWITH_SSO_MIB=OFF -DWITH_WEBVIEW=OFF -DWITH_LIBSYSTEMD=OFF -DWITH_PULSE=OFF \
        -DWITH_ALSA=OFF -DWITH_OSS=OFF -DWITH_SOXR=OFF -DWITH_LAME=OFF -DWITH_GSM=OFF -DWITH_DSP_FFMPEG=OFF \
        -DWITH_UNICODE_BUILTIN=ON -DWITH_TIMEZONE_ICU=OFF \
        -DCHANNEL_URBDRC=OFF -DCHANNEL_RDPECAM=OFF -DCHANNEL_TSMF=OFF -DCHANNEL_AUDIN=OFF \
        -DCHANNEL_SMARTCARD=OFF -DCHANNEL_PRINTER=OFF -DCHANNEL_SERIAL=OFF -DCHANNEL_PARALLEL=OFF \
        -DWITH_INTERNAL_RC4=ON -DWITH_INTERNAL_MD4=ON -DWITH_INTERNAL_MD5=ON \
        >"$WORK/cmake-$arch.log" 2>&1 || { tail -40 "$WORK/cmake-$arch.log"; exit 1; }
    cmake --build "build-$arch" -j"$JOBS" >"$WORK/build-$arch.log" 2>&1 || { grep -m20 -E "error" "$WORK/build-$arch.log"; tail -20 "$WORK/build-$arch.log"; exit 1; }
    cmake --install "build-$arch" >/dev/null
}

for a in $ARCHS; do build_arch "$a"; done

rm -rf "$OUT/include" "$OUT/lib"
mkdir -p "$OUT/lib"
first="$(echo $ARCHS | awk '{print $1}')"
cp -R "$WORK/prefix-$first/include" "$OUT/include"
for lib in "$WORK/prefix-$first"/lib/*.a; do
    name="$(basename "$lib")"
    if [ "$(echo $ARCHS | wc -w)" -gt 1 ]; then
        lipo -create $(for a in $ARCHS; do echo "$WORK/prefix-$a/lib/$name"; done) -output "$OUT/lib/$name"
    else
        cp "$lib" "$OUT/lib/$name"
    fi
done
echo "FreeRDP $FREERDP_VERSION + OpenSSL $OPENSSL_VERSION → $OUT"
ls "$OUT/lib"
