# MCP OAuth2 Gateway — POC

Test

A .NET 10 proof-of-concept showing how OAuth2 flows work in a **multi-tenant Gateway MCP server** that proxies requests to downstream MCP servers on behalf of multiple users.

The key ideas demonstrated:

- An AI agent connects to a single **Gateway MCP server** and calls a `relay_call` tool
- The gateway is protected by OAuth2 — the agent must authenticate first
- Each user independently registers and authorizes downstream servers via a **web dashboard**
- The gateway stores per-user tokens and relays tool calls using those tokens — the agent never deals with downstream auth directly
- Every OAuth client registration is **dynamic** — no pre-configured `client_id` or `client_secret` anywhere

---

## Architecture

```
┌─────────────────────────────────────────────────────────────────────┐
│                         Two OAuth Flows                             │
│                                                                     │
│  Flow 1 — Dashboard (PKCE, browser)                                 │
│  ┌───────────┐  login  ┌──────────────────┐  JWT  ┌─────────────┐   │
│  │ Browser   │ ──────► │ Gateway Keycloak │ ────► │  Dashboard  │   │
│  │ Dashboard │         │  (port 8080)     │       │  /index.html│   │
│  └───────────┘         └──────────────────┘       └─────────────┘   │
│                                                           │         │
│  Flow 2 — Downstream connect (Auth Code + DCR, popup)     │         │
│  ┌───────────┐  popup  ┌──────────────────┐  token ┌──────▼──────┐  │
│  │ Browser   │ ──────► │ Discord Keycloak │ ─────► │ GatewayMcp  │  │
│  │  (popup)  │         │  (port 8081)     │        │  backend    │  │
│  └───────────┘         └──────────────────┘        └─────────────┘  │
└─────────────────────────────────────────────────────────────────────┘

┌─────────────────────────────────────────────────────────────────────┐
│                         relay_call                                  │
│                                                                     │
│  ┌──────────┐  relay_call  ┌─────────────┐  tool call ┌──────────┐  │
│  │ AI Agent │ ───────────► │  GatewayMcp │ ─────────► │DiscordMcp│  │
│  │  (JWT)   │              │  (port 7071)│  (stored   │(port 7072│  │
│  └──────────┘              └─────────────┘   token)   └──────────┘  │
└─────────────────────────────────────────────────────────────────────┘

┌─────────────────────────────────────────────────────────────────────┐
│          Flow 3 — relay_call with MCP URL-mode elicitation          │
│                 (mid-call authorization, no dashboard)              │
│                                                                     │
│  ┌──────────┐  relay_call  ┌─────────────┐  -32042+URL ┌─────────┐  │
│  │ AI Agent │ ───────────► │  GatewayMcp │ ──────────► │   MCP   │  │
│  │  (JWT)   │ ◄─────────── │  (port 7071)│             │  Client │  │
│  └──────────┘  auto-retry  └──────┬──────┘             └────┬────┘  │
│                                   │ /connect/{serverId}      │      │
│                                   │ (HMAC-signed URL)  user opens   │
│                            ┌──────▼──────┐             ┌────▼────┐  │
│                            │  Discord    │ ◄─────────── │ Browser │  │
│                            │  Keycloak   │ ────────────►│         │  │
│                            │ (port 8081) │  auth code   └─────────┘  │
│                            └──────┬──────┘                          │
│                                   │ tokens stored                   │
│                            ┌──────▼──────┐                          │
│                            │  GatewayMcp │──elicitation/complete──► │
│                            │  (port 7071)│          MCP Client      │
│                            └─────────────┘                          │
└─────────────────────────────────────────────────────────────────────┘
```

**Flow 1** is a standard browser PKCE flow. The dashboard SPA authenticates the user against the Gateway Keycloak and receives a short-lived JWT, which is used to call the gateway's REST API (`/api/servers`, etc.).

**Flow 2** is triggered when a user clicks **Connect** on a registered downstream server. The gateway backend creates an MCP client targeting DiscordMcp, which kicks off Dynamic Client Registration + an authorization code flow. The gateway intercepts the redirect URL, returns it to the dashboard, and the dashboard opens it in a **popup**. The user logs in to the **Discord** Keycloak in the popup — this is an entirely separate identity domain from the Gateway Keycloak; the two logins are independent and do not need to match. The authorization code is sent to `/api/oauth/callback/{serverId}` and the gateway exchanges it for tokens, which are stored on disk keyed by the **gateway** user's `sub` and the server ID.

**relay_call** uses the `sub` claim from the AI agent's JWT to look up that user's stored downstream tokens and proxy the tool call.

---

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [Docker Desktop](https://www.docker.com/products/docker-desktop/)

---

## Quick Start

**1. Start both Keycloak instances**

```bash
docker compose up -d
```

Wait ~30 seconds for Keycloak to finish importing the realms.

**2. Start DiscordMcp**

```bash
dotnet run --project DiscordMcp
```

**3. Start GatewayMcp**

```bash
dotnet run --project GatewayMcp
```

GatewayMcp is now running at `http://localhost:7071`. The dashboard is at `http://localhost:7071/dashboard`.

---

## Walkthrough

### Multi-user dashboard demo

This demonstrates that each user gets an isolated server list and independent downstream tokens.

**As Alice:**

1. Open `http://localhost:7071/dashboard` — log in as `alice` / `password`
2. Click **Add Server**, enter name `DiscordMcp` and URL `http://localhost:7072/`
3. Click **Connect** — a popup opens to the **Discord** Keycloak login page (a separate identity domain)
4. Log in with any valid Discord Keycloak account — e.g. `alice` / `password`
5. The popup closes; the server status changes to **connected**
6. Alice can now relay calls to DiscordMcp tools through the gateway

**As Bob (in a separate incognito window):**

1. Open `http://localhost:7071/dashboard` in a new incognito window — log in as `bob` / `password`
2. Bob sees an empty server list — completely isolated from Alice's
3. Add the same DiscordMcp server and click **Connect**
4. In the Discord Keycloak popup, Bob can log in as any Discord account — including `alice` if he has those credentials; the gateway doesn't enforce a link between the two identity domains
5. Bob now has his own independent connection to DiscordMcp with his own stored tokens

Both users' tokens are stored in `GatewayMcp/data/` as `tokens/{userId}-{serverId}.json`. The gateway uses the `sub` claim from the **Gateway** JWT to namespace all per-user data — the Discord identity used in the popup is independent.

---

### CLI client demo (GatewayMcpClient)

This demonstrates an AI agent (or CLI tool) connecting to the gateway via OAuth and calling `relay_call`.

```bash
dotnet run --project GatewayMcpClient
```

The client:
1. Serves a CIMD document at `http://localhost:1180/client-metadata/cimd-client.json` describing itself
2. Connects to `http://localhost:7071/` — the gateway responds with a `401` and a `WWW-Authenticate` header pointing to the Gateway Keycloak
3. The MCP SDK discovers the authorization server, **dynamically registers** a new OAuth client via DCR, then builds the authorization URL
4. The browser opens — log in as any user (`alice`, `bob`, or `charlie`)
5. The client calls `say_hello`, `add`, `get_current_time`, and then `relay_call` to invoke a DiscordMcp tool via the gateway

---

### Cursor / VS Code

GatewayMcp is a standard HTTP MCP server — any MCP-capable IDE can connect to it.

```json
{
  "mcpServers": {
    "gateway": {
      "url": "http://localhost:7071/"
    }
  }
}
```

When the IDE connects for the first time, GatewayMcp returns a `401` with a `WWW-Authenticate` header pointing to the Gateway Keycloak. A compliant MCP client will:

1. Discover the authorization server via `/.well-known/oauth-protected-resource`
2. Dynamically register a new OAuth client at the Gateway Keycloak DCR endpoint
3. Open a browser window for the user to log in (`alice`, `bob`, or `charlie`)
4. Attach the resulting token to future requests automatically

After authenticating, the `relay_call` tool (and other gateway tools) appear in the IDE's agent/chat interface. Example prompt:

```
Use relay_call to invoke the "list_channels" tool on server "DiscordMcp"
```

> **Before calling `relay_call`:** the user must have **registered** the downstream server via the dashboard (`http://localhost:7071/dashboard`). **Authorization (connecting) is not required in advance** — if no usable token exists when `relay_call` is invoked, the gateway returns a JSON-RPC -32042 elicitation with a signed authorization URL. The MCP client presents the URL, the user completes the browser OAuth flow, and the tool call is retried automatically. Alternatively, the user can connect in advance through the dashboard as usual.

---

## How It Works

### Flow 1 — Dashboard ↔ Gateway Keycloak (PKCE)

The dashboard SPA (`wwwroot/index.html`) implements PKCE entirely in JavaScript:

- Generates a `code_verifier` / `code_challenge` pair
- Redirects to `http://localhost:8080/realms/mcp/protocol/openid-connect/auth`
- Keycloak redirects back to `/dashboard?code=...`
- The SPA exchanges the code for tokens at the token endpoint
- The resulting access token (JWT) is stored in `sessionStorage` and sent as `Authorization: Bearer` on every API call

The JWT contains a `sub` claim (user ID) and `preferred_username`. GatewayMcp validates it with `AddJwtBearer` pointed at the Gateway Keycloak. The `sub` claim is the key used to namespace all per-user data.

### Flow 2 — Gateway backend ↔ Discord Keycloak (DCR + Auth Code)

When a user clicks **Connect**:

1. The dashboard calls `GET /api/servers/{id}/connect`
2. The gateway creates a short-lived `McpClient` targeting DiscordMcp with `ClientOAuthOptions` — this triggers the MCP SDK's OAuth handshake
3. The SDK discovers DiscordMcp's authorization server via `/.well-known/oauth-protected-resource` → `/.well-known/openid-configuration`
4. The SDK **dynamically registers** a new client at the Discord Keycloak DCR endpoint — no pre-configured credentials needed
5. The SDK calls `AuthorizationRedirectDelegate` with the authorization URL — the gateway captures this URL and returns it to the dashboard as JSON
6. The dashboard opens the URL in a popup window; the user logs in
7. Discord Keycloak redirects to `http://localhost:7071/api/oauth/callback/{serverId}`
8. The gateway resolves the pending `TaskCompletionSource` with the authorization code; the SDK exchanges the code for tokens
9. Tokens are persisted to disk via `FileTokenCache` (`tokens/{userId}-{serverId}.json`)

### Dynamic Client Registration (DCR)

Neither GatewayMcp nor GatewayMcpClient has a pre-configured `client_id` or `client_secret`. All OAuth clients are registered on-the-fly by the MCP SDK using **RFC 7591 Dynamic Client Registration**:

1. The SDK fetches the MCP server's `/.well-known/oauth-protected-resource` to find the authorization server
2. It reads the authorization server's `/.well-known/openid-configuration` to find the `registration_endpoint`
3. It POSTs a client metadata document (redirect URIs, grant types, scopes) to that endpoint
4. Keycloak creates the client and returns an ephemeral `client_id`
5. The SDK uses that `client_id` for the rest of the authorization code flow

**GatewayMcpClient** takes this a step further by also providing a `ClientMetadataDocumentUri` — a hosted JSON document (CIMD, Client Initiation Metadata Document) served at `http://localhost:1180/client-metadata/cimd-client.json`. This is the MCP spec's preferred pattern: the client self-describes via a public URL, and the authorization server can reference it. The DCR registration payload includes this URI so the AS knows where to find the client's canonical metadata.

**What enables DCR in Keycloak** is the `trusted-hosts` client registration policy configured in both realm JSON files:

```json
{
  "name": "Trusted Hosts",
  "providerId": "trusted-hosts",
  "subType": "anonymous",
  "config": {
    "trusted-hosts": ["localhost", "127.0.0.1", "192.168.65.1"],
    "host-sending-registration-request-must-match": ["true"],
    "client-uris-must-match": ["false"]
  }
}
```

Without this, Keycloak rejects all anonymous DCR requests.

### Token storage and multi-tenancy

`FileTokenCache` implements the MCP SDK's `ITokenCache` interface. Each `(userId, serverId)` pair gets its own file:

```
GatewayMcp/
  data/
    servers-{userId}.json        ← registered server entries per user
  tokens/
    {userId}-{serverId}.json     ← OAuth tokens per user per server
```

On `relay_call`, the gateway:
1. Reads the `sub` claim from the incoming JWT to identify the caller
2. Looks up that user's entry for the requested server name
3. Creates an `McpClient` with the stored `FileTokenCache` — the SDK automatically attaches the cached token (refreshing if expired)
4. Forwards the tool call and returns the result

If the token is missing or expired, the gateway throws `UrlElicitationRequiredException` — the MCP client receives JSON-RPC error **-32042** containing a signed authorization URL, handles the browser flow automatically, and retries the tool call once tokens are available. See **Flow 3** below.

### Flow 3 — MCP URL-mode elicitation (mid-call authorization)

This is the protocol-native path for an AI agent to authorize a downstream server without ever touching the dashboard.

**Trigger:** `relay_call` is invoked and no usable token exists for the requested server (either the token file is absent, or `HasUsableTokens()` determines it is within 60 seconds of expiry).

**Steps:**

1. The gateway generates a random `elicitationId`, builds a signed `/connect/{serverId}?token=...&elicitationId=...` URL, stores `(userId, serverId, McpServer)` in `_pendingElicitations`, and throws `UrlElicitationRequiredException`
2. The MCP SDK converts this to JSON-RPC error **-32042** with an `ElicitRequestParams` (`mode=url`) carrying the connect URL — the MCP client (e.g. Cursor) presents the URL to the user
3. The user opens the URL in a browser; the gateway verifies the HMAC-SHA256 signed token (payload: `{userId}:{serverId}:{elicitationId}:{expUnix}`, 10-minute expiry) and redirects the browser directly to Keycloak — the same DCR + authorization code flow as Flow 2
4. After the user logs in, Keycloak redirects to `/api/oauth/callback/{serverId}`; the gateway exchanges the code for tokens and persists them via `FileTokenCache`
5. The gateway scans `_pendingElicitations` for all entries matching the server, removes them, and fire-and-forgets `notifications/elicitation/complete` to each waiting `McpServer` instance
6. The MCP client receives the notification and **automatically retries** the original `relay_call` — this time `HasUsableTokens()` returns `true` and the relay succeeds

**Late expiry:** If the token file exists but the token is rejected at use time (expired between the `HasUsableTokens()` check and the actual relay attempt), the same elicitation is triggered from the `catch` block inside `RelayCallAsync`, producing a re-authorization URL.

---

## Service Reference

| Service | URL |
|---|---|
| GatewayMcp | `http://localhost:7071` |
| GatewayMcp dashboard | `http://localhost:7071/dashboard` |
| GatewayMcp OAuth callback | `http://localhost:7071/api/oauth/callback/{serverId}` |
| GatewayMcp elicitation connect | `http://localhost:7071/connect/{serverId}` |
| Gateway Keycloak | `http://localhost:8080/realms/mcp` |
| DiscordMcp | `http://localhost:7072` |
| Discord Keycloak | `http://localhost:8081/realms/discord` |
| GatewayMcpClient CIMD | `http://localhost:1180/client-metadata/cimd-client.json` |
| GatewayMcpClient callback | `http://localhost:1181/callback` |

---

## Pre-configured Accounts

All passwords are `password`.

**Gateway Keycloak** (`http://localhost:8080`) — used to log in to the dashboard and GatewayMcpClient:

| Username | Email |
|---|---|
| alice | alice@example.com |
| bob | bob@example.com |
| charlie | charlie@example.com |

**Discord Keycloak** (`http://localhost:8081`) — used in the Connect popup when authorizing DiscordMcp:

| Username | Email |
|---|---|
| alice | alice@example.com |
| bob | bob@example.com |
| charlie | charlie@example.com |

Keycloak admin console: `http://localhost:8080` / `http://localhost:8081` — credentials `admin` / `admin`.

---

## Project Layout

```
mcp-auth/
├── docker-compose.yml                  Keycloak services (ports 8080, 8081)
├── keycloak/
│   └── mcp-realm.json                  Gateway realm — PKCE client, mcp:tools scope, 3 users
├── keycloak-discord/
│   └── discord-realm.json              Discord realm — DCR trust policy, discord:tools scope, 3 users
│
├── GatewayMcp/                         Gateway server (port 7071)
│   ├── Program.cs                      JWT auth, REST API endpoints, MCP server setup
│   ├── Tools.cs                        relay_call + utility tools
│   ├── DownstreamMcpRegistry.cs        Per-user server registry, OAuth connect flows (dashboard + elicitation), relay logic
│   ├── FileTokenCache.cs               ITokenCache implementation — persists tokens to disk
│   ├── DownstreamServerEntry.cs        Server entry model
│   └── wwwroot/index.html              PKCE dashboard SPA
│
├── GatewayMcpClient/
│   └── Program.cs                      CLI client — DCR + CIMD + browser OAuth → calls relay_call
│
├── DiscordMcp/                         Downstream MCP server (port 7072)
│   ├── Program.cs                      JWT auth against Discord Keycloak
│   └── Tools.cs                        Stub Discord tools (send_message, list_channels, etc.)
│
└── DiscordMcpClient/
    └── Program.cs                      CLI client connecting directly to DiscordMcp (bypass gateway)
```
