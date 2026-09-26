# General rules

- Never delete, reset, or wipe user data.
- Never run destructive data commands such as `docker compose down -v`, `DROP TABLE`, `DELETE FROM`, or `docker volume rm` on persistent database volumes.
- Do not read or write files outside the workspace. For temporary files, use `scripts/.tmp*` and keep them ignored by Git.
- If you have to write secrets, make sure they are in files ignored by Git

## Coding rules

- Time and memory efficient
- Very concise but easy to read and understand by humans
- No duplication — shared logic between store-front and b2b-api belongs in `commerce-core/src/`. When adding a feature that both services need, extract the shared code to commerce-core first, then wire both services to use it.
- Testable
- Extensible or composable when possible
- Secure
- English only
- Comments only on complex or unintuitive parts
- No files above 800 lines when reasonably avoidable

Respect the rules and refactor immediately when you spot violations.

## Specifications

- Maintain `spec.md` as the single source of truth for specifications, design decisions, API contracts, and architectural or technical choices
- If behavior, architecture, configuration, deployment, UX, schema, or testing expectations change, update `spec.md` in the same change
- Do not create competing specification files unless explicitly requested
- Keep `README.md` extra short: title and ascii art logo, two sentence project summary, installation instruction, usage instruction, maintainance instruction and a link to `spec.md` for detaileds specs

## Cross-service changes

- If you change an API endpoint, request/response format, environment contract, or database schema, check and update every consuming service
- Prefer explicit failures over runtime fallbacks for broken contracts or schema drift

## Testing guidelines

- If you add or change functionality, add tests to cover the new behavior
- After every significant change, run the full repo test script and fix all failures, even if they do not seem related to your change
- If a required script is missing, create it under `scripts/`
- If a change breaks existing behavior, fix it, add missing test coverage, and update these instructions if that would prevent the same mistake to happen again

## Project structure

- Keep the repository root limited to component folders and essential top-level project files
- Put development, testing, and deployment scripts in `scripts/`. The canonical names are `scripts/tests.sh` (run all PHPUnit + Gradle tests) and `scripts/dev.sh` (rebuild images and start the Docker Compose dev stack). Do not introduce alternate names like `run-tests.sh` or `rebuild-and-run.sh`.
- Keep Docker artifacts in `scripts/container/` (Dockerfile, its ignore file, entrypoint, health check, compose file) and never in the repository root. The build context stays the repository root, so build with `docker build -f scripts/container/Dockerfile .`. BuildKit only applies `scripts/container/Dockerfile.dockerignore` while it keeps that exact name next to the Dockerfile; `scripts/container-tests.sh` fails if it goes missing or stops filtering.
- Keep tests inside the relevant component, not in a separate top-level `tests/` directory
