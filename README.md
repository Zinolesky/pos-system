# Prime Supermarket POS — Hybrid Offline-First Retail System (v2.1)

An enterprise-grade, **Offline-First Hybrid Point-of-Sale (POS)** terminal and cloud synchronization backend built with **.NET 8**, **WPF (MVVM)**, **SQLite (WAL Mode)**, **ASP.NET Core Minimal APIs**, **EF Core 8 Migrations**, and **PostgreSQL 16**.

---

## Key Capabilities

1. **True Offline-First Hybrid Architecture**
   - **Local SQLite (`WAL` Mode):** Stored safely in `%LocalAppData%\PosSystem\pos_local.db` with non-destructive automatic schema upgrades (`EnsureSqliteSchemaUpgraded()`).
   - **Hybrid Authentication (`AuthenticationService`):** Attempts cloud JWT authentication (`POST /api/v1/auth/login`) with a 3-second timeout and caches credentials locally with **Argon2id** (`PasswordHasher`) + **Windows DPAPI** (`WindowsDpapiTokenVault`).
   - **Full Bidirectional Cloud Sync (`OutboxSyncService`):**
     - Reads `BackendApiUrl` from `appsettings.json` or `POS_BACKEND_URL`.
     - Pushes queued sales and `RET-...` return slips idempotently (`POST /api/v1/sync/sales`) with exponential retry backoff (`1s` to `64s`) and Dead-Letter Queue (`SyncStatus.Failed` after 10 retries) with per-item inspection, retry, and dismissal.
     - Syncs store operations (`Customers`, `ShiftSessions`, `AuditLogs`, `StoreSettings`) via `POST /api/v1/sync/operations` and pulls catalog/customer/setting deltas via `GET /api/v1/sync/catalog`.
   - **Production Migrations & Secret Enforcement (`PosSystem.Backend`):**
     - Applies versioned EF Core 8 PostgreSQL migrations (`dbContext.Database.Migrate()`) automatically on startup.
     - Enforces fail-fast startup validation (`JwtSecretPolicy`) requiring strong 256-bit `JWT_SECRET_KEY` and `ConnectionStrings__DefaultConnection` in non-Development environments (no hardcoded production secrets).

2. **Complete Supermarket Cashier Experience (`CheckoutView` & `CheckoutViewModel`)**
   - **Real SQLite Catalog & Deli/Produce Variable-Measure EAN-13 Barcodes (`WeightedBarcodeParser`):**
     - Dynamic category pills, real-time SKU/barcode/name search, stock level badges (`Low`, `OUT OF STOCK`, `NEG`), and USB HID Barcode Scanner wedge listener (`BarcodeScannerListener`).
     - Decodes 13-digit GS1 variable-measure labels (`20`–`24` weight-embedded in grams; `02`, `25`–`28` whole-currency price-embedded; `29` subunit price-embedded) with Modulo-10 check digit validation.
   - **Configurable Zero-Stock & Negative Inventory Policy (`InventorySellPolicy`):**
     - Toggleable in Back-Office Admin between **Block Out-of-Stock**, **Warn & Require Manager PIN Override** (with audit logging), and **Allow Negative Stock**.
   - **Interactive Touch Numpad & Action Row:**
     - `0-9`, `00`, `C`, `⌫` numpad buffer with live mode indicator.
     - `Qty`, `Disc %` (`>10%` requires Manager PIN), `Price Ovr`, `Void` / `Void All`, and `Hold Order` / `Recall Held (F4)`.
   - **Mandatory Shift Float & Multi-Tender Checkout Guard (`F12` / `PAY →`):**
     - Strictly requires an open cashier shift (`ShiftSession`) with a declared opening cash float before checkout—never silently auto-creates shifts.
     - Blocks non-cash (`Card + Transfer`) over-tendering and restricts `ChangeDue` strictly to cash over-tendering.
   - **Hardware ESC/POS Spooling (`ReceiptPrinterService`):**
     - Supports raw Windows Print Spooler (`winspool.Drv` P/Invoke), Direct Network TCP (`9100`), and local `.txt`/`.escpos.bin` archival in `%LocalAppData%\PosSystem\Receipts`.

3. **Full Store Operations, Partial Returns & Immutable Security Audit**
   - **Partial & Full Line-Item Returns/Refunds:** Select exact per-item return quantities, preview pro-rated discount and VAT refunds (tax-inclusive or tax-exclusive) with zero kobo drift, restore partial/full stock, track `PartiallyRefunded` / `Refunded` status, and print `RET-...` Customer Return Slips.
   - **Immutable Supervisor Override & Security Audit Trail (`AuditLog`):** Every supervisor override, refund, safe drop, shift open/close, staff creation, and inventory policy change is persisted locally and synced to PostgreSQL.
   - **Customers & Loyalty:** Register customers, track net spend & loyalty points (`1 pt` per `₦1,000`), and adjust balances automatically on returns.
   - **Shift Till & End-of-Day Z-Report:** Open shift with cash float, record mid-day safe drops/payouts, and perform blind cash counts with Over/Short variance calculation and Z-Report printing.

---

## Default Sign-In Profiles (Pre-Seeded Offline & Cloud)

| Role | Username | PIN | Permissions |
| :--- | :--- | :--- | :--- |
| **System Admin** | `admin` | `1234` | Full POS, Voids, Price Overrides, Refunds, Z-Reports, Admin Panel |
| **Floor Manager** | `manager` | `2222` | Full POS, Voids, Price Overrides, Refunds, Z-Reports, Admin Panel |
| **Cashier** | `cashier` | `0000` | Checkout, Hold/Recall, Customer Lookup (Manager PIN required for voids/overrides) |

---

## Running the Project

### 1. Run the WPF Desktop Terminal (Immediate Launch)
```powershell
.\run-pos.ps1
```
*(Or double-click `run-pos.cmd` / `src\PosSystem.Frontend\bin\Debug\net8.0-windows\PosSystem.Frontend.exe`)*

If `C:\Program Files\dotnet` is in your terminal's `PATH`, you can also run:
```powershell
dotnet run --project src/PosSystem.Frontend/PosSystem.Frontend.csproj
```

### 2. Run the 15-Test Automated Verification Suite
```powershell
.\run-tests.ps1
```

### 3. Start Cloud Backend & PostgreSQL (Optional — Terminal works offline without it!)
Copy `.env.example` to `.env`, set strong `POSTGRES_PASSWORD` and `JWT_SECRET_KEY` (32+ chars), then launch:
```powershell
Copy-Item .env.example .env
docker compose up --build -d
```
The API will be available at `http://localhost:5000` (`/api/v1/health`, `/swagger`).

### 4. Build Self-Contained Windows Installer (`PrimePOS_Setup_v2.exe`)
```powershell
.\build-installer.ps1
```
