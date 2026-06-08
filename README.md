# halley-cli

The Halley Utility.

## Projects

- `Halley.App.Cli` - command-line entrypoint based on `System.CommandLine`
- `Halley.App.Main` - shared main application metadata and helpers
- `Halley.App.Api` - handwritten client for the supported Halley API surface
- `Halley.App.Tests` - xUnit coverage for CLI parsing, formatting, session persistence, and request mapping

## Session Storage

Successful `login user` and `login api-key` commands save the returned JWT to `~/.halley/session.json`.

Sessions are stored per normalized endpoint, so each Halley environment keeps its own token entry.

All authenticated commands reuse the token for the current `--endpoint` automatically unless `--token <jwt>` is supplied explicitly.

`login tokens` lists every locally saved token entry across endpoints.

`login edit` creates the session file if needed and opens it in the local system text editor.

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

`calls create` also supports interactive prompting. When it is invoked without any call-creation options, the CLI now opens a Consolonia-powered step-by-step terminal UI wizard by default and builds the request from your answers.

In the rich interactive dialogs, press `Ctrl+C` to cancel cleanly.

The rich interactive flow covers the `calls create` wizard and password prompting. If the richer UI cannot be used, the CLI falls back to the plain console prompt flow.

The plain console fallback still supports Tab-cycling autocomplete for single-line suggested values, with prefix matches preferred over broader contains matches.

`calls create --wait` and `calls status --wait` poll the request and matching call results until a result exists or the optional `--timeout` expires.

`calls create` and `calls status` also support `--delete`, which deletes the latest returned call result after it has been fetched. If no result is available yet, the command still succeeds and prints a warning on stderr.

Calls can only be created for organisations with an active Hotline license. The CLI validates organisation and template references before creating the call.

## Examples

```bash
dotnet run --project ./src/Halley.App.Cli -- version

dotnet run --project ./src/Halley.App.Cli -- login user --username alice

dotnet run --project ./src/Halley.App.Cli -- login tokens

dotnet run --project ./src/Halley.App.Cli -- login edit

dotnet run --project ./src/Halley.App.Cli -- users me --output json

dotnet run --project ./src/Halley.App.Cli -- users me --output json --log

dotnet run --project ./src/Halley.App.Cli -- organisations list

dotnet run --project ./src/Halley.App.Cli -- api-keys create \
  --organisation-id 1 \
  --permission ops_read_hubs \
  --permission ops_read_organisations \
  --expires-at 2026-06-01T00:00:00Z

dotnet run --project ./src/Halley.App.Cli -- calls create

dotnet run --project ./src/Halley.App.Cli -- calls create \
  --organisation "Acme Care" \
  --call-method phone \
  --phone-number +61400000000 \
  --recipient-name "Test User" \
  --recipient-timezone Australia/Melbourne \
  --template-uuid 2e1ef80b-6b2e-420f-a3df-6922baeea290 \
  --question 1:boolean:"Were you able to speak with the resident?" \
  --wait

dotnet run --project ./src/Halley.App.Cli -- calls status request-uuid --delete

dotnet run --project ./src/Halley.App.Cli -- calls status request-uuid --wait --timeout 2m --output json

dotnet run --project ./src/Halley.App.Cli -- calls results request-uuid
```
