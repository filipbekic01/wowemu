#!/usr/bin/env python3
"""Guards against drift between the generated crypto vectors and the C# tests.

`generate_crypto_vectors.py` writes vectors.json; the C# tests hard-code those values as
InlineData. Nothing enforces that the two stay in sync -- regenerating the vectors without
updating the tests would leave the tests passing against stale values.

This script checks that every generated value still appears somewhere in the test sources.
Exit code 0 = in sync, 1 = drift.
"""
import glob
import json
import os
import sys

ROOT = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))
VECTORS = os.path.join(ROOT, "tools", "vectors", "vectors.json")
TEST_GLOB = os.path.join(ROOT, "tests", "WowEmu.Tests.Unit", "*.cs")


def collect(vectors):
    """Flatten vectors.json into (label, hex-value) pairs that must appear in the tests."""
    checks = []

    for case in vectors["srp6"]:
        for key in ("salt", "verifier", "b", "B", "A", "clientM", "sessionKey"):
            checks.append((f"srp6[{case['username']}].{key}", case[key]))

    for entry in vectors["interleave"]:
        checks.append((f"interleave[zeros={entry['zeros']}].S", entry["S"]))
        checks.append((f"interleave[zeros={entry['zeros']}].K", entry["K"]))

    checks.append(("authcrypt.serverEncryptKey", vectors["authcrypt"]["serverEncryptKey"]))
    checks.append(("authcrypt.encryptedHeader", vectors["authcrypt"]["encryptedHeader"]))
    checks.append(("rc4_drop1024.ciphertext", vectors["rc4_drop1024"]["ciphertext"]))

    for entry in vectors["rc4"]:
        checks.append((f"rc4[{entry['key']}].keystream", entry["keystream"]))

    # The 64-byte generator output is split across two string literals for line length.
    output = vectors["sessionKeyGenerator"]["output64"]
    checks.append(("sessionKeyGenerator.output64[0:32]", output[:64]))
    checks.append(("sessionKeyGenerator.output64[32:64]", output[64:]))

    return checks


def main():
    if not os.path.exists(VECTORS):
        print(f"error: {VECTORS} not found -- run generate_crypto_vectors.py first")
        return 1

    with open(VECTORS) as handle:
        vectors = json.load(handle)

    sources = glob.glob(TEST_GLOB)
    if not sources:
        print(f"error: no test sources matched {TEST_GLOB}")
        return 1

    haystack = "".join(open(path).read().lower() for path in sources)

    checks = collect(vectors)
    missing = [(label, value) for label, value in checks if value.lower() not in haystack]

    print(f"checked {len(checks)} generated values against {len(sources)} test files")

    if missing:
        print(f"\nDRIFT: {len(missing)} generated value(s) are not present in the tests:\n")
        for label, value in missing:
            print(f"  {label}\n    {value}")
        print("\nEither the vectors were regenerated without updating the tests,")
        print("or a test was edited to expect something the reference does not produce.")
        return 1

    print("in sync: every generated vector appears in the tests")
    return 0


if __name__ == "__main__":
    sys.exit(main())
