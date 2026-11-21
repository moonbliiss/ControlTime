using System.Security.Cryptography;

namespace ControlTime;

public sealed record ParentPassword(byte[] Salt, byte[] Hash)
{
    private const int Iterations = 600_000;
    private const int HashLength = 32;

    public static bool ValidLength(string? value) => value is { Length: >= 12 and <= 128 };

    public static ParentPassword Create(string value)
    {
        if (!ValidLength(value))
            throw new ArgumentException("Пароль должен содержать от 12 до 128 символов.");

        byte[] salt = RandomNumberGenerator.GetBytes(16);
        return new ParentPassword(salt, Derive(value, salt));
    }

    public bool Matches(string? value)
    {
        if (value is null || !ValidLength(value))
            return false;

        byte[] candidate = Derive(value, Salt);
        try
        {
            return CryptographicOperations.FixedTimeEquals(Hash, candidate);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(candidate);
        }
    }

    private static byte[] Derive(string value, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(value, salt, Iterations, HashAlgorithmName.SHA256, HashLength);
}

public sealed record Attempts(int Count, DateTimeOffset RetryAt)
{
    public static Attempts Empty { get; } = new(0, DateTimeOffset.MinValue);

    public Attempts Failed(DateTimeOffset now)
    {
        int failures = Math.Min(Count + 1, 5);
        return new Attempts(failures, now.AddSeconds(1 << failures));
    }
}
