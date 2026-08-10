using System.Text.Json;
using ExportXperiencePortal;
using Microsoft.Playwright;

// ── Argument parsing ──────────────────────────────────────────────────────────

string  url         = "https://xperience-portal.com";
string? username    = null;
string? password    = null;
string  output      = "xperience-export";
string  environment = "PROD";
int     months      = 2;
bool    saveSession = false;
bool    headed      = false;
bool    verbose     = false;

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--url":          url = args[++i];                       break;
        case "--user":         username    = args[++i];              break;
        case "--pass":         password    = args[++i];              break;
        case "--output":       output      = args[++i];              break;
        case "--environment":  environment = args[++i];              break;
        case "--months":       months      = int.Parse(args[++i]);   break;
        case "--save-session": saveSession = true;                   break;
        case "--headed":       headed      = true;                   break;
        case "--verbose":      verbose     = true;                   break;
    }
}

// Session file lives in ~/.xperience-portal/session.json
var sessionDir  = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".xperience-portal");
var sessionPath = Path.Combine(sessionDir, "session.json");
var hasSession  = File.Exists(sessionPath);

if (!saveSession && !hasSession && (username is null || password is null))
{
    Console.Error.WriteLine("No saved session found. Run with --save-session first, or provide --user and --pass.");
    return 1;
}

// ── Playwright browser install (no-op if already installed) ───────────────────

Console.WriteLine("Checking Playwright browsers...");
Microsoft.Playwright.Program.Main(["install", "chromium"]);

// ── Browser setup ─────────────────────────────────────────────────────────────

var forceHeaded = saveSession || headed;

using var playwright = await Playwright.CreateAsync();
await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
{
    Headless = !forceHeaded,
    SlowMo   = forceHeaded ? 100 : 0,
});

var contextOptions = new BrowserNewContextOptions
{
    ViewportSize = new ViewportSize { Width = 1400, Height = 900 },
};

if (hasSession && !saveSession)
{
    Console.WriteLine($"Loading saved session from {sessionPath}");
    contextOptions.StorageStatePath = sessionPath;
}

await using var context = await browser.NewContextAsync(contextOptions);
var page = await context.NewPageAsync();

// ── Save-session flow ─────────────────────────────────────────────────────────

if (saveSession)
{
    Console.WriteLine("Opening portal — please log in manually in the browser window.");
    await page.GotoAsync(url);

    // Wait until the browser leaves the auth/login domain (up to 5 minutes)
    await page.WaitForURLAsync(
        u => !u.Contains("auth.") && !u.Contains("/login") && !u.Contains("/signin"),
        new PageWaitForURLOptions { Timeout = 300_000 }
    );

    await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

    Directory.CreateDirectory(sessionDir);
    await context.StorageStateAsync(new BrowserContextStorageStateOptions { Path = sessionPath });

    Console.WriteLine($"Session saved to {sessionPath}");
    Console.WriteLine("You can now run the export without --save-session.");
    return 0;
}

// ── Verify session / fall back to automated login ─────────────────────────────

var scraper = new PortalScraper(page, environment, months, verbose || forceHeaded);

await page.GotoAsync(url);
await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

var isOnLoginPage = page.Url.Contains("auth.") || page.Url.Contains("/login") || page.Url.Contains("/signin");

if (isOnLoginPage)
{
    if (hasSession)
    {
        Console.WriteLine("Saved session has expired. Run with --save-session to refresh it.");
        // Delete stale session so the next run doesn't try to load it
        File.Delete(sessionPath);
    }

    if (username is null || password is null)
    {
        Console.Error.WriteLine("Cannot log in automatically — no --user/--pass provided and session is expired.");
        return 1;
    }

    Console.WriteLine("Logging in...");
    try
    {
        await scraper.LoginAsync(username, password);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Login failed: {ex.Message}");
        Console.Error.WriteLine("If there is a CAPTCHA, run with --save-session to log in manually.");
        return 1;
    }
}
else
{
    Console.WriteLine("Session valid — skipping login.");
}

scraper.Initialize(page.Url);

// ── Scrape ────────────────────────────────────────────────────────────────────

Console.WriteLine($"Environment: {environment}");

Console.WriteLine("Scraping Outages...");
var outages = await scraper.ScrapeOutagesAsync();

Console.WriteLine("Scraping Alerts...");
var alerts = await scraper.ScrapeAlertsAsync();

Console.WriteLine("Scraping Exceptions...");
var exceptions = await scraper.ScrapeExceptionsAsync();

Console.WriteLine("Scraping Event Log...");
var eventLog = await scraper.ScrapeEventLogAsync();

// ── Persist refreshed session ─────────────────────────────────────────────────

Directory.CreateDirectory(sessionDir);
await context.StorageStateAsync(new BrowserContextStorageStateOptions { Path = sessionPath });

// ── Write output ──────────────────────────────────────────────────────────────

Directory.CreateDirectory(output);

var result = new ExportResult(
    ExportedAt:  DateTimeOffset.UtcNow,
    Outages:     outages,
    Alerts:      alerts,
    Exceptions:  exceptions,
    EventLog:    eventLog
);

var json = JsonSerializer.Serialize(result, new JsonSerializerOptions
{
    WriteIndented        = true,
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
});

var outFile = Path.Combine(output, $"export-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json");
await File.WriteAllTextAsync(outFile, json);

Console.WriteLine();
Console.WriteLine($"Done. {outages.Count} outages, {alerts.Count} alerts, {exceptions.Count} exceptions, {eventLog.Count} event log entries.");
Console.WriteLine($"Output: {outFile}");

return 0;
