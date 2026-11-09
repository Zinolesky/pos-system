using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PosSystem.Shared.Models;
using PosSystem.Shared.Serialization;

namespace PosSystem.Frontend.Services;

public class StoredSessionPayload
{
    public string Token { get; set; } = string.Empty;
    public Cashier? User { get; set; }
    public DateTime SavedAtUtc { get; set; } = DateTime.UtcNow;
}

public interface ITokenVault
{
    void SaveToken(string token);
    string? ReadToken();
    void SaveSession(string token, Cashier user);
    StoredSessionPayload? ReadSession();
    void ClearToken();
}

public class WindowsDpapiTokenVault : ITokenVault
{
    private readonly string _filePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PosSystem",
        "session.dat"
    );

    public void SaveToken(string token)
    {
        var existing = ReadSession();
        SaveSession(token, existing?.User ?? new Cashier { Username = "offline", Name = "Cashier" });
    }

    public string? ReadToken() => ReadSession()?.Token;

    public void SaveSession(string token, Cashier user)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        var payload = new StoredSessionPayload
        {
            Token = token,
            User = user,
            SavedAtUtc = DateTime.UtcNow
        };

        string json = JsonSerializer.Serialize(payload, PosJsonOptions.Default);
        byte[] rawBytes = Encoding.UTF8.GetBytes(json);

        byte[] encryptedBytes = ProtectedData.Protect(
            rawBytes,
            optionalEntropy: null,
            DataProtectionScope.CurrentUser
        );

        File.WriteAllBytes(_filePath, encryptedBytes);
    }

    public StoredSessionPayload? ReadSession()
    {
        if (!File.Exists(_filePath)) return null;

        try
        {
            byte[] encryptedBytes = File.ReadAllBytes(_filePath);
            byte[] rawBytes = ProtectedData.Unprotect(
                encryptedBytes,
                optionalEntropy: null,
                DataProtectionScope.CurrentUser
            );

            string json = Encoding.UTF8.GetString(rawBytes);
            var session = JsonSerializer.Deserialize<StoredSessionPayload>(json, PosJsonOptions.Default);
            if (session == null || (DateTime.UtcNow - session.SavedAtUtc).TotalHours > 24)
            {
                ClearToken();
                return null;
            }
            return session;
        }
        catch
        {
            ClearToken();
            return null;
        }
    }

    public void ClearToken()
    {
        if (File.Exists(_filePath))
        {
            File.Delete(_filePath);
        }
    }
}
