# pdf2png

A .NET 8 web app that converts PDFs to PNGs for **safe viewing** of
potentially malicious PDFs. The PDF is only ever *parsed and rasterized* —
Ghostscript runs with `-dSAFER` (blocks file/exec access and script execution
from PDF content). The UI is a small vanilla-JS single page (drag & drop,
thumbnail rail, prev/next page navigation, per-page hyperlink chips) that ships
as static files — nothing PDF-derived is ever executed.

## How it works

1. `GET /` — single-page app (static `wwwroot/`: index.html + app.css +
   app.js, vanilla JS, no framework, no build step). Upload card with
   drag & drop + dpi select.
2. `POST /convert` (JSON) — server validates the `%PDF` magic bytes, caps size
   (50 MB) and pages (100), then returns `{ "jobId": "…" }`.
3. Before rendering, **UglyToad.PdfPig** (pure-managed, parse-only) extracts:
   - all URI link annotations per page
   - any `/JavaScript` actions (parsed and shown, **never executed**)
   then Ghostscript renders:
   `gs -dNOPAUSE -dBATCH -dSAFER -dMaxBitmap=120000000 -sDEVICE=png16m -r<dpi> ...`
   with a 180 s hard timeout, and deletes the uploaded PDF (keeps PNGs only).
4. `GET /api/job/<jobId>` (JSON) — `{ jobId, pages[], links{page:[url]},
   javascript[], error }`; the SPA renders the viewer: thumbnail rail,
   big-page view with prev/next (+ ← → keys), per-page link chips, download,
   JS-warning banner.
5. `GET /view/<jobId>/page-N.png` — the image itself (`?dl=1` for download).

Rendered files auto-delete after 1 hour.

## Security notes

- **No PDF content is ever executed.** The UI's own static JS is allowed
  (CSP `default-src 'none'; script-src 'self'; style-src 'self';
  img-src 'self' data:; connect-src 'self'`), plus `X-Content-Type-Options:
  nosniff`, `frame-ancestors 'none'`, no-store caching.
- All server-rendered HTML is entity-escaped (job errors, titles, filenames).
- Ghostscript runs with `-dSAFER` (sandboxed file access, no
  `/exec`-class operators from PDF content, no JS/XFA execution) and a
  `-dMaxBitmap` cap to blunt billion-laughs-style page bombs.
- Job IDs are 32-char hex GUIDs; filenames regex-locked to `page-\d+.png`
  with a path-traversal check.
- Uploads are only accepted if the first 4 bytes are `%PDF`.
- Container runs as the non-root `app` user with `no-new-privileges`.

> Rendering is not the same as executing: Ghostscript rasterizes page
> operators and ignores script layers. This is the standard approach
> (same as browser "PDF preview" or `gs` on the CLI).

## Run

```bash
# with docker compose
docker compose up --build

# or plain docker
docker build -t pdftopng .
docker run --rm -p 8080:8080 --security-opt no-new-privileges pdftopng
```

Open <http://localhost:8080>.

## Local (no Docker)

Requires the .NET 8 SDK and Ghostscript on PATH.

```bash
dotnet run
```
