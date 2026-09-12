# wslc-docker-socket

`wslc-docker-socket` exposes a deliberately limited Docker Remote API over a Windows named pipe, backed by [Microsoft WSL Containers (WSLC)](https://learn.microsoft.com/windows/wsl/containers/). Its purpose is to let Docker API clients such as Testcontainers .NET start and inspect ordinary Linux containers without a Docker daemon.

> **Preview boundary:** WSLC and this adapter are experimental. The service implements only the Docker API features that it can map faithfully to WSLC; it rejects the others rather than reporting a false success.

## Requirements

- Windows with a working WSLC runtime.
- .NET SDK selected by `global.json`.
- The Windows SDK reference package version in `Directory.Build.props`.

## Run

The default endpoint is the local named pipe `\\.\pipe\wslc-docker-socket`. Start the service from the repository root:

```pwsh
dotnet run --project WslcDockerSocket/WslcDockerSocket.csproj --configuration Release
```

The pipe name can be changed with `WSLC_DOCKER_SOCKET_PIPE_NAME`. Set `WSLC_DOCKER_SOCKET_DISABLE_NAMED_PIPE=true` only when a named pipe must not be created.

TCP is **disabled by default**. It is unauthenticated Docker-engine access and can create privileged containers, so enable it only for a local development client that cannot use the named pipe:

```pwsh
$env:WSLC_DOCKER_SOCKET_ENABLE_TCP = 'true'
$env:WSLC_DOCKER_SOCKET_TCP_PORT = '2375' # optional; this is the default
```

TCP listens only on `127.0.0.1`. Do not expose or forward that port to an untrusted network.

## Testcontainers .NET

Point Testcontainers/Docker.DotNet at the named pipe, for example:

```pwsh
$env:DOCKER_HOST = 'npipe://./pipe/wslc-docker-socket'
$env:TESTCONTAINERS_RYUK_DISABLED = 'true'
```

Ryuk requires mounting the Docker socket into a container. WSLC Docker socket intentionally rejects Docker socket mounts, bind mounts, volumes, and tmpfs mounts, so Ryuk must be disabled. This service is currently suitable for basic generic containers that use an image, command, environment, labels, working directory, port bindings, logs, wait, and exec.

## Implemented API surface

Both unversioned paths and `/v{major.minor}/...` paths are accepted for:

`/version` reports the loaded `Microsoft.WSL.Containers` SDK assembly version. Its Docker API range is an adapter capability declaration, not a Docker daemon/Go/kernel probe. `/info` image counts come from the active WSLC session; after catalog discovery is enabled, its container counts come from the current WSLC catalog rather than this process's runtime overlay. For Docker-client diagnostics, `/info` also caches best-effort local probes of `wslc version`, the default WSL distribution's `uname -r`, and `/proc/meminfo`; it reports the Windows host description explicitly qualified as `with WSL Containers`. Failed probes are emitted as an empty string or zero rather than fabricated values. WSLC 2.9.9 does not expose Docker daemon metadata, global container enumeration, Docker-network metadata, or a typed full inspect schema, so the adapter does not fabricate those values.

- `/_ping`, `/version`, `/info`
- image inspect and pull
- volume list and inspect
- network list and inspect
- container create, start, stop, wait, list, inspect, logs, attach, and delete
- exec create, start, and inspect

### WSLC SDK catalog compatibility boundary

`Microsoft.WSL.Containers` is the primary integration layer for lifecycle operations, events, logs, attach, and exec. However, the currently pinned **2.9.9** SDK exposes operations for a *known* container (`WslcOpenContainer`, `WslcInspectContainer`, and `WslcGetContainerState`), but no public operation that enumerates every container in a WSLC scope. That means a fresh adapter process cannot discover containers created directly with `wslc` or by another Docker API client such as Portainer.

The adapter therefore uses the local `wslc` CLI only as a temporary authoritative catalog boundary:

```pwsh
wslc list -a --format json
wslc container inspect <id-or-name> --format json
```

The CLI supplies global discovery and Docker-shaped inspection data for `/containers/json`, `/containers/{id}/json`, and the container totals in `/info`. It is not used to replace SDK-backed lifecycle and streaming operations. The list response is deliberately parsed as either a JSON array or a single JSON object because the CLI emits the latter when exactly one container exists.

#### Replacing the CLI catalog after an SDK upgrade

When updating `Microsoft.WSL.Containers`, first check whether its **public** session/container APIs can enumerate every container in the default scope and all active named WSLC sessions, without relying on prior IDs or names. If that capability exists, replace `WslcCliContainerCatalog` with an SDK-backed catalog and remove process invocation rather than introducing a second cache.

Before accepting that replacement, validate it against a WSLC installation containing a container created outside this adapter (for example `mongo`):

1. An SDK catalog call returns the full ID, name, image, creation time, and current state for both externally created and adapter-created containers.
2. `GET /v1.24/containers/json?all=1` contains the external container with the same ID/name/image/state as `wslc list -a --format json`.
3. `GET /v1.24/containers/<id-prefix>/json` returns HTTP 200 and preserves Docker-compatible inspect data; do not replace the CLI inspect path until the SDK data can be mapped faithfully.
4. `/v1.24/info` container totals match the SDK catalog result.
5. Restarting the adapter leaves existing containers intact and continues to discover them; shutting down the adapter must release handles, not terminate the WSLC session or delete workloads.
6. Run the normal test suite plus the real WSLC smoke check described below.

Container output is emitted as Docker's raw multiplexed stream. Attach honours the `logs`, `stream`, `stdout`, and `stderr` query flags. It is output-only: attach stdin/hijacking is not implemented.

Volume and network list/inspect endpoints query the WSLC CLI for the current authoritative state rather than reporting fabricated empty collections. They are read-only at present: volume/network mutation and mounting semantics remain unsupported until their Docker lifecycle contracts are mapped deliberately.

`HostConfig.AutoRemove` maps to WSLC's `container create --rm` option. WSLC can remove the container promptly after its init process exits; after that, Docker inspect, logs, wait, and delete requests can return `404`. Clients that require post-exit container state must set `AutoRemove` to `false`.

## Explicit compatibility limits

The service returns a Docker-style `501 Not Implemented` for features it cannot represent safely, including:

- bind/volume/tmpfs/Docker-socket mounts and archive copy;
- network creation/deletion/connect/disconnect, non-default network modes, and custom Docker networks;
- TTY containers and attach stdin;
- multiple host bindings for one container port, host-IP-specific/IPv6 bindings, and port protocols other than TCP/UDP;
- anonymous Docker Registry authentication envelopes are accepted for image pulls; username/password and identity/registry tokens remain unsupported because WSLC exposes no credential option;

Containers remain WSLC workloads after this service stops. The adapter must release its own handles without deleting containers or terminating a WSLC session, so it can be used as a long-lived container-management endpoint rather than only as a Testcontainers helper.

Exec output is collected before the response is sent and is capped at 16 MiB per stream. This prevents unbounded memory use but means exec start is not yet live streaming. Container attach subscriptions are bounded; slow consumers are disconnected rather than allowing the service to grow memory without bound.

## Build and test

```pwsh
dotnet restore WslcDockerSocket.slnx -p:WindowsSdkPackageVersion=10.0.26100.80
dotnet build WslcDockerSocket.slnx --configuration Release --no-restore -p:WindowsSdkPackageVersion=10.0.26100.80
dotnet WslcDockerSocket.Tests/bin/Release/net10.0/WslcDockerSocket.Tests.dll -noLogo -parallelMode none -reporter verbose -stopOnFail
```

The unit/HTTP contract suite does not require WSLC. A separate, manually run smoke verification should pull an image and exercise create/start/inspect/logs/exec/delete against a real WSLC installation.

### RocketMQ Testcontainers E2E

`RocketMqTestcontainersE2eTest` uses the `apache/rocketmq:5.3.3` NameServer + Broker + Proxy startup script, a randomly selected valid gRPC port, the proxy startup log wait, and the `mqadmin clusterList` readiness probe. It is deliberately gated to avoid starting a real container in normal unit-test runs. Start the adapter in one terminal, then run the test with both required settings in another:

```pwsh
# Terminal 1
dotnet run --project WslcDockerSocket/WslcDockerSocket.csproj --configuration Release

# Terminal 2
$env:DOCKER_HOST = 'npipe://./pipe/wslc-docker-socket'
$env:TESTCONTAINERS_RYUK_DISABLED = 'true'
$env:WSLC_DOCKER_SOCKET_RUN_E2E = 'true'
dotnet WslcDockerSocket.Tests/bin/Release/net10.0/WslcDockerSocket.Tests.dll -noLogo -parallelMode none -reporter verbose -stopOnFail
```

The E2E test will not run unless both `WSLC_DOCKER_SOCKET_RUN_E2E=true` and the exact adapter `DOCKER_HOST` value are present. This prevents an accidental run against a locally installed Docker/Rancher engine.

## License

MIT.
