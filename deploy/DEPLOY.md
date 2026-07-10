# Deploying DeEnv (self-contained bundle + systemd + nginx)

Runs the DeEnv kernel host directly on a box (no Docker) as a systemd service behind nginx:
starts on boot, restarts on crash, data persists on disk, reachable over HTTPS. Proven on a
**1 GB Linode (Debian 13)**.

The kernel hosts every instance in `kernel.json` behind **two shared, loopback-bound ports** — an
app port and an asset port — addressing each instance by **path** (`/apps/<name>`). nginx terminates
TLS and maps a **wildcard subdomain** to each instance, so `<name>.deenv.org` serves the instance
named `<name>` and a newly created instance is reachable immediately.

Layout:
- **Binaries** → `/opt/deenv` (replaceable on update; a self-contained build, no .NET on the box)
- **Data home** → `/var/lib/deenv` (`kernel.json` + `instances/<id>/`; survives updates) — the
  service points the kernel here via `DEENV_HOME`.

---

## 1. Build the bundle (on your dev machine)

A self-contained linux-x64 build bundles the runtime, so the box needs no .NET installed:

```bash
dotnet publish DeEnv/DeEnv.csproj -c Release -r linux-x64 --self-contained true -o artifacts/linux
tar -C artifacts/linux -czf deenv-linux.tar.gz .
```

(The box needs `libicu` for globalization — preinstalled on a stock Debian 13.)

## 2. First-time setup (on the box)

```bash
# Ship + unpack the binaries (Windows tar drops the +x bit, so restore it).
scp deenv-linux.tar.gz root@BOX:/tmp/
ssh root@BOX
mkdir -p /opt/deenv && tar -C /opt/deenv -xzf /tmp/deenv-linux.tar.gz
chmod +x /opt/deenv/DeEnv

# /opt holds binaries only — move the committed registry + app docs to the data home.
mkdir -p /var/lib/deenv
mv /opt/deenv/kernel.json /opt/deenv/instances /var/lib/deenv/

# Unprivileged service user owns the data.
useradd -r -s /usr/sbin/nologin deenv
chown -R deenv:deenv /var/lib/deenv

# Install + start the service (the unit sets DEENV_HOME + the reverse-proxy env vars).
cp /path/to/deploy/deenv.service /etc/systemd/system/deenv.service
systemctl daemon-reload
systemctl enable --now deenv
```

Verify it runs (loopback only — nothing public yet):

```bash
systemctl status deenv
journalctl -u deenv -n 20            # "Kernel listening on 127.0.0.1 ... assets same-origin"
curl -s -o /dev/null -w '%{http_code}\n' http://127.0.0.1:8080/apps/todo/   # 200
```

### Bootstrap the app admin (ruled apps)

An app with access rules — e.g. `devlog` (public read, admin-only write) — is deny-by-default: it needs
a seeded admin before anyone can log in. Set `DEENV_ADMIN_PASSWORD` (and optionally `DEENV_ADMIN_USER`,
default `admin`, and `DEENV_ADMIN_ROLE`, default `Admin`) in the service environment; on boot the kernel
seeds that admin into every **ruled** instance once. Deployed pattern (2026-07-02): a root-only env file
+ a systemd drop-in, so the secret never sits in the world-readable unit; the plaintext is also kept in
`/root/deenv-admin-credentials.txt` (600) for the operator:

```bash
PW=$(tr -dc 'A-Za-z0-9' </dev/urandom | head -c 20)
printf 'DEENV_ADMIN_PASSWORD=%s\n' "$PW" > /etc/deenv-admin.env && chmod 600 /etc/deenv-admin.env
printf 'devlog admin\nusername: admin\npassword: %s\n' "$PW" > /root/deenv-admin-credentials.txt && chmod 600 /root/deenv-admin-credentials.txt
unset PW
mkdir -p /etc/systemd/system/deenv.service.d
printf '[Service]\nEnvironmentFile=/etc/deenv-admin.env\n' > /etc/systemd/system/deenv.service.d/admin.conf
```

Then `systemctl daemon-reload && systemctl restart deenv`. Notes:
- **Idempotent** — a later restart never duplicates the admin, and (today) never *rotates* the password:
  changing the env var after the first seed has no effect. Rotate via the in-app setPassword path (later).
- An **unset** password leaves ruled apps read-only (no admin to log in as); no-auth apps are unaffected.

### Trim what runs (optional, low RAM)

Each hosted instance costs RAM (all five together ~27 MB). Edit `/var/lib/deenv/kernel.json` to keep
only what you need, then `systemctl restart deenv`.

---

## 3. nginx + HTTPS (wildcard, behind the gate)

The service binds `127.0.0.1:8080` (app) + `127.0.0.1:8081` (assets) — see the env vars in
`deenv.service`:
- `DEENV_BIND=loopback` keeps the raw ports off the public interface.
- `DEENV_PUBLIC_ASSET_PORT=0` makes the page advertise an **empty asset authority**, so the browser
  loads `/js` and opens the WebSocket on the **same origin**; nginx routes those two paths to the
  asset port. One TLS cert and one auth gate then cover the app *and* the WebSocket.

### Wildcard cert (Let's Encrypt via Cloudflare DNS-01)

DNS-01 needs the zone's nameservers on Cloudflare + an API token (acme.sh remembers it after the
first run). Install the cert to a stable path with a reload hook so renewals reload nginx:

```bash
acme.sh --issue --dns dns_cf -d deenv.org -d '*.deenv.org' --keylength ec-256 --server letsencrypt
acme.sh --install-cert -d deenv.org --ecc \
  --key-file       /etc/nginx/ssl/deenv.org.key \
  --fullchain-file /etc/nginx/ssl/deenv.org.cer \
  --reloadcmd      "systemctl reload nginx"
```

### Basic-auth gate (interim — the designer + no-auth apps have no login)

Ruled apps (e.g. `devlog`) now have their own login, but the designer can create/delete instances and
the no-auth apps (todo/crm/…) are open, so keep the subdomain set behind a gate for now.

**Per-subdomain opt-out (deployed 2026-07-02 — devlog is public):** a `map` turns the realm into a
variable; `off` disables the gate for that subdomain only. Add near the other maps (top-level of the
conf file, NOT inside an existing multi-line `map` block) and use the variable in `auth_basic`:

```nginx
map $deenv_sub $deenv_realm { devlog off; default "DeEnv"; }
# in the server block:
auth_basic           $deenv_realm;
``` Generate an
htpasswd (keep the plaintext only in a root-only file):

```bash
PW=$(tr -dc 'A-Za-z0-9' </dev/urandom | head -c 16)
printf 'deenv:%s\n' "$(openssl passwd -apr1 "$PW")" > /etc/nginx/deenv.htpasswd
printf 'username: deenv\npassword: %s\n' "$PW" > /root/deenv-credentials.txt
chmod 600 /root/deenv-credentials.txt; unset PW
```

### Server block (`/etc/nginx/conf.d/deenv.conf`)

```nginx
map $http_upgrade $connection_upgrade { default upgrade; '' close; }
map $deenv_sub    $deenv_app          { admin designer; default $deenv_sub; }  # 'admin' -> the designer

server { listen 80; listen [::]:80; server_name *.deenv.org deenv.org; return 301 https://$host$request_uri; }

server {
    listen 443 ssl; listen [::]:443 ssl; http2 on;
    server_name ~^(?<deenv_sub>[^.]+)\.deenv\.org$;
    ssl_certificate /etc/nginx/ssl/deenv.org.cer; ssl_certificate_key /etc/nginx/ssl/deenv.org.key;
    ssl_protocols TLSv1.2 TLSv1.3;

    auth_basic "DeEnv"; auth_basic_user_file /etc/nginx/deenv.htpasswd;

    # WebSocket + bundle: same origin, routed to the asset port (8081).
    location = /ws {
        proxy_pass http://127.0.0.1:8081/apps/$deenv_app/ws;
        proxy_http_version 1.1;
        proxy_set_header Upgrade $http_upgrade; proxy_set_header Connection $connection_upgrade;
        proxy_set_header Host $host; proxy_read_timeout 3600s; proxy_send_timeout 3600s;
    }
    location = /js { proxy_pass http://127.0.0.1:8081/apps/$deenv_app/js; proxy_set_header Host $host; }

    # Blob upload (assets slice 4): POST /assets — and ONLY the POST method — goes to the asset
    # host's upload edge; the session cookie rides because this is the app's own origin. Any other
    # method on /assets falls through to the app (GET /assets stays ordinary page URL space — the
    # framework reserves the METHOD+path combination, not the path). 12m matches the kernel's 10 MB
    # streaming cap with headroom.
    location = /assets {
        client_max_body_size 12m;
        if ($request_method = POST) {
            rewrite ^ /apps/$deenv_app/assets break;
            proxy_pass http://127.0.0.1:8081;
        }
        proxy_pass http://127.0.0.1:8080/apps/$deenv_app$request_uri;
        proxy_set_header Host $host;
        proxy_set_header X-Forwarded-Prefix /;
        proxy_set_header X-Forwarded-Proto $scheme;
        proxy_http_version 1.1;
    }

    # App SSR: root-mounted at the subdomain. NOTE: X-Forwarded-Prefix is "/", not empty —
    # GenHTTP throws on an EMPTY value; "/" yields the same root base.
    location / {
        proxy_pass http://127.0.0.1:8080/apps/$deenv_app$request_uri;
        proxy_set_header Host $host;
        proxy_set_header X-Forwarded-Prefix /;
        proxy_set_header X-Forwarded-Proto $scheme;
        proxy_http_version 1.1;
    }
}

# Blob serve (assets slice 4): the dedicated blob domain. An EXACT server_name wins over the
# wildcard regex above, so "assets" is never treated as an app subdomain. Deliberately NO
# auth_basic — public apps embed these images, and the capability hash (a strict-shape 64-hex
# content name, validated kernel-side) is the sole serve boundary by design
# (docs/plans/assets-design.md §3). The instance rides the PATH: /<name>/<hash>.<ext>.
server {
    listen 443 ssl; listen [::]:443 ssl; http2 on;
    server_name assets.deenv.org;
    ssl_certificate /etc/nginx/ssl/deenv.org.cer; ssl_certificate_key /etc/nginx/ssl/deenv.org.key;
    ssl_protocols TLSv1.2 TLSv1.3;

    location ~ ^/(?<aname>[a-z0-9-]+)/(?<bname>[0-9a-f]{64}\.[a-z0-9]+)$ {
        proxy_pass http://127.0.0.1:8081/apps/$aname/assets/$bname;
        proxy_set_header Host $host;
    }
    location / { return 404; }
}
```

The kernel side of the split is one env var in `deenv.service`
(`DEENV_PUBLIC_BLOB_BASE=https://assets.deenv.org`): image URLs (`sys.assetUrl`) then resolve on
the blob domain (origin-isolated user content), while uploads stay a same-origin `POST /assets`
on the app subdomain (where the session cookie rides; routed above by method). The wildcard cert
already covers `assets.deenv.org`; no new cert. NOTE the serve URL is cross-origin from the app
page, so blob GETs bypass the htpasswd gate by design — the capability hash is the boundary.

Apply + firewall (only ssh/http/https are public; the app ports stay loopback):

```bash
nginx -t && systemctl reload nginx
ufw allow 22/tcp && ufw allow 80/tcp && ufw allow 443/tcp && ufw --force enable
```

---

## Updating after a code change

```bash
# dev machine: rebuild + ship (see step 1), scp deenv-linux.tar.gz to the box, then:
systemctl stop deenv
rm -rf /opt/deenv.bak && mv /opt/deenv /opt/deenv.bak      # keep one rollback copy
mkdir /opt/deenv && tar -C /opt/deenv -xzf /tmp/deenv-linux.tar.gz
chmod +x /opt/deenv/DeEnv
rm -f /opt/deenv/kernel.json && rm -rf /opt/deenv/instances  # data stays in /var/lib/deenv
systemctl start deenv
```

**GOTCHA — framework-owned app docs version WITH the binary (hit 2026-07-02).** "Data stays" is true
for `app-data.json`, but the **designer's** `app.deenv` (and the registry `kernel.json`) are part of
the framework: a new binary whose designer schema evolved against a stale designer doc fails that
instance's boot (`SyncDesignHost` → seed abort). Since the per-instance boot isolation added after
the 2026-07-02 outage this no longer takes the kernel down — the bad instance is skipped loudly (full
error in `journalctl -u deenv`, its mount answers 503) while the other apps serve — but the skipped
instance stays down until fixed. When the designer schema (or the doc format —
e.g. the 2026-06-28 `*.app` → `*.deenv` rename) has moved since the last deploy, also ship the current
`kernel.json` + `instances/*/app.deenv` into `/var/lib/deenv`. On a box with no precious data, the
simple path is a clean reseed (`rm /var/lib/deenv/instances/*/app-data.json`); with real data, the
app-doc update must go through the non-destructive apply path instead (publish), not a raw file swap.

## Backup

```bash
tar czf ~/deenv-data-$(date +%F).tgz -C /var/lib/deenv .   # cron this
```
