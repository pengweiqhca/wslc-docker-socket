# wslc-docker-socket

`wslc-docker-socket` exposes a Docker Remote API surface over a Windows named pipe (or loopback TCP), backed by [Microsoft WSL Containers (WSLC)](https://learn.microsoft.com/windows/wsl/containers/). It lets ordinary Docker clients — Docker CLI, Docker.DotNet, Testcontainers .NET, Portainer, container tooling in editors — manage Linux containers on Windows without a Docker daemon.

> [!IMPORTANT]
> WSLC and this adapter are both experimental. Treat this as a local development tool.

> [!NOTE]
> Verified against **WSLC 2.9.11**. WSLC's CLI output is still changing between releases, so re-verify after upgrading.
>
> The service declares Docker API **1.43** (`MinAPIVersion` 1.24) and accepts any `/v{major}.{minor}` prefix, so clients negotiating newer versions (1.44, 1.51, …) still work.

**The design rule:** every endpoint either maps faithfully onto a real WSLC capability, or returns an explicit Docker-style error explaining why it cannot. The adapter never fabricates data or reports a false success. Most of this document is about where that line falls.

---

## Quick Start

```pwsh
dotnet run --project WslcDockerSocket/WslcDockerSocket.csproj --configuration Release
```

The default listener is the named pipe `\\.\pipe\docker_engine` — the same pipe Docker Desktop uses — so clients that look there by default need no configuration:

```pwsh
docker ps
docker images
```

If you change the pipe name, point clients at it explicitly:

```pwsh
$env:DOCKER_HOST = 'npipe://./pipe/my-pipe-name'
```

---

## Key Features

- **Drop-in pipe name.** Listens on Docker's conventional named pipe by default; loopback TCP is opt-in.
- **Container lifecycle.** Create, start, stop, wait, delete, list, inspect, logs, exec, and archive upload.
- **Real image metadata.** Image listings report exact byte sizes and creation timestamps, not WSLC's rounded display text.
- **Docker-shaped listings.** `/containers/json` reports published ports and the container's bridge IP, so UIs like Portainer populate those columns.
- **Automatic host ports.** A client asking for an ephemeral published port gets a real free port allocated for it.
- **Live WSLC state.** Volumes, networks, containers, and images are read from WSLC on demand; nothing is cached or invented.
- **OpenAPI document** at `/swagger/v1/swagger.json`, with Swagger UI enabled.
- **Honest failures** for anything WSLC cannot back.

---

## Requirements

- Windows with a working WSLC runtime and `wslc` available on `PATH`. Verified against **WSLC 2.9.11** (`wslc version`); WSLC 2.9.10 and newer are the primary target, and the container listing also still reads the pre-2.9.10 output.
- **To run a published build:** the ASP.NET Core 10 runtime or newer. Release archives are framework-dependent, so they need `Microsoft.AspNetCore.App` installed, not just the base .NET runtime; the app rolls forward across major versions.
- **To build from source:** .NET SDK 10.0.100 or newer (pinned by `global.json`, `rollForward: latestMinor`).
- A real WSLC installation for the Testcontainers tests; the unit and HTTP contract tests do not need one.

---

## Configuration

| Environment variable | Default | Purpose |
| --- | --- | --- |
| `WSLC_DOCKER_SOCKET_PIPE_NAME` | `docker_engine` | Named pipe to listen on. Cannot be empty. |
| `WSLC_DOCKER_SOCKET_DISABLE_NAMED_PIPE` | `false` | Set `true` only when no named pipe may be created. |
| `WSLC_DOCKER_SOCKET_ENABLE_TCP` | `false` | Enables the loopback TCP listener. |
| `WSLC_DOCKER_SOCKET_TCP_PORT` | `2375` | TCP port, used only when TCP is enabled. |

Settings can be supplied as environment variables, command-line switches, or an optional `appsettings.user.json` beside the executable, which is reloaded when it changes.

At least one listener must remain enabled; disabling the pipe without enabling TCP fails at startup.

> [!WARNING]
> TCP is unauthenticated Docker-engine access, and the adapter performs no authentication of its own. It binds `127.0.0.1` only. Do not expose or port-forward it to an untrusted network.

```pwsh
$env:WSLC_DOCKER_SOCKET_ENABLE_TCP = 'true'
$env:WSLC_DOCKER_SOCKET_TCP_PORT = '2375'   # optional; this is the default
```

---

## Usage with Testcontainers .NET

```pwsh
$env:DOCKER_HOST = 'npipe://./pipe/docker_engine'
$env:TESTCONTAINERS_RYUK_DISABLED = 'true'
```

Ryuk must be disabled because it mounts the Docker socket into a container, and this adapter rejects socket and bind mounts. Generic containers work when they rely on an image, command, entrypoint, environment, labels, published ports, logs, wait, exec, and archive upload — which covers the Redis, Kafka, and RocketMQ scenarios in the test suite.

---

## API surface

Available on both unversioned and `/v{major}.{minor}` paths:

| Area | Endpoints |
| --- | --- |
| System | `GET/HEAD /_ping`, `GET /version`, `GET /info` |
| Images | `GET /images/json`, `GET /images/{name}/json`, `POST /images/create` |
| Containers | `POST /containers/create`, `.../start`, `.../stop`, `.../wait`, `GET /containers/json`, `GET /containers/{id}/json`, `GET /containers/{id}/logs`, `PUT /containers/{id}/archive`, `DELETE /containers/{id}` |
| Exec | `POST /containers/{id}/exec`, `POST /exec/{id}/start`, `GET /exec/{id}/json` |
| Volumes | `GET /volumes`, `GET /volumes/{name}` |
| Networks | `GET /networks`, `GET /networks/{id}` |

`GET /version` reports this adapter's assembly version plus its Docker API range. That range is a capability declaration, not a probe of a Docker daemon. `GET /info` reports counts from live WSLC state and best-effort local probes (`wslc version`, the default distribution's kernel release, and `/proc/meminfo`); a failed probe yields an empty string or zero rather than a fabricated value.

---

## Explicit compatibility limits

These return a Docker-style `501 Not Implemented` with the reason in the message, because WSLC exposes no equivalent:

| Request | Why it cannot be mapped |
| --- | --- |
| `GET /events` | WSLC has no event source at all: no `events` command and no change notifications. |
| `POST /containers/{id}/resize`, `POST /exec/{id}/resize` | There is no TTY to resize — TTY containers are rejected at create and execs run without one. |
| `GET /images/{name}/history` | WSLC reports no per-layer build history. Inspect exposes only layer digests, without the per-layer command, size, or timestamp Docker's response requires. |
| Bind, volume, tmpfs, and Docker-socket mounts | Mount semantics are not mapped. |
| Network create/delete/connect/disconnect, non-default network modes | Only the default bridge network is available. |
| TTY containers | Not supported. |
| `POST /containers/{id}/attach` | Not implemented, with or without stdin. Use `GET /containers/{id}/logs` for output. |
| Multiple host bindings per container port, host-IP-specific and IPv6 bindings, protocols other than TCP/UDP | WSLC publishing cannot express them. |
| Registry credentials | WSLC's `image pull` has no authentication option. |
| `copyUIDGID`, `noOverwriteDirNonDir` on archive upload | WSLC's copy cannot honour them. |

Registry auth is classified locally before any CLI call: an anonymous envelope (absent, literal `null`, or JSON whose credential fields are all empty) is accepted for pulls; a malformed value is rejected with `400`; username/password and identity/registry tokens are rejected with `501` and are never logged or forwarded.

Unknown endpoints return Docker's `404` shape with `endpoint not implemented: <method> <path>`, which makes missing surface easy to spot in logs.

---

## How it maps onto WSLC

All state comes from the `wslc` CLI in its default, unqualified scope; the adapter never passes `--session`.

**CLI output is a moving target.** WSLC 2.9.10 aligned `wslc container list --format json` with Docker's CLI: `Id` became `ID`, `Name` became `Names`, `State` and `CreatedAt` became text, and IDs are now abbreviated while `container create` and inspect still report the full digest. The list reader accepts both shapes, and identifiers are matched on a prefix in either direction so short and full IDs both resolve. Expect to re-verify against a real installation after every WSLC upgrade; fake-driven tests cannot detect this kind of drift.

**Image sizes and timestamps.** `wslc image list` renders these for humans (`146MB`, `2026-08-25 08:48:50 +0800 GMT+8`), which is lossy and not what Docker clients parse. The adapter therefore issues a single batched `wslc image inspect <id> <id> … --format json` per listing and takes exact byte counts and RFC3339 timestamps from it. If an image is missing from that payload — for example, removed between the two calls — the rendered list values are kept rather than failing the whole listing.

**Container ports and addresses.** `/containers/json` similarly resolves published ports and the bridge IP from one batched `wslc container inspect`, since the list output carries no network address. Single-container lookups and `/info` deliberately use the cheap listing so they never trigger a full inspect sweep.

**Archive upload.** `PUT /containers/{id}/archive` streams the raw request body into `wslc container cp - <id>:<path>`. The tar is never extracted on the host or buffered to disk, which avoids both path-traversal exposure and unbounded memory use. Destination paths must be absolute and free of control characters.

**Ephemeral ports.** WSLC requires an explicit nonzero host port, so a Docker request for port `0` or an unspecified host port has a free TCP port allocated before the publish argument is built.

**API version prefix.** A leading `/v{major}.{minor}` is moved into `PathBase` by middleware ahead of routing, so each endpoint is registered once. Error messages and logs still report the client's original path.

**AutoRemove.** `HostConfig.AutoRemove` maps to `container create --rm`. WSLC may remove the container promptly once its init process exits, after which inspect, logs, wait, and delete can return `404`. Clients needing post-exit state must set `AutoRemove` to `false`.

**Streaming boundaries.** Container output uses Docker's raw multiplexed stream. `GET /containers/{id}/logs` is the supported output path: it honours `stdout` and `stderr`, and with `follow=1` it forwards `wslc container logs --follow` as a chunked raw stream. Exec output is collected before the response is sent and capped at 16 MiB per stream, so exec start is not yet live streaming. Output subscriptions are bounded; a consumer that cannot keep up is disconnected rather than letting memory grow.

**Lifecycle ownership.** Containers remain WSLC workloads after the adapter stops. Shutdown releases the adapter's own handles without deleting containers or terminating a WSLC session, so it can run as a long-lived management endpoint rather than only as a test helper.

---

## Build and test

```pwsh
dotnet restore WslcDockerSocket.slnx
dotnet build WslcDockerSocket.slnx --configuration Release --no-restore
dotnet WslcDockerSocket.Tests/bin/Release/net10.0/WslcDockerSocket.Tests.dll -noLogo -parallelMode none
```

The suite mixes three kinds of test:

- **Unit and protocol tests** cover port parsing, stream framing, registry-auth classification, and CLI command vectors against a recording fake. No WSLC required.
- **HTTP contract tests** run the real application on an in-process pipe and assert Docker's status codes and error payloads. No WSLC required.
- **Testcontainers tests** start real Redis, Kafka, and RocketMQ containers through an in-process adapter, so they need a working WSLC installation and will pull images on first run. They dominate the roughly 70-second runtime.

Run one class at a time with `-class`, for example:

```pwsh
dotnet WslcDockerSocket.Tests/bin/Release/net10.0/WslcDockerSocket.Tests.dll -noLogo -parallelMode none -class WslcDockerSocket.Tests.DockerApiContractTest
```

Because the tests bind a named pipe, an interrupted run can leave a host process holding it and the next run fails with `address already in use`. Terminate the leftover test host and re-run.

---

## Security and limitations

- Intended for local development and experimentation.
- The adapter authenticates nothing. Anyone able to reach the pipe or the TCP port has full control over WSLC containers, including creating new ones.
- Docker API coverage is partial by design; see the limits above.
- Client UIs will show gaps for data WSLC does not expose. Compose/stack grouping and label-driven views are empty because container labels are not yet reported in listings, and views that rely on `/events` do not refresh automatically.

---

## License

MIT.

---

## Acknowledgements

- Built on [Microsoft WSL Containers](https://learn.microsoft.com/windows/wsl/containers/).
- README structure inspired by [socktainer](https://github.com/socktainer/socktainer), which does the same job for Apple's containerization libraries on macOS.
