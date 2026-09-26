# Router Kely

[![CI](https://github.com/manitra/router-kely/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/manitra/router-kely/actions/workflows/ci.yml)
[![p50 overhead](https://img.shields.io/endpoint?url=https%3A%2F%2Fmanitra.github.io%2Frouter-kely%2Fbadges%2Fp50.json&cacheSeconds=300)](https://github.com/manitra/router-kely/actions/workflows/ci.yml)
[![alloc / request](https://img.shields.io/endpoint?url=https%3A%2F%2Fmanitra.github.io%2Frouter-kely%2Fbadges%2Fallocation.json&cacheSeconds=300)](https://github.com/manitra/router-kely/actions/workflows/ci.yml)
[![binary size](https://img.shields.io/endpoint?url=https%3A%2F%2Fmanitra.github.io%2Frouter-kely%2Fbadges%2Fbinary.json&cacheSeconds=300)](https://github.com/manitra/router-kely/actions/workflows/ci.yml)

```text
 ____             _             _  __    _       
|  _ \ ___  _   _| |_ ___ _ __ | |/ /___| |_   _ 
| |_) / _ \| | | | __/ _ \ '__|| ' // _ \ | | | |
|  _ < (_) | |_| | ||  __/ |   | . \  __/ | |_| |
|_| \_\___/ \__,_|\__\___|_|   |_|\_\___|_|\__, |
                                             |___/ 
```

Router Kely is a deliberately small .NET 10 Native AOT gateway for OpenAI-compatible inference against DeepSeek. It prioritizes direct streaming, predictable resource use, and a minimal operational footprint.

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
