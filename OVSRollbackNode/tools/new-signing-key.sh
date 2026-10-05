#!/usr/bin/env bash
# new-signing-key.sh DIR: a key pair for the node lockdown (ECDSA P-256).
#
#   DIR/node-config-signing-key.pem   the private key (PKCS#8 PEM). The servers sign with it: P2P_NODE_SIGNING_KEY in
#                                     their environment, the PEM text itself. Keep it off every machine but the servers.
#   DIR/node-config-public-key.txt    the public key (base64 SubjectPublicKeyInfo, one line). Nodes are built with it:
#                                     build.sh reads pki/<env>/node-config-public-key.txt (committed: it is public),
#                                     with pki/<env>/server-url.txt beside it, for each env in NODE_PKI ("prod
#                                     testing" by default); the bench passes local/pki/bench's with -p:NodeTrust.
#
# A node built with one pair's public key trusts only configs signed with that pair's private key.
set -euo pipefail
dir=${1:?usage: new-signing-key.sh DIR}
mkdir -p "$dir"
[ -e "$dir/node-config-signing-key.pem" ] && { echo "$dir/node-config-signing-key.pem exists; not overwriting it" >&2; exit 1; }
umask 077
openssl ecparam -name prime256v1 -genkey -noout | openssl pkcs8 -topk8 -nocrypt -out "$dir/node-config-signing-key.pem"
openssl pkey -in "$dir/node-config-signing-key.pem" -pubout -outform DER | base64 -w0 > "$dir/node-config-public-key.txt"
echo >> "$dir/node-config-public-key.txt"
echo "private: $dir/node-config-signing-key.pem"
echo "public:  $dir/node-config-public-key.txt ($(cat "$dir/node-config-public-key.txt"))"
