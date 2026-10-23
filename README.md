# Reader

A remake of Google Reader as it looked from the 2011 redesign until it shut down in July 2013.

- `server/reader.cs`: the backend, a single-file .NET 10 app with a shebang. It downloads feeds, stores everything in SQLite and serves the JSON API and the built frontend.
- `web/`: the Vue 3 + Vite frontend.
- `data/reader.db`: the SQLite database. It is created on first run and is gitignored.

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
./server/reader.cs import subscriptions.opml    # e.g. subscriptions.xml from a Google Takeout
./server/reader.cs fetch                        # fetch every feed once and exit (for cron)
```

Environment variables: `READER_DB` (database path), `READER_PORT` (default 5199), `READER_INTERVAL` (minutes between fetches of a feed, default 30).

## Features

- RSS 0.9x/2.0, RSS 1.0 (RDF), Atom and YouTube/Media RSS feeds. Feeds are found from a site's `<link rel="alternate">` or common feed paths.
- Conditional GET (ETag/Last-Modified), parallel fetching, and backoff for feeds that fail. Favicons are fetched and stored once per feed.
- All items, starred items, folders, and New items/All items filters. Sort by newest or oldest. Search.
- Expanded and list views. In expanded view, items are marked read as they scroll past. Keep unread, mark all as read (including "older than a day/week/two weeks").
- Rename and unsubscribe from feeds. Folders, including dragging a feed onto a folder in the sidebar. OPML import and export.
- Reader's keyboard shortcuts: `j/k`, `n/p`, `o`, `s`, `m`, `v`, `shift+a`, `shift+j/k`, `r`, `u`, `1/2`, `g a`, `g s`, `/`, `?`.
