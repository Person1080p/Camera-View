# Camera Viewer

A browser-based replacement for the Silverlight/ActiveX web UI on Hikvision-compatible NVRs, DVRs and IP cameras
(including OEM rebrands such as the THK-NR32P6-16). Built with ASP.NET Core 8 and React. It works in any modern
browser with no plugins.

- **Login**: cookie-based sign-in with Admin and Viewer roles. New and reset accounts must choose their own password.
- **Multiple devices**: admins add any number of NVRs or cameras and choose which devices each viewer can see.
- **Live view**: 1/4/9/16 camera grid plus a single-camera view with SD/HD switching and snapshots.
- **Playback**: a 24-hour timeline of recordings with zoom. Click the bar to play from that time, or jump by ±10s/±1m.
- **Export**: mark in/out on the timeline to queue an MP4 export, then download it from the Exports page.
- **NVR administration** (admins only): manage the recorders themselves. See [NVR administration](#nvr-administration).
- **Embedded Tailscale**: reach NVRs at other sites over your tailnet without installing Tailscale on the server. See
  [Remote sites over Tailscale](#remote-sites-over-tailscale).

## Requirements

The app runs on **Windows and Linux**.

| Tool | Version | Windows | Debian / Ubuntu |
|------|---------|---------|-----------------|
| .NET SDK | 8.0 | https://dotnet.microsoft.com/download | `sudo apt install dotnet-sdk-8.0` |
| Node.js | 18+ (only to build the UI) | `winget install OpenJS.NodeJS.LTS` | `sudo apt install nodejs npm` |
| ffmpeg | any recent, with libx264 | `winget install Gyan.FFmpeg` | `sudo apt install ffmpeg` |
| Go | 1.23+ (only to build the Tailscale helper) | `winget install GoLang.Go` | https://go.dev/dl |

ffmpeg is found on `PATH` (and, on Windows, in winget's install folder). To point at a specific copy, set
`Ffmpeg:Path` in `server/CameraViewer.Api/appsettings.json`.

## Running

```powershell
.\start.ps1            # Windows: builds the UI on first run, then starts the server
.\start.ps1 -Rebuild   # rebuild the UI after changing client code
```

```bash
./start.sh             # Linux: the same
./start.sh --rebuild
```

Open **http://localhost:5080**. Other machines on your network can use `http://<server-ip>:5080`; allow port 5080
through the firewall if needed.

Only one server can use a given data folder at a time. A second copy exits with a message instead of starting.

### Running on a Linux server

For a permanent install, publish a self-contained build and run it under systemd. A self-contained build needs no .NET
on the server; it only needs ffmpeg.

```bash
# On a build machine with the .NET SDK, Node.js and Go (Windows or Linux):
cd client && npm install && npm run build && cd ..
./tailscale/build.sh                 # or tailscale\build.ps1 on Windows; skip if you don't use Tailscale
dotnet publish server/CameraViewer.Api -c Release -r linux-x64 --self-contained -o publish
#   (use -r linux-arm64 for a Raspberry Pi 4/5 or other 64-bit ARM board)

# On the server:
sudo apt install ffmpeg
sudo useradd --system --home /var/lib/cameraviewer --shell /usr/sbin/nologin cameraviewer
sudo mkdir -p /opt/cameraviewer /var/lib/cameraviewer
sudo cp -r publish/. /opt/cameraviewer/
sudo chown -R cameraviewer:cameraviewer /var/lib/cameraviewer
sudo cp deploy/cameraviewer.service /etc/systemd/system/
sudo systemctl daemon-reload && sudo systemctl enable --now cameraviewer
journalctl -u cameraviewer -f        # logs, including the CameraViewer.Audit entries
```

Notes for Linux:

- **Device password file.** The publish folder includes `appsettings.Local.json`, which seeds your NVR (password
  included) on the first run. Delete it from `/opt/cameraviewer` once the app has started.
- **Data folder.** `/var/lib/cameraviewer` holds the database, encryption keys and exports, readable only by the
  service account. Linux has no DPAPI, so this folder's permissions are what protect the keys; keep it private and
  back it up together.
- **Time zone.** Set the server's time zone (`sudo timedatectl set-timezone America/New_York`). "Sync NVR clock to this
  server" uses the server's local time.
- **Crashes.** If the service stops or crashes, systemd also stops its ffmpeg processes. Any left over from an
  unsupervised crash are cleaned up the next time the server starts.

Tested on Ubuntu 24.04 (WSL2) with a self-contained build against the NVR:

- Login, live view, playback and export all work.
- NVR administration works.
- Permissions on the data folder are correct.
- Duplicate-instance protection, cleanup after a crash and normal shutdown all behave.

The systemd unit passes `systemd-analyze verify` but hasn't been run as a real service.

On first start the app creates:

- An **`admin` / `admin`** account. You'll be asked to set a real password straight away.
- The devices listed in `server/CameraViewer.Api/appsettings.Local.json`.
  Device passwords are encrypted into the database, so after the first run you can delete the password from that file.

### Development

Run the API and the Vite dev server separately to get hot reload:

```powershell
cd server/CameraViewer.Api; dotnet run      # http://localhost:5080
cd client; npm run dev                      # http://localhost:5173 (proxies /api to :5080)
```

## How it works

```
Browser (React + hls.js)  ──HTTP──>  ASP.NET Core  ──ISAPI (HTTP, digest auth)──>  NVR :80
                                          │
                                          └─ ffmpeg  ──RTSP──>  NVR :554
                                               └─ writes HLS segments / MP4 files
```

- **ISAPI** (port 80) handles device info, the channel list, snapshots and recording search.
- **RTSP** (port 554) carries the video:
  - Live SD relays each camera's H.264 sub stream unchanged, so it costs almost no CPU.
  - Live HD and playback are transcoded to H.264. Recordings are H.265, which most browsers can't play.
- The iVMS **SDK port 8000 is not used**.
- Live streams are shared, so ten people watching the same camera use one connection to the NVR.
- Streams stop automatically about 30–45 seconds after the last viewer leaves.

## Remote sites over Tailscale

Camera Viewer can join your tailnet as its own machine, so it reaches NVRs at other sites without Tailscale being
installed on the server. This works on Windows and Linux and needs no admin rights or network adapter. See
[docs/remote-sites.md](docs/remote-sites.md) for how the sites' routers are set up.

1. **Tailscale** page → tick *Run Camera Viewer's Tailscale node* → Save.
2. Click **Open Tailscale sign-in** and log in with the account that owns the sites' routers, or paste an auth key.
   The page shows *Connected* and lists the tailnet's machines and the subnets each one shares.
3. **Devices** page → add the site's NVR with its LAN address (e.g. 192.168.20.101) and tick **Connect through
   Tailscale**. Press *Test connection*, then save.

How it works:

- **The node.** `tailscale/tsproxy` is a small Go program built on Tailscale's embeddable library, tsnet. The server
  starts it, keeps it running, and stops it when the server stops.
- **The node's identity.** Its keys live in `<data folder>/tailscale-node/`, readable only by the account running the server (and, on
  Windows, SYSTEM and Administrators), so it stays signed in
  across restarts.
- **Web requests** to Tailscale devices go through the node's loopback SOCKS5 proxy.
- **Video** goes through a loopback port forwarder for each device, because ffmpeg can't use a SOCKS proxy for RTSP.
- **Subnet routes are accepted by default**, so NVRs behind a site router are reachable at their LAN addresses. The
  route must be approved in the Tailscale admin console.
- **Addresses the tailnet doesn't route** fall back to the server's normal network. So ticking the option on a local
  device does no harm.

Things to know:

- **Remote sites are slow to connect.** Over a site's LTE uplink the first connection can take several seconds. The
  app allows 20 s to connect to Tailscale devices, versus 5 s for local ones.
- **One NVR per subnet across sites.** Two sites sharing the same subnet (for example both 192.168.20.0/24) can't both
  be reached as they are. Either renumber one site's LAN first, or:
  - give that site's router a second subnet and advertise only that one
  - map the NVR into it, so 192.168.21.138 on the tailnet reaches 192.168.20.138 on the LAN

  See [docs/remote-sites.md](docs/remote-sites.md).
- **The tailnet's access rules apply** to this machine like any other.
- **Not tested end to end yet.** The app's side (status, sign-in, and video and API traffic through the proxy and
  forwarder) was tested against a local NVR with a stand-in for the Tailscale node. The real node reaches the sign-in
  step, but signing it in and reaching a remote site's NVR haven't been done yet, because that needs your Tailscale
  account.

## NVR administration

The **NVR Admin** page (or **Manage** on the Devices page) replaces the admin parts of the old web UI:

| Tab | What you can do |
|-----|-----------------|
| Status | Model, firmware, uptime, CPU and memory, the clock compared with this server, network settings, disk health (SMART temperature, power-on time, status), and each camera's address and online state. Refreshes every 30 s. |
| Network | Static IP or DHCP, subnet, gateway and DNS. Service ports: HTTP, HTTPS, RTSP and the iVMS server port. Turn UPnP port forwarding and SSH on or off. Dynamic DNS (provider, server, domain, account) with its live status, and cloud access (EZVIZ/Hik-Connect) with its verification code. |
| System | The device name, and whether to overwrite the oldest recordings when the disk is full. |
| Maintenance | Sync the NVR clock to this server, set the time zone and NTP, and reboot the NVR. |
| Cameras | Rename a camera, restart it, edit the on-screen date/time and name, and change main/sub stream codec, resolution, bitrate and frame rate. Choices come from the camera's own option lists. |
| Logs | Search the NVR's alarm, exception, operation and information logs by date range. |
| Device accounts | Add, edit and delete the NVR's own logins (used by iVMS and this app). |

How changes are made, and what's protected:

- **Only the edited fields change.** Each change re-reads the device's current settings, edits just those fields and
  writes them back, so settings this app doesn't show are kept.
- **Every change is logged** with the signed-in user's name, under the `CameraViewer.Audit` category in the server log.
- **The connection account is protected.** The built-in administrator and the NVR account Camera Viewer itself uses
  can't be deleted. If you change that account's password here, the app updates its own stored copy.
- **Account limits.** New NVR accounts can be Operator or Viewer, with passwords of up to 16 characters (the device's
  limit).
- **Address and port changes follow the NVR.** When you change the NVR's IP address or its HTTP/RTSP ports, Camera
  Viewer updates its own connection settings to match. If the NVR stops answering mid-change because it has already
  moved, the app looks for it at the new address before reporting a failure. Changes the NVR only applies after a
  reboot say so.
- **Checks before network changes.** The app rejects an invalid IP, subnet mask or gateway, and a gateway on another
  subnet, before sending anything. Switching to DHCP comes with a warning that the address may change.
- **Write-only secrets are kept.** The NVR never returns the DDNS password or the cloud verification code, so those
  fields start empty, and leaving them empty keeps the stored value. On this firmware the NVR keeps a stored password
  when a change leaves it out, which was verified with a test device account.

Tested against the THK-NR32P6-16 (firmware V3.4.101). Clock sync was run for real. Everything else was tested only
with changes that leave settings as they were, and works: status and disk health, network/IP, ports, UPnP and SSH,
device name and overwrite, logs, the on-screen display, stream settings, the time setting and device accounts.

Not yet tried, because doing so would interrupt cameras or recording:

- Rebooting the NVR or a single camera.
- Actually changing the NVR's IP address or ports, including the "follow the NVR" behaviour.
- Saving Dynamic DNS. It is live and working, so it wasn't rewritten. The cloud settings were saved back unchanged
  successfully.
- Renaming a camera that is online. The NVR seems to take the name from the camera, so an offline camera ignores the
  rename.
- Saving a new time zone or NTP server. The daylight-saving presets use Hikvision's usual format; check the NVR time on
  the Status tab after saving.

## Things to know

- **One recording stream at a time.** This NVR refuses a second simultaneous playback or export with "453 Not
  Enough Bandwidth". The app tracks this per device (the *Simultaneous playback streams* setting, default 1):
  - Starting a new playback replaces your previous one.
  - If someone else is using the stream, you're told who.
  - Exports wait until the stream is free. Exporting from the Playback page stops your playback first.
  - Close the iVMS client while exporting: it uses the same limit, and exports retry while it's busy.
- **Exports run at real-time speed.** The NVR only streams recordings at 1×, so a 10-minute clip takes about 10
  minutes. The limit is 2 hours per export (`Exports:MaxMinutes`).
- **Export formats.** H.264 plays everywhere and is capped at 1080p. H.265 keeps the original quality and makes a
  smaller file, but some players (for example Windows "Movies & TV" without the HEVC extension) can't open it.
- **No audio.** Video only.
- **Times are the NVR's clock.** Timeline and export times use the recorder's local time, which matches the timestamp
  burned into the video.
- **Device passwords** are encrypted with ASP.NET Data Protection. Where its keys are protected depends on the OS:
  - **Windows:** the keys are encrypted with DPAPI for the account running the server. Keep running it under the same
    Windows account, or re-enter device passwords on the Devices page.
  - **Linux:** the keys are protected by the data folder's owner-only permissions.
  - **Moving servers:** a data folder copied between Windows and Linux can't decrypt the stored device passwords, so
    re-enter them on the Devices page.
- **HTTP only.** The app serves plain HTTP. Before exposing it beyond your LAN, put it behind a reverse proxy with
  HTTPS, or a VPN.
- **Schema changes.** The database is created with `EnsureCreated`. If you change entity classes, delete
  `server/CameraViewer.Api/App_Data/cameraviewer.db` (or add EF migrations).

## Configuration (`appsettings.json`)

| Key | Default | Meaning |
|-----|---------|---------|
| `Urls` | `http://0.0.0.0:5080` | Listen address |
| `Storage:DataPath` | `App_Data` | Database, encryption keys and exported MP4s |
| `Ffmpeg:Path` | *(auto)* | Explicit path to ffmpeg.exe |
| `Streaming:LiveIdleSeconds` | 30 | Stop a live relay after this long without viewers |
| `Streaming:PlaybackIdleSeconds` | 45 | Release a playback session (and the NVR stream) after this long |
| `Streaming:PlaybackWindowHours` | 3 | How far one playback session runs before you need to click again |
| `Streaming:MaxSessions` | 40 | Cap on concurrent ffmpeg processes |
| `Exports:MaxMinutes` | 120 | Longest allowed export |
| `Exports:MaxConcurrent` | 2 | Export workers (exports on the same device still go one at a time) |
| `Seed:AdminUsername` / `Seed:AdminPassword` | admin / admin | First-run admin account |
| `Seed:Devices` | `[]` | Devices to add at start-up when their host and HTTP port aren't registered yet (best kept in `appsettings.Local.json`); each may set `"UseTailscale": true`. A configured device deleted in the app comes back at the next start. |
| `Tailscale:HelperPath` | *(auto)* | Explicit path to the tsproxy helper; normally found next to the server binaries |

## Project layout

```
server/CameraViewer.Api/
  Program.cs              DI, cookie auth, rate-limited login, SPA hosting
  Endpoints/              Auth, Users, Devices, Streams (HLS), Exports, NvrAdmin
  Services/
    IsapiClient.cs        Hikvision ISAPI: device info, channels, snapshots, recording search
    NvrAdmin.cs           NVR administration: status, network, system, clock, reboot, cameras, logs, accounts
    StreamManager.cs      ffmpeg → HLS sessions for live/playback, idle reaping
    ExportService.cs      Background MP4 export queue with progress
    PlaybackSlots.cs      Per-device limit on simultaneous recording streams
    Ffmpeg.cs             ffmpeg discovery, process wrapper (Windows job object / Linux orphan sweep), RTSP URLs
    Tailscale.cs          embedded Tailscale: helper supervision, LocalAPI, SOCKS5 client, RTSP forwarders
client/src/
  pages/                  Live, Playback, Exports, Devices, Users, Login, Account, NvrAdmin (+ nvr/ tabs)
  components/             HlsPlayer, LiveTile, Timeline, Layout
deploy/cameraviewer.service   systemd unit for Linux servers
start.ps1 / start.sh          build-and-run helpers for Windows / Linux
tailscale/tsproxy/            Go source of the embedded Tailscale helper (build.ps1 / build.sh build it into tailscale/bin/)
```
