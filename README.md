# LLM Router Kely

[![CI](https://github.com/manitra/llm-router-kely/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/manitra/llm-router-kely/actions/workflows/ci.yml)
[![unit tests](https://img.shields.io/endpoint?url=https%3A%2F%2Fmanitra.github.io%2Fllm-router-kely%2Fbadges%2Ftests.json&cacheSeconds=300)](https://github.com/manitra/llm-router-kely/actions/workflows/ci.yml)
[![p50 latency overhead](https://img.shields.io/endpoint?url=https%3A%2F%2Fmanitra.github.io%2Fllm-router-kely%2Fbadges%2Fp50.json&cacheSeconds=300)](https://github.com/manitra/llm-router-kely/actions/workflows/ci.yml)
[![alloc / request](https://img.shields.io/endpoint?url=https%3A%2F%2Fmanitra.github.io%2Fllm-router-kely%2Fbadges%2Fallocation.json&cacheSeconds=300)](https://github.com/manitra/llm-router-kely/actions/workflows/ci.yml)
[![single binary size](https://img.shields.io/endpoint?url=https%3A%2F%2Fmanitra.github.io%2Fllm-router-kely%2Fbadges%2Fbinary.json&cacheSeconds=300)](https://github.com/manitra/llm-router-kely/actions/workflows/ci.yml)

```text
 _     _     __  __   ____             _             _  __    _
| |   | |   |  \/  | |  _ \ ___  _   _| |_ ___ _ __ | |/ /___| |_   _
| |   | |   | |\/| | | |_) / _ \| | | | __/ _ \ '__|| ' // _ \ | | | |
| |___| |___| |  | | |  _ < (_) | |_| | ||  __/ |   | . \  __/ | |_| |
|_____|_____|_|  |_| |_| \_\___/ \__,_|\__\___|_|   |_|\_\___|_|\__, |
                                                                  |___/
```

**OpenAI-compatible LLM routing with sub-millisecond latency and a tiny memory footprint.**
`Kely` means "small" in Malagasy and expresses its tiny, locally rooted design.

Tired of provisioning multi-gigabyte instances for your LiteLLM gateway? Welcome home.
- 4.8 KiB allocated per routed request in our latest benchmark
- 80 µs p50 incremental proxy overhead against a same-host mock upstream
- 29.8 MiB idle working set after load
- Designed to handle 256 concurrent streams within a 250 MiB memory limit
You can downsize your VPS.

## Install

```bash
cp config/router-kely.local.json.example config/router-kely.local.json
dotnet build RouterKely.slnx --configuration Release
```

## Use

```bash
dotnet run --project src/RouterKely/RouterKely.csproj --configuration Release
```

## Deploy

Docker is used only to publish the image and to deploy on Coolify. There is no local container workflow: run the router with `dotnet run` during development.

### Published image

The `container image` workflow publishes a multi-architecture image to GitHub Container Registry on every push to `main` and on `v*` tags. Pulling it is the fastest deployment: the host downloads 26 MB instead of running a Native AOT compile, and it runs the exact artifact CI verified.

A deployment must provide three things, or the container exits at startup:

| Requirement | Why |
|---|---|
| `ROUTERKELY_ADMIN_API_KEY` | The administrator bearer key. Read at startup, never written to disk. |
| `ROUTERKELY_DEEPSEEK_API_KEY` | The upstream credential. Read at startup, never written to disk. |
| A volume mounted at `/data` | Holds the configuration file and the identity file the admin UI rewrites. |

```bash
docker run -d --name router-kely -p 8080:8080 \
  -v router-kely-data:/data \
  -e ROUTERKELY_ADMIN_API_KEY=sk-rk_... \
  -e ROUTERKELY_DEEPSEEK_API_KEY=sk-... \
  ghcr.io/manitra/llm-router-kely:latest
```

If a variable is missing the router refuses to start and names every one it needs in a single message. Pin `:sha-<commit>` or a release tag such as `:1.2.3` for reproducible deployments and easy rollback.

The container also carries `org.opencontainers.image.description` and `...url` labels, so these requirements appear in the registry UI and in `docker inspect`.

Use a **named** volume, not a host bind mount: a named volume inherits the ownership of the image's `/data` directory and needs no preparation. A bind mount must be chowned to `1654:1654` first, because the container runs unprivileged for its whole lifetime and never takes ownership of a mount. The container refuses to start with that exact instruction if it cannot write to `/data`.

On the first start the router creates `/data/router-kely.local.json` from its embedded default. Edit it and restart the container to apply changes, or manage users in the admin UI. Secrets live in the environment, never in the volume.

### Coolify

Coolify builds the image from the branch you select and deploys it, so any branch or commit can be deployed without waiting for a published image. Set **Base Directory** `/` and **Compose Location** `/scripts/container/coolify.compose.yml`.

Coolify's environment form lets you edit every variable the deployment accepts: `ROUTERKELY_ADMIN_API_KEY`, `ROUTERKELY_DEEPSEEK_API_KEY`, `ROUTERKELY_CONFIG`, `ROUTERKELY_HEALTH_URL`, and `ASPNETCORE_FORWARDEDHEADERS_ENABLED`. Each row is declared as an override with a default, so you can set any of them from the UI and a row you leave empty uses the default — the image default for the last three, and no value at all for the two secrets, which makes startup fail with the list of missing variables. The Compose file declares the persistent `/data` volume, so there is nothing to add by hand. Coolify's proxy terminates TLS and assigns the host port, which is why the listener stays on plain HTTP port 8080.

If you prefer to deploy the published image with no build, create the app with the **Docker Image** build pack using `ghcr.io/manitra/llm-router-kely:latest`, then add both variables, a persistent volume mounted at `/data`, and port `8080` manually.

## Maintain

Run the complete unit, Native AOT, integration, and performance suite with `./scripts/tests.sh`, and the container image checks with `./scripts/container/image-tests.sh`. Performance badges show the latest successful `main` run on GitHub-hosted Linux; enable GitHub Pages with **GitHub Actions** as its source to publish them.

See [spec.md](spec.md) for detailed specifications, contracts, and architecture.

License: [Apache 2.0](LICENSE).
