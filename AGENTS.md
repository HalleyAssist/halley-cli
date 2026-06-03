# AGENTS

## Core Dumps

- Treat any `core` or `core.*` file created during `dotnet build`, `dotnet test`, `dotnet publish`, or other project tooling as a real failure, even if the command itself exits successfully.
- Run compile and test commands through `scripts/run-with-core-dump-check.sh` so new core dumps fail fast and are listed in the error output.
- Do not disable core dumping, suppress the failure, or delete the dump before investigating it.
- Preserve the dump path, the exact command that produced it, and the surrounding stderr/stdout in your notes or PR description.
- Investigate the crash with the platform-native tools that are available, for example `file`, `gdb`, `lldb`, or `coredumpctl`, and summarize what you learned before moving on.
- If a core dump appears in CI, upload or retain the generated dump files and fix the underlying crash instead of papering over it.
