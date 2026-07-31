using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpencodeRemote.Cli;

internal static class ConfigurationCommand
{
    public static async Task<int> ConfigureAsync(string path, CancellationToken cancellationToken)
    {
        var current = await ReadAsync(path, cancellationToken);
        Console.WriteLine("Configuração global do OpenCode Remote. Deixe em branco para manter o valor atual.\n");

        var token = ReadValue("Token do bot Telegram", current.Token, secret: true);
        var userIdText = ReadValue("Telegram user ID permitido", current.AllowedUserId == 0 ? "" : current.AllowedUserId.ToString());
        if (!long.TryParse(userIdText, out var userId) || userId <= 0)
        {
            Console.Error.WriteLine("O Telegram user ID deve ser um número positivo.");
            return 2;
        }

        var username = ReadValue("Usuário do servidor OpenCode", current.Username, defaultValue: "opencode");
        var password = ReadValue("Senha do servidor OpenCode", current.Password, secret: true);
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(password))
        {
            Console.Error.WriteLine("Token e senha são obrigatórios.");
            return 2;
        }

        await SaveAsync(path, token, userId, username, password, cancellationToken);

        Console.WriteLine($"\nConfiguração salva em {path}");
        return 0;
    }

    public static async Task<int> ShowAsync(string path, CancellationToken cancellationToken)
    {
        var current = await ReadAsync(path, cancellationToken);
        Console.WriteLine($"Arquivo: {path}");
        Console.WriteLine($"Telegram token: {Mask(current.Token)}");
        Console.WriteLine($"Telegram user ID: {(current.AllowedUserId == 0 ? "não configurado" : current.AllowedUserId)}");
        Console.WriteLine($"OpenCode usuário: {current.Username}");
        Console.WriteLine($"OpenCode senha: {Mask(current.Password)}");
        return File.Exists(path) ? 0 : 1;
    }

    internal static async Task SaveAsync(
        string path,
        string token,
        long userId,
        string username,
        string password,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var root = await ReadRootAsync(path, cancellationToken);
        var remote = GetOrCreateObject(root, "Remote");
        var telegram = GetOrCreateObject(remote, "Telegram");
        SetValue(telegram, "Token", token);
        SetValue(telegram, "AllowedUserId", userId);
        var openCode = GetOrCreateObject(remote, "OpenCode");
        SetValue(openCode, "Username", username);
        SetValue(openCode, "Password", password);

        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            var fileOptions = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.WriteThrough,
            };
            if (!OperatingSystem.IsWindows())
            {
                fileOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }
            await using (var stream = new FileStream(temporaryPath, fileOptions))
            {
                await JsonSerializer.SerializeAsync(stream, root, new JsonSerializerOptions { WriteIndented = true }, cancellationToken);
            }
            File.Move(temporaryPath, path, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }

    }

    private static async Task<ConfigurationModel> ReadAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return new ConfigurationModel("", 0, "opencode", "");
        }

        await using var stream = File.OpenRead(path);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var remote = TryGetProperty(document.RootElement, "Remote", out var remoteElement) ? remoteElement : default;
        var telegram = remote.ValueKind != JsonValueKind.Undefined && TryGetProperty(remote, "Telegram", out var telegramElement)
            ? telegramElement
            : default;
        var openCode = remote.ValueKind != JsonValueKind.Undefined && TryGetProperty(remote, "OpenCode", out var openCodeElement)
            ? openCodeElement
            : default;
        return new ConfigurationModel(
            GetString(telegram, "Token"),
            GetInt64(telegram, "AllowedUserId"),
            GetString(openCode, "Username", "opencode"),
            GetString(openCode, "Password"));
    }

    private static async Task<JsonObject> ReadRootAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        await using var stream = File.OpenRead(path);
        return await JsonNode.ParseAsync(stream, cancellationToken: cancellationToken) as JsonObject
            ?? throw new JsonException("O arquivo de configuração deve conter um objeto JSON.");
    }

    private static JsonObject GetOrCreateObject(JsonObject parent, string name)
    {
        var existing = parent.FirstOrDefault(property => string.Equals(property.Key, name, StringComparison.OrdinalIgnoreCase));
        if (existing.Value is JsonObject child)
        {
            return child;
        }

        child = [];
        parent[existing.Key ?? name] = child;
        return child;
    }

    private static void SetValue<T>(JsonObject parent, string name, T value)
    {
        var existing = parent.FirstOrDefault(property => string.Equals(property.Key, name, StringComparison.OrdinalIgnoreCase));
        parent[existing.Key ?? name] = JsonValue.Create(value);
    }

    private static string ReadValue(string label, string current, bool secret = false, string? defaultValue = null)
    {
        var hint = string.IsNullOrEmpty(current) ? defaultValue : secret ? Mask(current) : current;
        Console.Write($"{label}{(string.IsNullOrEmpty(hint) ? "" : $" [{hint}]")}: ");
        var value = secret ? ReadSecret() : Console.ReadLine() ?? "";
        return string.IsNullOrWhiteSpace(value) ? (string.IsNullOrEmpty(current) ? defaultValue ?? "" : current) : value.Trim();
    }

    private static string ReadSecret()
    {
        if (Console.IsInputRedirected)
        {
            return Console.ReadLine() ?? "";
        }

        var value = new List<char>();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                return new string([.. value]);
            }
            if (key.Key == ConsoleKey.Backspace && value.Count > 0)
            {
                value.RemoveAt(value.Count - 1);
                Console.Write("\b \b");
            }
            else if (!char.IsControl(key.KeyChar))
            {
                value.Add(key.KeyChar);
                Console.Write('*');
            }
        }
    }

    private static string Mask(string value) => string.IsNullOrEmpty(value) ? "não configurado" : new string('*', Math.Min(8, value.Length));
    private static string GetString(JsonElement element, string name, string fallback = "")
        => element.ValueKind != JsonValueKind.Undefined && TryGetProperty(element, name, out var value) ? value.GetString() ?? fallback : fallback;
    private static long GetInt64(JsonElement element, string name)
        => element.ValueKind != JsonValueKind.Undefined && TryGetProperty(element, name, out var value) && value.TryGetInt64(out var result) ? result : 0;

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private sealed record ConfigurationModel(string Token, long AllowedUserId, string Username, string Password);
}
