# Router Kely — implementation specification

**Status:** Implementation-ready MVP specification  
**Version:** 1.0  
**Target runtime:** .NET 10 LTS, Linux x64/arm64, Native AOT  
**Deployment:** one self-contained `router-kely` executable/container; no database or sidecar required
**Primary upstream:** DeepSeek OpenAI-compatible API  
**Compatibility goal:** the smallest observed subset of OpenAI and LiteLLM required by the organization's coding clients

> **Global engineering guideline:** Router Kely MUST remain an ultra-high-speed, ultra-low-latency, high-throughput router with a nano memory footprint. Every feature, dependency, abstraction, allocation, retained byte, background task, and persistence choice is subordinate to measured data-plane performance and the smallest practical runtime footprint.

## 1. Product definition

**Router Kely** is a minimalist authenticated reverse proxy for LLM traffic. It is intended to replace the part of LiteLLM actually used by a small development organization without recreating LiteLLM's routing, provider, policy, or organizational model. The technical slug is `router-kely`.

The data plane behaves like an HTTP streaming proxy with authentication, a two-entry model map, user-level quota admission, and asynchronous accounting attached:

```text
coding client
    │
    │ OpenAI-compatible HTTP + Router Kely key
    ▼
Router Kely
    ├─ authenticate key from an in-memory hash table
    ├─ check the owning user's in-memory current-day usage
    ├─ replace one top-level JSON property: model
    ├─ stream the request to DeepSeek
    ├─ stream the response back without rewriting it
    └─ update bounded in-memory counters
```

The control plane is a small server-rendered UI under `/ui`. The default identity provider stores users, quotas, and key hashes in one configuration file; an administrator credential is injected through an environment variable. Statistics are bounded and in memory. Optional state providers, beginning with PostgreSQL after MVP, may replace identity storage, statistics storage, or both without changing the data plane.

### 1.1 Success definition

The MVP is successful when:

- Existing coding clients can switch from LiteLLM by changing only the base URL, while retaining their Router Kely-issued API key and one of the two advertised aliases.
- The measured incremental proxy overhead is below 1 ms at p99 under the benchmark conditions in section 18.
- The service stays below 250 MiB RSS with a 1-vCPU limit under the required workload.
- No inference request performs external state I/O, waits for accounting, buffers a complete body, or constructs an OpenAI request/response object graph.
- Users, keys, daily quotas, usage, and the two model aliases are understandable without teams or inheritance rules.

### 1.2 Normative language

`MUST`, `MUST NOT`, `SHOULD`, and `MAY` are requirements in the RFC 2119 sense. An implementation is conforming only if every MVP `MUST` and the acceptance criteria in section 21 pass.

## 2. Fixed product decisions

These choices resolve ambiguity and are not implementation options for the MVP.

| Topic | Decision |
|---|---|
| Organization model | Users only. No teams, organizations, memberships, or inheritance. |
| Quota scope | One daily monetary quota per user, shared by all that user's keys. |
| Quota period | One UTC calendar day; usage resets at each 00:00:00Z boundary. |
| Keys | Credentials only; a key has no quota or model policy. |
| Roles | `admin` and `user`. |
| Upstreams | One DeepSeek base URL and API credential. |
| Models | Exactly two configured public aliases: `deepseek-fast` and `deepseek-pro`. |
| Default mapping | `deepseek-fast` → `deepseek-chat`; `deepseek-pro` → `deepseek-reasoner`. Both are configurable. |
| Inference protocols | OpenAI-compatible Chat Completions and Responses request forwarding. |
| Response behavior | Preserve upstream status, body bytes, and streaming behavior; do not translate response bodies. |
| Identity state | Default: one atomically replaced configuration file containing users, quotas, and key hashes, plus one environment-supplied administrator key. |
| Statistics | Default: bounded in-memory hourly/daily aggregates; loss on restart is accepted. |
| Optional persistence | A later PostgreSQL adapter may own identity, statistics, or both. It is not part of the default executable or MVP dependency graph. |
| Provider boundary | Narrow startup/control-plane ports selected at build/startup; no dynamic assembly loading, reflection discovery, or provider call on an inference request. |
| Quota strictness | Soft admission limit based on confirmed local usage; explicitly bounded concurrent overshoot and explicit default-provider restart-reset semantics. |
| UI | Server-rendered HTML at `/ui`, styled with the vendored Pico CSS 2.1.1 classless build; no SPA framework, Node.js, or frontend build pipeline. |
| UI authentication | Existing Router Kely API key exchanged for a short-lived in-memory browser session. No passwords in Router Kely. |
| ORM | None. A future PostgreSQL adapter uses Npgsql and explicit SQL. |
| Cache/broker | None. No Redis, message broker, or worker service. |
| Process model | One process per container. The certified MVP deployment is one replica. |
| Runtime | .NET 10 LTS Native AOT with workstation GC. |
| Telemetry | Prometheus text metrics, structured warnings/errors, no request or response bodies. |

## 3. Scope

### 3.1 MVP capabilities

- Authenticate high-entropy bearer API keys in O(1) expected time.
- Reject disabled/revoked keys and disabled users.
- Enforce one user-level daily quota using integer nano-US-dollar accounting.
- Advertise two public model aliases.
- Rewrite only the top-level request `model` value and forward unknown JSON fields unchanged.
- Proxy streaming and non-streaming Chat Completions and Responses requests.
- Observe usage fields without delaying or modifying response bytes.
- Retain bounded hourly and daily aggregate usage in memory for a configurable few days.
- Provide minimal LiteLLM-compatible model, key, user, usage, and key-management endpoints.
- Provide an admin/user web UI under `/ui`.
- Provide readiness, liveness, metrics, and safe operational logs.
- Provide a shadow traffic inventory method for discovering additional endpoints used by real clients.

### 3.2 Explicit non-goals

The MVP MUST NOT include:

- Teams, organizations, membership, team budgets, key budgets, model budgets, budget inheritance, or priority rules.
- Provider abstraction, multiple providers, load balancing, fallbacks, retries across providers, semantic routing, or weighted routing.
- Prompt templates, prompt storage, prompt caching, response caching, guardrails, moderation, content inspection, PII detection, or policy engines.
- Full LiteLLM API parity, its database schema, its UI, or import of its internal identifiers.
- OpenAI Assistants, Threads, Files, Batches, Fine-tuning, Images, Audio, Embeddings, Moderations, Realtime/WebSocket, or vector stores.
- Request retries after any upstream request bytes have been sent.
- Tokenization or pre-counting prompts on the request path.
- Per-request accounting records, prompt/response capture, or searchable audit of inference content.
- Exact hard-stop billing guarantees across concurrent requests or multiple replicas.
- A public user registration flow, password storage, password reset, email delivery, SSO, or OIDC in MVP.
- A generic/dynamic plugin system, embedded scripting, dynamic assemblies, runtime compilation, reflection-based controllers, MVC model binding on `/v1/*`, or Swagger in production. Explicit compile-time state-provider adapters are allowed.
- Horizontal scalability certification in MVP. The default provider is single-replica only; section 11 defines the consequences of ignoring that constraint.

Any proposed feature outside section 3.1 requires a measured client trace proving it is needed or a separate post-MVP decision.

## 4. Architecture and process boundaries

```text
                         ┌───────────────────────────────────────┐
                         │ Router Kely, one Native AOT process   │
                         │                                       │
Client ─────────────────►│ /v1/*  minimal data plane             │──────► DeepSeek
Bearer Router Kely key    │  immutable snapshot + atomic counters│        persistent pool
                         │                                       │
Browser ────────────────►│ /ui    server-rendered control plane  │
Session cookie            │ /internal/ui-api/*                   │
                         │                                       │
Probe/Prometheus ───────►│ /health/* and /metrics               │
                         │ file identity + bounded memory stats  │
                         └──────────────────────────────────────┘
```

An optional PostgreSQL adapter sits behind the same cold-path identity/statistics ports. The default process does not reference or load it.

### 4.1 Hot-path dependency rule

An inference handler may depend only on preconstructed singleton services and immutable/atomic in-memory state. It MUST NOT resolve scoped services, call a state provider, touch a configuration file or database, acquire a contended application lock, await an accounting queue, perform DNS/TLS setup for every request, or call another internal HTTP endpoint.

### 4.2 Startup sequence

1. Parse and validate configuration. Refuse startup on missing secrets, duplicate aliases, invalid prices, or a non-HTTPS upstream unless explicitly in development mode.
2. Load and validate the configured identity provider. By default, read the identity file and hash the environment-supplied administrator key directly into the snapshot.
3. Initialize empty current-day and historical in-memory aggregates. A restart intentionally starts usage at zero for the default statistics provider.
4. Create the singleton `SocketsHttpHandler` and `HttpClient`.
5. Verify that the configured upstream base URI is syntactically valid. A network call is not required for liveness.
6. Start the UTC rollover/retention tick and, when enabled, the identity-file watcher.
7. Mark readiness true and begin accepting traffic.

If the identity file is absent, unreadable, malformed, or contains duplicate IDs/hashes, readiness remains false and inference is not served. The last valid snapshot remains active after a failed live reload. Optional providers define their own startup health without changing these data-plane rules.

### 4.3 Runtime snapshot

One immutable root object is published using `Volatile.Write` and read using `Volatile.Read`:

```text
RuntimeSnapshot
├─ version: Int64
├─ keysBySha256: FrozenDictionary<KeyHash, AuthRecord>
├─ usersById: FrozenDictionary<Int64, UserRuntimeState>
└─ modelsByAlias: FrozenDictionary<Utf8Alias, ModelRoute>
```

`AuthRecord` contains numeric key/user IDs and references the owning `UserRuntimeState`. `UserRuntimeState` contains enabled/role/quota data plus mutable atomic counters stored separately from the immutable identity/configuration fields. Snapshot publication MUST be atomic. Readers never lock.

Control-plane changes go through the selected identity provider, then build and publish a fresh snapshot. The default file provider serializes mutations, writes a complete replacement file with owner-only permissions, atomically renames it over the configured file, and only then publishes the new snapshot. External file replacements are debounced, fully validated, and atomically published; invalid changes leave the last valid snapshot active.

## 5. Runtime and build profile

### 5.1 Required defaults

- Target `net10.0`.
- Publish self-contained Native AOT for `linux-x64` and `linux-arm64`.
- Use ASP.NET Core minimal APIs and source-generated JSON only for small control-plane response types.
- Set `<PublishAot>true</PublishAot>`, `<InvariantGlobalization>true</InvariantGlobalization>`, and `<ServerGarbageCollection>false</ServerGarbageCollection>`.
- Treat all trim and AOT analysis warnings as build failures.
- Use invariant UTC timestamps and invariant numeric formatting.
- Do not enable dynamic code generation, runtime compilation, Razor runtime compilation, or reflection-based JSON serialization.
- Use workstation GC with background collection. Do not enable server GC in the 1-vCPU/250-MiB profile.
- Do not set a GC heap hard limit in the default image. The container limit is 250 MiB; an artificial lower heap cap risks avoidable failures in network bursts. Revisit only from measurements.
- Do not set fixed thread-pool minima or maxima by default. Tune only with benchmark evidence.

Native AOT is the release profile, not an optional optimization. Development may use JIT for edit/run speed, but CI MUST publish and execute the AOT binary and run integration tests against it.

### 5.2 Dependency policy

Allowed production dependencies should be limited to:

- ASP.NET Core shared/native runtime components used by minimal APIs and Kestrel.
- A small source-generated HTML/template mechanism or compiled Razor components proven AOT-safe. Plain compiled HTML rendering is preferred.
- A cryptographic library only if not already supplied by the BCL.

The default executable MUST NOT depend on a database client. A future PostgreSQL adapter may add Npgsql in a separate project/package included only in the PostgreSQL build/profile.

Every added package requires evidence of Native AOT compatibility, retained-size impact, steady-state allocation impact, and necessity. A dependency that introduces runtime reflection on the inference path is rejected.

## 6. HTTP data plane

### 6.1 Supported endpoints

| Method | Path | Authentication | Behavior |
|---|---|---|---|
| `POST` | `/v1/chat/completions` | Router Kely bearer key | Rewrite model and proxy. |
| `POST` | `/chat/completions` | Router Kely bearer key | Exact alias of `/v1/chat/completions`. |
| `POST` | `/v1/responses` | Router Kely bearer key | Rewrite model and proxy. |
| `POST` | `/responses` | Router Kely bearer key | Exact alias of `/v1/responses`. |
| `GET` | `/v1/models` | Router Kely bearer key | Return public alias list. |
| `GET` | `/models` | Router Kely bearer key | Exact alias of `/v1/models`. |
| `GET` | `/v1/models/{id}` | Router Kely bearer key | Return one alias or OpenAI-style 404. |

No generic `/v1/{**path}` proxy is permitted. Unsupported paths return 404 and are counted by normalized path signature without logging query strings or bodies.

### 6.2 Authentication header

Accepted form:

```http
Authorization: Bearer sk-rk_<base64url-secret>
```

Rules:

- Header matching for `Bearer` is case-insensitive; exactly one ASCII space separates scheme and token.
- Query-string keys, cookies, `x-api-key`, and multiple authorization headers are rejected in MVP.
- Maximum token length is 128 bytes; malformed or oversized values return 401 before body reading.
- The client key is never forwarded. The upstream request uses the configured DeepSeek credential.

### 6.3 Models response

`GET /v1/models` returns:

```json
{
  "object": "list",
  "data": [
    {"id":"deepseek-fast","object":"model","created":0,"owned_by":"router-kely"},
    {"id":"deepseek-pro","object":"model","created":0,"owned_by":"router-kely"}
  ]
}
```

Only enabled configured aliases appear. Order is stable and follows configuration order. `created` is `0` for deterministic output. A user over quota may still list models.

### 6.4 OpenAI-style errors

Data-plane errors use:

```json
{
  "error": {
    "message": "Human-readable message",
    "type": "router_kely_error",
    "param": null,
    "code": "quota_exceeded"
  }
}
```

| Status | Code | Condition |
|---:|---|---|
| 400 | `invalid_request` | Invalid JSON framing, absent/non-string/duplicate model, or invalid route input. |
| 401 | `invalid_api_key` | Missing, malformed, unknown, disabled, or revoked key. |
| 403 | `user_disabled` | Owning user disabled. |
| 404 | `model_not_found` | Alias is not configured/enabled. |
| 413 | `request_too_large` | Body limit exceeded. |
| 415 | `unsupported_media_type` | Not `application/json` or request content encoding is not identity. |
| 429 | `quota_exceeded` | Confirmed current-day usage is at or above quota. |
| 429 | `too_many_requests` | Local concurrency cap reached. |
| 502 | `upstream_error` | Connection/protocol failure before upstream headers. |
| 504 | `upstream_timeout` | Header or inactivity timeout. |

If upstream headers were received, preserve the upstream status and response body rather than wrapping it. Do not retry.

### 6.5 Request headers

Forward end-to-end headers needed by OpenAI-compatible clients, including `Content-Type`, `Accept`, and explicitly approved `OpenAI-*` beta/version headers discovered in traffic. Strip:

- `Authorization`, `Cookie`, `Host`, `Content-Length`, `Connection`, `Proxy-Connection`, `Keep-Alive`, `TE`, `Trailer`, `Transfer-Encoding`, `Upgrade`, and all hop-by-hop headers named by `Connection`.
- `Accept-Encoding`; send `Accept-Encoding: identity` upstream so usage observation sees plain bytes.
- Client-supplied `X-Forwarded-*` headers unless the deployment's trusted proxy policy explicitly accepts them.

Add the configured upstream authorization header, a generated `X-Request-ID` if none is supplied, and `User-Agent: router-kely/<version>`. Request IDs are fixed-size random/monotonic values and MUST NOT contain user data.

### 6.6 Limits and timeouts

Defaults, all configurable at startup:

| Limit | Default |
|---|---:|
| Request headers total | 32 KiB |
| Request body | 32 MiB |
| Bytes allowed before top-level `model` is completely read | 64 KiB |
| Concurrent inference requests process-wide | 256 |
| Concurrent inference requests per user | 32 |
| Upstream response-header timeout | 30 seconds |
| Stream inactivity timeout | 120 seconds |
| Overall stream duration | unlimited |
| UI/control request body | 64 KiB |

The 64-KiB model-prefix limit is a deliberate bounded-prefix exception to “no body buffering.” The implementation may retain only the bytes required to locate and replace `model`, never the full request. Clients SHOULD put `model` near the start of the top-level object.

## 7. Model alias rewrite

### 7.1 Required behavior

For a JSON object request, replace the value bytes of the single top-level `model` property while preserving every other byte, field, order, unknown extension, whitespace choice, and numeric representation.

Example:

```text
incoming:  {"model":"deepseek-fast","messages":[...],"vendor_extension":true}
upstream:  {"model":"deepseek-chat","messages":[...],"vendor_extension":true}
```

The transformation MUST NOT deserialize into DTOs, build a `JsonDocument`, convert the body to a string, use regex, or buffer the full body.

### 7.2 Streaming transformer algorithm

Implement a purpose-built UTF-8 JSON tokenizer/state machine over `PipeReader` segments:

1. Require a top-level JSON object.
2. Track object/array depth, string state, escape state, and top-level property/value boundaries.
3. Search only top-level property names for the exact JSON string `model` after escape decoding.
4. Retain a pooled prefix until the complete model string is known, capped at 64 KiB.
5. Reject missing, non-string, duplicate, unknown, or malformed `model` before opening/sending an upstream body.
6. Write the retained prefix to upstream, substituting only a correctly JSON-escaped configured upstream model string.
7. Copy all remaining request segments directly from client to upstream with a running 32-MiB limit.
8. Return pooled buffers in `finally`; cancellation must propagate in both directions.

The transformed request normally uses HTTP chunked transfer or HTTP/2 framing because content length can change. Do not recalculate or buffer to preserve `Content-Length`.

Escaped property names equivalent to `model` MAY be rejected as `invalid_request` in MVP. The model value MUST accept valid JSON escapes but resolves to an alias of at most 64 UTF-8 bytes. Configuration restricts alias and upstream model IDs to ASCII `[A-Za-z0-9._:-]+`, so replacement output needs no escaping in normal use.

### 7.3 Endpoint mapping

- `/v1/chat/completions` and `/chat/completions` forward to `{DeepSeekBaseUrl}/chat/completions` using the configured base-path normalization.
- `/v1/responses` and `/responses` forward to `{DeepSeekBaseUrl}/responses`.
- Redirects are disabled. Upstream base URL and path composition MUST prevent path traversal and accidental host changes.

If DeepSeek does not support `/responses` in the target environment, the endpoint remains implemented but returns a deterministic 501 `upstream_capability_disabled` unless `ResponsesEnabled=true`. Router Kely does not translate Responses into Chat Completions.

## 8. Streaming and response handling

### 8.1 Forwarding

- Use one process-lifetime `SocketsHttpHandler` and `HttpClient`.
- Send with `HttpCompletionOption.ResponseHeadersRead`.
- Disable automatic decompression, cookies, redirects, and proxy auto-discovery unless an explicit outbound proxy is configured.
- Configure pooled connection lifetime (default 15 minutes), idle timeout (default 2 minutes), and sufficient per-server connections (default 256).
- Negotiate HTTP/2 or HTTP/1.1; do not force one until the real DeepSeek endpoint benchmark selects a winner.
- Copy response bytes to `HttpResponse.BodyWriter` as they arrive. Flush promptly for SSE and after each received segment when Kestrel has not already applied backpressure.
- Backpressure MUST propagate; never accumulate an unbounded queue between upstream and client.
- If the client disconnects, cancel upstream immediately.
- Do not coalesce, parse/re-serialize, gzip, cache, or retry response content.

Preserve end-to-end upstream headers such as `Content-Type` and request IDs. Strip hop-by-hop headers and headers whose values become invalid after proxying. Do not expose upstream authorization or internal host data.

### 8.2 SSE

For `text/event-stream`:

- Forward bytes exactly, including comments, event names, `data:` formatting, blank lines, and `[DONE]`.
- Disable proxy/Kestrel response buffering for the route.
- Do not manufacture keepalives.
- The inactivity timer resets whenever upstream bytes arrive.
- A slow client is governed by Kestrel backpressure and the configured output limits; it must not cause arbitrary memory growth.

### 8.3 Usage observer

A side observer reads the same `ReadOnlySequence<byte>` segments that are written to the client. It MUST NOT alter, hold back, or copy the complete response.

Recognized usage fields:

- Chat Completions: `usage.prompt_tokens`, `usage.completion_tokens`, optional `usage.prompt_cache_hit_tokens` or equivalent configured cache field.
- Responses: `usage.input_tokens`, `usage.output_tokens`, and known cached-input detail when present.

The observer is a small incremental UTF-8 JSON/SSE state machine. It stores only parser state and small numeric tokens across segment boundaries. It ignores content strings without allocating them. For SSE it examines `data:` payloads while forwarding the original bytes. It records the last valid usage object observed.

The configured upstream MUST be integration-tested to return usage for streaming and non-streaming success. If a successful response ends without valid usage:

- record `usage_missing=1`, request count, duration, alias, user, and key;
- add zero cost because fabricating billable usage is forbidden;
- emit a rate-limited warning and increment `router_kely_usage_missing_total`;
- continue serving the response;
- make readiness degraded if the rolling missing-usage ratio exceeds 1% over 5 minutes.

This is a conscious MVP limitation. If real DeepSeek streaming requires a request option such as usage inclusion, add that exact top-level option through the same streaming transformer only after a captured integration test proves the need. Do not generally rewrite client options.

### 8.4 Upstream model in responses

Router Kely does not rewrite response bodies. Therefore an upstream response may report `deepseek-chat` or `deepseek-reasoner` instead of the public alias. This is an intentional compatibility deviation required for byte-for-byte streaming. Clients that require response-model aliasing are out of MVP scope until observed.

## 9. API keys and authorization

### 9.1 Key generation and storage

- Generate 32 random bytes with the operating system CSPRNG.
- Encode as unpadded base64url and prefix with `sk-rk_`.
- Display plaintext exactly once at creation.
- Hash the complete ASCII token using SHA-256.
- Store only the lowercase 64-character SHA-256 hex hash, a non-secret display prefix, last four characters, and metadata in the selected identity provider.
- Never log, persist, return again, or place plaintext keys in URLs.

The default identity file never contains plaintext credentials. The bootstrap administrator key is the sole exception to file-based credential storage: its plaintext is supplied through `ROUTERKELY_ADMIN_API_KEY`, hashed once during startup, and never written by Router Kely. A mounted secret-file environment indirection SHOULD be used where the orchestrator supports it.

Argon2/bcrypt/PBKDF2 are intentionally not used: these are machine-generated 256-bit secrets, not human passwords. SHA-256 supports the required constant-work, O(1) in-memory lookup without weakening a high-entropy key.

### 9.2 Hot-path lookup

1. Validate token length/prefix without allocation where practical.
2. Hash UTF-8/ASCII token bytes into a 32-byte stack buffer.
3. Construct a fixed-size `KeyHash` value and perform one frozen-dictionary lookup.
4. Check key enabled/revocation and user enabled flags.
5. Zero temporary token/hash buffers where practical; never retain the plaintext.

The dictionary comparer MUST compare all 32 hash bytes. O(1) expected lookup is required. A prefix or last-four value must never participate in authentication.

### 9.3 Roles

| Operation | User | Admin |
|---|:---:|:---:|
| View own profile, quota, and usage | Yes | Yes |
| List/create/rename/revoke own keys | Yes | Yes |
| View another user | No | Yes |
| Create/disable/edit users | No | Yes |
| Set user quota or role | No | Yes |
| Manage another user's keys | No | Yes |
| View aggregate system usage | No | Yes |

An admin MUST NOT be allowed to retrieve an existing plaintext key. An admin may revoke it or create a replacement.

## 10. UI and control API

### 10.1 UI routes

All browser UI routes live under `/ui`:

| Path | Purpose |
|---|---|
| `/ui/login` | Paste an existing Router Kely key to begin a browser session. |
| `/ui` | Redirect to dashboard. |
| `/ui/dashboard` | Own daily usage, quota, remaining amount, model split. |
| `/ui/keys` | Own key list; create, rename, revoke. |
| `/ui/admin/users` | Admin user list with usage/quota/status. |
| `/ui/admin/users/{id}` | Admin edit user and manage that user's keys. |
| `/ui/admin/usage` | System totals and per-user/model aggregates. |

The look and route placement SHOULD feel familiar to LiteLLM users, but pixel/API parity is not a goal. The UI must work without JavaScript for primary operations. Small progressive-enhancement JavaScript embedded in the executable is allowed.

The UI uses the pinned Pico CSS 2.1.1 classless build. Its minified stylesheet is vendored as an embedded resource and served from the immutable, versioned same-origin path `/ui/assets/pico.classless-2.1.1.min.css`; browsers never fetch UI code, styles, fonts, or analytics from a third party. The HTML shell uses a direct `<main>` child of `<body>` so Pico provides the centered responsive container without framework-specific classes.

The initial administration UI is intentionally one server-rendered users table plus one user-detail page. “Remove user” means disable the user; “remove key” means revoke the key. Neither operation physically deletes identity history. The user-detail page generates a replacement key and displays its plaintext exactly once. The environment administrator is visible but cannot be edited, disabled, or issued file-backed keys.

### 10.2 Browser session

`POST /ui/login` accepts a Router Kely key over TLS, authenticates it using the normal in-memory lookup, and creates a 256-bit opaque random session ID. The session is stored only in a bounded in-memory table and sent in a cookie:

```text
HttpOnly; Secure; SameSite=Strict; Path=/ui
```

- Default idle lifetime: 2 hours; absolute lifetime: 8 hours.
- Maximum sessions: 1,024 with expired/least-recently-used eviction.
- Session contains user ID, authenticating key ID, role snapshot, CSRF secret, issued/expiry times.
- Every request rechecks that user/key against the current runtime snapshot, so revocation or disablement invalidates the session.
- Sessions intentionally disappear on process restart and are not shared across replicas.
- State-changing form posts require an HMAC-backed synchronizer CSRF token and same-origin validation.
- Login is rate-limited by source IP and globally; failures have a small fixed delay on the cold path.
- Same-origin validation accepts an exact `Origin` match against the request scheme and host. An opaque `Origin: null` (sandboxed iframe, webview, or header-rewriting client) or a missing `Origin` falls back to a same-origin `Referer`, which is available because every UI page is served with `Referrer-Policy: same-origin`.
- A rejected same-origin check returns `403` with a distinct message; only the login rate limiter returns `429`. The two outcomes MUST NOT share a status code.

The login form MUST warn users that the key is submitted only to create the session and is not stored. Browser local/session storage MUST NOT contain the key.

The `Secure` cookie flag is mandatory under HTTPS. The loopback-only HTTP development listener may omit it so the local administration UI remains usable; non-loopback deployments require TLS.

### 10.3 Bootstrap

The default environment-administrator secret is generated by a one-shot command in the same executable:

```text
router-kely admin bootstrap
```

For the default provider, this command validates the configured identity path, creates an empty versioned identity document only when the file does not exist, and prints a generated value suitable for `ROUTERKELY_ADMIN_API_KEY` exactly once. Administrator ID, name, and email remain ordinary non-secret runtime configuration; the reserved environment administrator is never written to the identity file. The command refuses to overwrite an existing identity file and refuses to print a secret when stdout is not an interactive terminal unless `--output-key-file <explicit-path>` is supplied. Optional identity providers implement equivalent bootstrap semantics on their own cold path.

### 10.4 Internal UI API

HTML forms may post to compiled handlers under `/ui/actions/*`. A separate broad JSON admin API is not required. If JavaScript enhancement needs JSON, expose only same-origin, session-authenticated `/internal/ui-api/*` routes with the same authorization and CSRF rules. These routes are not part of the public compatibility contract.

## 11. Quota semantics

### 11.1 Definition

- Quota is an integer `quota_nano_usd` on the user, applied per UTC calendar day.
- `NULL` means unlimited. Zero means no billable requests are admitted.
- The accounting period is a calendar day in UTC, keyed by its date, starting at 00:00:00Z.
- Confirmed usage resets to zero at each UTC day boundary; the previous day's totals remain for reporting.
- All a user's keys share the same confirmed usage counter.
- Quota has no inheritance and no per-key or per-model override.

Admission pseudocode:

```text
if user disabled: reject 403
usage = atomic read confirmedCostForCurrentUtcDay
if quota is not null and usage >= quota: reject 429
if per-user or process concurrency slot unavailable: reject 429
admit request
```

At completion, observed cost is calculated with the route's immutable pricing snapshot and added with an atomic integer operation before the request scope is released. No persistence or statistics-provider work is awaited.

### 11.2 Concurrency and overshoot

The quota is a soft admission boundary, not a prepaid ledger reservation. Multiple requests admitted just below the quota may collectively exceed it because exact cost is known only after their responses. Once confirmed usage reaches/exceeds quota, subsequent admissions fail.

For one process, overshoot is bounded by the actual cost of requests already in flight at the crossing. Per-user concurrency defaults to 32 to bound exposure. No request is terminated mid-stream when the quota is crossed.

A daily window is short: a small number of large requests MAY exhaust a user's quota for the remainder of the UTC day, after which admissions fail until the next day boundary. The daily period bounds accumulated exposure; it does not smooth spend across days.

Exact hard monetary enforcement is explicitly rejected for MVP because it would require one or more of: request tokenization, pessimistic maximum-cost reservation, a distributed transactional counter, or terminating valid streams. Those add latency, memory, complexity, or surprising behavior.

### 11.3 Multiple replicas

The certified default deployment uses one replica. With the default in-memory statistics provider, replicas have independent counters and therefore independent effective quotas. Running more than one replica multiplies the permitted daily spend and is unsupported unless a shared statistics provider explicitly implements cross-replica quota state. The UI labels quota as process-local when the default provider is active.

Hard cross-replica quotas are a post-MVP feature and MUST NOT be approximated with a database call per request.

### 11.4 Day rollover

Every admission compares a cached UTC day key. The first request after rollover atomically switches the user's current-day counter to the new day under a rare slow-path lock or compare/exchange initialization. The previous day's bucket remains available for reporting until retention expires. A periodic background tick performs the same rollover even with no requests.

## 12. Accounting and retention

### 12.1 Cost arithmetic

Money uses signed 64-bit integer nano-US dollars (`1 USD = 1,000,000,000 nanoUSD`). Floating point and `decimal` are forbidden on the hot path.

Each model has versioned integer prices per million tokens:

```text
input_cost_nano_usd_per_million
cached_input_cost_nano_usd_per_million
output_cost_nano_usd_per_million
```

Calculate each component with checked integer arithmetic and division that rounds up to the nearest nanoUSD:

```text
ceil(tokens × pricePerMillion / 1,000,000)
```

Use a 128-bit intermediate to prevent overflow. The pricing snapshot captured at request admission applies to that request even if configuration reloads before completion.

### 12.2 In-memory aggregation

Do not allocate and enqueue one object per request. Maintain pre-created/sharded counters keyed by:

```text
(UTC hour, user_id, key_id, public_model_alias, outcome_class)
```

Completion atomically increments request count, input/output/cached tokens, cost, duration sum, and usage-missing count. Cardinality is bounded by configured users × keys × two models × small outcome set. New hourly buckets are created on a cold rollover path.

`outcome_class` is one of `success`, `client_error`, `upstream_error`, or `cancelled`; raw status may be aggregated into a fixed status-class counter. Do not create a label/key from arbitrary paths, status text, client IDs, or request data.

### 12.3 Default in-memory statistics provider

The default provider retains hourly aggregates for 72 hours and daily aggregates for 7 UTC days. Both values are configurable within hard bounds of 1–168 hours and 1–31 days. Retention cleanup happens only on hour/day rollover; there is no per-request timer, queue item, batch object, or I/O.

Counter capacity is planned from the validated identity/model configuration, but hourly detail cells are allocated lazily on first use and then updated without per-completion allocation. Startup computes a conservative worst-case retained-size bound from the actual configured users/keys, models, outcomes, and retention. If that exceeds `MaxBytes`, startup fails explicitly; a control-plane change that would exceed it is rejected. Old buckets are recycled or released on the cold rollover path.

Reads for the UI and compatibility endpoints snapshot the relevant counters without stopping inference. Values may be slightly inconsistent across buckets during a concurrent update; this is acceptable for operational statistics. The atomic per-user current-day cost used for quota admission remains authoritative inside the process.

### 12.4 Restart and crash semantics

The default provider performs no disk writes. Graceful shutdown and abrupt loss therefore have the same simple rule: all statistics and consumed-quota counters disappear, and startup begins the current UTC day's usage at zero.

This means the default quota is a process-local daily guardrail, not a durable billing ledger: restarting Router Kely can grant a user the remainder of the configured daily quota again. This tradeoff is accepted for the default lightweight deployment and MUST be visible in the UI and operations documentation. Deployments that require restart-safe quota enforcement or historical reporting MUST use a persistent statistics adapter such as the future PostgreSQL provider.

No local WAL, periodic snapshot, or shutdown flush exists in the default provider. Adding one to the core is forbidden without benchmark evidence and a specification change.

### 12.5 Optional persistent statistics provider contract

A persistent adapter MAY consume immutable aggregate batches on a background cold path and restore current-day per-user totals at startup. It MUST preserve these invariants:

- inference completion only updates in-memory atomics and never awaits the adapter;
- retries are idempotent and memory backlog is strictly bounded;
- provider unavailability cannot introduce provider calls on inference requests;
- readiness fails before unpersisted state can grow without bound;
- restored usage is installed before readiness becomes true;
- disabling/removing the adapter yields exactly the default in-memory behavior.

The detailed PostgreSQL flush protocol and schema belong to that adapter's specification when it is implemented, not to the core MVP.

## 13. State-provider boundaries

The core defines two small cold-path contracts. They are ordinary interfaces wired explicitly by the composition root, not a general plugin framework.

### 13.1 Identity provider

The identity provider loads a complete versioned identity document and applies serialized control-plane mutations. Its data contains stable user/key IDs, display metadata, roles, enabled state, daily quotas, SHA-256 key hashes, and non-secret key display fragments. It never exposes plaintext keys.

The default file provider uses a single UTF-8 JSON file. It MUST:

- reject unknown schema versions, duplicate IDs/emails/key hashes, invalid hashes, and references to absent users;
- require at least one enabled administrator identity, with the environment administrator satisfying this rule;
- cap users and keys with configured hard limits before allocating the runtime snapshot;
- write mutations as a complete validated replacement using a same-directory temporary file, restrictive permissions, flush, and atomic rename;
- serialize concurrent mutations and reject stale version writes;
- preserve the last valid file and runtime snapshot on any write or reload failure;
- never copy `ROUTERKELY_ADMIN_API_KEY` into the file.

The environment administrator has a reserved stable user/key ID configured alongside its name/email. Its secret comes only from `ROUTERKELY_ADMIN_API_KEY`. The environment key cannot be renamed, revoked, or revealed through the UI; rotation requires changing the secret and restarting. Additional administrators may be ordinary file-backed users.

### 13.2 Statistics provider

The statistics provider receives already-bounded aggregate state outside the inference path and supplies optional startup recovery/reporting. The default provider is the in-memory implementation in section 12. A provider may implement identity only, statistics only, or both; selection is explicit so mixed deployments are possible.

Provider contracts MUST use core-owned primitive/value types and immutable batches. Core MUST NOT reference provider-specific connection, SQL, migration, retry, or serialization types. Provider callbacks never execute inline on an inference request.

### 13.3 Future PostgreSQL adapter

PostgreSQL is a post-MVP optional adapter, shipped separately from the default binary. It may take over identity data, statistics, or both. Its implementation must use explicit SQL, versioned non-destructive migrations, idempotent aggregate writes, bounded retry state, and startup recovery of current-day usage. It must pass the same data-plane performance and zero-provider-I/O invariants as the default implementation before release.

No PostgreSQL schema, Npgsql dependency, connection setting, migration command, or background flusher belongs in the core until this adapter is implemented and measured.

## 14. LiteLLM compatibility profile

Compatibility is intentionally behavioral and narrow. The project publishes a versioned profile named `router-kely-litellm-v1`. Unknown response fields may be added, but defined fields are stable.

### 14.1 Endpoints

| Method | Path | Purpose | Authorization |
|---|---|---|---|
| `GET` | `/key/info` | Current key, owner, usage, quota. | Any Router Kely key; own key only. |
| `GET` | `/v1/model/info` and `/model/info` | LiteLLM-compatible metadata for configured public aliases. | Any Router Kely key. |
| `GET` | `/user/daily/activity` | Retained daily aggregate usage with model/provider/key breakdowns. | Any Router Kely key; own usage only in MVP. |
| `GET` | `/user/info` | Current user and aggregate usage. | Any Router Kely key; self by default. Admin may pass `user_id`. |
| `POST` | `/key/generate` | Create a key for self, or specified user for admin. | Any Router Kely key. |
| `POST` | `/key/delete` | Revoke named key IDs. | Owner or admin. |
| `GET` | `/spend/logs` | Aggregated usage only, not raw logs. | Self; admin may filter user. |

`/v1/models` is the model-discovery endpoint. Additional LiteLLM paths are not implemented unless section 20 proves a real client requires them.

### 14.2 `GET /key/info`

Query parameters are ignored unless discovered as required. Response:

```json
{
  "key": "sk-rk_…a1b2",
  "info": {
    "token": "sk-rk_…a1b2",
    "key_id": 42,
    "key_name": "VS Code",
    "user_id": 7,
    "user_email": "developer@example.com",
    "models": ["deepseek-fast", "deepseek-pro"],
    "spend": 12.345678,
    "max_budget": 100.0,
    "budget_reset_at": "2026-09-26T00:00:00Z",
    "blocked": false,
    "router_kely": {
      "quota_scope": "user",
      "quota_period": "day",
      "currency": "USD",
      "usage_nano_usd": 12345678000,
      "quota_nano_usd": 100000000000
    }
  }
}
```

`key` and `info.token` are always masked. LiteLLM-compatible metadata is nested under `info`. `spend` and `max_budget` are JSON numbers derived on this cold path; the integer `router_kely` fields are authoritative. `spend` is the current UTC-day usage, and `budget_reset_at` is the start of the next UTC day. Unlimited quota emits `max_budget: null` and `quota_nano_usd: null`. `quota_period` is always `day` and distinguishes Router Kely's daily window from LiteLLM's monthly `budget_reset_at` convention.

### 14.3 `GET /user/info`

Response:

```json
{
  "user_id": 7,
  "user_email": "developer@example.com",
  "user_role": "user",
  "user_info": {
    "name": "Developer",
    "spend": 12.345678,
    "max_budget": 100.0,
    "budget_reset_at": "2026-09-26T00:00:00Z",
    "models": ["deepseek-fast", "deepseek-pro"]
  },
  "keys": [
    {"key_id":42,"key_name":"VS Code","key_alias":"sk-rk_…a1b2","enabled":true}
  ],
  "router_kely": {
    "quota_scope": "user",
    "quota_period": "day",
    "usage_nano_usd": 12345678000,
    "quota_nano_usd": 100000000000
  }
}
```

Normal users receive 403 if they request another user. Admin lookup accepts numeric `user_id` only.

### 14.4 `POST /key/generate`

Request:

```json
{"key_name":"VS Code","user_id":7}
```

For a normal user, `user_id` must be absent or equal to self. Quota/model/team fields sent by clients are rejected with 400 rather than silently creating unsupported policy. Response is returned once:

```json
{
  "key": "sk-rk_<plaintext>",
  "key_id": 43,
  "key_name": "VS Code",
  "user_id": 7,
  "models": ["deepseek-fast", "deepseek-pro"]
}
```

### 14.5 `POST /key/delete`

Request:

```json
{"key_ids":[43]}
```

Maximum 100 IDs. Revocation is idempotent. Response:

```json
{"deleted_keys":[43]}
```

The currently authenticating key may revoke itself; the response completes and its browser sessions become invalid immediately afterward.

### 14.6 `GET /spend/logs`

This is an aggregate compatibility view, not a per-request log. Parameters: `start_date`, `end_date` (UTC dates, maximum the selected provider's retained history), optional `user_id` for admin. With the default provider, dates older than the retained hourly window return no rows. Response rows are hourly and contain no prompt/request content:

```json
[
  {
    "startTime":"2026-09-25T10:00:00Z",
    "user_id":7,
    "api_key":"sk-rk_…a1b2",
    "model":"deepseek-fast",
    "spend":0.123456,
    "prompt_tokens":1200,
    "completion_tokens":340,
    "api_requests":3
  }
]
```

If actual clients require a different field name/shape, add an adapter only after a captured contract test. Do not add raw request logging to emulate LiteLLM.

### 14.7 `GET /v1/model/info` and `/model/info`

Both paths return the same authenticated response. The response contains a top-level `data` array with one item per configured public alias. Each item contains:

- `model_name`: the public Router Kely alias;
- `litellm_params.model`: the configured upstream model identifier, with no credential or internal host data;
- `model_info.id`: a stable deterministic identifier derived from the alias;
- configured context/output limits and per-token input/cache/output prices;
- `litellm_provider: "deepseek"`, `mode: "chat"`, and the supported capability/parameter flags.

Prices are JSON USD-per-token numbers derived from the authoritative integer nanoUSD-per-million-token configuration. This endpoint performs no upstream or statistics-provider I/O.

### 14.8 `GET /user/daily/activity`

Accept `start_date` and `end_date` as inclusive UTC `YYYY-MM-DD` dates, defaulting to the most recent 30 days and rejecting reversed or greater-than-366-day ranges. The response follows the observed LiteLLM aggregate shape:

```json
{
  "results": [
    {
      "date": "2026-09-25",
      "metrics": {
        "spend": 0.123,
        "prompt_tokens": 1200,
        "completion_tokens": 340,
        "total_tokens": 1540,
        "api_requests": 3,
        "cache_read_input_tokens": 800
      },
      "breakdown": {
        "models": {},
        "providers": {},
        "api_keys": {}
      }
    }
  ],
  "metadata": {
    "total_spend": 0.123,
    "total_prompt_tokens": 1200,
    "total_completion_tokens": 340,
    "total_tokens": 1540,
    "total_api_requests": 3
  }
}
```

The default provider returns only retained in-memory days. A newly started process therefore returns an empty `results` array until observed inference completes and the background aggregate pump publishes a batch. The inference path never calls this endpoint or the statistics provider.

## 15. Configuration

Runtime configuration is read once at startup from `appsettings.json` plus environment variables. Secrets MUST come from environment variables or mounted secret files, not the image. Model changes require restart in MVP. The separate identity file may be atomically replaced and reloaded without restarting.

Example:

```json
{
  "RouterKely": {
    "PublicBaseUrl": "https://llm.example.com",
    "Identity": {
      "Provider": "file",
      "FilePath": "/config/identities.json",
      "Watch": true,
      "MaxUsers": 256,
      "MaxKeys": 1024,
      "EnvironmentAdminUserId": 1,
      "EnvironmentAdminKeyId": 1,
      "EnvironmentAdminName": "Router Kely Admin",
      "EnvironmentAdminEmail": "admin@example.com",
      "EnvironmentAdminKeyEnv": "ROUTERKELY_ADMIN_API_KEY"
    },
    "Upstream": {
      "BaseUrl": "https://api.deepseek.com/v1",
      "ApiKeyEnv": "ROUTERKELY_DEEPSEEK_API_KEY",
      "ResponsesEnabled": false,
      "ConnectTimeoutSeconds": 5,
      "ResponseHeaderTimeoutSeconds": 30,
      "InactivityTimeoutSeconds": 120,
      "PooledConnectionLifetimeMinutes": 15,
      "MaxConnectionsPerServer": 256
    },
    "Models": [
      {
        "Alias": "deepseek-fast",
        "UpstreamModel": "deepseek-chat",
        "InputNanoUsdPerMillion": 0,
        "CachedInputNanoUsdPerMillion": 0,
        "OutputNanoUsdPerMillion": 0
      },
      {
        "Alias": "deepseek-pro",
        "UpstreamModel": "deepseek-reasoner",
        "InputNanoUsdPerMillion": 0,
        "CachedInputNanoUsdPerMillion": 0,
        "OutputNanoUsdPerMillion": 0
      }
    ],
    "MaxRequestBodyBytes": 33554432,
    "MaxModelPrefixBytes": 65536,
    "MaxConcurrentRequests": 256,
    "MaxConcurrentRequestsPerUser": 32,
    "Statistics": {
      "Provider": "memory",
      "HourlyRetentionHours": 72,
      "DailyRetentionDays": 7,
      "MaxBytes": 16777216
    },
    "Compatibility": {
      "EnableUnversionedInferenceAliases": true,
      "EnableSpendLogs": true
    }
  }
}
```

Zero prices are permitted only in development. Production startup fails if any enabled model lacks reviewed, nonnegative prices. Pricing is operator-supplied; Router Kely never scrapes mutable provider pricing.

Environment overrides use double underscores, for example `RouterKely__Upstream__BaseUrl`. Log the effective non-secret configuration at startup with secrets redacted.

The default identity file has this shape:

```json
{
  "schemaVersion": 1,
  "version": 12,
  "users": [
    {
      "id": 7,
      "name": "Developer",
      "email": "developer@example.com",
      "role": "user",
      "enabled": true,
      "quotaNanoUsd": 100000000000
    }
  ],
  "keys": [
    {
      "id": 42,
      "userId": 7,
      "name": "VS Code",
      "sha256": "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
      "displayPrefix": "sk-rk_abcd",
      "lastFour": "a1b2",
      "enabled": true
    }
  ]
}
```

The environment administrator is merged into this document in memory after validation. Its reserved IDs MUST NOT appear in the file. `quotaNanoUsd` omitted or `null` means unlimited. Timestamps are intentionally absent from the default identity schema unless a demonstrated UI requirement justifies them.

## 16. Security requirements

- TLS is mandatory outside local development. Router Kely may terminate TLS or run behind a trusted TLS reverse proxy.
- Configure trusted proxy networks explicitly; otherwise ignore forwarded client identity headers.
- Never log authorization headers, cookies, request/response bodies, query strings containing secrets, upstream credentials, or generated plaintext keys.
- Structured logs use numeric user/key IDs, public alias, status class, duration bucket, and request ID only.
- Optional database adapters use parameterized SQL exclusively and a least-privilege database role.
- Container runs as a non-root UID, read-only root filesystem, no privilege escalation, and all Linux capabilities dropped. The default provider receives write access only to the directory containing its mounted identity file so atomic replacement is possible.
- Upstream host is fixed by configuration. Client input cannot select scheme, host, port, or path.
- Reject request `Content-Encoding` other than absent/identity; decompression bombs are therefore impossible on the request path.
- Enforce request/header/concurrency limits before expensive work.
- UI renders all user-supplied names/emails with HTML escaping and sets a restrictive CSP; styles load only from the same origin, with no inline styles or third-party scripts, fonts, analytics, or assets.
- UI cookies are `Secure`, `HttpOnly`, and `SameSite=Strict`. State changes require CSRF defense.
- Key creation, revocation, user/role/quota/status changes, and bootstrap actions emit structured security audit events. The default provider relies on the external log sink for retention; it does not maintain a second audit store.
- At least one enabled admin must remain. Demoting/disabling the last enabled admin is rejected atomically.
- Use constant-size generic authentication errors; do not reveal whether a user or key exists.
- Dependency and container vulnerability scanning is required in CI. Secrets scanning is required on the repository.
- Rotate the upstream key by updating the mounted secret and restarting. Router Kely keys are individually revocable.

## 17. Observability and operations

### 17.1 Health

| Endpoint | Meaning |
|---|---|
| `GET /health/live` | Process event loop is alive. No dependency calls. |
| `GET /health/ready` | Valid identity snapshot loaded; bounded statistics state healthy; selected optional providers healthy enough for their declared guarantees. |

Readiness response is tiny JSON with `status`, `snapshot_version`, `identity_provider`, `statistics_provider`, and `usage_observation`. Optional providers may add one bounded status field. It never includes secrets. A transient upstream outage does not make the process unready; it is visible in metrics.

### 17.2 Metrics

Expose Prometheus text format at `/metrics`, unauthenticated only on a private management listener or protected by network policy. Required bounded-cardinality metrics:

```text
router_kely_http_requests_total{route,method,status_class}
router_kely_inference_requests_total{endpoint,model,outcome}
router_kely_inference_active{model}
router_kely_proxy_overhead_seconds{endpoint,phase}
router_kely_upstream_duration_seconds{endpoint,model}
router_kely_stream_duration_seconds{endpoint,model}
router_kely_request_bytes_total{endpoint}
router_kely_response_bytes_total{endpoint}
router_kely_usage_tokens_total{model,type}
router_kely_usage_cost_nano_usd_total{model}
router_kely_usage_missing_total{endpoint,model}
router_kely_quota_rejections_total
router_kely_auth_failures_total{reason}
router_kely_statistics_buckets{period}
router_kely_statistics_retention_evictions_total{period}
router_kely_snapshot_version
router_kely_snapshot_reload_total{outcome}
router_kely_process_working_set_bytes
router_kely_gc_allocated_bytes_total
router_kely_gc_collections_total{generation}
```

Never label metrics by user ID, key ID, email, request ID, raw path, IP address, or error message. Per-user usage belongs in the selected statistics provider/UI, not Prometheus labels.

### 17.3 Logs

- Default production level is `Information` for lifecycle/config version/accounting summary and `Warning` for degraded behavior.
- Successful inference requests do not emit one log event each. Metrics and aggregates cover them.
- Errors are rate-limited/grouped by stable reason.
- Exceptions are not used for expected authentication, quota, limit, cancellation, or 4xx control flow.
- A temporary diagnostic mode may sample metadata for at most 15 minutes, never bodies or secrets, and auto-disables.

### 17.4 Shutdown

On SIGTERM:

1. Mark unready immediately.
2. Stop accepting new inference requests.
3. Allow active streams to drain for the orchestrator's grace period.
4. Dispose the upstream handler and exit. The default statistics provider does not flush.

## 18. Performance budget and benchmark contract

### 18.1 Required budget

Measured on Linux, release Native AOT, one pinned vCPU, container memory limit 250 MiB, logging at production defaults, default file/memory providers, and a same-host deterministic mock upstream:

| Metric | Requirement |
|---|---:|
| Incremental proxy latency, 1-KiB non-stream response, p50 | `< 0.25 ms` |
| Incremental proxy latency, 1-KiB non-stream response, p99 | `< 1.0 ms` |
| Per-SSE-chunk incremental forwarding delay, p99 | `< 1.0 ms` |
| State-provider operations per inference request | `0` |
| Full request/response buffering | `0` |
| Steady idle RSS after warm-up | `< 100 MiB` |
| RSS at 64 concurrent streams | `< 180 MiB` |
| RSS at 256 concurrent streams | `< 250 MiB` |
| Native AOT executable size | `<= 20 MiB` |
| Native AOT publish file count | `1` |
| Sustained throughput for 1-KiB mock responses | `>= 5,000 req/s` or CPU saturation without queue instability |
| Auth lookup complexity | O(1) expected |
| Accounting enqueue/write wait on request path | `0` |
| Allocations while forwarding each additional response chunk | `0 B` target; regression requires justification |
| Process allocations per routed 1-KiB non-stream request | `<= 8 KiB` |
| Gen 2 collections during 10-minute 64-stream test | `0` target |

“Incremental proxy latency” is the gateway result minus the direct-to-mock-upstream baseline collected in the same run, using an interleaved test to remove scheduler/network drift. It excludes DeepSeek WAN/model time. The sub-millisecond claim MUST always be stated with this definition.

The 5,000 req/s figure is a synthetic regression target, not expected LLM traffic. If the chosen CI runner cannot reach it, establish a pinned baseline and require no more than 5% throughput regression while retaining the latency/RSS hard limits.

### 18.2 Benchmark scenarios

The repository MUST include a repeatable harness with a zero-delay and scripted-delay upstream supporting HTTP/1.1, HTTP/2, SSE, large bodies, slow consumers, cancellation, and usage trailers/events.

Run at concurrency 1, 16, 64, and 256:

1. Authenticated `GET /v1/models`.
2. 1-KiB Chat Completions non-stream request/response.
3. 32-KiB request with model early and a 1-KiB response.
4. SSE: 100 chunks × 64 bytes at 10-ms intervals.
5. 1-MiB streamed response.
6. Slow client consuming 1 KiB/s to verify bounded backpressure.
7. Invalid key and over-quota rejection.
8. Statistics rollover, retention eviction, and concurrent UI reads.
9. Snapshot reload while traffic is active.
10. Cancellation before upstream headers and mid-SSE stream.

Record requests/s, CPU/request, p50/p95/p99/p99.9, TTFB delta, chunk delay, allocated bytes/request, GC counts/pause, RSS, socket count, and accounting lag.

### 18.3 Regression gates

CI compares the candidate with the default branch on the same runner. Fail when:

- p99 incremental overhead exceeds 1 ms or regresses by >10%;
- throughput regresses by >5% without an approved explanation;
- allocation/request grows by >256 B or per-chunk allocation becomes nonzero;
- process allocation exceeds 8 KiB per routed request in the default smoke scenario;
- RSS grows by >10 MiB or exceeds the hard budget;
- the Native AOT executable exceeds 20 MiB or its publish directory contains anything other than the single executable;
- any inference-path state-provider call or file/database I/O appears;
- Native AOT/trim warnings appear.

Benchmark noise must be controlled with warm-up, CPU affinity where available, repeated samples, and median-of-runs reporting. Store machine/runtime metadata with results.

The default `scripts/tests.sh` run publishes and executes the release Native AOT binary in a temporary directory, then runs a short, concurrency-1 end-to-end smoke benchmark against a local deterministic upstream. It prints interleaved direct/upstream and routed p50/p95/p99 latency, incremental p50/p95/p99 overhead, sequential throughput, process-wide allocated bytes per routed request, idle working set after load, executable size, and publish-file count. The `<= 8 KiB/request` allocation, `< 100 MiB` idle working-set, `<= 20 MiB` executable-size, and exactly-one-published-file limits are always enforced so local and CI runs cannot silently grow the footprint. The harness enables `ROUTERKELY_BENCHMARK_METRICS=true`, which conditionally exposes `/internal/benchmark/allocated-bytes`; production deployments MUST NOT enable it. The smoke also verifies that per-user concurrency saturation rejects immediately without queueing. Latency results are informational on ordinary developer machines; setting `ROUTERKELY_PERF_ENFORCE=true` also enforces the p50 and p99 incremental latency limits on controlled CI runners. `ROUTERKELY_PERF_WARMUP` and `ROUTERKELY_PERF_SAMPLES` may increase sample counts without changing the scenario.

The harness may set `Upstream.AllowInsecureLoopback=true` only for a loopback HTTP mock. The option never permits plaintext traffic to a non-loopback address and defaults to false.

## 19. Testing strategy

### 19.1 Unit/property tests

- JSON transformer across every segment boundary, escape sequence, whitespace form, nested misleading `model`, duplicate/missing/non-string model, malformed UTF-8/JSON, and prefix/body limits.
- Key format, random generation, hashing, equality, lookup, revocation, and malformed header cases.
- Integer price arithmetic, round-up behavior, cached tokens, overflow rejection, and day boundaries.
- Atomic quota behavior with concurrent completions and rollover.
- SSE/JSON usage observer across every byte boundary and irrelevant content containing the word `usage`.
- Header stripping and forwarding rules.
- Role/ownership authorization matrix.
- CSRF/session expiry/revocation.

Property tests MUST assert that, for every valid generated request within limits, transformed output differs from input only in the top-level model string bytes.

### 19.2 Integration tests

Run the published AOT binary with the default file/memory providers and the mock upstream. Verify:

- All endpoint/status/schema contracts in this document.
- Byte-for-byte preservation of response bodies and SSE framing.
- Unknown request fields survive unchanged.
- Client disconnect cancels upstream.
- Identity-file replacement reloads atomically; malformed or stale files preserve the last valid snapshot.
- UI mutations atomically replace the identity file and survive restart.
- Restart preserves file-backed identity but resets all default statistics and consumed-quota counters.
- Retention remains within configured hour/day and cardinality bounds during rollover and concurrent reads.
- No plaintext key appears in the identity file, logs, metrics, crash output, or HTML after the one-time response.
- No inference-path state-provider method or file I/O is observed.

### 19.3 Real-upstream contract tests

A manually triggered, secret-backed suite against DeepSeek verifies:

- Both mapped upstream models.
- Chat Completions streaming and non-streaming.
- Tool calls and unknown extension fields pass through.
- Usage field names and cache-token semantics.
- Error and rate-limit pass-through.
- Connection reuse and chosen HTTP protocol.
- Responses endpoint only if enabled.

These tests use minimal prompts and never run on untrusted pull requests.

## 20. Discovering the compatibility surface

Do not guess and implement all of LiteLLM. Observe actual traffic, build contract fixtures, and add only what is used.

### 20.1 Observation method

For at least seven representative working days before cutover:

1. Put a trusted reverse proxy or an access-log middleware in front of the existing LiteLLM deployment.
2. Record metadata only: UTC time, HTTP method, normalized path template, status, content type, response streaming flag, client user-agent family/version, request/response byte counts, and latency.
3. Never record authorization headers, cookies, query-string values, or request/response bodies.
4. Normalize numeric/UUID/key-looking path segments to placeholders and hash any unavoidable client identifier with a rotating salt.
5. Group counts by method + normalized path + client family. Flag endpoints outside Router Kely's proposed list.
6. Exercise every approved VS Code plugin/client workflow: start, model discovery, chat, streaming, tool calls, view usage/quota, create/revoke key if supported, cancellation, and errors.
7. For each extra endpoint, capture its contract in a sanitized local mock: method, path, required headers, query parameter names (not values), minimal redacted request shape, status, response field names/types, and streaming behavior.

Body capture is disabled by default. If metadata cannot reveal a required schema, reproduce the call in an isolated test account with synthetic content and explicitly enable a one-request sanitized capture. Review and delete the raw capture after converting it to a fixture.

### 20.2 Admission rule for new endpoints

An endpoint is added only when all are true:

- A named supported client invokes it in a required workflow.
- Absence causes a user-visible failure or material loss of required function.
- The smallest compatible response/request subset is documented as a fixture.
- It does not require an explicit non-goal such as teams or raw prompt logs.
- Its hot-path cost is measured if it touches inference.

If a client probes an endpoint but works after a 404/501, record the probe and do not implement it. If several response fields are unused, return only the minimal observed stable set plus fields already specified here.

### 20.3 Shadow and canary validation

Inference requests must never be duplicated to DeepSeek merely for shadowing because that changes cost and side effects. Instead:

- Replay sanitized synthetic fixtures against Router Kely in CI.
- Route one consenting user/client to Router Kely as a canary.
- Compare endpoint/status/latency metadata between LiteLLM and Router Kely.
- Increase canary users only after one week without an unexplained compatibility error.

## 21. Acceptance criteria

The MVP is releasable only when all items pass.

### 21.1 Functional

- [ ] `deepseek-fast` and `deepseek-pro` appear in model discovery and map to configured DeepSeek model IDs.
- [ ] Chat Completions works for streaming, non-streaming, tool calls, cancellation, upstream errors, and unknown JSON fields.
- [ ] Responses forwarding works when enabled, or returns the specified deterministic 501 when disabled.
- [ ] Both `/v1/...` and configured unversioned compatibility aliases behave identically.
- [ ] Admin can create/edit/disable users, set user quota/role, and create/rename/revoke any key.
- [ ] User can view own usage/quota and create/rename/revoke only own keys.
- [ ] No key/team/model quota exists; user quota is the only budget value.
- [ ] Plaintext keys are shown once and cannot be recovered.
- [ ] Default current-day usage and quota consumption reset on restart, and the UI labels this behavior clearly.
- [ ] LiteLLM profile endpoints return documented schemas and pass captured coding-client fixtures.
- [ ] `/ui` primary workflows function without JavaScript.

### 21.2 Data-plane invariants

- [ ] Zero state-provider calls and zero file/database I/O per inference request, proven by instrumentation/test.
- [ ] No whole-body buffering and no OpenAI DTO/DOM deserialization.
- [ ] Only top-level `model` bytes change in a proxied request.
- [ ] Response and SSE bytes are forwarded unchanged.
- [ ] Accounting and metrics never delay request completion.
- [ ] Long-lived upstream connections are reused.
- [ ] Backpressure and cancellation remain bounded under slow/disconnected clients.
- [ ] Successful inference produces no per-request information log allocation/event.

### 21.3 Security and reliability

- [ ] Key hashes, role checks, CSRF, session expiry, ownership, last-admin protection, and security-audit event tests pass.
- [ ] No secrets/content appear in logs, metrics, statistics aggregates, identity files, or error responses.
- [ ] Identity-file corruption/reload failure preserves the last valid snapshot and reports degraded readiness.
- [ ] Statistics retention and configured identity cardinality remain strictly bounded.
- [ ] AOT publish has zero trim/AOT warnings; container and dependency scans have no unwaived critical finding.
- [ ] Graceful shutdown and documented restart-reset behavior tests pass.

### 21.4 Performance

- [ ] Every hard metric in section 18.1 passes on the reference environment.
- [ ] A 30-minute soak at 64 concurrent SSE streams shows stable RSS, handle/socket counts, and bounded statistics state.
- [ ] A burst to 256 streams remains under 250 MiB with no OOM, deadlock, or unbounded queue.
- [ ] Benchmark results and comparison with the default branch are attached to the release.

## 22. Delivery plan

### Phase 0 — evidence and harness

- Deploy metadata-only LiteLLM traffic observation.
- Build mock upstream and benchmark harness.
- Commit captured/sanitized client contract fixtures.
- Establish direct and proxy baseline measurements.

### Phase 1 — streaming data plane

- Native AOT minimal host, authentication snapshot, models endpoint.
- Streaming JSON model transformer.
- Chat Completions/Responses forwarding, header policy, cancellation, SSE.
- Usage observer and integer pricing.
- Performance tests must pass before control-plane work expands.

### Phase 2 — lightweight state and control plane

- File-backed users/keys/quotas, environment administrator, and atomic snapshot reload.
- Bounded in-memory statistics, UTC rollover, retention, and explicit restart-reset UX.
- `/ui`, browser sessions, roles, and structured security-audit events.
- Minimal LiteLLM profile endpoints.

### Phase 3 — migration

- Import or recreate users and quotas.
- Issue Router Kely keys; existing LiteLLM plaintext keys cannot be imported unless their original plaintext is available. Prefer rotation.
- Run synthetic compatibility suite, then one-user canary.
- Expand canary, monitor missing usage and unknown endpoints.
- Switch hostname/base URL and keep LiteLLM available for rollback during the agreed observation window.

## 23. Migration and compatibility policy

### 23.1 Data migration

Migrate only:

- user display name and email;
- role mapping to `admin`/`user`;
- enabled status;
- one daily user quota;
- optional opening current-day usage balance with an audited migration batch.

Do not migrate teams, memberships, team/key/model budgets, routing rules, fallbacks, provider objects, raw spend logs, or LiteLLM internal IDs. If several LiteLLM budgets exist, an operator must choose the single user quota explicitly; Router Kely does not infer precedence.

A LiteLLM monthly budget is not equivalent to a Router Kely daily quota: copying a monthly amount into a daily value grants roughly 30 times more spend. The operator MUST set the daily value explicitly, and the migration report MUST show the monthly source amount and the chosen daily value.

### 23.2 Key migration

Because secure systems store hashes and different systems may hash/format keys differently, key portability is not assumed. Default migration creates new Router Kely keys and revokes LiteLLM keys after cutover. A one-time key-hash import tool is permitted only if a security review proves the incoming hash represents the exact bearer token with compatible SHA-256 semantics; plaintext must never be exported for migration.

### 23.3 Compatibility versioning

- OpenAI-compatible paths remain stable within a major Router Kely version.
- LiteLLM compatibility behavior is named/versioned in documentation, even though paths remain conventional.
- Additive response fields are allowed. Removing/renaming fields or changing types requires a major compatibility version and captured-client tests.
- Unsupported fields sent to inference are forwarded; unsupported control-plane policy fields are rejected.
- A compatibility endpoint without an observed consumer is a candidate for removal at the next major version.

### 23.4 Rollback

Rollback changes routing/DNS to LiteLLM; it does not replay requests. Maintain both gateways' keys during canary or use separate client profiles. Export Router Kely aggregate usage as CSV/JSON if finance needs a combined reporting period, but do not attempt bidirectional live synchronization.

## 24. Repository and implementation constraints

Recommended layout:

```text
/src/RouterKely                 executable, routes, proxy, UI, workers
/src/RouterKely.Core            allocation-sensitive value types/state machines if separation helps AOT
/tests/RouterKely.Unit
/tests/RouterKely.Integration
/tests/RouterKely.Performance
/deploy/Dockerfile
/deploy/kubernetes           optional manifests
/docs/compatibility
/plugins/RouterKely.Postgres optional post-MVP adapter; absent from the default build
```

Keep project count low; separation must not create abstraction overhead. The implementation should favor explicit code over generic frameworks on the data path.

Required engineering rules:

- No LINQ, regex, interpolation-based success logging, `MemoryStream`, body-to-string conversion, or exception-driven expected flow on the inference path.
- Use spans, sequences, pipelines, pooled buffers, `ValueTask`, and source generation only where measurement/test supports correctness and lower allocation.
- Pool ownership must be explicit and exception/cancellation safe.
- Avoid async state-machine creation in tight per-segment loops where a synchronous fast path is available.
- All dictionaries and route/config objects used by inference are constructed before publication.
- No user-supplied value may create unbounded metric/log/accounting cardinality.
- Optimize only against profiler/benchmark evidence after preserving the architectural invariants.

## 25. Open items requiring operator values, not design decisions

Implementation can proceed while these deployment values are supplied later:

- Production public base URL.
- DeepSeek base URL and API secret.
- Reviewed input/cached-input/output prices for both upstream model mappings.
- Whether DeepSeek Responses is enabled in the target account.
- Trusted reverse-proxy network ranges and management-listener exposure.
- Final user quotas and bootstrap administrator identity.
- Identity file mount/path, environment administrator identity/key secret, and retention values if defaults conflict with policy.

No other product ambiguity should block MVP implementation. When real traffic contradicts this document, use the evidence process in section 20 and amend the smallest possible compatibility surface.

## Appendix A — implementation references

- Microsoft, ASP.NET Core Native AOT: <https://learn.microsoft.com/aspnet/core/fundamentals/native-aot?view=aspnetcore-10.0>
- Microsoft, .NET garbage collector configuration: <https://learn.microsoft.com/dotnet/core/runtime-config/garbage-collector>
- OpenAI, Models API object/list contract: <https://platform.openai.com/docs/api-reference/models/object>
- LiteLLM, AI Gateway overview: <https://docs.litellm.ai/docs/simple_proxy>
- LiteLLM, virtual keys: <https://docs.litellm.ai/docs/proxy/virtual_keys>

These references inform compatibility and runtime choices; this specification is the normative contract for Router Kely.
