using Microsoft.Playwright;

namespace ExportXperiencePortal;

public class PortalScraper(IPage page, string environment, int months, bool verbose)
{
    private string _baseUrl      = "";
    private string _projectPath  = "";

    // Call this after login to extract the project GUID path from the current URL.
    // e.g. https://xperience-portal.com/fce3944c-d04d-4984-1b99-08dc62a6d0b2/dashboard
    //   → _baseUrl    = "https://xperience-portal.com"
    //   → _projectPath = "/fce3944c-d04d-4984-1b99-08dc62a6d0b2"
    public void Initialize(string currentUrl)
    {
        var uri      = new Uri(currentUrl);
        _baseUrl     = $"{uri.Scheme}://{uri.Host}";
        var segments = uri.AbsolutePath.TrimStart('/').Split('/');
        _projectPath = segments.Length > 0 ? "/" + segments[0] : "";
        Log($"Project path: {_projectPath}");
    }

    // ── Login ─────────────────────────────────────────────────────────────────

    // Attempts automated login. Not reliable when a CAPTCHA is present — use --save-session instead.
    public async Task LoginAsync(string username, string password)
    {
        await page.Locator("input[type='email'], input[placeholder*='mail']").First.FillAsync(username);

        var pwField = page.Locator("input[type='password']");
        if (await pwField.CountAsync() == 0)
        {
            // Some portals show email → click Continue → then password
            await page.Locator("button[type='submit']").ClickAsync();
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            pwField = page.Locator("input[type='password']");
        }

        await pwField.FillAsync(password);
        await page.Locator("button[type='submit']").ClickAsync();
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        var stillOnLogin = page.Url.Contains("auth.") || page.Url.Contains("/login") || page.Url.Contains("/signin");
        if (stillOnLogin)
            throw new InvalidOperationException("Login appeared to fail — still on the auth page. A CAPTCHA may be blocking automated login.");
    }

    // ── Public scrapers ───────────────────────────────────────────────────────

    // Outages are filtered by calendar month — iterate the last 6 months.
    public async Task<List<Dictionary<string, string>>> ScrapeOutagesAsync()
    {
        Log("Navigating to Outages");
        await page.GotoAsync(SectionUrl("outages"));
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        await SetEnvironmentAsync();

        var all          = new List<Dictionary<string, string>>();
        var monthSelect  = page.Locator("select:visible").Nth(1); // second visible combobox = Calendar month
        var monthOptions = await monthSelect.Locator("option").AllAsync();

        // Options are newest-first; take the first N months
        foreach (var option in monthOptions.Take(months))
        {
            var value = await option.GetAttributeAsync("value") ?? "";
            var label = (await option.InnerTextAsync()).Trim();
            Log($"  Month: {label}");

            await monthSelect.SelectOptionAsync(new SelectOptionValue { Value = value });
            await page.Locator("button[type='submit']").ClickAsync();
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

            var rows = await ScrapeTableAsync();
            foreach (var row in rows)
                row["Month"] = label;

            Log($"    {rows.Count} rows");
            all.AddRange(rows);
        }

        return all;
    }

    // Alerts use a date range + severities + page size + pagination.
    public async Task<List<Dictionary<string, string>>> ScrapeAlertsAsync()
    {
        Log("Navigating to Alerts");
        await page.GotoAsync(SectionUrl("alerts"));
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        await SetEnvironmentAsync();
        await SetDateRangeAsync(DateTime.UtcNow.AddMonths(-months), DateTime.UtcNow);
        await page.Locator("select:visible").Last.SelectOptionAsync("200"); // page size
        await page.Locator("button").Filter(new() { HasText = "Apply" }).First.ClickAsync();
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        return await ScrapeAllPagesAsync();
    }

    // Exceptions use a date range limited to 32 days — iterate 30-day chunks over 6 months.
    // Each row has a Details modal with stack trace.
    public async Task<List<Dictionary<string, string>>> ScrapeExceptionsAsync()
    {
        Log("Navigating to Exceptions");
        return await ScrapeInChunksAsync("exceptions", withDetails: true);
    }

    // Event log has the same 32-day limit as Exceptions.
    public async Task<List<Dictionary<string, string>>> ScrapeEventLogAsync()
    {
        Log("Navigating to Event log");
        return await ScrapeInChunksAsync("eventlog", withDetails: true);
    }

    // Iterates 7-day date windows to work around the portal's 32-day limit.
    private async Task<List<Dictionary<string, string>>> ScrapeInChunksAsync(string section, bool withDetails)
    {
        var all    = new List<Dictionary<string, string>>();
        var chunks = DateChunks(months: months, chunkDays: 7).ToList();
        var total  = chunks.Count;

        for (var i = 0; i < total; i++)
        {
            var (from, to) = chunks[i];

            ChunkProgress(i, total, from, to);
            Log($"  Chunk: {from:yyyy-MM-dd} → {to:yyyy-MM-dd}");

            await page.GotoAsync(SectionUrl(section));
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

            await SetEnvironmentAsync();
            await SetDateRangeAsync(from, to);
            await page.Locator("select:visible").Last.SelectOptionAsync("200");
            await page.Locator("button[type='submit']").ClickAsync();
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

            var rows = withDetails
                ? await ScrapeAllPagesWithDetailsAsync()
                : await ScrapeAllPagesAsync();

            Log($"    {rows.Count} rows this chunk");
            all.AddRange(rows);
        }

        // Finish the progress line
        if (!verbose && total > 0)
            Console.WriteLine($"\r  [{new string('█', 20)}] {total}/{total}  done{new string(' ', 25)}");

        return all;
    }

    // Prints an in-place progress bar (skipped when --verbose is on, since Log already shows chunks).
    private void ChunkProgress(int chunkIndex, int total, DateTime from, DateTime to)
    {
        if (verbose || total == 0)
            return;
        var filled = (int)Math.Round((double)chunkIndex / total * 20);
        var bar    = $"[{new string('█', filled)}{new string('░', 20 - filled)}]";
        Console.Write($"\r  {bar} {chunkIndex + 1}/{total}  {from:MMM d} – {to:MMM d}   ");
    }

    private static IEnumerable<(DateTime From, DateTime To)> DateChunks(int months, int chunkDays)
    {
        var end   = DateTime.UtcNow.Date;
        var start = end.AddMonths(-months);

        var chunkStart = start;
        while (chunkStart < end)
        {
            var chunkEnd = chunkStart.AddDays(chunkDays);
            if (chunkEnd > end)
                chunkEnd = end;
            yield return (chunkStart, chunkEnd);
            chunkStart = chunkEnd.AddDays(1);
        }
    }

    // ── Shared helpers ────────────────────────────────────────────────────────

    private string SectionUrl(string section) => $"{_baseUrl}{_projectPath}/{section}";

    // Selects the environment in the first <select> on the page.
    // The portal uses lowercase values ("prod", "qa").
    private async Task SetEnvironmentAsync()
    {
        var envSelect = page.Locator("select:visible").First;
        await envSelect.SelectOptionAsync(new SelectOptionValue { Value = environment.ToLower() });
        Log($"  Environment: {environment}");
    }

    private async Task SetDateRangeAsync(DateTime from, DateTime to)
    {
        var fromStr = from.ToString("yyyy-MM-dd") + " 00:00";
        var toStr   = to.ToString("yyyy-MM-dd")   + " 23:59";

        var inputs = page.Locator("input[type='text']");
        await FillDateInputAsync(inputs.Nth(0), fromStr);
        await FillDateInputAsync(inputs.Nth(1), toStr);

        Log($"  Date range: {fromStr} → {toStr}");
    }

    private async Task FillDateInputAsync(ILocator input, string value)
    {
        var handle = await input.ElementHandleAsync();
        await page.EvaluateAsync(
            "([el, dateStr]) => { if (el._flatpickr) el._flatpickr.setDate(dateStr, true); }",
            new object[] { handle!, value }
        );
    }

    // Scrapes all pages of the current table by clicking "next" until exhausted.
    private async Task<List<Dictionary<string, string>>> ScrapeAllPagesAsync()
    {
        var all     = new List<Dictionary<string, string>>();
        var pageNum = 1;

        while (true)
        {
            Log($"  Page {pageNum}");
            var rows = await ScrapeTableAsync();
            all.AddRange(rows);
            Log($"    {rows.Count} rows (total: {all.Count})");

            if (!await AdvancePageAsync())
                break;

            pageNum++;
        }

        return all;
    }

    // Same as ScrapeAllPagesAsync but also opens the Details modal for each row.
    private async Task<List<Dictionary<string, string>>> ScrapeAllPagesWithDetailsAsync()
    {
        var all     = new List<Dictionary<string, string>>();
        var pageNum = 1;

        while (true)
        {
            Log($"  Page {pageNum}");

            var rows = await ScrapeTableAsync();

            for (var i = 0; i < rows.Count; i++)
            {
                // Re-query each time — the portal may re-render the table after a modal closes,
                // which would make a pre-captured list of locators stale.
                var detailLink = page.Locator("table tbody tr td a").Nth(i);
                if (await detailLink.CountAsync() > 0)
                {
                    var details = await ScrapeDetailsModalAsync(detailLink);
                    foreach (var (k, v) in details)
                        rows[i][k] = v;
                }
            }

            all.AddRange(rows);
            Log($"    {rows.Count} rows (total: {all.Count})");

            if (!await AdvancePageAsync())
                break;

            pageNum++;
        }

        return all;
    }

    // Opens the Details modal for a row, scrapes all th→td pairs, then closes it.
    private async Task<Dictionary<string, string>> ScrapeDetailsModalAsync(ILocator detailLink)
    {
        var result = new Dictionary<string, string>();

        try
        {
            await detailLink.ClickAsync();

            // Wait for modal body to load (spinner disappears, content appears)
            await page.WaitForSelectorAsync(".modal.show .modal-body-content", new() { Timeout = 10_000 });
            await page.WaitForFunctionAsync("!document.querySelector('.modal-body-loading:not(.d-none)')");

            var modal = page.Locator(".modal.show .modal-body-content");

            // Scrape all label→value pairs from all tables in the modal
            var rows = await modal.Locator("table tr").AllAsync();
            foreach (var row in rows)
            {
                var headers = await row.Locator("th").AllInnerTextsAsync();
                var cells   = await row.Locator("td").AllInnerTextsAsync();

                for (var i = 0; i < Math.Min(headers.Count, cells.Count); i++)
                {
                    var key = headers[i].Trim();
                    if (!string.IsNullOrWhiteSpace(key))
                        result[key] = cells[i].Trim();
                }
            }

            // Stack traces (may be in <pre> or plain text)
            var stackTrace = await modal.Locator("pre, [class*='stack']").AllInnerTextsAsync();
            if (stackTrace.Count > 0)
                result["Stack trace"] = string.Join("\n---\n", stackTrace.Select(s => s.Trim()));
        }
        catch (Exception ex)
        {
            Log($"    [warn] Could not load details: {ex.Message}");
        }
        finally
        {
            // Close modal (Bootstrap dismiss)
            var closeBtn = page.Locator(".modal.show .btn-close");
            if (await closeBtn.CountAsync() > 0)
                await closeBtn.ClickAsync();
            await page.WaitForSelectorAsync(".modal.show", new() { State = WaitForSelectorState.Detached, Timeout = 5_000 });

            // Wait for the table to be present again in case the portal re-renders it after close
            await page.WaitForSelectorAsync("table tbody tr", new() { Timeout = 5_000 });
        }

        return result;
    }

    // Reads the first <table> on the page into a list of column→value dicts.
    private async Task<List<Dictionary<string, string>>> ScrapeTableAsync()
    {
        var rows    = new List<Dictionary<string, string>>();
        var headers = await page.Locator("table thead th").AllInnerTextsAsync();

        var dataRows = await page.Locator("table tbody tr").AllAsync();
        foreach (var row in dataRows)
        {
            var cells = await row.Locator("td").AllInnerTextsAsync();
            if (cells.Count == 0)
                continue;

            // Single colspan cell = empty-state message ("No events found…") — skip it
            if (cells.Count == 1 && await row.Locator("td[colspan]").CountAsync() > 0)
                continue;

            var dict = new Dictionary<string, string>();
            for (var i = 0; i < cells.Count; i++)
            {
                var key = i < headers.Count ? headers[i].Trim() : $"col_{i}";
                if (!string.IsNullOrWhiteSpace(key) && key != "Details")
                    dict[key] = cells[i].Trim();
            }

            if (dict.Count > 0)
                rows.Add(dict);
        }

        return rows;
    }

    // Clicks the "next page" arrow. Returns false when on the last page.
    private async Task<bool> AdvancePageAsync()
    {
        var nextItem = page.Locator("ul.pagination li.page-item:last-child");
        if (await nextItem.CountAsync() == 0)
            return false;

        var isDisabled = (await nextItem.GetAttributeAsync("class") ?? "").Contains("disabled");
        if (isDisabled)
            return false;

        await nextItem.Locator("a").ClickAsync();
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        return true;
    }

    private void Log(string message)
    {
        if (verbose)
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");
    }
}
