#!/usr/bin/env bash
# Makes the catalog signing key pair (ECDSA P-256) and prints the public key to paste into
# src/Wicked.Core/Catalog/CatalogVerifier.cs (PublicKey). The private key stays OUTSIDE the repository, mode 600.
#
# usage: tools/catalog/new-key.sh [private-key-path]
#        default path: ~/.config/wicked-signing/catalog-p256.pem
set -euo pipefail

KEY="${1:-$HOME/.config/wicked-signing/catalog-p256.pem}"

if [[ -e "$KEY" ]]; then
  echo "refusing to overwrite $KEY (delete it yourself if you really mean to replace the signing key)" >&2
  exit 1
fi

case "$(cd "$(dirname "$KEY")" 2>/dev/null && pwd -P || true)" in
  "$(cd "$(dirname "$0")/../.." && pwd -P)"*)
    echo "the private key must not live inside the repository" >&2
    exit 1
    ;;
esac

mkdir -p "$(dirname "$KEY")"
chmod 700 "$(dirname "$KEY")"
umask 077
openssl ecparam -name prime256v1 -genkey -noout -out "$KEY"
chmod 600 "$KEY"

echo "private key: $KEY (mode 600, never commit it)"
echo "public key (base64 DER SubjectPublicKeyInfo) for CatalogVerifier.PublicKey:"
openssl ec -in "$KEY" -pubout -outform DER 2>/dev/null | openssl base64 -A
echo
