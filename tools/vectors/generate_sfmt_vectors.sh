#!/usr/bin/env bash
# Regenerates tools/vectors/sfmt_vectors.json from AzerothCore's vendored C implementation.
#
# Needs azerothcore-wotlk/ checked out (it is a reference-only tree, not part of the build) and any
# C compiler. Re-running this must produce a byte-identical file unless the reference itself
# changed — if the diff is non-empty, something moved and the C# port needs re-checking.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo="$(cd "$here/../.." && pwd)"
sfmt="$repo/azerothcore-wotlk/deps/SFMT"

if [[ ! -f "$sfmt/SFMT.c" ]]; then
    echo "error: $sfmt/SFMT.c not found — is azerothcore-wotlk/ checked out?" >&2
    exit 1
fi

tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT

# SFMT_MEXP=19937 matches SFMTRand; the scalar path is what the C# port transcribes.
cc -O2 -std=c99 -DSFMT_MEXP=19937 \
   -I"$sfmt" \
   -o "$tmp/gen" \
   "$here/generate_sfmt_vectors.c" "$sfmt/SFMT.c"

"$tmp/gen" > "$here/sfmt_vectors.json"
echo "wrote $here/sfmt_vectors.json"
