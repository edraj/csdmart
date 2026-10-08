# End-to-end tests (Playwright)

Browser tests for the two embedded SPAs against a real dmart server with the
bundled sample spaces: cxb (`/cxb`) and the catalog (`/cat`). They cover what
the unit tests cannot — the pages actually rendering under Routify, sign-in,
navigation, the language and theme switches, dark mode, phone width — and
every test fails on an uncaught exception or an unexpected `console.error`,
which is how each regression caught in the manual browser walks showed up.

## Run locally

```sh
yarn --cwd cxb build && yarn --cwd catalog build   # the server embeds dist/
dotnet build dmart.csproj -c Release
yarn --cwd e2e playwright install chromium          # once
e2e/run-server.sh                                   # seeds SQLite, serves on :5399
yarn --cwd e2e test                                 # or test:headed
kill "$(cat e2e/.work/pid)"
```

`E2E_BASE_URL`, `E2E_PORT`, `E2E_ADMIN` and `E2E_PASSWORD` override the
defaults; `yarn --cwd e2e report` opens the last HTML report.

## In CI

The `e2e-playwright` job in `.github/workflows/ci.yml` builds both SPAs and
the server, runs `e2e/run-server.sh`, then the suite, and uploads the report
and traces when something fails.
