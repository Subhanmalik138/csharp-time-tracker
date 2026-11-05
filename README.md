# TimeLedger — CLI Time Tracker for Freelancers

Track billable hours from the terminal: clients, projects, a start/stop timer,
manual entries, and invoices you can hand to a client. No account, no cloud —
your data lives in one JSON file on your machine.

## The problem

Freelancers bill by the hour, but most time trackers are either a SaaS
subscription or a spreadsheet that drifts out of date. TimeLedger is the
middle ground: a single binary, a plain-text data file you own, and just
enough structure to turn "what did I work on this week?" into an invoice.

## Quick start

```bash
# Build
dotnet build -c Release

# Or run straight from source
dotnet run --project src/TimeLedger -- client add Acme --rate 50
```

All data goes to `~/.timeledger/data.json` by default; pass `--data <path>`
before any command (or set `TIMELEDGER_DATA`) to use another file.

## Usage

```bash
# Clients and projects — a project inherits its client's rate unless overridden
timeledger client add Acme --rate 50
timeledger project add Acme Website --rate 60
timeledger project add Acme Support            # bills at the client rate, 50/h

# The timer — one running timer at a time
timeledger start Website --desc "Landing page" --tags design,frontend
timeledger status
timeledger stop

# Log work after the fact (e.g. a call you forgot to time)
timeledger log Website "2026-11-05 09:00" "2026-11-05 11:30" --desc "Landing page"
timeledger log Website "2026-11-05 13:00" "2026-11-05 14:00" --desc "Bug fixes" --no-bill

# See what you did, and what it's worth
timeledger entries --from 2026-11-01 --to 2026-11-07
timeledger report --week
timeledger report --client Acme --month
timeledger invoice --client Acme --month --csv invoice-nov.csv
```

Projects with the same name under different clients are addressed as
`Client/Project`, e.g. `timeledger start Globex/Website`.

## Design decisions

- **One JSON document, written atomically.** The whole log is a single file,
  saved by writing a temp file and moving it over the old one, so a crash
  mid-save can never corrupt your hours. No database to install or back up
  separately — copy the file and you have a backup.
- **Billing rate resolution is explicit.** A project's rate overrides its
  client's; otherwise the client rate applies. Non-billable entries are
  tracked (they still show up in reports) but never contribute to an amount,
  so "I did an hour of free bug fixes" stays visible without inflating the
  invoice.
- **Manual entries can't overlap the running timer or each other** on the
  same project. Double-billing the same hour is the one mistake a time
  tracker must make impossible.
- **Zero dependencies.** The CLI is hand-rolled parsing over the BCL only,
  so the tool builds anywhere .NET 8 runs and stays small.

## Running the tests

```bash
dotnet test
```

20 xUnit tests cover the timer rules (one timer at a time, stop with none
running), project resolution and rate fallback, billing math (billable vs
non-billable, rate overrides), CSV quoting, and JSON store round-trips.

## Tech stack

- C# / .NET 8, no third-party packages
- xUnit for tests
- `System.Text.Json` for storage
