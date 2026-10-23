# Changelog

All notable changes to Reader Reborn. Versions follow [semantic versioning](https://semver.org).

## [1.0.0] - 2026-10-23

READER IS REBORN! The first release.

### Reading
- Both of Google Reader's looks: Classic (the light blue one from 2008) and Modern (the 2011 redesign).
- Expanded and list views. In expanded view, posts are marked read as they scroll past.
- Star, Keep unread, Mark all as read (also "older than a day / week / two weeks"), New items / All items, sort by newest or oldest, and search.
- Reader's keyboard shortcuts, plus `shift+c` to collapse or expand all folders.
- Trends: the most active subscriptions over the last 30 days, and the inactive ones.
- The "Subscribe..." bookmarklet (Settings → Goodies).

### Organising
- Folders that work like Reader's tags: a feed can be in several.
- Drag and drop in the sidebar to move feeds between folders, or to arrange feeds and folders in your own order.
- Bulk unsubscribe and filing from Settings → Subscriptions, and from the Inactive list in Trends.

### Keeping
- Posts are kept after they drop out of a feed.
- Post images are saved in the background, so posts keep their pictures if the original site goes away.
- Podcast and video attachments play in the post. Optionally, the attachments of starred posts are saved too.
- Import subscriptions (OPML) and starred items (`starred.json`) from a Google Takeout, and export both.

### Running it
- A single-file .NET 10 backend, a Vue 3 frontend, and two SQLite files: `reader.db`, and `reader-media.db` for saved images and attachments, each stored once by its SHA-256.
- Reads RSS 0.9x / 1.0 / 2.0, Atom and Media RSS, and finds a site's feed from its address.
- Server status: feed check progress, image and attachment downloads, totals, database sizes and recent activity, with a Check all feeds now button.
- A container image for Docker and Podman, for Intel/AMD and ARM.

[1.0.0]: https://github.com/mika76/reader-reborn/releases/tag/v1.0.0
