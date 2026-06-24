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
seeds that admin into every **ruled** instance once. Add it to `deenv.service`:

```ini
Environment=DEENV_ADMIN_PASSWORD=a-strong-password
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
the no-auth apps (todo/crm/…) are open, so keep the subdomain set behind a gate for now. Generate an
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
```

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

## Backup

```bash
tar czf ~/deenv-data-$(date +%F).tgz -C /var/lib/deenv .   # cron this
```
