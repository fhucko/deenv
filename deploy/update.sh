#!/usr/bin/env bash
# One-command UPDATE deploy for DeEnv. Assumes first-time setup (systemd unit, nginx, TLS,
# the /var/lib/deenv data home) is already done per deploy/DEPLOY.md. Builds a self-contained
# linux-x64 bundle, ships it, and hot-swaps the binaries; data in /var/lib/deenv is preserved.
#
# Connection (box IP + key) is intentionally NOT in this repo. Define a `deenv` host in
# ~/.ssh/config (HostName + User root + IdentityFile ~/.ssh/deenv_deploy), or override:
#     DEENV_HOST=root@1.2.3.4 ./deploy/update.sh
set -euo pipefail

HOST="${DEENV_HOST:-deenv}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"

echo "==> Building self-contained linux-x64 bundle"
dotnet publish DeEnv/DeEnv.csproj -c Release -r linux-x64 --self-contained true -o artifacts/linux
tar -C artifacts/linux -czf deenv-linux.tar.gz .

echo "==> Shipping to $HOST"
scp deenv-linux.tar.gz "$HOST:/tmp/"

echo "==> Swapping binaries on $HOST (data in /var/lib/deenv is preserved)"
ssh "$HOST" 'bash -s' <<'REMOTE'
set -eu
systemctl stop deenv
rm -rf /opt/deenv.bak && mv /opt/deenv /opt/deenv.bak
mkdir /opt/deenv && tar -C /opt/deenv -xzf /tmp/deenv-linux.tar.gz
chmod +x /opt/deenv/DeEnv
rm -f /opt/deenv/kernel.json && rm -rf /opt/deenv/instances
systemctl start deenv
set +e
for i in $(seq 1 15); do [ "$(systemctl is-active deenv)" = active ] && break; sleep 1; done
echo "service: $(systemctl is-active deenv)"
for i in $(seq 1 15); do c=$(curl -s -o /dev/null -w '%{http_code}' http://127.0.0.1:8080/apps/todo/); [ "$c" = 200 ] && break; sleep 1; done
echo "loopback /apps/todo/ -> ${c:-no-response}"
journalctl -u deenv -n 8 --no-pager
REMOTE

rm -f deenv-linux.tar.gz
echo "==> Done. Rollback if needed:"
echo "    ssh $HOST 'systemctl stop deenv && rm -rf /opt/deenv && mv /opt/deenv.bak /opt/deenv && systemctl start deenv'"
