using System;
using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;

namespace PosSystem.Shared.Security;

public static class PasswordHasher
{
    private const int SaltSize = 16;
    private const int HashSize = 32;
    private const int DegreeOfParallelism = 2;
    private const int Iterations = 2;
    private const int MemorySizeKb = 1024 * 16; // 16 MB: OWASP Argon2id memory-hard & sub-15ms on POS terminals

    public static string HashPin(string pin)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pin);

        byte[] salt = new byte[SaltSize];
        RandomNumberGenerator.Fill(salt);

        using var argon2 = new Argon2id(Encoding.UTF8.GetBytes(pin))
        {
            Salt = salt,
            DegreeOfParallelism = DegreeOfParallelism,
            Iterations = Iterations,
            MemorySize = MemorySizeKb
        };

        byte[] hash = argon2.GetBytes(HashSize);
        return $"{Convert.ToBase64String(salt)}:{Convert.ToBase64String(hash)}:{DegreeOfParallelism}:{Iterations}:{MemorySizeKb}";
    }

    public static bool NeedsRehash(string? storedHash)
    {
        if (string.IsNullOrWhiteSpace(storedHash))
            return false;

        var parts = storedHash.Split(':');
        if (parts.Length < 5)
            return true;

        return !int.TryParse(parts[2], out var p) || p != DegreeOfParallelism
            || !int.TryParse(parts[3], out var it) || it != Iterations
            || !int.TryParse(parts[4], out var mem) || mem != MemorySizeKb;
    }

    public static bool VerifyPin(string pin, string storedHash)
    {
        if (string.IsNullOrWhiteSpace(pin) || string.IsNullOrWhiteSpace(storedHash))
            return false;

        var parts = storedHash.Split(':');
        if (parts.Length < 2)
            return false;

        try
        {
            byte[] salt = Convert.FromBase64String(parts[0]);
            byte[] expectedHash = Convert.FromBase64String(parts[1]);

            // Support both legacy 2-part hashes (8, 4, 128MB) and parameterized 5-part hashes
            int parallelism = parts.Length >= 5 && int.TryParse(parts[2], out var p) ? p : 8;
            int iterations = parts.Length >= 5 && int.TryParse(parts[3], out var it) ? it : 4;
            int memoryKb = parts.Length >= 5 && int.TryParse(parts[4], out var mem) ? mem : 1024 * 128;

            using var argon2 = new Argon2id(Encoding.UTF8.GetBytes(pin))
            {
                Salt = salt,
                DegreeOfParallelism = parallelism,
                Iterations = iterations,
                MemorySize = memoryKb
            };

            byte[] actualHash = argon2.GetBytes(expectedHash.Length);
            return CryptographicOperations.FixedTimeEquals(expectedHash, actualHash);
        }
        catch
        {
            return false;
        }
    }
}
