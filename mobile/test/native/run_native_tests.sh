#!/usr/bin/env bash
# Builds and runs the host-side crypto/framing vector test with MSVC.
#
# The test compiles the shipping MobileCrypto.cpp against the bundled mbedTLS
# and compares its output with test/native/crypto_vectors.h, which is produced
# by vectors_gen (the Windows/BCrypt algorithm). Regenerate the fixture with:
#   cd test/native/vectors_gen && dotnet run -c Release
#
# Requires: Visual Studio Build Tools + Windows SDK. Override locations with
# VS_ROOT / SDK_ROOT. Run from anywhere; artifacts go to $TMPDIR.

set -euo pipefail

# MSVC expects /flag arguments verbatim; stop Git Bash from rewriting them as
# file system paths.
export MSYS_NO_PATHCONV=1
export MSYS2_ARG_CONV_EXCL='*'

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
BUILD="${TMPDIR:-/tmp}/osmium_native_vectors"
VS_ROOT="${VS_ROOT:-/d/Software/VisualStudio}"
SDK_ROOT="${SDK_ROOT:-/c/Program Files (x86)/Windows Kits/10}"

if command -v cygpath >/dev/null 2>&1; then
  win() { cygpath -w "$1"; }
else
  win() { printf '%s' "$1"; }
fi

MSVC="$(find "$VS_ROOT/VC/Tools/MSVC" -mindepth 1 -maxdepth 1 -type d | sort -V | tail -1)"
SDKVER="$(find "$SDK_ROOT/Include" -mindepth 1 -maxdepth 1 -type d | sort -V | tail -1)"
if [[ -z "$MSVC" || -z "$SDKVER" ]]; then
  echo "MSVC/Windows SDK not found (set VS_ROOT / SDK_ROOT)" >&2
  exit 1
fi
SDKVER_NAME="$(basename "$SDKVER")"

export PATH="$MSVC/bin/Hostx64/x64:$PATH"
export INCLUDE="$(win "$MSVC/include");$(win "$SDK_ROOT/Include/$SDKVER_NAME/ucrt");$(win "$SDK_ROOT/Include/$SDKVER_NAME/um");$(win "$SDK_ROOT/Include/$SDKVER_NAME/shared");$(win "$SDK_ROOT/Include/$SDKVER_NAME/winrt")"
export LIB="$(win "$MSVC/lib/x64");$(win "$SDK_ROOT/Lib/$SDKVER_NAME/ucrt/x64");$(win "$SDK_ROOT/Lib/$SDKVER_NAME/um/x64")"

mkdir -p "$BUILD"

if [[ ! -f "$BUILD/mbedcrypto.lib" ]]; then
  echo "building mbedTLS..."
  mkdir -p "$BUILD/obj"
  (
    cd "$ROOT/third_party/mbedtls/library"
    cl /nologo /c /O2 /W0 /D_CRT_SECURE_NO_WARNINGS \
      /I"$(win "$ROOT/third_party/mbedtls/include")" \
      /Fo"$(win "$BUILD/obj")\\" *.c >/dev/null
  )
  (cd "$BUILD" && lib /nologo /OUT:mbedcrypto.lib obj/*.obj >/dev/null)
fi

echo "building vector harness..."
(
  cd "$BUILD"
  cl /nologo /EHsc /std:c++17 /O2 /W3 \
    /I"$(win "$ROOT/android/app/src/main/cpp")" \
    /I"$(win "$ROOT/test/native")" \
    /I"$(win "$ROOT/test/native/android_stub")" \
    /I"$(win "$ROOT/../native/osmium-engine/include")" \
    /I"$(win "$ROOT/third_party/mbedtls/include")" \
    "$(win "$ROOT/test/native/crypto_vector_test.cpp")" \
    "$(win "$ROOT/android/app/src/main/cpp/MobileCrypto.cpp")" \
    "$(win "$BUILD/mbedcrypto.lib")" ws2_32.lib bcrypt.lib advapi32.lib \
    /Fe:"$(win "$BUILD/crypto_vector_test.exe")" >/dev/null
)

"$BUILD/crypto_vector_test.exe"
