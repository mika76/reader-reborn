# Reader

A remake of Google Reader as it looked from the 2011 redesign until it shut down in July 2013.

- `server/reader.cs`: the backend, a single-file .NET 10 app with a shebang. It downloads feeds, stores everything in SQLite and serves the JSON API and the built frontend.
- `web/`: the Vue 3 + Vite frontend.
- `data/reader.db`: the SQLite database, and `data/reader-media.db`: a second one holding saved files (post images, and optionally attachments of starred posts), each stored once by its SHA-256 with its type and size, so the main database stays small. Both are created on first run and are gitignored.

## Run

```bash
cd web && npm install && npm run build
```

```bash
./server/reader.cs
```

Then open http://localhost:5199. For frontend work, run `npm run dev` in `web/` (http://localhost:5173, with `/api` proxied to the backend).

The backend has other modes:

```bash
./server/reader.cs add daringfireball.net       # subscribe; finds the feed from a site URL
./server/reader.cs import subscriptions.xml     # OPML, e.g. from a Google Takeout
./server/reader.cs fetch                        # fetch every feed and save new images once, then exit (for cron)
```

Environment variables: `READER_DB` (database path), `READER_PORT` (default 5199), `READER_HOST` (address to listen on, default `localhost`), `READER_WEB` (the built frontend, default `web/dist`), `READER_INTERVAL` (minutes between fetches of a feed, default 30), `READER_IMAGES` (`off` to stop saving post images).

## Run with Docker or Podman

The `Dockerfile` builds the frontend and publishes the server, then runs it on the ASP.NET runtime image as a non-root user. The databases live in `/data` inside the container, so keep that on a volume.

```bash
podman compose up -d
```

Then open http://localhost:5199. `docker compose up -d` works the same way. To use another port, set `READER_PORT` (for example `READER_PORT=8080 podman compose up -d`); other settings go under `environment:` in `compose.yaml`.

Without compose:

```bash
podman build -t reader .
```

```bash
podman run -d --name reader -p 5199:5199 -v reader-data:/data reader
```

To move an existing database into the volume, stop the container and copy `reader.db` and `reader-media.db` into it, for example with `podman cp data/. reader:/data/` while it's stopped. Subscriptions can also be imported from Settings → Import/Export.

## Features

- RSS 0.9x/2.0, RSS 1.0 (RDF), Atom and YouTube/Media RSS feeds. Feeds are found from a site's `<link rel="alternate">` or common feed paths.
- Conditional GET (ETag/Last-Modified), parallel fetching, and backoff for feeds that fail. Favicons are fetched and stored once per feed.
- Posts are kept with whatever text the feed provides, plus their attachments (podcast audio, video, files), which play on the post.
- Post images are downloaded in the background (up to 10 MB each) into `reader-media.db` and shown from the saved copies, so posts keep their pictures if the original site goes away. Downloads are checked by their bytes (JPEG, PNG, GIF, WebP, AVIF, ICO, BMP, SVG), and saved files are served with headers that stop scripts in them from running. Copies are deleted when no post uses them any more. Settings → Preferences shows how much space they take.
- Optionally (Settings → Preferences, off by default), the attachments of starred posts, such as podcast episodes, are saved too, up to 900 MB each, and play from the saved copy. A copy is deleted when its post is unstarred.
- Server status (in the gear menu, or the top links in the Classic theme) shows what the server is doing: feed check progress, image downloads, totals, database sizes and recent activity. Its Check all feeds now button starts a check of every feed in the background.
- All items, starred items, folders, and New items/All items filters. Sort by newest or oldest. Search.
- Expanded and list views. In expanded view, items are marked read as they scroll past. Keep unread, mark all as read (including "older than a day/week/two weeks").
- Two themes: Classic (the light blue look from 2008 to 2011) and Modern (the 2011 redesign).
- Rename and unsubscribe from feeds. Unsubscribing keeps that feed's starred items. In Settings → Subscriptions, check several subscriptions (or Select: All after filtering) and use More actions… to unsubscribe or add them to or remove them from a folder.
- Trends: the most active subscriptions over the last 30 days, and the inactive ones (nothing new in 30 days), which you can select and unsubscribe from.
- Folders work like Reader's tags: a feed can be in several. In the sidebar, drag a feed onto a folder to move it there, onto "Subscriptions" to take it out of its folder, or between rows to arrange feeds and folders in your own order.
- The "Subscribe..." bookmarklet (Settings → Goodies) opens Reader with the page you're on ready to subscribe.
- Import and export subscriptions as OPML, and starred items as Google Takeout's `starred.json`. Starred items from feeds you no longer follow are kept without adding subscriptions.
- Reader's keyboard shortcuts: `j/k`, `n/p`, `o`, `s`, `m`, `v`, `shift+a`, `shift+j/k`, `shift+c` (collapse/expand all folders; not in the original), `r`, `u`, `1/2`, `g a`, `g s`, `/`, `?`.
