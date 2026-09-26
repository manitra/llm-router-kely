# LLM Router Kely

[![CI](https://github.com/manitra/llm-router-kely/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/manitra/llm-router-kely/actions/workflows/ci.yml)
[![unit tests](https://img.shields.io/endpoint?url=https%3A%2F%2Fmanitra.github.io%2Fllm-router-kely%2Fbadges%2Ftests.json&cacheSeconds=300)](https://github.com/manitra/llm-router-kely/actions/workflows/ci.yml)
[![p50 overhead](https://img.shields.io/endpoint?url=https%3A%2F%2Fmanitra.github.io%2Fllm-router-kely%2Fbadges%2Fp50.json&cacheSeconds=300)](https://github.com/manitra/llm-router-kely/actions/workflows/ci.yml)
[![alloc / request](https://img.shields.io/endpoint?url=https%3A%2F%2Fmanitra.github.io%2Fllm-router-kely%2Fbadges%2Fallocation.json&cacheSeconds=300)](https://github.com/manitra/llm-router-kely/actions/workflows/ci.yml)
[![binary size](https://img.shields.io/endpoint?url=https%3A%2F%2Fmanitra.github.io%2Fllm-router-kely%2Fbadges%2Fbinary.json&cacheSeconds=300)](https://github.com/manitra/llm-router-kely/actions/workflows/ci.yml)

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
- 7.1 KiB allocated per routed request in our latest benchmark
- 274 µs p99 incremental proxy overhead against a same-host mock upstream
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

Coolify and any Docker host build the image from `scripts/container/Dockerfile`; it runs unprivileged on Alpine. Docker stays out of the repository root, so nothing changes for the .NET workflow.

```bash
ROUTERKELY_ADMIN_API_KEY=sk-rk_... ROUTERKELY_DEEPSEEK_API_KEY=sk-... \
  docker compose -f scripts/container/compose.yml up --build
```

The `container image` workflow publishes a multi-architecture image to GitHub Container Registry on every push to `main` and on `v*` tags. Pulling that image is the fastest deployment: the host downloads 26 MB instead of running a Native AOT compile, and it runs the exact artifact CI verified.

```bash
docker run -d --name router-kely -p 8080:8080 -v router-kely-data:/data \
  -e ROUTERKELY_ADMIN_API_KEY=sk-rk_... \
  -e ROUTERKELY_DEEPSEEK_API_KEY=sk-... \
  ghcr.io/manitra/llm-router-kely:latest
```

Pin `:sha-<commit>` or a release tag such as `:1.2.3` for reproducible deployments and easy rollback.

Mount a persistent volume at `/data`. It holds `router-kely.local.json` and the `router-kely.identities.json` the admin UI rewrites. On the first start the container seeds the configuration from the image default; edit it and restart the container to apply changes, or manage users in the admin UI. Secrets live in the environment, never in the volume.

The entrypoint takes ownership of the volume and then drops to the unprivileged `app` user (uid 1654) before the router starts, so both named volumes and bind mounts work with no host-side preparation. Only a platform that forces a non-root user needs a volume already writable by uid 1654.

On Coolify, either:

- **Dockerfile build pack** — **Base Directory** `/`, **Dockerfile Location** `/scripts/container/Dockerfile`; or
- **Docker Image build pack** — image `ghcr.io/manitra/llm-router-kely:latest` to deploy the published artifact directly.

In both cases set the two secret variables, add a volume mount at `/data`, expose port `8080`, and deploy. The first deployment creates the configuration file in the volume for you to edit.

## Maintain

Run the complete unit, Native AOT, integration, and performance suite with `./scripts/tests.sh`, and the container checks with `./scripts/container-tests.sh`. Performance badges show the latest successful `main` run on GitHub-hosted Linux; enable GitHub Pages with **GitHub Actions** as its source to publish them.

See [spec.md](spec.md) for detailed specifications, contracts, and architecture.
