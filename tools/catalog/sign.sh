#!/usr/bin/env bash
# Signs the catalog: ECDSA P-256 over SHA-256 of the exact file bytes, written as base64 of the 64-byte
# IEEE P1363 form (r || s), which is what CatalogVerifier checks. Writes <file>.sig beside the file.
#
# usage: tools/catalog/sign.sh [catalog-file] [private-key-path]
#        defaults: catalog/catalog.json and ~/.config/wicked-signing/catalog-p256.pem
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd -P)"
FILE="${1:-$ROOT/catalog/catalog.json}"
KEY="${2:-$HOME/.config/wicked-signing/catalog-p256.pem}"

[[ -f "$FILE" ]] || { echo "no such file: $FILE" >&2; exit 1; }
[[ -f "$KEY" ]] || { echo "no signing key at $KEY (run tools/catalog/new-key.sh)" >&2; exit 1; }

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

# OpenSSL writes the signature as DER: SEQUENCE { INTEGER r, INTEGER s }. Take the two integers out.
openssl dgst -sha256 -sign "$KEY" -out "$work/sig.der" "$FILE"
mapfile -t ints < <(openssl asn1parse -inform DER -in "$work/sig.der" | awk -F: '/INTEGER/ { print $NF }')
[[ ${#ints[@]} -eq 2 ]] || { echo "unexpected signature structure" >&2; exit 1; }

# Each integer may carry a leading 00 (sign byte) or be shorter than 32 bytes: normalise to exactly 64 hex digits.
fixed=""
for n in "${ints[@]}"; do
  n="${n,,}"
  while [[ ${#n} -gt 64 && "${n:0:2}" == "00" ]]; do n="${n:2}"; done
  while [[ ${#n} -lt 64 ]]; do n="0$n"; done
  fixed+="$n"
done

: > "$work/sig.raw"
for ((i = 0; i < ${#fixed}; i += 2)); do
  printf "\\x${fixed:i:2}" >> "$work/sig.raw"
done
[[ "$(wc -c < "$work/sig.raw")" -eq 64 ]] || { echo "signature is not 64 bytes" >&2; exit 1; }

openssl base64 -A -in "$work/sig.raw" > "$FILE.sig"
echo >> "$FILE.sig"

# Check with OpenSSL against the matching public key before leaving the file behind.
openssl ec -in "$KEY" -pubout -out "$work/pub.pem" 2>/dev/null
openssl dgst -sha256 -verify "$work/pub.pem" -signature "$work/sig.der" "$FILE" >/dev/null
echo "signed $FILE -> $FILE.sig"
