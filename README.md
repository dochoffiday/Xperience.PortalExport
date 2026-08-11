# Xperience.PortalExport

A .NET global tool that scrapes the Kentico Xperience Portal and exports outages, alerts, exceptions, and event log entries to JSON.

## Installation

```bash
dotnet tool install -g Xperience.PortalExport
```

On first run the tool will automatically download the Chromium browser it needs — no manual setup required.

## Usage

The portal login has a CAPTCHA, so the recommended flow is to save your session once and reuse it for all future exports.

**Step 1 — save your session (one time, or when it expires):**

```bash
export-xperience-portal --save-session
```

This opens a browser window. Log in manually (CAPTCHA and all), and once you're past the login page the session is saved automatically to `~/.xperience-portal/session.json`.

**Step 2 — run the export:**

```bash
export-xperience-portal
```

The saved session is loaded automatically. If it has expired, you'll be prompted to run `--save-session` again.

### Options

| Flag | Description |
|------|-------------|
| `--url` | Base URL of the Xperience Portal (default: `https://xperience-portal.com`) |
| `--save-session` | Open a browser, log in manually, and save the session |
| `--user` | Login email (only needed if logging in without a CAPTCHA) |
| `--pass` | Login password (only needed if logging in without a CAPTCHA) |
| `--environment` | Environment filter applied to each section (default: `PROD`) |
| `--months` | How many months back to export for a full run (default: `2`) |
| `--since` | Export only entries after this date (e.g. `2026-07-01`), overrides incremental detection |
| `--full` | Ignore any previous export and fetch the full `--months` window |
| `--output` | Directory to write the JSON file (default: `./xperience-export`) |
| `--headed` | Open a visible browser window — useful for debugging |
| `--verbose` | Log each step with timestamps |

Output is written to a timestamped file: `xperience-export/export-20260808-143000.json`

### Incremental exports

By default the tool runs incrementally. On each run it scans the output directory for the most recent `export-*.json` file, reads its `exportedAt` timestamp, and only fetches entries newer than that date. This keeps runs fast and avoids re-downloading data you already have.

A failed run leaves no output file, so the most recent file always represents a completed export — there is no risk of a partial run poisoning the incremental baseline.

Use `--full` to bypass this and re-fetch the entire `--months` window, or `--since <date>` to set the cutoff manually.

### Examples

```bash
# Default: incremental from last export, or last 2 months if no prior export exists
export-xperience-portal

# Force a full re-fetch of the last 6 months, QA environment
export-xperience-portal --full --months 6 --environment QA

# Fetch everything since a specific date
export-xperience-portal --since 2026-07-01

# Watch the browser while it runs
export-xperience-portal --headed --verbose
```

## How each section is scraped

| Section | Strategy |
|---------|----------|
| **Outages** | Iterates the last N calendar months using the month dropdown |
| **Alerts** | Single date range (N months back → today), page size 200, paginated |
| **Exceptions** | 7-day chunks over N months, limit 200 per chunk, opens the Details modal per row to capture stack traces |
| **Event log** | Same as Exceptions |

Exceptions and Event Log are chunked weekly because the portal enforces a 32-day maximum date range for those sections.

## Output format

```json
{
  "exportedAt": "2026-08-08T14:30:00Z",
  "outages": [
    { "Month": "August 2026", "From UTC": "...", "To UTC": "...", "Description": "..." }
  ],
  "alerts": [
    { "Fired UTC": "...", "Resolved": "...", "Severity": "Error", "Type": "...", "Description": "..." }
  ],
  "exceptions": [
    { "Date": "...", "Message": "...", "Stack trace": "...", "..." }
  ],
  "eventLog": [
    { "Date and time (UTC)": "...", "Event type": "Warning", "Source": "...", "Event name": "...", "..." }
  ]
}
```

Each section is a flat array of objects whose keys are the column headers from the portal table. Exceptions and Event Log rows also include any fields captured from the Details modal.

## Debugging

Run with `--headed --verbose` to watch the browser navigate each section and see timestamped progress per chunk and page. If a section isn't returning results, `--headed` lets you see exactly what the portal is showing after filters are applied.

## Development

```bash
git clone https://github.com/dochoffiday/Xperience.PortalExport
cd Xperience.PortalExport
dotnet build ExportXperiencePortal.csproj
```

**Run directly without installing:**

```bash
dotnet run --project ExportXperiencePortal.csproj -- --url https://xperience-portal.com --headed --verbose
```

**Pack and install globally from source:**

```bash
dotnet pack ExportXperiencePortal.csproj
dotnet tool install -g --add-source ./bin/Debug Xperience.PortalExport

# To update after making changes:
dotnet tool uninstall -g Xperience.PortalExport
dotnet pack ExportXperiencePortal.csproj
dotnet tool install -g --add-source ./bin/Debug Xperience.PortalExport
```
