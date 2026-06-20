# Deploying DeEnv (direct hosting: .NET + systemd + nginx)

Runs the DeEnv kernel host directly on the box (no Docker) as a systemd service: starts on boot,
restarts on crash, data persists on disk. Written for a **1 GB Linode nanode (Debian 11)** with
**.NET and nginx already installed**.

The kernel hosts every instance in `kernel.json`, each on its own app + infra port pair.

Layout used below:
- **Binaries** → `/opt/deenv` (replaceable on update)
- **Data home** → `/var/lib/deenv` (`kernel.json` + `instances/<id>/`; survives updates) — the service
  points the kernel here via `DEENV_HOME`.

---

## Slice 1 — run it as a service

### 1. Check .NET on the box

```bash
dotnet --info        # need a 9.x runtime (the app targets net9.0); the SDK too if you build here
```

If it's older than 9, either install the .NET 9 SDK/runtime, or build elsewhere with
`-r linux-x64 --self-contained` (bundles the runtime — then `ExecStart` runs `/opt/deenv/DeEnv`).

### 2. (Optional, 1 GB) a little swap

The native build is far lighter than Docker's, but a 1 GB swapfile keeps the publish + runtime
comfortable:

```bash
sudo fallocate -l 1G /swapfile && sudo chmod 600 /swapfile && sudo mkswap /swapfile
sudo swapon /swapfile && echo '/swapfile none swap sw 0 0' | sudo tee -a /etc/fstab
```

### 3. Build the binaries

Commit + push first, then on the box (needs the SDK):

```bash
git clone <your-repo-url> ~/deenv-src    # or: cd ~/deenv-src && git pull
cd ~/deenv-src
sudo mkdir -p /opt/deenv
sudo dotnet publish DeEnv/DeEnv.csproj -c Release -o /opt/deenv
```

(No SDK on the box? Run that `dotnet publish` on your dev machine and `rsync -a` the output to
`/opt/deenv` — the DLLs are cross-platform; the service runs them via `dotnet DeEnv.dll`.)

### 4. Seed the data home (once)

The publish includes the registry + the committed app documents; move them to the persistent home:

```bash
sudo mkdir -p /var/lib/deenv
sudo mv /opt/deenv/kernel.json /opt/deenv/instances /var/lib/deenv/
```

### 5. Create the service user + install the unit

```bash
sudo useradd -r -s /usr/sbin/nologin deenv
sudo chown -R deenv:deenv /var/lib/deenv

sudo cp ~/deenv-src/deploy/deenv.service /etc/systemd/system/deenv.service
# If `which dotnet` isn't /usr/bin/dotnet, fix ExecStart in that file first.
sudo systemctl daemon-reload
sudo systemctl enable --now deenv
```

### 6. Verify

```bash
systemctl status deenv          # active (running)
journalctl -u deenv -f          # "Hosting ... on app:8082 infra:8083" lines
curl -i http://localhost:8082/  # the todo app — HTTP 200 + HTML
```

At this point it runs as a service on the raw HTTP ports. **Slice 2** puts it behind your domain
over HTTPS and closes the raw ports.

### (Optional, 1 GB) trim what runs

Each hosted instance costs RAM. Edit `/var/lib/deenv/kernel.json` to keep only what you need
(e.g. the designer + your real app), then `sudo systemctl restart deenv`.

---

## Backup

```bash
sudo tar czf ~/deenv-data-$(date +%F).tgz -C /var/lib/deenv .   # cron this; slice 3 formalizes it
```

## Updating after a code change

```bash
cd ~/deenv-src && git pull
sudo dotnet publish DeEnv/DeEnv.csproj -c Release -o /opt/deenv   # data at /var/lib/deenv untouched
sudo systemctl restart deenv
```

---

## Slice 2 — nginx + HTTPS (next)

Put it behind your domain over TLS. The plan (decided: keep the two-port model, TLS both):

- **nginx** terminates TLS (one Let's Encrypt cert via certbot):
  - `:443` → `127.0.0.1:8082` (the app) — the clean public URL.
  - a public TLS port (e.g. `:8443`) → `127.0.0.1:8083` (the infra port: `/ws` + `/js`), **with the
    WebSocket upgrade headers**. The client already speaks `wss://` automatically on an HTTPS page
    ([ws.ts:64](../DeEnv/Instance/ws.ts:64)); it just needs the injected infra port to point at this
    public TLS port — a one-line config in the SSR injection.
- **ufw**: allow `443`, the infra TLS port, and ssh; deny the raw `8080–8087`.
- This keeps the app's URL space clean and the port stays invisible to you (only the WS uses it).

(Slice 2 is the small SSR change + the nginx server block + certbot — built next.)
