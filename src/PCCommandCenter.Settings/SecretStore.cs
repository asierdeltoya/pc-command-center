using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PCCommandCenter.Core;

namespace PCCommandCenter.Settings;

public interface ISecretStore
{
    string? Get(string name);
    void Set(string name, string? value);
    bool Has(string name);
}

/// <summary>
/// Stores secrets (e.g. the AI API key) encrypted with Windows DPAPI, bound to the current user account.
/// The file on disk is unreadable for other users and other machines.
/// </summary>
public sealed class DpapiSecretStore : ISecretStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("PCCommandCenter.Secrets.v1");
    private readonly string _path;
    private readonly object _lock = new();
    private Dictionary<string, string> _cache;

    public DpapiSecretStore() : this(AppInfo.SecretsFile) { }

    public DpapiSecretStore(string path)
    {
        _path = path;
        _cache = Load();
    }

    public string? Get(string name)
    {
        lock (_lock) return _cache.TryGetValue(name, out var v) ? v : null;
    }

    public bool Has(string name) => !string.IsNullOrEmpty(Get(name));

    public void Set(string name, string? value)
    {
        lock (_lock)
        {
            if (string.IsNullOrEmpty(value)) _cache.Remove(name);
            else _cache[name] = value;
            Persist();
        }
    }

    private Dictionary<string, string> Load()
    {
        try
        {
            if (!File.Exists(_path)) return [];
            var plain = ProtectedData.Unprotect(File.ReadAllBytes(_path), Entropy, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(plain) ?? [];
        }
        catch (Exception ex)
        {
            AppLog.Warn("Unable to decrypt the secret store; secrets must be entered again", ex);
            return [];
        }
    }

    private void Persist()
    {
        try
        {
            var plain = JsonSerializer.SerializeToUtf8Bytes(_cache);
            var cipher = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
            CryptographicOperations.ZeroMemory(plain);
            File.WriteAllBytes(_path, cipher);
        }
        catch (Exception ex)
        {
            AppLog.Error("Unable to save the secret store", ex);
        }
    }
}
