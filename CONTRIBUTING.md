# Contributing to BoomNetwork

Thanks for your interest in contributing! Here's how to get started.

## Development Setup

```bash
# Clone
git clone https://github.com/boomlulu/BoomNetwork.git
cd BoomNetwork

# Go server
cd svr && go test ./...

# C# client
cd cli && dotnet test

# Run server locally
cd svr && go run ./cmd/framesync/ -config cmd/framesync/config.yaml
```

## Project Structure

| Directory | Language | What |
|-----------|----------|------|
| `svr/` | Go | Frame-sync server |
| `cli/` | C# | Client library |
| `unity/com.boom.boomnetwork/` | C# | Unity UPM package (mirrors cli/) |
| `doc/` | Markdown | Documentation |

## Making Changes

1. Fork the repo and create a branch from `dev1.0`
2. Make your changes
3. Add tests for new functionality
4. Run the full test suite:
   ```bash
   make test    # or: cd svr && go test ./... && cd ../cli && dotnet test
   ```
5. Open a PR against `dev1.0`

## Important Rules

- **cli/ and unity/ must stay in sync** — if you change a `.cs` file in `cli/`, copy it to the matching path under `unity/com.boom.boomnetwork/Runtime/`
- **No rollback in core** — see [Core Philosophy](doc/shared/04-core-philosophy.md)
- **Silent When Idle** — don't add code that sends packets when there's no player input
- **Zero-allocation hot path** — avoid allocations in frame encode/decode/broadcast

## Code Style

- Go: standard `gofmt`, checked by `golangci-lint` in CI
- C#: standard .NET conventions

## Commit Messages

Format: `type: short description`

Types: `feat`, `fix`, `test`, `docs`, `refactor`, `perf`, `ci`, `chore`

## Reporting Issues

Use the [issue templates](https://github.com/boomlulu/BoomNetwork/issues/new/choose) to report bugs or request features.

## License

By contributing, you agree that your contributions will be licensed under the MIT License.
