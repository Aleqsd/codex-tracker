using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodexTracker.Core;

namespace CodexTracker.Codex;

/// <summary>
/// Password-encrypted profile file, readable on another PC (DPAPI is tied to this Windows account).
/// AES-256-GCM with a PBKDF2-SHA256 key; the header is authenticated, so any change is detected.
/// </summary>
public static class ProfileArchive
{
    public const int MinimumPasswordLength = 8;
    public const int MaximumBytes = 64 * 1024 * 1024;
    private const string Format = "codex-tracker-profile";
    private const int Version = 1, DefaultIterations = 600_000, MinimumIterations = 100_000, MaximumIterations = 5_000_000;
    private sealed record Envelope(string Format, int Version, int Iterations, string Salt, string Nonce, string Tag, string Data);

    public static byte[] Protect(byte[] content, string password)
    {
        CheckPassword(password);
        using var compressed = new MemoryStream();
        using (var gzip = new GZipStream(compressed, CompressionLevel.Optimal, leaveOpen: true)) gzip.Write(content);
        var plain = compressed.ToArray();
        var salt = RandomNumberGenerator.GetBytes(16); var nonce = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[plain.Length]; var tag = new byte[16];
        using (var aes = new AesGcm(Key(password, salt, DefaultIterations), tag.Length))
            aes.Encrypt(nonce, plain, cipher, tag, Header(DefaultIterations));
        return JsonSerializer.SerializeToUtf8Bytes(new Envelope(Format, Version, DefaultIterations,
            Convert.ToBase64String(salt), Convert.ToBase64String(nonce), Convert.ToBase64String(tag), Convert.ToBase64String(cipher)));
    }

    public static byte[] Unprotect(byte[] archive, string password)
    {
        if (archive.Length > MaximumBytes) throw new TrackerException(Loc.T("Fichier de profil trop volumineux."));
        Envelope envelope;
        byte[] salt, nonce, tag, cipher;
        try
        {
            envelope = JsonSerializer.Deserialize<Envelope>(archive) ?? throw new JsonException();
            if (envelope.Format != Format) throw new JsonException();
            salt = Convert.FromBase64String(envelope.Salt); nonce = Convert.FromBase64String(envelope.Nonce);
            tag = Convert.FromBase64String(envelope.Tag); cipher = Convert.FromBase64String(envelope.Data);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or ArgumentNullException or NotSupportedException)
        { throw new TrackerException(Loc.T("Ce fichier n’est pas un profil Codex Tracker.")); }
        if (envelope.Version != Version) throw new TrackerException(Loc.T("Ce profil vient d’une version plus récente de Codex Tracker. Mettez à jour avant de l’importer."));
        if (envelope.Iterations is < MinimumIterations or > MaximumIterations || salt.Length != 16 || nonce.Length != 12 || tag.Length != 16)
            throw new TrackerException(Loc.T("Ce fichier n’est pas un profil Codex Tracker."));
        var plain = new byte[cipher.Length];
        try
        {
            using var aes = new AesGcm(Key(password, salt, envelope.Iterations), tag.Length);
            aes.Decrypt(nonce, cipher, tag, plain, Header(envelope.Iterations));
        }
        catch (AuthenticationTagMismatchException) { throw new TrackerException(Loc.T("Mot de passe incorrect, ou fichier modifié.")); }
        using var input = new GZipStream(new MemoryStream(plain), CompressionMode.Decompress);
        using var output = new MemoryStream();
        var buffer = new byte[81920]; int read;
        while ((read = input.Read(buffer)) > 0)
        {
            output.Write(buffer, 0, read);
            if (output.Length > MaximumBytes) throw new TrackerException(Loc.T("Fichier de profil trop volumineux."));
        }
        return output.ToArray();
    }

    private static void CheckPassword(string password)
    {
        if (string.IsNullOrEmpty(password) || password.Length < MinimumPasswordLength)
            throw new TrackerException(Loc.F("Choisissez un mot de passe d’au moins {0} caractères.", MinimumPasswordLength));
    }
    private static byte[] Key(string password, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, 32);
    private static byte[] Header(int iterations) => Encoding.UTF8.GetBytes($"{Format}/{Version}/{iterations}");
}
