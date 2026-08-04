#!/usr/bin/env bash
# Regenerates tools/vectors/random_vectors.json.
#
# Runs inside a gcc container because the vectors describe *libstdc++'s* distribution algorithms.
# Building this with libc++ (what clang uses on macOS) would compile and run happily while
# producing a different, wrong answer — the #error in the .cpp guards against that, but the
# container is what makes the right toolchain available in the first place.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo="$(cd "$here/../.." && pwd)"
sfmt="$repo/azerothcore-wotlk/deps/SFMT"

if [[ ! -f "$sfmt/SFMT.c" ]]; then
    echo "error: $sfmt/SFMT.c not found — is azerothcore-wotlk/ checked out?" >&2
    exit 1
fi

if ! docker info >/dev/null 2>&1; then
    echo "error: Docker is not running; it provides the libstdc++ toolchain." >&2
    exit 1
fi

docker run --rm \
    -v "$here:/vectors:ro" \
    -v "$sfmt:/sfmt:ro" \
    gcc:14 \
    bash -c 'g++ -O2 -std=c++17 -DSFMT_MEXP=19937 -I/sfmt -o /tmp/gen /vectors/generate_random_vectors.cpp /sfmt/SFMT.c && /tmp/gen' \
    > "$here/random_vectors.json"

echo "wrote $here/random_vectors.json"
