using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PosSystem.Frontend.Data;
using PosSystem.Shared.Models;
using PosSystem.Shared.Serialization;

namespace PosSystem.Frontend.Services;

public class OutboxSyncService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ISessionService _sessionService;
    private readonly HttpClient _httpClient;
    private readonly string _backendBaseUrl;
    private DateTime? _lastCatalogSyncUtc;
    private DateTime? _lastOperationsSyncUtc;

    public const string DefaultBackendBaseUrl = "http://localhost:5000";
    public const int MaxRetryAttempts = 10;

    public static OutboxSyncService? Instance { get; private set; }

    public bool IsOnline { get; private set; }
    public int PendingCount { get; private set; }
    public int DeadLetterCount { get; private set; }
    public DateTime? LastSyncedAt { get; private set; }
    public string StatusSummary => IsOnline
        ? (DeadLetterCount > 0
            ? $"Online • {DeadLetterCount} Failed in DLQ"
            : (PendingCount > 0 ? $"Online • Syncing ({PendingCount})" : "Cloud Online • Synced"))
        : $"Offline Mode • {PendingCount} Queued" + (DeadLetterCount > 0 ? $" ({DeadLetterCount} Failed)" : "");

    public event Action? SyncStateChanged;

    public OutboxSyncService(
        IServiceProvider serviceProvider,
        ISessionService sessionService,
        HttpClient httpClient,
        IConfiguration? configuration = null)
    {
        _serviceProvider = serviceProvider;
        _sessionService = sessionService;
        _httpClient = httpClient;
        _httpClient.Timeout = TimeSpan.FromSeconds(6);
        _backendBaseUrl = (Environment.GetEnvironmentVariable("POS_BACKEND_URL")
                           ?? configuration?["BackendApiUrl"]
                           ?? DefaultBackendBaseUrl).TrimEnd('/');
        Instance = this;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RefreshQueueCountsAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunSyncCycleAsync(stoppingToken);
            }
            catch
            {
                IsOnline = false;
                SyncStateChanged?.Invoke();
            }

            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
        }
    }

    public async Task TriggerImmediateSyncAsync()
    {
        try
        {
            await RunSyncCycleAsync(CancellationToken.None);
        }
        catch
        {
            IsOnline = false;
            SyncStateChanged?.Invoke();
        }
    }

    public async Task RetryDeadLetterQueueAsync()
    {
        using (var scope = _serviceProvider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LocalDbContext>();
            var failed = await db.SyncQueues.Where(sq => sq.Status == SyncStatus.Failed).ToListAsync();
            foreach (var item in failed)
            {
                item.Status = SyncStatus.Pending;
                item.RetryCount = 0;
                item.LastAttemptAt = null;
                item.ErrorMessage = null;
            }
            await db.SaveChangesAsync();
        }
        await TriggerImmediateSyncAsync();
    }

    public async Task DismissDeadLetterEntryAsync(Ulid queueId)
    {
        using (var scope = _serviceProvider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LocalDbContext>();
            var item = await db.SyncQueues.FirstOrDefaultAsync(sq => sq.Id == queueId && sq.Status == SyncStatus.Failed);
            if (item != null)
            {
                db.SyncQueues.Remove(item);
                await db.SaveChangesAsync();
            }
        }
        await RefreshQueueCountsAsync(CancellationToken.None);
        SyncStateChanged?.Invoke();
    }

    private async Task RunSyncCycleAsync(CancellationToken cancellationToken)
    {
        await RefreshQueueCountsAsync(cancellationToken);

        var healthResponse = await _httpClient.GetAsync($"{_backendBaseUrl}/api/v1/health", cancellationToken);
        IsOnline = healthResponse.IsSuccessStatusCode;

        var token = _sessionService.CurrentToken;
        bool hasRealJwt = !string.IsNullOrEmpty(token) && !token.StartsWith("OFFLINE-TOKEN-", StringComparison.Ordinal);

        if (IsOnline && hasRealJwt)
        {
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            await SyncStoreOperationsAsync(cancellationToken);
            await SyncPendingSalesAsync(cancellationToken);
            await PullCatalogDeltasAsync(cancellationToken);
        }

        await RefreshQueueCountsAsync(cancellationToken);
        SyncStateChanged?.Invoke();
    }

    private async Task RefreshQueueCountsAsync(CancellationToken cancellationToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LocalDbContext>();
        PendingCount = await db.SyncQueues.CountAsync(sq => sq.Status == SyncStatus.Pending, cancellationToken);
        DeadLetterCount = await db.SyncQueues.CountAsync(sq => sq.Status == SyncStatus.Failed, cancellationToken);
    }

    public static TimeSpan ComputeBackoffDelay(int retryCount)
    {
        int cappedExponent = Math.Clamp(retryCount, 0, 6);
        return TimeSpan.FromSeconds(Math.Pow(2, cappedExponent));
    }

    private async Task SyncPendingSalesAsync(CancellationToken cancellationToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LocalDbContext>();

        var now = DateTime.UtcNow;
        var candidates = await db.SyncQueues
            .Include(sq => sq.Sale)
            .ThenInclude(s => s!.Items)
            .Where(sq => sq.Status == SyncStatus.Pending)
            .OrderBy(sq => sq.QueuedAt)
            .Take(50)
            .ToListAsync(cancellationToken);

        var readyToSync = candidates
            .Where(sq => !sq.LastAttemptAt.HasValue ||
                         (now - sq.LastAttemptAt.Value) >= ComputeBackoffDelay(sq.RetryCount))
            .ToList();

        if (!readyToSync.Any()) return;

        var salesToSync = readyToSync
            .Where(sq => sq.Sale != null)
            .Select(sq => sq.Sale!)
            .ToList();

        try
        {
            var response = await _httpClient.PostAsJsonAsync(
                $"{_backendBaseUrl}/api/v1/sync/sales",
                salesToSync,
                PosJsonOptions.Default,
                cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                foreach (var queueEntry in readyToSync)
                {
                    queueEntry.Status = SyncStatus.Synced;
                    queueEntry.SyncedAt = now;
                    queueEntry.LastAttemptAt = now;
                    queueEntry.ErrorMessage = null;
                }
                LastSyncedAt = now;
                await db.SaveChangesAsync(cancellationToken);
            }
            else
            {
                string err = $"HTTP {(int)response.StatusCode} ({response.ReasonPhrase})";
                RecordSyncFailure(readyToSync, err, now);
                await db.SaveChangesAsync(cancellationToken);
            }
        }
        catch (Exception ex)
        {
            RecordSyncFailure(readyToSync, ex.Message, DateTime.UtcNow);
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    public static void RecordSyncFailure(IEnumerable<SyncQueue> entries, string error, DateTime attemptTimeUtc)
    {
        foreach (var queueEntry in entries)
        {
            queueEntry.RetryCount++;
            queueEntry.LastAttemptAt = attemptTimeUtc;
            queueEntry.ErrorMessage = error;
            if (queueEntry.RetryCount >= MaxRetryAttempts)
            {
                queueEntry.Status = SyncStatus.Failed;
            }
        }
    }

    private async Task SyncStoreOperationsAsync(CancellationToken cancellationToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LocalDbContext>();

        var unsyncedLogs = await db.AuditLogs
            .Where(a => !a.IsSynced)
            .OrderBy(a => a.CreatedAt)
            .Take(100)
            .ToListAsync(cancellationToken);

        var customersQuery = db.Customers.AsNoTracking().AsQueryable();
        var settingsQuery = db.StoreSettings.AsNoTracking().AsQueryable();
        if (_lastOperationsSyncUtc.HasValue)
        {
            customersQuery = customersQuery.Where(c => c.UpdatedAt >= _lastOperationsSyncUtc.Value);
            settingsQuery = settingsQuery.Where(s => s.UpdatedAt >= _lastOperationsSyncUtc.Value);
        }

        var customers = await customersQuery.Take(100).ToListAsync(cancellationToken);
        var shifts = await db.ShiftSessions.AsNoTracking().OrderByDescending(s => s.OpenedAt).Take(25).ToListAsync(cancellationToken);
        var settings = await settingsQuery.ToListAsync(cancellationToken);

        if (!unsyncedLogs.Any() && !customers.Any() && !shifts.Any() && !settings.Any())
            return;

        try
        {
            var payload = new SyncOperationsRequest
            {
                Customers = customers,
                ShiftSessions = shifts,
                AuditLogs = unsyncedLogs,
                StoreSettings = settings
            };

            var resp = await _httpClient.PostAsJsonAsync(
                $"{_backendBaseUrl}/api/v1/sync/operations",
                payload,
                PosJsonOptions.Default,
                cancellationToken);

            if (resp.IsSuccessStatusCode)
            {
                foreach (var log in unsyncedLogs)
                    log.IsSynced = true;
                await db.SaveChangesAsync(cancellationToken);
                _lastOperationsSyncUtc = DateTime.UtcNow;
            }
        }
        catch
        {
            // Non-blocking: will retry next sync cycle
        }
    }

    private async Task PullCatalogDeltasAsync(CancellationToken cancellationToken)
    {
        string url = _lastCatalogSyncUtc.HasValue
            ? $"{_backendBaseUrl}/api/v1/sync/catalog?since={Uri.EscapeDataString(_lastCatalogSyncUtc.Value.ToString("O"))}"
            : $"{_backendBaseUrl}/api/v1/sync/catalog";

        var catalog = await _httpClient.GetFromJsonAsync<CatalogSyncResponse>(url, PosJsonOptions.Default, cancellationToken);
        if (catalog == null) return;

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LocalDbContext>();

        foreach (var cat in catalog.Categories)
        {
            var local = await db.Categories.FirstOrDefaultAsync(c => c.Id == cat.Id, cancellationToken);
            if (local == null) db.Categories.Add(cat);
            else if (cat.UpdatedAt > local.UpdatedAt)
            {
                local.Name = cat.Name;
                local.Description = cat.Description;
                local.ColorHex = cat.ColorHex;
                local.IsActive = cat.IsActive;
                local.UpdatedAt = cat.UpdatedAt;
            }
        }

        foreach (var prod in catalog.Products)
        {
            var local = await db.Products.FirstOrDefaultAsync(p => p.Id == prod.Id, cancellationToken);
            if (local == null) db.Products.Add(prod);
            else if (prod.UpdatedAt > local.UpdatedAt)
            {
                local.CategoryId = prod.CategoryId;
                local.Name = prod.Name;
                local.Sku = prod.Sku;
                local.Barcode = prod.Barcode;
                local.Unit = prod.Unit;
                local.CostPrice = prod.CostPrice;
                local.Price = prod.Price;
                local.IsTaxable = prod.IsTaxable;
                local.Stock = prod.Stock;
                local.ReorderLevel = prod.ReorderLevel;
                local.IsActive = prod.IsActive;
                local.UpdatedAt = prod.UpdatedAt;
            }
        }

        foreach (var tax in catalog.TaxRates)
        {
            var local = await db.TaxRates.FirstOrDefaultAsync(t => t.Id == tax.Id, cancellationToken);
            if (local == null) db.TaxRates.Add(tax);
            else if (tax.UpdatedAt > local.UpdatedAt)
            {
                local.Name = tax.Name;
                local.Percentage = tax.Percentage;
                local.IsInclusive = tax.IsInclusive;
                local.IsDefault = tax.IsDefault;
                local.UpdatedAt = tax.UpdatedAt;
            }
        }

        foreach (var c in catalog.Cashiers)
        {
            var local = await db.Cashiers.FirstOrDefaultAsync(x => x.Id == c.Id || x.Username.ToLower() == c.Username.ToLower(), cancellationToken);
            if (local == null)
            {
                db.Cashiers.Add(new Cashier
                {
                    Id = c.Id,
                    Username = c.Username,
                    Name = c.Name,
                    PinHash = c.PinHash,
                    Role = c.Role,
                    IsActive = c.IsActive,
                    UpdatedAt = c.UpdatedAt
                });
            }
            else if (c.UpdatedAt > local.UpdatedAt)
            {
                local.Name = c.Name;
                local.PinHash = c.PinHash;
                local.Role = c.Role;
                local.IsActive = c.IsActive;
                local.UpdatedAt = c.UpdatedAt;
            }
        }

        foreach (var cust in catalog.Customers)
        {
            var local = await db.Customers.FirstOrDefaultAsync(
                x => x.Id == cust.Id || x.Phone == cust.Phone,
                cancellationToken);
            if (local == null)
            {
                db.Customers.Add(cust);
            }
            else if (cust.UpdatedAt > local.UpdatedAt)
            {
                local.Name = cust.Name;
                local.Phone = cust.Phone;
                local.Email = cust.Email;
                local.LoyaltyPoints = cust.LoyaltyPoints;
                local.StoreCreditBalance = cust.StoreCreditBalance;
                local.TotalSpent = cust.TotalSpent;
                local.VisitCount = cust.VisitCount;
                local.UpdatedAt = cust.UpdatedAt;
            }
        }

        foreach (var setting in catalog.StoreSettings)
        {
            var local = await db.StoreSettings.FirstOrDefaultAsync(
                x => x.Key == setting.Key,
                cancellationToken);
            if (local == null)
            {
                db.StoreSettings.Add(setting);
            }
            else if (setting.UpdatedAt > local.UpdatedAt)
            {
                local.Value = setting.Value;
                local.UpdatedAt = setting.UpdatedAt;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        _lastCatalogSyncUtc = catalog.ServerTimeUtc;
        LastSyncedAt = DateTime.UtcNow;
    }
}
