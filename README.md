# Jellyfin Remote Share

A Jellyfin server plugin for sharing libraries between servers. The same plugin does both jobs:

- **Host:** pick libraries and create a share code for a friend's server. Revoke or pause it at any time.
- **Guest:** paste a share code and give it a friendly name. Each shared library becomes a normal library on your server, named e.g. `Movies (Mike)` by default so it doesn't clash with your own. You can rename each one, or turn it off.

Shared libraries behave like any other library: browsing, search, Home rows, per-user watched state and parental access all work, because Jellyfin sees ordinary movie/TV folders.

## How it works

```
 guest clients ──▶ guest Jellyfin ──(relay, token)──▶ host Jellyfin ──▶ media file
                     │  .strm + .nfo + artwork
                     └─ local library "Movies (Mike)"
```

1. **Sync** (scheduled task *Sync remote shares*, every 6 h, at startup, and right after joining or editing a share): the guest pulls the item list for each shared library. It writes one `.strm` per movie or episode, plus `.nfo` metadata and artwork, into its storage folder, and registers that folder as a local library. Only changed files are rewritten. Items removed on the host are removed on the guest.
2. **Playback:** each `.strm` points at the guest's own relay endpoint (`/RemoteShare/Relay/...`, protected by a per-share key). The relay forwards the request to the host with the share token. Range requests are passed through, so seeking works. Clients never see the host or the token, and only the guest server needs to reach the host. The guest transcodes as needed for its own clients.
3. **Access control:** host endpoints (`/RemoteShare/Host/...`) bypass Jellyfin user login and check the share token instead. Every item and image request is checked against the share's libraries.

Supported library types: **Movies** and **Shows**. Discs (BDMV/VIDEO_TS) and `.strm` items on the host are skipped.

## Install

Run `./build.sh` (needs the .NET 10 SDK). It produces:

| Zip | Jellyfin |
| --- | --- |
| `dist/RemoteShare_<ver>_jellyfin-12.zip` | 12.x (built against 12.0, so any 12.x) |
| `dist/RemoteShare_<ver>_jellyfin-10.11.zip` | 10.11.x |

Unzip the matching one into the server's `plugins` folder so you get `plugins/RemoteShare_<ver>/Jellyfin.Plugin.RemoteShare.dll` and `meta.json`. Restart Jellyfin. Settings are under **Dashboard → Plugins → Remote Share**.

Both servers need the plugin.

## Setup

**On the host**
1. Set **This server's public URL** to the address the guest server can reach, e.g. `https://jellyfin.example.com`.
2. Under **New share**, enter who it's for, tick libraries, and click **Create share code**.
3. Send the `rs1.…` code privately. Anyone with it can stream those libraries.

**On the guest**
1. Under **Add a share**, paste the code and enter a friendly name.
2. The first sync and library scan run automatically. Rename or disable individual libraries on the share's card, then click **Save**.
3. If the server uses a base path or a non-default port, set **This server's local URL** under Advanced (default `http://127.0.0.1:8096`).

## Notes and limits

- Bandwidth: playback streams the original file from host to guest. The host does no transcoding for guests.
- The guest library disables trickplay, chapter-image and embedded-title extraction, because those would pull whole files across the internet.
- Watched state is per server. Watching on the guest doesn't mark anything on the host.
- Changing a host's public URL invalidates existing share codes' URL. Guests would need a new code.
