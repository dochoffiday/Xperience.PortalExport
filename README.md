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
export-xperience-portal --url https://portal.xperience-portal.com --save-session
```

This opens a browser window. Log in manually (CAPTCHA and all), and once you're past the login page the session is saved automatically to `~/.xperience-portal/session.json`.

**Step 2 — run the export:**

```bash
export-xperience-portal --url https://portal.xperience-portal.com
```

The saved session is loaded automatically. If it has expired, you'll be prompted to run `--save-session` again.

### Options

| Flag | Description |
|------|-------------|
| `--url` | **(required)** Base URL of the Xperience Portal |
| `--save-session` | Open a browser, log in manually, and save the session |
| `--user` | Login email (only needed if logging in without a CAPTCHA) |
| `--pass` | Login password (only needed if logging in without a CAPTCHA) |
| `--environment` | Environment filter applied on each tab (default: `PROD`) |
| `--output` | Directory to write the JSON file (default: `./xperience-export`) |
| `--headed` | Open a visible browser window — useful for debugging |
| `--verbose` | Log each step with timestamps |

Output is written to a timestamped file: `xperience-export/export-20260808-143000.json`

## Output format

```json
{
  "exportedAt": "2026-08-08T14:30:00Z",
  "outages": [
    { "Date": "2026-08-01", "Status": "Resolved", "..." }
  ],
  "alerts": [ ... ],
  "exceptions": [ ... ],
  "eventLog": [ ... ]
}
```

Each section is a flat array of objects whose keys are the column headers from the portal table.

## Debugging

Run with `--headed --verbose` to watch the browser navigate each section and see timestamped progress. If a section fails to scrape, the `--headed` mode lets you inspect the page while the tool is running.

## Development

```bash
git clone https://github.com/dochoffiday/Xperience.PortalExport
cd Xperience.PortalExport
dotnet build
```

**Run directly without installing:**

```bash
dotnet run -- --url https://portal.xperience.io --user you@example.com --pass yourpassword --headed
```

**Pack and install globally from source:**

```bash
dotnet pack
dotnet tool install -g --add-source ./bin/Debug Xperience.PortalExport

# To update after making changes:
dotnet tool uninstall -g Xperience.PortalExport
dotnet pack
dotnet tool install -g --add-source ./bin/Debug Xperience.PortalExport
```
