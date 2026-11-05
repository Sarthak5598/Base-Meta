using System.Security.Cryptography;
using System.Text;

namespace DataLens.Infrastructure.Security;

/// <summary>
/// Encrypts connection strings at rest with AES-256-GCM. Output is base64 of nonce | tag | ciphertext.
/// The key comes from the DATALENS_KEY environment variable (base64, 32 bytes) or, for local
/// development, from a key file that is created on first use.
/// </summary>
public sealed class ConnectionProtector
{
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private readonly byte[] _key;

    public ConnectionProtector(byte[] key)
    {
        if (key.Length != 32)
            throw new ArgumentException("The key must be exactly 32 bytes.", nameof(key));
        _key = key;
    }

    public static ConnectionProtector Create(string keyFilePath)
    {
        var fromEnv = Environment.GetEnvironmentVariable("DATALENS_KEY");
        if (!string.IsNullOrWhiteSpace(fromEnv))
            return new ConnectionProtector(Convert.FromBase64String(fromEnv));

        if (File.Exists(keyFilePath))
            return new ConnectionProtector(Convert.FromBase64String(File.ReadAllText(keyFilePath).Trim()));

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(keyFilePath))!);
        var key = RandomNumberGenerator.GetBytes(32);
        File.WriteAllText(keyFilePath, Convert.ToBase64String(key));
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(keyFilePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return new ConnectionProtector(key);
    }

    public string Protect(string plaintext)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var data = Encoding.UTF8.GetBytes(plaintext);
        var cipher = new byte[data.Length];
        var tag = new byte[TagSize];

        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(nonce, data, cipher, tag);

        var output = new byte[NonceSize + TagSize + cipher.Length];
        nonce.CopyTo(output, 0);
        tag.CopyTo(output, NonceSize);
        cipher.CopyTo(output, NonceSize + TagSize);
        return Convert.ToBase64String(output);
    }

    /// <exception cref="CryptographicException">Wrong key or tampered data.</exception>
    public string Unprotect(string protectedText)
    {
        var input = Convert.FromBase64String(protectedText);
        if (input.Length < NonceSize + TagSize)
            throw new CryptographicException("The stored value is too short to be valid.");

        var nonce = input.AsSpan(0, NonceSize);
        var tag = input.AsSpan(NonceSize, TagSize);
        var cipher = input.AsSpan(NonceSize + TagSize);
        var plain = new byte[cipher.Length];

        using var aes = new AesGcm(_key, TagSize);
        aes.Decrypt(nonce, cipher, tag, plain);
        return Encoding.UTF8.GetString(plain);
    }
}
