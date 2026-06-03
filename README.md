# halley-cli

The Halley Utility.

## Projects

- `Halley.App.Cli` - command-line entrypoint based on `System.CommandLine`
- `Halley.App.Main` - shared main application metadata and helpers
- `Halley.App.Api` - handwritten client for the supported Halley API surface
- `Halley.App.Tests` - xUnit coverage for CLI parsing, formatting, session persistence, and request mapping

## Session Storage

Successful `login user` and `login api-key` commands save the returned JWT to `~/.halley/session.json`.

All authenticated commands reuse that token automatically unless `--token <jwt>` is supplied explicitly.

## Output Modes

All commands support `--output human|json`.

- `human` is the default
- `json` emits machine-readable JSON
- login commands print the raw token in `human` mode and `{ "token": "..." }` in `json` mode

All commands also support `--endpoint`, which defaults to `https://cloud.halleyassist.com` and accepts inputs such as `halleyassist.com`, `cloud.halleyassist.com`, or a full URL like `https://cloud.halleyassist.com`.

All commands also support `--log [level]`. Logs are written to stderr so stdout stays safe for scripts.

- the default log level is `warning`
- use `--log` by itself for `info`
- accepted levels are `trace`, `debug`, `info`, `warning`, `error`, `fatal`, and `none`
- `info` includes the response dump with headers followed by the body
- `debug` adds a curl-style request/response trace with headers and bodies on stderr, with sensitive values redacted

`login user` accepts `--password`, but if it is omitted the CLI prompts for the password interactively.

## Examples

```bash
dotnet run --project ./src/Halley.App.Cli -- version

dotnet run --project ./src/Halley.App.Cli -- login user --username alice

dotnet run --project ./src/Halley.App.Cli -- users me --output json

dotnet run --project ./src/Halley.App.Cli -- users me --output json --log

dotnet run --project ./src/Halley.App.Cli -- organisations list

dotnet run --project ./src/Halley.App.Cli -- api-keys create \
  --organisation-id 1 \
  --permission ops_read_hubs \
  --permission ops_read_organisations \
  --expires-at 2026-06-01T00:00:00Z
```
