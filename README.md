# Missing Episodes for Jellyfin

A Jellyfin plugin that finds gaps in your TV library and optionally asks Sonarr to fill them in — with a cleaner UI than Sonarr's default missing-episodes view.

> ⚠️ **Pre-release.** This plugin has not been released yet. Test builds only.

## What it does

- Connects to your Sonarr instance and pulls the list of shows and episodes.
- Diffs against either **Sonarr's own monitored/hasFile state** (fast, trusts Sonarr) or **your Jellyfin library** (strict — catches episodes Sonarr thinks exist but Jellyfin can't see).
- Displays a poster grid of shows that are missing episodes, with a badge for the missing count.
- Click a show to see exactly which episodes are missing, grouped by season, with a one-click **Search** button per episode or season.
- Optional **auto-search**: runs the scan on a schedule and sends every missing episode to Sonarr without you lifting a finger.

## Multi-episode files

Some shows ship two or more episodes as a single file (typical for comedies, and some animation). When you pick **Jellyfin library** as the scan source, the plugin expands Jellyfin's `IndexNumberEnd` range so a file covering `S01E01-E02` is counted as both E01 *and* E02 present — you won't see false "missing" reports for those.

For the edge cases that don't fit that rule (absolute numbering, weird aggregations, etc.), each show has an **Ignore** button that adds it to a per-plugin ignore list.

## Configuration

1. Jellyfin → Dashboard → Plugins → Missing Episodes.
2. Open **Settings**.
3. Paste your Sonarr URL (e.g. `http://sonarr.local:8989`) and API key (Sonarr → Settings → General).
4. Pick a scan source:
   - **Sonarr library** — trust Sonarr's `monitored` + `hasFile` flags. Faster; matches what Sonarr itself sees.
   - **Jellyfin library** — diff against what's actually importable in your Jellyfin library. Stricter — catches files that didn't import, failed metadata matches, etc.
5. (Optional) Enable **Send missing episodes to Sonarr automatically** and set an interval.
6. Click **Test connection**, then **Save**.
7. Hit **Scan now**.

## Building from source

Requires the .NET 9 SDK.

```bash
cd Jellyfin.Plugin.MissingEpisodes
dotnet build -c Release
```

The plugin DLL lands in `Jellyfin.Plugin.MissingEpisodes/bin/Release/net9.0/Jellyfin.Plugin.MissingEpisodes.dll`.

## Installing a dev build

1. Copy the DLL into your Jellyfin server's plugin directory:
   - Docker: `/config/data/plugins/MissingEpisodes_0.1.0.0/`
   - Linux package: `/var/lib/jellyfin/plugins/MissingEpisodes_0.1.0.0/`
   - Windows: `%ProgramData%\Jellyfin\Server\plugins\MissingEpisodes_0.1.0.0\`
2. Restart Jellyfin.
3. Dashboard → Plugins should now list **Missing Episodes**.

## License

MIT — see [LICENSE](LICENSE).

## Not affiliated with

Jellyfin, Sonarr, or anyone else. This is a third-party plugin.
