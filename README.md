<p align="center">
  <img src="https://img.shields.io/badge/Jellyfin-10.11%2B-0b0b0b?style=for-the-badge&labelColor=000000&color=2b2b2b" />
  <img src="https://img.shields.io/badge/Type-Plugin-4d9ed1?style=for-the-badge&labelColor=000000&color=4d9ed1" />
  <img src="https://img.shields.io/badge/Integrates-Sonarr%20%7C%20TMDB-0b0b0b?style=for-the-badge&labelColor=000000&color=2b2b2b" />
  <img src="https://img.shields.io/badge/Version-1.0.3-0b0b0b?style=for-the-badge&labelColor=000000&color=2b2b2b" />
  <img src="https://img.shields.io/badge/License-MIT-0b0b0b?style=for-the-badge&labelColor=000000&color=2b2b2b" />
</p>

# Missing Episodes for Jellyfin

Find the gaps in your TV library — and let Sonarr fill them.

Scans your **Jellyfin library**, your **Sonarr instance**, or queries **TMDB** when neither has the data. Shows exactly what's missing per show, per season, per episode, with thumbnails, air dates, storage sizes, paths, and a one-click Search button that hands off to Sonarr.

> **Status:** stable (**v1.0.3**). Works end-to-end on Jellyfin 10.11.x + Sonarr v3. Install via the plugin repository URL below or as a manual DLL drop.

---

## 📑 Table of contents

- [Features](#-features)
- [Installation](#️-installation)
- [Configuration](#-configuration)
- [How each scan source works](#-how-each-scan-source-works)
- [Accuracy — filename parsing](#-accuracy--filename-parsing)
- [Sending to Sonarr](#-sending-to-sonarr)
- [What this plugin stores](#-what-this-plugin-stores)
- [Security](#-security)
- [Troubleshooting](#-troubleshooting)
- [Building from source](#-building-from-source)
- [License](#-license)

---

## ✨ Features

- **Three scan sources**
  - **Sonarr** — trust Sonarr's `monitored` + `hasFile` flags. Authoritative, fast.
  - **Jellyfin / Virtual items** — use Jellyfin's own missing-episode items (requires *Display missing episodes within seasons* in the library).
  - **Jellyfin / TMDB** — query TMDB for each show's canonical episode list. Works without any Jellyfin library reconfiguration. Needs a free TMDB API key.
  - **Jellyfin / Gap detection** — find numbering holes in what's on disk. No external deps, limited but useful.
- **Filename parsing safety net** — walks the series folder on disk and parses `S01E05` / `1x05` / multi-ep patterns. Catches episodes that exist on disk but Jellyfin's library never imported — the exact class of bug where a show reads as 0/186 despite 144 GB sitting there.
- **One-click Sonarr search** — per episode, per season, per show, or everything currently visible.
- **Auto-search** (optional) — scan + dispatch to Sonarr on a schedule.
- **Rescan this show** — targeted refresh from the detail view, no full library scan.
- **Active / Ignored tabs** — hide shows you never care about; click an ignored card to open its detail with a **Remove from ignore list** button.
- **All / TV / Anime filter** + title search. Anime detection uses Sonarr's series type or Jellyfin genre/tag.
- **Persistent results + rolling 25-scan history** — survives restarts, clearable from the UI.
- **Live progress** — background scan with progress strip (X / Y series, current show, percentage). Leave the page, come back, and it picks up. Toast fires when done.
- **Admin sidebar entry** — *Missing Episodes* in the Jellyfin admin sidebar, not buried in Plugins.
- **Storage + path** — GB chip on each card (computed from on-disk size when Jellyfin can read the folder; from Sonarr otherwise). Series path in the detail view. Smart reconciliation: trust whichever side has files.
- **Sorts** — Most missing / Title / Recent / Size / Progress (least-complete first).
- **Admin-only API** — every endpoint requires the `RequiresElevation` policy.

---

## ⚙️ Installation

### Option 1 — Jellyfin plugin repository (recommended)

1. Go to **Dashboard → Plugins → Repositories → ➕**
2. Name: `Missing Episodes`
3. URL:

```
https://raw.githubusercontent.com/ZL154/MissingEpisodesJellyfin/main/manifest.json
```

4. **Save** → **Catalog** → install **Missing Episodes**
5. Restart Jellyfin
6. **Dashboard → Missing Episodes → Settings** to configure

### Option 2 — Manual install

1. Download the latest zip from [Releases](https://github.com/ZL154/MissingEpisodesJellyfin/releases).
2. Extract the DLL into your Jellyfin plugin directory:
   - Docker: `/config/plugins/MissingEpisodes_x.y.z.w/`
   - Linux: `/var/lib/jellyfin/plugins/MissingEpisodes_x.y.z.w/`
   - Windows: `%ProgramData%\Jellyfin\Server\plugins\MissingEpisodes_x.y.z.w\`
3. Restart Jellyfin.

---

## 🔧 Configuration

1. **Dashboard → Missing Episodes** (or the sidebar entry) → **Settings**.
2. **Sonarr URL** (e.g. `http://sonarr.local:8989`) and **API key** (Sonarr → Settings → General). Click **Test Sonarr**.
3. (Optional) **TMDB API key** — get one free at <https://www.themoviedb.org/settings/api>. Click **Test TMDB**. Only needed for the TMDB Jellyfin mode.
4. **Scan source**:
   - **Sonarr** — use Sonarr's data (best if Sonarr tracks every show).
   - **Jellyfin** — use Jellyfin's library. Pick a method: *Virtual items* / *TMDB* / *Gap detection*.
5. Toggles: **Only monitored**, **Ignore specials**, **Ignore unaired**.
6. (Optional) **Auto-send missing episodes to Sonarr** + interval.
7. **Save**, then **Scan now**.

---

## 🔎 How each scan source works

### Sonarr source

- `GET /api/v3/series` for the series list.
- `GET /api/v3/episode?seriesId=N&includeImages=true` per series.
- Flags episodes where `hasFile == false`, honoring your toggles.
- Thumbnails come from Sonarr's episode `screenshot` images.

### Jellyfin / Virtual items

- Queries Jellyfin for `Episode` items, expands the virtual ones (`IsVirtualItem = true`).
- Requires *Display missing episodes within seasons* in your Jellyfin library settings + a library scan to create virtual items.
- Best fidelity — full Jellyfin metadata + artwork.

### Jellyfin / TMDB

- For each series with a TMDB id, fetches `GET /3/tv/{id}` + `GET /3/tv/{id}/season/{n}`.
- Diffs the canonical TMDB list against what's actually on disk in Jellyfin.
- Catches whole missing seasons and trailing episodes that gap detection can't.

### Jellyfin / Gap detection

- Walks on-disk episode numbers per season, flags any `{1..max}` not present.
- No external dependencies. Limited (can't see missing final episodes or whole missing seasons).

### Enrichment

If Sonarr is configured AND you're running a Jellyfin-source scan, the plugin matches shows by **TVDB or TMDB id** and stamps them with Sonarr episode IDs. That's what makes **Search** buttons work in any mode.

---

## 🎯 Accuracy — filename parsing

Library drift is common. Files can be on disk while Jellyfin's library lists them as missing (stale metadata, failed imports, renamed paths). To avoid false missing counts, every Jellyfin scan **walks the series folder and parses filenames** for patterns like `S01E05`, `s01e05e06`, `1x05`. Any episode coordinate found on disk is counted as present even if Jellyfin hasn't imported it.

Limitations:

- Absolute-numbered anime filenames (e.g. `Gintama - 042`) don't match the regex — those are hard to disambiguate from random three-digit numbers. Re-rename with `S01E42` or use Sonarr source for those shows.
- A filename has to actually contain the S/E markers. `Pilot.mkv` won't be detected.

---

## 📨 Sending to Sonarr

- **Per-episode** — inline Search button on each missing episode row.
- **Per-season** — header button on each season in the detail view.
- **Search all missing (one show)** — bottom of the detail view.
- **Search all (across current filter)** — toolbar button on the grid. Respects type, ignored, and text filters.
- **Auto-search** — background worker runs at the configured interval, full scan, dispatches everything.

Every path hits `POST /api/v3/command` with a batched `EpisodeSearch`.

---

## 💾 What this plugin stores

All under the plugin's data folder (`/config/plugins/Jellyfin.Plugin.MissingEpisodes/`):

| File | Purpose |
| ---- | ------- |
| `last-result.json` | Full result of the most recent scan. Lazily loaded on first request after restart. |
| `history.json` | Rolling log of the last 25 scans (timestamps, totals, duration). |

Plugin configuration (URLs, API keys, toggles) lives in Jellyfin's own plugin config XML, not here.

To wipe: stop Jellyfin, delete the files, restart. Next scan regenerates them. History can also be cleared from the UI.

---

## 🔒 Security

- **Admin-only.** Every API endpoint requires Jellyfin's `RequiresElevation` policy.
- **No secret logging.** Sonarr and TMDB API keys are passed to outbound HTTP calls only — never written to Jellyfin logs.
- **No secrets in plugin files.** `last-result.json` and `history.json` contain scan data only. API keys stay in Jellyfin's plugin config (as with any other plugin).
- **XSS-hardened.** Image URLs from Sonarr / TMDB / Jellyfin are applied via `element.style.backgroundImage` with `JSON.stringify` escaping, not interpolated into inline HTML — can't break out of the `url()` literal even with a hostile response.
- **No path traversal.** File walks only touch `series.Path` from Jellyfin's library manager; no user-supplied paths.

---

## 🛠️ Troubleshooting

**"No scan yet" after a scan completed.**
Hard-refresh. Results persist to disk and a completion toast fires in the Jellyfin client, so a reload shouldn't lose them.

**Search buttons greyed out.**
Either Sonarr isn't configured, or the selected shows have no Sonarr id yet. In Jellyfin-source mode the plugin enriches with Sonarr IDs only when a URL + API key is set.

**Jellyfin / Virtual items returns 0 missing.**
*Display missing episodes within seasons* isn't enabled, or the library scan hasn't created virtual items yet. Enable it in **Dashboard → Libraries → (library)**, run **Scan Media Library**, rescan. Or switch to **TMDB** mode.

**Show reads as 0 episodes even though files exist.**
Fixed in 1.0.3 — the plugin now walks the folder and parses S##E## from filenames as a safety net. If you're on 1.0.3+ and still seeing this, check that the filenames actually contain S/E markers (not just an episode number).

**"An error occurred while getting the plugin details from the repository."**
Harmless. Appears when the plugin was installed manually. To silence: add the manifest URL under **Plugins → Repositories**, then uninstall and reinstall from the catalog.

**Rescan finished but the missing count didn't change.**
Rescan re-reads Sonarr/TMDB data — it doesn't wait for Sonarr to download anything. Give Sonarr a minute to grab a release, then rescan again.

**Settings don't seem to save.**
Scan source / Jellyfin method segments auto-save on click. URL, API key, interval, checkboxes need the **Save** button. Reload to verify.

---

## 🏗️ Building from source

.NET 9 SDK required.

```bash
git clone https://github.com/ZL154/MissingEpisodesJellyfin.git
cd MissingEpisodesJellyfin/Jellyfin.Plugin.MissingEpisodes
dotnet build -c Release
```

DLL lands in `bin/Release/net9.0/Jellyfin.Plugin.MissingEpisodes.dll`.

---

## 📜 License

MIT — see [LICENSE](LICENSE).

Not affiliated with Jellyfin, Sonarr, TMDB, or anyone else. Third-party plugin.
