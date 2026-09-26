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

## Maintain

Run the complete unit, Native AOT, integration, and performance suite with `./scripts/tests.sh`. Performance badges show the latest successful `main` run on GitHub-hosted Linux; enable GitHub Pages with **GitHub Actions** as its source to publish them.

See [spec.md](spec.md) for detailed specifications, contracts, and architecture.
