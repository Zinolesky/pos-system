using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PosSystem.Frontend.Data;
using PosSystem.Shared.Models;
using PosSystem.Shared.Security;
using PosSystem.Shared.Serialization;

namespace PosSystem.Frontend.Services;

public record AuthResult(bool Success, Cashier? Cashier, string Token, bool IsOfflineMode, string? ErrorMessage);

public class AuthenticationService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly string _backendBaseUrl;
    private readonly ConcurrentDictionary<string, Ulid> _verifiedPinSessionCache = new();
    public const string DefaultBackendBaseUrl = "http://localhost:5000";

    public AuthenticationService(
        IServiceProvider serviceProvider,
        IHttpClientFactory httpClientFactory,
        IConfiguration? configuration = null)
    {
        _serviceProvider = serviceProvider;
        _httpClientFactory = httpClientFactory;
        _backendBaseUrl = (Environment.GetEnvironmentVariable("POS_BACKEND_URL")
                           ?? configuration?["BackendApiUrl"]
                           ?? DefaultBackendBaseUrl).TrimEnd('/');
    }

    public async Task<AuthResult> AuthenticateHybridAsync(string username, string pin)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(pin))
            return new AuthResult(false, null, string.Empty, false, "Please enter both Username and PIN.");

        var normalizedUser = username.Trim();

        // 1. Local-First Authentication off the UI thread (<15ms response, zero network wait when offline)
        var offlineCashier = await Task.Run(() => AuthenticateOffline(normalizedUser, pin)).ConfigureAwait(false);
        if (offlineCashier != null)
        {
            string offlineToken = $"OFFLINE-TOKEN-{offlineCashier.Id}-{DateTime.UtcNow.Ticks}";

            // If cloud is already known to be online, upgrade to a cloud JWT in the background without blocking UI login
            if (OutboxSyncService.Instance?.IsOnline == true)
            {
                _ = TryUpgradeTokenInBackgroundAsync(normalizedUser, pin);
            }

            return new AuthResult(true, offlineCashier, offlineToken, true, null);
        }

        // 2. If not matched locally, try Cloud Login only when cloud is online (or unknown) with a fast 1.2s timeout
        if (OutboxSyncService.Instance == null || OutboxSyncService.Instance.IsOnline)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(1200));
                using var client = _httpClientFactory.CreateClient();
                client.Timeout = TimeSpan.FromMilliseconds(1200);

                var req = new LoginRequest { Username = normalizedUser, Pin = pin };
                var response = await client.PostAsJsonAsync(
                    $"{_backendBaseUrl}/api/v1/auth/login",
                    req,
                    PosJsonOptions.Default,
                    cts.Token).ConfigureAwait(false);

                if (response.IsSuccessStatusCode)
                {
                    var loginResp = await response.Content
                        .ReadFromJsonAsync<LoginResponse>(PosJsonOptions.Default, cts.Token)
                        .ConfigureAwait(false);

                    if (loginResp?.Cashier != null && !string.IsNullOrEmpty(loginResp.Token))
                    {
                        await CacheCashierLocallyAsync(loginResp.Cashier, pin).ConfigureAwait(false);
                        return new AuthResult(true, loginResp.Cashier, loginResp.Token, false, null);
                    }
                }
                else if ((int)response.StatusCode == 401)
                {
                    return new AuthResult(false, null, string.Empty, false, "Invalid Username or PIN.");
                }
            }
            catch
            {
                // Cloud unreachable -> return standard offline failure message immediately
            }
        }

        return new AuthResult(false, null, string.Empty, true, "Invalid Username or PIN. Default logins: admin / 1234, manager / 2222, cashier / 0000.");
    }

    private async Task TryUpgradeTokenInBackgroundAsync(string username, string pin)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(1500));
            using var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromMilliseconds(1500);

            var req = new LoginRequest { Username = username, Pin = pin };
            var response = await client.PostAsJsonAsync(
                $"{_backendBaseUrl}/api/v1/auth/login",
                req,
                PosJsonOptions.Default,
                cts.Token).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                var loginResp = await response.Content
                    .ReadFromJsonAsync<LoginResponse>(PosJsonOptions.Default, cts.Token)
                    .ConfigureAwait(false);

                if (loginResp?.Cashier != null && !string.IsNullOrEmpty(loginResp.Token))
                {
                    await CacheCashierLocallyAsync(loginResp.Cashier, pin).ConfigureAwait(false);
                    var session = _serviceProvider.GetService<ISessionService>();
                    if (session != null && session.CurrentUser?.Id == loginResp.Cashier.Id)
                    {
                        session.Login(loginResp.Token, loginResp.Cashier);
                    }
                }
            }
        }
        catch
        {
            // Keep working with local offline token
        }
    }

    public Cashier? AuthenticateOffline(string username, string pin)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LocalDbContext>();

        var normalized = username.Trim().ToLowerInvariant();
        var cashier = db.Cashiers
            .FirstOrDefault(c => c.IsActive && c.Username.ToLower() == normalized);

        if (cashier == null)
            return null;

        string cacheKey = ComputeSessionCacheKey(cashier.Id, pin, cashier.PinHash);
        if (_verifiedPinSessionCache.TryGetValue(cacheKey, out var cachedId) && cachedId == cashier.Id)
            return cashier;

        if (PasswordHasher.VerifyPin(pin, cashier.PinHash))
        {
            if (PasswordHasher.NeedsRehash(cashier.PinHash))
            {
                cashier.PinHash = PasswordHasher.HashPin(pin);
                db.SaveChanges();
                cacheKey = ComputeSessionCacheKey(cashier.Id, pin, cashier.PinHash);
            }
            _verifiedPinSessionCache[cacheKey] = cashier.Id;
            return cashier;
        }

        return null;
    }

    public Cashier? AuthenticateOfflineByPinOnly(string pin)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LocalDbContext>();

        var cashiers = db.Cashiers.Where(c => c.IsActive).ToList();
        foreach (var cashier in cashiers)
        {
            string cacheKey = ComputeSessionCacheKey(cashier.Id, pin, cashier.PinHash);
            if (_verifiedPinSessionCache.TryGetValue(cacheKey, out var cachedId) && cachedId == cashier.Id)
                return cashier;

            if (PasswordHasher.VerifyPin(pin, cashier.PinHash))
            {
                if (PasswordHasher.NeedsRehash(cashier.PinHash))
                {
                    cashier.PinHash = PasswordHasher.HashPin(pin);
                    db.SaveChanges();
                    cacheKey = ComputeSessionCacheKey(cashier.Id, pin, cashier.PinHash);
                }
                _verifiedPinSessionCache[cacheKey] = cashier.Id;
                return cashier;
            }
        }
        return null;
    }

    public Cashier? VerifyManagerOrAdminPin(string pin)
    {
        if (string.IsNullOrWhiteSpace(pin))
            return null;

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LocalDbContext>();

        var managers = db.Cashiers
            .Where(c => c.IsActive && (c.Role == UserRole.Admin || c.Role == UserRole.Manager))
            .ToList();

        // First pass: check in-memory verified session cache (<0.1ms)
        foreach (var mgr in managers)
        {
            string cacheKey = ComputeSessionCacheKey(mgr.Id, pin, mgr.PinHash);
            if (_verifiedPinSessionCache.TryGetValue(cacheKey, out var cachedId) && cachedId == mgr.Id)
                return mgr;
        }

        // Second pass: Argon2id verification + automatic upgrade of legacy heavy hashes
        foreach (var mgr in managers)
        {
            if (PasswordHasher.VerifyPin(pin, mgr.PinHash))
            {
                if (PasswordHasher.NeedsRehash(mgr.PinHash))
                {
                    mgr.PinHash = PasswordHasher.HashPin(pin);
                    db.SaveChanges();
                }
                string cacheKey = ComputeSessionCacheKey(mgr.Id, pin, mgr.PinHash);
                _verifiedPinSessionCache[cacheKey] = mgr.Id;
                return mgr;
            }
        }
        return null;
    }

    private static string ComputeSessionCacheKey(Ulid userId, string pin, string storedHash)
    {
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{userId}:{pin}:{storedHash}"));
        return Convert.ToHexString(bytes);
    }

    private async Task CacheCashierLocallyAsync(Cashier cloudCashier, string plainPin)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LocalDbContext>();

        var existing = await db.Cashiers.FirstOrDefaultAsync(c =>
            c.Id == cloudCashier.Id || c.Username.ToLower() == cloudCashier.Username.ToLower());

        var pinHash = PasswordHasher.HashPin(plainPin);
        if (existing == null)
        {
            db.Cashiers.Add(new Cashier
            {
                Id = cloudCashier.Id,
                Username = cloudCashier.Username,
                Name = cloudCashier.Name,
                Role = cloudCashier.Role,
                PinHash = pinHash,
                IsActive = true,
                UpdatedAt = DateTime.UtcNow
            });
        }
        else
        {
            existing.Name = cloudCashier.Name;
            existing.Role = cloudCashier.Role;
            existing.PinHash = pinHash;
            existing.IsActive = true;
            existing.UpdatedAt = DateTime.UtcNow;
        }

        await db.SaveChangesAsync();
    }

    public static string HashPin(string pin) => PasswordHasher.HashPin(pin);
}
