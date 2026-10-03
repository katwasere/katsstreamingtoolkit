# VPS setup - from zero to live on every platform (~15 min)

You need: a PC with OBS, and a credit card for the server. Everything else is copy-paste.

## 1. Rent the server

Any Ubuntu 22.04/24.04 VPS works. Good cheap picks:

| Provider | Box | Price | Traffic included |
|---|---|---|---|
| Oracle Cloud Free tier | Ampere A1 Flex (ARM, up to 4 cores) | 0 | 10 TB |
| Hetzner | CX22 (2 vCPU) | ~4 EUR/mo | 20 TB |
| DigitalOcean | 2 GB droplet | $12/mo | 4 TB |

### Oracle Cloud specifics (the free 4-core ARM box)

1. Sign-up needs a card for verification (hold is refunded). **Home region is
   permanent** - pick the nearest big one. ARM capacity can be scarce: if creation
   fails with "out of capacity", retry at off-peak times or upgrade to Pay As You
   Go (A1 usage inside the always-free limits stays free).
2. Create instance: Ubuntu 22.04/24.04, shape **VM.Standard.A1.Flex**, 2 OCPU /
   12 GB (up to 4/24 free). Download the generated SSH **private key** - the
   toolkit's Deploy tab uses it (tick "Use SSH key file instead of password").
3. Open port 1935 in the cloud console too: instance -> Subnet -> Security List ->
   Add Ingress Rule: source `0.0.0.0/0`, TCP, port 1935.
4. SSH in as user `ubuntu` (not root) and fix Oracle's extra host firewall -
   Ubuntu images ship with iptables locked down to SSH only:
   ```bash
   sudo iptables -I INPUT -p tcp --dport 1935 -j ACCEPT
   sudo apt-get install -y iptables-persistent
   sudo netfilter-persistent save
   ```
5. Oracle may reclaim always-free instances that sit idle (low CPU for ~a week).
   A relay actively receiving streams counts as activity; if it ever disappears,
   redeploying to a new instance is two clicks in the Deploy tab.

Pick a region close to you **and** close to your platforms (Frankfurt for EU streaming,
Ashburn for US). The toolkit's status bar tells you the traffic your destinations need -
compare with the table above. Twitch+YouTube+Kick at 6 Mbps each is ~1.4 TB per 8-hour day.

## 2. Connect and install Docker (once)

Open PowerShell on your PC:

```powershell
ssh root@YOUR.SERVER.IP
# Oracle Cloud instead: ssh -i C:\path\to\downloaded.key ubuntu@YOUR.SERVER.IP
```

Then on the server:

```bash
curl -fsSL https://get.docker.com | sh
ufw allow OpenSSH
ufw allow 1935/tcp
ufw --force enable
```

(Oracle Cloud: use `sudo` for the docker command, and skip `ufw` - you opened port
1935 with iptables in the Oracle section above.)

## 3. Upload the server bundle

In the toolkit: Relay tab -> "Export server bundle..." -> pick your Desktop.

Upload it (pick one):

```powershell
scp -r "$env:USERPROFILE\Desktop\kat-relay" root@YOUR.SERVER.IP:/opt/kat-relay
```

...or install WinSCP and drag the folder to `/opt/` on the server.

## 4. Start the relay

```bash
cd /opt/kat-relay
docker compose up -d --build
```

Done. It restarts itself after crashes and reboots.

## 5. Point OBS at the relay

OBS -> Settings -> Stream:

- Service: **Custom...**
- Server: `rtmp://YOUR.SERVER.IP/live`
- Stream key: the stream name from the toolkit (the `kat-...` value)

Start streaming in OBS. Within seconds the relay copies it out to every enabled
destination. Check the platform dashboards to confirm.

## Daily use

- Just OBS -> Start Streaming. The server never needs touching again.
- Changed destinations in the toolkit? Re-export the bundle, upload, run
  `docker compose up -d --build` again.
- Watch logs: `docker compose logs -f`

## Troubleshooting

- **Destination not live**: is the key pasted fully? Is the destination enabled and
  the export re-uploaded? `docker compose logs` shows every push attempt.
- **Can't connect at all**: `ufw status` must show `1935/tcp ALLOW`.
- **Stutter on one platform only**: lower that destination's bitrate in the toolkit.

## Security

- `nginx.conf` contains your real platform keys. Never commit it. (The exported
  folder ships with a `.gitignore` that blocks it.)
- Your OBS stream key is the only password guarding your relay - keep it random.
- Rotate any key from a platform dashboard, update it in the toolkit, re-export.
