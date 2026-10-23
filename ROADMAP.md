# Roadmap

Where Reader Reborn is going after 1.0. Each release has a theme. Each feature says what it is, why it matters, how it fits the code, and when it counts as done. Sizes are rough: **S** is an evening, **M** a weekend, **L** longer.

Some rules carry through every release:

- **Faithful first.** Anything Google Reader had should look and work like it did. Anything new is marked as not in the original, as `shift+c` is today.
- **Simple to run.** One C# file for the server, one Vue app, two SQLite files and one container. New dependencies have to earn their place.
- **Your data stays yours.** Everything stays in `reader.db` and `reader-media.db`. No outside services are required.

---

## 1.1: Foundations

Before adding the big features, make changes safe to make.

### Automated tests (M)
**Why:** there are no tests yet. Everything so far was checked by hand, which won't hold up once other people contribute.
**How:**
- **Server:** an API test project that starts `reader.cs` against a temporary database and checks the endpoints: subscribe, items, mark read, folders, OPML and starred.json round trips, image cleanup.
- **Feed parser:** a folder of sample feeds (RSS 0.9x/1.0/2.0, Atom, Media RSS, broken dates, relative links) with the expected results.
- **Frontend:** Vitest for `store.js` and `format.js`, plus a smoke test in a headless browser, reusing the screenshot script's approach.

**Done when:** `Build` in GitHub Actions runs all of it and blocks a merge when something fails.

### Version in one place (S)
**Why:** releasing means updating `AppInfo.Version` and `web/package.json` by hand.
**How:** read the version from the git tag at build time, or from a single `VERSION` file, and pass it to both.
**Done when:** releasing is "add a changelog section and push a tag".

---

## 1.2: Everywhere

Use Reader Reborn on your phone and in your favourite apps, safely.

### Sign-in (M)
**Why:** today anyone who can reach the server can use it, so putting it on the internet isn't safe. Sync (below) needs it anyway.
**How:**
- On first run, ask for a password, stored as a PBKDF2 hash in the `settings` table.
- Sign-in uses a cookie, with a sign-in page in Reader's style.
- `READER_PASSWORD` can set the password for containers.
- A setting allows the current open mode for use on a trusted network only.
- Everything under `/api` requires sign-in, except the endpoints sync clients use, which have their own tokens.

**Done when:** a fresh install asks for a password, and nothing loads without signing in.

### Sync with apps: the Google Reader API (L)
**Why:** most feed reader apps can sync with servers that speak the old Google Reader API, including Reeder, NetNewsWire, ReadKit, FeedMe and Read You. With it, you read on your phone and the unread counts stay in step everywhere.
**How:**
- Add the endpoints as FreshRSS and Miniflux do:
  - `accounts/ClientLogin`, `reader/api/0/token`, `user-info`
  - `subscription/list`, `subscription/edit`, `subscription/quickadd`
  - `tag/list`, `unread-count`
  - `stream/contents`, `stream/items/ids`, `stream/items/contents`
  - `edit-tag` (read / starred), `mark-all-as-read`
- Item IDs are the long `tag:google.com,2005:reader/item/<hex>` form of our numeric IDs.
- Folders become labels.
- Authentication uses an app password from Settings, sent as `GoogleLogin auth=` tokens.

**Done when:** NetNewsWire and Read You can add the server, list subscriptions and folders, read, star and mark as read, and the web app reflects it.

### Phone-sized layout (M)
**Why:** the interface is desktop-only, just as Reader's web version was.
**How:**
- Below about 760px wide, the sidebar becomes a slide-in drawer, list view is the default, and the toolbar collapses into a menu.
- Posts open full screen, with large previous/next buttons.
- Both themes keep their look.

**Done when:** reading, starring and subscribing all work comfortably on a phone.

---

## 1.3: Keyboard and Home

Finish the parts of Reader that are still missing.

### Keyboard navigation in the sidebar (S)
**Why:** Reader had a separate cursor for the subscription tree, and with hundreds of feeds it's the fastest way around.
**How:**
- `shift+n` / `shift+p` move a cursor through the tree without opening anything, `shift+o` opens the item under it, and `shift+x` expands or collapses a folder.
- The cursor is drawn the way Reader drew it, and follows "reveal in tree" (open the folder, scroll to the row).

**Done when:** every row can be reached and opened without the mouse.

### The "go to" box (S)
**Why:** Reader's `g u` and `g t` popped up a box to type part of a feed or folder name and jump to it.
**How:**
- A small overlay with a text box and a filtered, keyboard-selectable list.
- `g u` searches subscriptions and `g t` searches folders. Settings moves from `g t` to a new key.

**Done when:** with 1,000+ feeds, any one is a few keystrokes away.

### Home page (M)
**Why:** Reader opened on Home: a few fresh items from your busiest feeds, your recent stars, and quick links.
**How:**
- A new `#home` route, with sections built from the existing APIs plus a small `/api/home` summary.
- Choose between Home and All items as the start page in Preferences.

**Done when:** Home looks and reads like Reader's, in both themes.

### Dark theme (S)
**Why:** often requested. It's not in the original, so it's marked as an addition.
**How:** a third theme that reuses the Modern layout with dark colours, and optionally follows the system setting.
**Done when:** every screen, dialog and menu reads well in it, screenshots included.

---

## 1.4: Read it all

Get the whole story, whatever the feed sends.

### Full article text (M)
**Why:** many feeds send only a summary or a link (Hacker News, Deadline, /Film). It's the most common complaint about feeds.
**How:**
- A **Load full article** action on a post, and a per-feed "always load full articles" setting.
- The server fetches the page and extracts the article with a Readability port such as SmartReader.
- The result is stored alongside the post, and its pictures go through the existing image saving.

**Done when:** a Hacker News post shows the linked article, pictures included, without leaving Reader Reborn.

### Embedded videos (S)
**Why:** YouTube and Vimeo embeds are currently removed from posts.
**How:** keep `<iframe>`s from an allowlist (youtube-nocookie.com, player.vimeo.com) as a click-to-play placeholder, so nothing loads until you ask. DOMPurify gets a matching rule.
**Done when:** a post with a video shows a play button that plays it.

### Better image saving (S)
**Why:** for pictures with several sizes (`srcset`, `<picture>`), only the default is saved. For lazy-loaded ones (`data-src`), the placeholder is saved instead of the real picture.
**How:** when scanning a post, pick the largest reasonable `srcset` candidate, and prefer `data-src`/`data-lazy-src` when `src` is a placeholder. Rewrite the saved copy into `src`.
**Done when:** the posts that used these patterns show their real pictures from saved copies.

### More feed formats and clearer errors (S)
**Why:** JSON Feed isn't supported, and when a feed fails you only see the error text in the sidebar's tooltip.
**How:**
- Parse JSON Feed 1.0 and 1.1 in `FeedParser`.
- A failing feed's page shows what went wrong, when it last worked, and a **Try again now** button.

**Done when:** JSON Feeds subscribe, and a broken feed explains itself.

---

## 1.5: Keep it tidy

Run it for years without thinking about it.

### Keep storage in check (M)
**Why:** saved images grow without limit (2.6 GB for one person after a day).
**How:**
- A Preferences setting: "delete read, unstarred posts after 3 / 6 / 12 months / never".
- The existing clean-up then removes their images and attachments.
- Server status shows what was freed.

**Done when:** storage levels off instead of growing forever, and starred posts are never touched.

### Automatic backups (S)
**Why:** two SQLite files are easy to back up, but only if someone remembers to.
**How:**
- A nightly `VACUUM INTO` snapshot of both databases plus an OPML export, written to `READER_BACKUP_DIR` (default `data/backups`).
- Keep the last 7.
- Shown in Server status.

**Done when:** a week of backups exists without anyone doing anything, and one restores cleanly.

### Instant updates with WebSub (M)
**Why:** Reader used PubSubHubbub, now called WebSub, so blogs that support it showed new posts within seconds.
**How:**
- When a feed advertises a hub (`<link rel="hub">`) and `READER_PUBLIC_URL` is set, subscribe to it with a callback endpoint that verifies and stores pushed updates.
- Otherwise keep checking every 30 minutes.

**Done when:** a WebSub-enabled blog's new post appears without waiting for a check.

### Filters (M)
**Why:** mute topics you never read, or highlight the ones you always do. This is new, not from the original.
**How:**
- A `filters` table of "if the title, author or text contains … then mark read / star / add to folder" rules, applied as posts are stored.
- Managed in a Settings tab.

**Done when:** a filter set up in Settings affects new posts as they arrive.

---

## 2.0: Together

The social side of Reader, and more than one person per server.

### Multiple users (L)
**Why:** families and friends sharing one server, each with their own subscriptions.
**How:**
- Users and per-user subscriptions, folders, read state and stars.
- Feeds, posts, images and attachments are fetched and stored once and shared between users.
- A migration moves the existing single user's data to the first account.

**Done when:** two people on one server see only their own subscriptions and read state, and each feed is downloaded once.

### Tags on posts, notes and shared items (M)
**Why:** Reader's "Edit tags", "Share" and "Your stuff". The social network is gone, but a public page of things you recommend is still useful.
**How:**
- Tags on individual posts (`item_tags`), shown as their own streams.
- A note on any post.
- **Share** adds the post to a public `/shared` page and `/shared.atom` feed that others can subscribe to.

**Done when:** you can tag a post, find it by tag, and share it to a public page and feed.

---

## Smaller ideas

- **Send to read-later services**: Pocket-style alternatives, Wallabag, email with the full text.
- **Feed discovery in Subscribe**: search by name, not just address, using the feeds other users of the server follow.
- **Per-feed settings**: check interval, full text, and image saving on or off.
- **Notifications**: a browser notification for new posts in chosen feeds.

Ideas and pull requests are welcome. Open an issue to discuss one first, so the work fits the plan above.
