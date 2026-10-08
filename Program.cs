using System.Diagnostics;
using System.Text;

var builder = WebApplication.CreateBuilder(args);

const long MaxUploadBytes = 50L * 1024 * 1024;   // 50 MB upload limit
const int MaxPages = 100;                          // pages to render
const int RenderTimeoutSeconds = 180;              // hard cap on ghostscript runtime
TimeSpan JobLifetime = TimeSpan.FromHours(1); // rendered files auto-purged

builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = MaxUploadBytes);

var app = builder.Build();

string jobsRoot = Path.Combine(AppContext.BaseDirectory, "jobs");
Directory.CreateDirectory(jobsRoot);

// ---------------------------------------------------------------------------
// Security headers: no JS is ever served or allowed.
// ---------------------------------------------------------------------------
app.Use(async (ctx, next) =>
{
    var h = ctx.Response.Headers;
    // The UI itself is our own static files (script/style from 'self' only);
// nothing PDF-derived is ever executed.
h["Content-Security-Policy"] = "default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; base-uri 'none'; frame-ancestors 'none'";
    h["X-Content-Type-Options"] = "nosniff";
    h["Referrer-Policy"] = "no-referrer";
    h["Cross-Origin-Opener-Policy"] = "same-origin";
    h["Cross-Origin-Resource-Policy"] = "same-origin";
    h["Cache-Control"] = "no-store";
    await next();
});

// ---------------------------------------------------------------------------
// Exception handler: log to container stderr and show the message in-browser.
// (This is an internal security tool; surfacing the message speeds diagnosis.)
// ---------------------------------------------------------------------------
app.Use(async (ctx, next) =>
{
    try
    {
        await next();
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine(ex);
        if (!ctx.Response.HasStarted)
        {
            ctx.Response.StatusCode = 500;
            ctx.Response.ContentType = "text/plain; charset=utf-8";
            await ctx.Response.WriteAsync("Server error: " + ex.Message + "\n\nSee container logs for stack trace.");
        }
    }
});

// ---------------------------------------------------------------------------
// Background cleanup of expired job folders.
// ---------------------------------------------------------------------------
_ = Task.Run(async () =>
{
    using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
    while (await timer.WaitForNextTickAsync())
    {
        try
        {
            var cutoff = DateTime.UtcNow - JobLifetime;
            foreach (var dir in Directory.EnumerateDirectories(jobsRoot))
            {
                if (Directory.GetCreationTimeUtc(dir) < cutoff)
                    Directory.Delete(dir, recursive: true);
            }
        }
        catch { /* best effort */ }
    }
});

// Static UI (index.html, app.css, app.js from wwwroot).
app.UseDefaultFiles();
app.UseStaticFiles();

// ---------------------------------------------------------------------------
// Routes
// ---------------------------------------------------------------------------

// Accept upload, render pages with Ghostscript (-dSAFER), return { jobId }.
app.MapPost("/convert", async (HttpContext ctx) =>
{
    if (!ctx.Request.HasFormContentType)
        return Results.Json(new { error = "Invalid request: multipart/form-data expected." }, statusCode: 400);

    var form = await ctx.Request.ReadFormAsync();
    if (form.Files.Count == 0)
        return Results.Json(new { error = "No file uploaded." }, statusCode: 400);

    var file = form.Files[0];
    if (file.Length == 0)
        return Results.Json(new { error = "Uploaded file is empty." }, statusCode: 400);
    if (file.Length > MaxUploadBytes)
        return Results.Json(new { error = "File too large (max 50 MB)." }, statusCode: 400);

    // Sanity check the magic bytes before handing anything to ghostscript.
    using (var probe = new MemoryStream())
    {
        await file.CopyToAsync(probe);
        var head = probe.ToArray();
        if (head.Length < 4 || !head.AsSpan(0, 4).SequenceEqual(new byte[] { (byte)'%', (byte)'P', (byte)'D', (byte)'F' }))
            return Results.Json(new { error = "File does not look like a PDF (missing %PDF header)." }, statusCode: 400);
    }

    int dpi = 150;
    if (form.ContainsKey("dpi")) int.TryParse(form["dpi"], out dpi);
    dpi = Math.Clamp(dpi, 50, 400);

    var jobId = Guid.NewGuid().ToString("N");
    var jobDir = Path.Combine(jobsRoot, jobId);
    Directory.CreateDirectory(jobDir);
    var pdfPath = Path.Combine(jobDir, "input.pdf");

    await using (var fs = File.Create(pdfPath))
        await file.CopyToAsync(fs);

    // Best-effort extraction of embedded links / JS indicators (parse-only, never executed).
    TryExtractLinks(pdfPath, jobDir);

    // Ghostscript expands %d to the zero-padded page number in -sOutputFile.
    var psPath = Path.Combine(jobDir, "page-%d.png");
    var psi = new ProcessStartInfo("gs")
    {
        UseShellExecute = false,
        RedirectStandardError = true,
        RedirectStandardOutput = true,
        CreateNoWindow = true
    };
    psi.ArgumentList.Add("-dNOPAUSE");
    psi.ArgumentList.Add("-dBATCH");
    psi.ArgumentList.Add("-dNOINTERACTIVE");
    psi.ArgumentList.Add("-dQUIET");
    psi.ArgumentList.Add("-dSAFER");                 // restrict PDF ops (no exec/JS)
    psi.ArgumentList.Add("-dMaxBitmap=120000000");   // cap ~4000x3000 px pages
    psi.ArgumentList.Add("-dFirstPage=1");
    psi.ArgumentList.Add($"-dLastPage={MaxPages}");
    psi.ArgumentList.Add("-sDEVICE=png16m");
    psi.ArgumentList.Add($"-r{dpi}");
    psi.ArgumentList.Add($"-sOutputFile={psPath}");
    psi.ArgumentList.Add(pdfPath);

    int exitCode = -1;
    string? gsError = null;
    try
    {
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start ghostscript.");
        var errTask = proc.StandardError.ReadToEndAsync();
        var outTask = proc.StandardOutput.ReadToEndAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(RenderTimeoutSeconds));
        try
        {
            await proc.WaitForExitAsync(cts.Token);
            exitCode = proc.ExitCode;
        }
        catch (OperationCanceledException)
        {
            proc.Kill(entireProcessTree: true);
            gsError = $"Render timed out after {RenderTimeoutSeconds}s.";
        }
        gsError ??= await errTask;
        _ = await outTask;
    }
    catch (Exception ex)
    {
        gsError = $"Failed to run ghostscript: {ex.Message}";
    }

    // gs exit codes: 0 = ok, 1 = error, 2 = resource, 3 = fatal, 4 = usage, 5 = interrupt, 9 = license
    if (exitCode != 0)
    {
        await File.WriteAllTextAsync(Path.Combine(jobDir, "error.txt"),
            $"ghostscript exit code {exitCode}{Environment.NewLine}{Environment.NewLine}{gsError}");
    }

    // Clean up the PDF source; keep only the PNGs.
    try { File.Delete(pdfPath); } catch { }

    // The job page surfaces render errors (error.txt) even when gs partially failed.
    return Results.Json(new { jobId });
});

// Job metadata for the SPA: rendered pages, per-page links, JS findings, errors.
app.MapGet("/api/job/{jobId}", (string jobId) =>
{
    if (!IsValidJobId(jobId))
        return Results.NotFound();

    var jobDir = Path.Combine(jobsRoot, jobId);
    if (!Directory.Exists(jobDir))
        return Results.NotFound();

    var pageNameRx = new System.Text.RegularExpressions.Regex(@"^page-\d+\.png$");
    var pages = Directory.EnumerateFiles(jobDir, "page-*.png")
        .Where(p => pageNameRx.IsMatch(Path.GetFileName(p)!))
        .OrderBy(p => int.Parse(Path.GetFileName(p)![5..^4]))
        .Select(Path.GetFileName)
        .ToList();

    var links = new Dictionary<string, List<string>>();
    var linksFile = Path.Combine(jobDir, "links.txt");
    if (File.Exists(linksFile))
    {
        foreach (var line in File.ReadAllLines(linksFile).Where(l => !string.IsNullOrWhiteSpace(l)))
        {
            var i = line.IndexOf('\t');
            var pg = i > 0 ? line[..i] : "?";
            var uri = i > 0 ? line[(i + 1)..] : line;
            if (!links.TryGetValue(pg, out var list))
                links[pg] = list = new List<string>();
            list.Add(uri);
        }
    }

    var jsFile = Path.Combine(jobDir, "javascript.txt");
    var javascript = File.Exists(jsFile)
        ? File.ReadAllLines(jsFile).Where(l => !string.IsNullOrWhiteSpace(l)).ToList()
        : new List<string>();

    var errorFile = Path.Combine(jobDir, "error.txt");
    var error = File.Exists(errorFile) ? File.ReadAllText(errorFile).Trim() : null;

    return Results.Json(new { jobId, pages, links, javascript, error });
});

// Serve a rendered PNG (or offer download via ?dl=1).
app.MapGet("/view/{jobId}/{fileName}", (string jobId, string fileName, HttpContext ctx) =>
{
    if (!IsValidJobId(jobId) || !System.Text.RegularExpressions.Regex.IsMatch(fileName, @"^page-\d+\.png$"))
        return Results.NotFound();

    var path = Path.GetFullPath(Path.Combine(jobsRoot, jobId, fileName));
    if (!path.StartsWith(Path.GetFullPath(jobsRoot), StringComparison.Ordinal) || !File.Exists(path))
        return Results.NotFound();

    var isDownload = ctx.Request.Query["dl"] == "1";
    return Results.File(
        path,
        "image/png",
        isDownload ? fileName : null);
});

app.Run();

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

// Parses the PDF with PdfPig to list URI link annotations and flag embedded JavaScript.
// Parse-only: nothing in the PDF is ever executed. Best-effort — failures never block rendering.
static void TryExtractLinks(string pdfPath, string jobDir)
{
    try
    {
        using var doc = UglyToad.PdfPig.PdfDocument.Open(pdfPath);
        var links = new StringBuilder();
        foreach (var page in doc.GetPages())
        {
            foreach (var hyperlink in page.GetHyperlinks())
            {
                var uri = hyperlink.Uri;
                if (!string.IsNullOrWhiteSpace(uri))
                    links.Append(page.Number).Append('\t').Append(uri).Append('\n');
            }
        }
        if (links.Length > 0)
            File.WriteAllText(Path.Combine(jobDir, "links.txt"), links.ToString());

        // JavaScript indicators: annotations carrying a /JavaScript action (parsed, never executed).
        var jsText = new StringBuilder();
        foreach (var page in doc.GetPages())
        {
            foreach (var annot in page.ExperimentalAccess.GetAnnotations())
            {
                var isJs = annot.Action?.Type == UglyToad.PdfPig.Actions.ActionType.JavaScript;
                var dictStr = annot.AnnotationDictionary?.ToString() ?? "";
                if (!isJs) isJs = dictStr.Contains("/JavaScript");
                if (isJs)
                    jsText.Append("Page ").Append(page.Number).Append(": JavaScript action (NOT executed): ").Append(dictStr.Trim()).Append('\n');
            }
        }
        if (jsText.Length > 0)
            File.WriteAllText(Path.Combine(jobDir, "javascript.txt"), jsText.ToString());
    }
    catch (Exception ex)
    {
        File.WriteAllText(Path.Combine(jobDir, "links-error.txt"), "Link extraction failed: " + ex.Message);
    }
}

static bool IsValidJobId(string id) =>
    id.Length == 32 && id.All(Uri.IsHexDigit);
