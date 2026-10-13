#!/bin/sh
# Builds uno-wl-inject into $1 (default: ./out). Needs wayland-scanner, libwayland-client headers and a C compiler.
# Run the result with --socket <private-socket> (or UNO_WL_INJECT_SOCKET).
# wayland-0/wayland-1 require deliberate --allow-session opt-in.
set -eu
here=$(cd "$(dirname "$0")" && pwd)
out=${1:-$here/out}
mkdir -p "$out"
wayland-scanner client-header "$here/fake-input.xml" "$out/fake-input-client-protocol.h"
wayland-scanner private-code "$here/fake-input.xml" "$out/fake-input-protocol.c"
${CC:-cc} -std=c99 -O2 -Wall -Wextra -Werror -I"$out" -o "$out/uno-wl-inject" "$here/uno-wl-inject.c" "$out/fake-input-protocol.c" $(pkg-config --cflags --libs wayland-client)
echo "$out/uno-wl-inject"
