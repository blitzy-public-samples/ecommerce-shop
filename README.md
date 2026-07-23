# Online Web Store

This repository contains the source code for an online web store application built with .NET 5.0 as the backend framework and Angular 11 as the frontend framework. This application allows users to browse products, add them to their cart, and make purchases online.

Note: All changes in building process are documented in `CHANGES.md` file. You can open file for more details on implementation.

## Getting Started

These instructions will get you a copy of the project up and running on your local machine for development and testing purposes.

### Prerequisites

Before you can run the project, you will need to install the following software:

- [.NET 5.0 SDK](https://dotnet.microsoft.com/download/dotnet/5.0)
- [Node.js](https://nodejs.org/) (which includes npm for Angular)
- [Angular CLI](https://angular.io/cli) (version 11)

1. **Clone the repository**

   ```bash
   git clone https://github.com/your-username/your-repository-name.git
   cd your-repository-name
   ```

2. ** Install backend dependencies**
    ```bash
    dotnet restore
    ```

3. **Install frontend dependencies**
    ```bash
    cd client
    npm install --legacy-peer-deps
    ```
    > On npm 7+ (bundled with Node 17+), `--legacy-peer-deps` is required so the Angular 11 peer-dependency graph resolves.

4. **Start the backend server.**
    ```bash
    cd ..
    dotnet run --project API
    ```
    > The solution root has no runnable project, so `--project API` is required. The API auto-applies EF Core migrations and seeds data on startup, then listens on `http://localhost:5000` and `https://localhost:5001` (self-signed dev certificate — use `curl -k` / `-Lk`).

5. **Start the Angular application in separate terminal session.**
    ```bash
    cd client
    NODE_OPTIONS=--openssl-legacy-provider ng serve
    ```
    > On Node 17+ the `NODE_OPTIONS=--openssl-legacy-provider` prefix is required for the Angular 11 (webpack 4) toolchain. The same prefix applies to `ng build` and `ng test`.

## Features
- Authorization and authentication
- Product browsing
- Shopping cart functionalities
- Order checkout and payment processing (demo)
- **Real-Time Inventory & Flash Sale** — live per-product stock counts and time-boxed promotions pushed over SignalR, with reservation-based oversell prevention (see below)
- Dockerized for easy hosting

## Real-Time Inventory & Flash Sale

This store supports **time-boxed flash sales** with **live, per-product stock counts** pushed to the browser in real time, plus a **reservation** mechanism that prevents overselling during high-concurrency sale windows.

- **Live inventory** — the server pushes stock changes to connected product pages over SignalR, so the displayed quantity updates without a page refresh.
- **Flash sales** — a sale defines a discounted price valid only inside a `[startAt, endAt]` window, surfaced on the product page as a promotional banner and a live countdown.
- **Zero oversell** — adding a flash-sale item reserves the requested quantity for a bounded window; concurrent reservations can never allocate more than the sale's `stockAllocation` (guarded by an optimistic-concurrency version token, retried exactly once).
- **Auto-release** — an unfinished reservation expires after its TTL and its held stock is returned to the pool by a background sweep.

> **Base-price checkout (important):** the flash-sale price is a **display-only** overlay on the product page. Order totals are always computed from the product's base `price`. The `/api/products` and `/api/orders` request/response shapes are unchanged.

### REST endpoints

| Method | Path | Auth | Cached | Purpose |
|--------|------|------|--------|---------|
| `POST` | `/api/flash-sales` | JWT (Bearer) | — | Schedule a flash sale |
| `GET` | `/api/flash-sales/active` | anonymous | **no** (live) | List currently-active sales with live availability |
| `POST` | `/api/inventory/reserve` | anonymous | — | Reserve stock for a session (rate-limited) |
| `DELETE` | `/api/inventory/reserve/{id}?sessionId=<uuid>` | anonymous | — | Explicitly release a reservation |

**`POST /api/flash-sales`** (authenticated) — request body:

```json
{ "productId": 1, "startAt": "2026-01-01T00:00:00Z", "endAt": "2026-01-01T01:00:00Z", "salePrice": 9.99, "stockAllocation": 100 }
```

`200 OK` returns the created sale (`FlashSaleDto`). Failures return the standard `ApiResponse` body with: `400` (invalid window/allocation/price, or sale price not below the product's base price), `404` (product not found), `409` (an overlapping sale already exists for the product), or `401` (unauthenticated).

**`GET /api/flash-sales/active`** — `200 OK` with a JSON array of `FlashSaleDto`:

```json
[ { "id": 5, "productId": 1, "salePrice": 9.99, "startAt": "...", "endAt": "...", "stockAllocation": 100, "quantityAvailable": 87 } ]
```

This endpoint is **deliberately not cached** (unlike `/api/products`, which is cached for 600s) so that live price and stock are never masked.

**`POST /api/inventory/reserve`** — request body:

```json
{ "productId": 1, "quantity": 2, "sessionId": "3f2504e0-4f89-41d3-9a0c-0305e82c3301" }
```

`sessionId` is the client basket UUID (`localStorage['basket_id']`), reused as the reservation session key — no new identity concept is introduced. `200 OK` returns the reservation (`ReservationToReturnDto`: `id`, `productId`, `quantity`, `sessionId`, `expiresAt`). The exact error contracts are:

| Status | Body | When |
|--------|------|------|
| `409` | `{"error":"INSUFFICIENT_STOCK","available":N}` | the requested quantity exceeds the remaining allocation (no partial reservation is made) |
| `409` | `{"error":"RESERVATION_CONFLICT"}` | the optimistic-concurrency version check failed twice (the server retries the read-modify-write **exactly once**) |
| `429` | `{"error":"RATE_LIMIT_EXCEEDED"}` | more than **10 requests/minute/session** |
| `400` | `{"error":"INVALID_SESSION"}` | the session id is missing or is not a canonical UUID v4 |

**`DELETE /api/inventory/reserve/{id}?sessionId=<uuid>`** — releases a reservation the caller owns. `204 No Content` on success; `400` (missing `sessionId`), `404` (not found, or owned by a different session — deliberately indistinguishable to prevent id enumeration), or `409` (owned but no longer active) return an `ApiResponse` body.

### SignalR hub

- **Path:** `/hubs/inventory` (configurable via `SIGNALR_HUB_PATH`).
- **Server → client events:**
  - `InventoryUpdated` → `{ productId, quantityAvailable }`
  - `FlashSaleStarted` → `{ id, productId, startAt, endAt, salePrice, stockAllocation, quantityAvailable }`
  - `FlashSaleEnded` → `{ productId, saleId }`
- **Client → server methods:** `JoinProductGroup(productId)` / `LeaveProductGroup(productId)` — a connection receives updates only for the product group it has joined.
- **Authentication:** the hub reuses the existing JWT bearer scheme and is `[Authorize]`-protected. Because a browser WebSocket cannot send an `Authorization` header, the token is passed as a query-string `access_token`; the server lifts it via `JwtBearerEvents.OnMessageReceived` (scoped to the hub path), and the Angular client supplies it via `accessTokenFactory: () => localStorage['token']`. The CORS policy allows credentials for the SPA origin (`https://localhost:4200`).
- **Security note:** because the token travels in the query string, the `Microsoft.AspNetCore.Hosting` log level is pinned to `Warning` so request URLs containing tokens are not written to logs.

### Reservation lifecycle

- A reservation is created `Active` with `expiresAt = now + RESERVATION_TTL_SECONDS` (default **300s**).
- A background sweep runs every `FLASH_SALE_POLL_INTERVAL_MS` (default **5000ms**), expiring lapsed reservations, returning their stock, and re-broadcasting `InventoryUpdated`.
- On successful checkout the session's active reservations are **consumed** (marked sold and never returned to the pool); the checkout hook is defensive, so a missing reservation never breaks the order flow.
- A client may release a hold early with `DELETE /api/inventory/reserve/{id}`.
- Availability for a sale is derived, not stored: `stockAllocation − SUM(quantity)` of that sale's reservations that still hold stock (`Consumed`, plus non-expired `Active`), clamped at zero.

### Configuration

| Key | Default | Purpose |
|-----|---------|---------|
| `SIGNALR_HUB_PATH` | `/hubs/inventory` | SignalR hub route (the endpoint mapping and the JWT query-token check resolve through the same value) |
| `RESERVATION_TTL_SECONDS` | `300` | How long a reservation is held before auto-expiry |
| `FLASH_SALE_POLL_INTERVAL_MS` | `5000` | Cadence of the reservation-expiry background sweep |

These keys are defined in `API/appsettings.Development.json` and are environment-overridable. The Angular client reads the hub URL and poll interval from `client/src/environments/environment*.ts`.

### Dependencies & limitations

- The only new dependency is the Angular SignalR client **`@microsoft/signalr` (`^8.0.0`)**. No new .NET package is required — ASP.NET Core SignalR ships in the shared framework.
- **Limitations (by design):** the hub is **single-instance and in-memory** — there is **no Redis backplane**, **no distributed lock** (the zero-oversell guarantee relies on the database concurrency token, not a lock), and **no horizontal SignalR scaling**.


