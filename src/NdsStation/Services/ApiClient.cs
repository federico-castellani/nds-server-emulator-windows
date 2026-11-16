using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NdsStation.Services;

/// <summary>
/// Client for the NDS Station server. Endpoints and payloads match the web app and the
/// original Qt client, including their tolerance for both camelCase and snake_case fields.
/// </summary>
public sealed class ApiClient
{
    private readonly CookieContainer _cookies = new();
    private readonly HttpClient _http;
    private Uri _baseUri = new("http://localhost:3000");

    public ApiClient()
    {
        var handler = new HttpClientHandler
        {
            CookieContainer = _cookies,
            UseCookies = true,
            AllowAutoRedirect = true,
        };
        _http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public string BaseUrl
    {
        get => _baseUri.ToString().TrimEnd('/');
        set => _baseUri = NormalizeBaseUrl(value);
    }

    public string? Token { get; set; }

    public static Uri NormalizeBaseUrl(string url)
    {
        url = url.Trim();
        if (!url.Contains("://", StringComparison.Ordinal)) url = "http://" + url;
        if (!Uri.TryCreate(url.TrimEnd('/') + "/", UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ApiException("Indirizzo del server non valido.");
        }
        return uri;
    }

    /// <summary>Reads admin flags from the JWT payload, as the server may omit them elsewhere.</summary>
    public static bool TokenSaysAdmin(string? token)
    {
        if (string.IsNullOrEmpty(token)) return false;
        var parts = token.Split('.');
        if (parts.Length != 3) return false;
        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            var json = JsonNode.Parse(Convert.FromBase64String(payload)) as JsonObject;
            if (json is null) return false;
            foreach (var key in new[] { "is_admin", "isAdmin", "role" })
            {
                if (json.TryGetPropertyValue(key, out var value)) return IsTruthy(value);
            }
        }
        catch (FormatException) { }
        catch (JsonException) { }
        return false;
    }

    // --- Auth ---------------------------------------------------------------

    public async Task<LoginResult> LoginAsync(string username, string password, CancellationToken ct = default)
    {
        var body = new JsonObject { ["username"] = username, ["password"] = password };
        using var response = await SendAsync(HttpMethod.Post, "auth/login", JsonContent(body), ct, authenticated: false);
        var json = await ReadJsonObjectAsync(response, ct);

        if (json?["error"] is JsonNode error)
            throw new ApiException(error.ToString(), (int)response.StatusCode);

        string? token = _cookies.GetCookies(_baseUri)["auth_token"]?.Value;
        if (string.IsNullOrEmpty(token)) token = json?["token"]?.GetValue<string>();
        if (string.IsNullOrEmpty(token))
            throw new ApiException("Autenticazione fallita: nessun token ricevuto.");

        bool mustChange = ReadBool(json, "mustChangePassword", "must_change_password");
        bool isAdmin = ReadBool(json, "isAdmin", "is_admin") || TokenSaysAdmin(token);
        Token = token;
        return new LoginResult(token, mustChange, isAdmin);
    }

    public async Task ChangePasswordAsync(string newPassword, CancellationToken ct = default)
    {
        var body = new JsonObject { ["new_password"] = newPassword };
        using var _ = await SendAsync(HttpMethod.Post, "auth/change-password", JsonContent(body), ct);
    }

    public void Logout()
    {
        Token = null;
        foreach (Cookie cookie in _cookies.GetCookies(_baseUri)) cookie.Expired = true;
    }

    // --- Games, ROMs and saves ---------------------------------------------

    public async Task<IReadOnlyList<GameInfo>> GetGamesAsync(CancellationToken ct = default)
    {
        using var response = await SendAsync(HttpMethod.Get, "games", null, ct);
        var array = await ReadArrayAsync(response, "games", ct);
        return array.OfType<JsonObject>()
            .Select(g => new GameInfo(
                ReadId(g["id"]),
                g["name"]?.GetValue<string>() ?? "Senza titolo",
                g["url"]?.GetValue<string>()))
            .ToList();
    }

    public async Task<byte[]> DownloadRomAsync(string gameId, IProgress<double>? progress, CancellationToken ct = default)
    {
        using var response = await SendAsync(HttpMethod.Get, $"roms/{Uri.EscapeDataString(gameId)}.nds", null, ct,
            completion: HttpCompletionOption.ResponseHeadersRead);

        long? total = response.Content.Headers.ContentLength;
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = total is > 0 ? new MemoryStream((int)total.Value) : new MemoryStream();
        var chunk = new byte[81920];
        long received = 0;
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            buffer.Write(chunk, 0, read);
            received += read;
            if (total is > 0) progress?.Report((double)received / total.Value);
        }
        progress?.Report(1.0);
        return buffer.ToArray();
    }

    /// <summary>Returns the save on the server, or null when the game has none yet.</summary>
    public async Task<byte[]?> DownloadSaveAsync(string gameId, CancellationToken ct = default)
    {
        using var request = CreateRequest(HttpMethod.Get, $"saves?gameId={Uri.EscapeDataString(gameId)}", null, authenticated: true);
        using var response = await _http.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        await EnsureSuccessAsync(response, ct);

        // A JSON body means "no save" (e.g. {"error": ...} or {}), never raw save memory.
        if (response.Content.Headers.ContentType?.MediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true)
            return null;

        var data = await response.Content.ReadAsByteArrayAsync(ct);
        return data.Length > 0 ? data : null;
    }

    /// <summary>
    /// Uploads a save "on top of" the version the game started from: <paramref name="basedOn"/>
    /// is the SHA-256 (hex) of that server save, or null when the server had none. If another
    /// device saved in the meantime the server refuses with 409 (<see cref="ApiException.IsConflict"/>).
    /// <paramref name="force"/> overwrites regardless, after the player chose to.
    /// </summary>
    public async Task UploadSaveAsync(string gameId, byte[] saveData, string? basedOn, bool force = false, CancellationToken ct = default)
    {
        using var form = new MultipartFormDataContent();
        AddField(form, "gameId", gameId);
        AddFile(form, "saveFile", $"{gameId}.sav", new ByteArrayContent(saveData));
        using var _ = await SendAsync(HttpMethod.Post, "saves", form, ct, configure: request =>
        {
            if (force) return;
            if (basedOn is null) request.Headers.TryAddWithoutValidation("If-None-Match", "*");
            else request.Headers.TryAddWithoutValidation("If-Match", $"\"{basedOn}\"");
        });
    }

    // --- Console identity ---------------------------------------------------
    // The account's DS firmware (Wi-Fi settings and Nintendo WFC user ID), shared by all
    // the user's devices so games see one console instead of offering to erase friends.

    /// <summary>
    /// The firmware the server keeps for this account, or null when it has none yet.
    /// Supported is false on servers without the /console endpoint.
    /// </summary>
    public async Task<(bool Supported, byte[]? Firmware)> GetConsoleAsync(CancellationToken ct = default)
    {
        using var request = CreateRequest(HttpMethod.Get, "console", null, authenticated: true);
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new ApiException("Impossibile contattare il server. Controlla la connessione e l'indirizzo.", null, ex);
        }
        using (response)
        {
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed) return (false, null);
            await EnsureSuccessAsync(response, ct);
            var data = await response.Content.ReadAsByteArrayAsync(ct);
            return (true, data.Length > 0 ? data : null);
        }
    }

    public async Task UploadConsoleAsync(byte[] firmware, CancellationToken ct = default)
    {
        using var form = new MultipartFormDataContent();
        AddFile(form, "firmware", "console.bin", new ByteArrayContent(firmware));
        using var _ = await SendAsync(HttpMethod.Post, "console", form, ct);
    }

    // --- Users --------------------------------------------------------------

    public async Task<IReadOnlyList<SearchUser>> SearchUsersAsync(string query, CancellationToken ct = default)
    {
        using var response = await SendAsync(HttpMethod.Get, $"users/search?q={Uri.EscapeDataString(query)}", null, ct);
        var array = await ReadArrayAsync(response, "users", ct);
        return array.OfType<JsonObject>()
            .Select(u => new SearchUser(ReadInt(u["id"]), u["username"]?.GetValue<string>() ?? ""))
            .ToList();
    }

    public async Task<IReadOnlyList<AdminUser>> GetAdminUsersAsync(CancellationToken ct = default)
    {
        using var response = await SendAsync(HttpMethod.Get, "admin/users", null, ct);
        var array = await ReadArrayAsync(response, "users", ct);
        return array.OfType<JsonObject>()
            .Select(u => new AdminUser(
                ReadInt(u["id"]),
                u["username"]?.GetValue<string>() ?? "",
                IsTruthy(u["is_admin"]) || IsTruthy(u["isAdmin"]) || IsTruthy(u["role"]),
                IsTruthy(u["must_change_password"]) || IsTruthy(u["mustChangePassword"])))
            .ToList();
    }

    public async Task RegisterUserAsync(string username, string password, CancellationToken ct = default)
    {
        var body = new JsonObject { ["username"] = username, ["password"] = password };
        using var _ = await SendAsync(HttpMethod.Post, "auth/register", JsonContent(body), ct);
    }

    public async Task DeleteUserAsync(int id, CancellationToken ct = default)
    {
        using var _ = await SendAsync(HttpMethod.Delete, $"admin/users/{id}", null, ct);
    }

    public async Task ResetUserPasswordAsync(int id, string newPassword, CancellationToken ct = default)
    {
        var body = new JsonObject { ["new_password"] = newPassword, ["must_change_password"] = true };
        using var _ = await SendAsync(HttpMethod.Post, $"admin/users/{id}/reset-password", JsonContent(body), ct);
    }

    // --- Library management -------------------------------------------------

    public async Task AddGameTitleAsync(string name, CancellationToken ct = default)
    {
        // The server reads a form field named "title" here.
        using var form = new MultipartFormDataContent();
        AddField(form, "title", name);
        using var _ = await SendAsync(HttpMethod.Post, "admin/games", form, ct);
    }

    public async Task UploadGameAsync(string name, string romPath, IProgress<double>? progress, CancellationToken ct = default)
    {
        await using var file = File.OpenRead(romPath);
        using var form = new MultipartFormDataContent();
        AddField(form, "gameName", name);
        AddFile(form, "ndsFile", Path.GetFileName(romPath), new ProgressStreamContent(file, progress));
        using var _ = await SendAsync(HttpMethod.Post, "games", form, ct);
    }

    // --- Plumbing -----------------------------------------------------------

    private HttpRequestMessage CreateRequest(HttpMethod method, string path, HttpContent? content, bool authenticated)
    {
        var request = new HttpRequestMessage(method, new Uri(_baseUri, path)) { Content = content };
        if (authenticated && !string.IsNullOrEmpty(Token))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        return request;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, HttpContent? content,
        CancellationToken ct, bool authenticated = true, HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead,
        Action<HttpRequestMessage>? configure = null)
    {
        using var request = CreateRequest(method, path, content, authenticated);
        configure?.Invoke(request);
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, completion, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new ApiException("Impossibile contattare il server. Controlla la connessione e l'indirizzo.", null, ex);
        }

        try
        {
            await EnsureSuccessAsync(response, ct);
        }
        catch
        {
            response.Dispose();
            throw;
        }
        return response;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;

        string message = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "Sessione non valida o scaduta.",
            HttpStatusCode.Forbidden => "Non hai i permessi per questa operazione.",
            HttpStatusCode.NotFound => "Risorsa non trovata sul server.",
            _ => $"Errore del server (HTTP {(int)response.StatusCode}).",
        };
        try
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            // {"error": "..."} from the original server, {"detail": "..."} from FastAPI.
            if (JsonNode.Parse(text) is JsonObject obj && (obj["error"] ?? obj["detail"]) is JsonValue err)
                message = err.ToString();
        }
        catch (JsonException) { }
        throw new ApiException(message, (int)response.StatusCode);
    }

    // .NET writes multipart names unquoted (name=gameId); some server parsers only accept
    // the quoted form the browser and the Qt client send, so set the header explicitly.
    private static void AddField(MultipartFormDataContent form, string name, string value)
    {
        var content = new StringContent(value);
        content.Headers.ContentType = null;
        content.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data") { Name = Quote(name) };
        form.Add(content);
    }

    private static void AddFile(MultipartFormDataContent form, string name, string fileName, HttpContent content)
    {
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data")
        {
            Name = Quote(name),
            FileName = Quote(fileName),
        };
        form.Add(content);
    }

    private static string Quote(string value) => "\"" + value.Replace("\"", "") + "\"";

    private static StringContent JsonContent(JsonNode body) =>
        new(body.ToJsonString(), Encoding.UTF8, "application/json");

    private static async Task<JsonObject?> ReadJsonObjectAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var text = await response.Content.ReadAsStringAsync(ct);
        if (string.IsNullOrWhiteSpace(text)) return null;
        try
        {
            return JsonNode.Parse(text) switch
            {
                JsonObject obj => obj,
                // The server answers a failed login with [{"error": ...}, 401].
                JsonArray { Count: > 0 } array when array[0] is JsonObject first => first,
                _ => null,
            };
        }
        catch (JsonException) { return null; }
    }

    private static async Task<JsonArray> ReadArrayAsync(HttpResponseMessage response, string wrapperKey, CancellationToken ct)
    {
        var text = await response.Content.ReadAsStringAsync(ct);
        JsonNode? node;
        try { node = JsonNode.Parse(text); }
        catch (JsonException) { throw new ApiException("Risposta del server non valida."); }

        return node switch
        {
            JsonArray array => array,
            JsonObject obj when obj[wrapperKey] is JsonArray inner => inner,
            _ => new JsonArray(),
        };
    }

    private static bool ReadBool(JsonObject? json, params string[] keys)
    {
        if (json is null) return false;
        foreach (var key in keys)
        {
            if (json.TryGetPropertyValue(key, out var value)) return IsTruthy(value);
        }
        return false;
    }

    private static bool IsTruthy(JsonNode? node)
    {
        if (node is not JsonValue value) return false;
        if (value.TryGetValue<bool>(out var b)) return b;
        if (value.TryGetValue<double>(out var d)) return d == 1;
        if (value.TryGetValue<string>(out var s))
        {
            s = s.ToLowerInvariant();
            return s is "true" or "admin" or "1";
        }
        return false;
    }

    private static string ReadId(JsonNode? node)
    {
        if (node is JsonValue value)
        {
            if (value.TryGetValue<string>(out var s)) return s;
            if (value.TryGetValue<double>(out var d)) return ((long)d).ToString();
        }
        return "";
    }

    private static int ReadInt(JsonNode? node)
    {
        if (node is JsonValue value)
        {
            if (value.TryGetValue<double>(out var d)) return (int)d;
            if (value.TryGetValue<string>(out var s) && int.TryParse(s, out var i)) return i;
        }
        return 0;
    }

    /// <summary>Stream content that reports upload progress.</summary>
    private sealed class ProgressStreamContent : HttpContent
    {
        private readonly Stream _source;
        private readonly IProgress<double>? _progress;

        public ProgressStreamContent(Stream source, IProgress<double>? progress)
        {
            _source = source;
            _progress = progress;
        }

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            var buffer = new byte[81920];
            long sent = 0;
            long total = _source.Length;
            int read;
            while ((read = await _source.ReadAsync(buffer)) > 0)
            {
                await stream.WriteAsync(buffer.AsMemory(0, read));
                sent += read;
                if (total > 0) _progress?.Report((double)sent / total);
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            length = _source.Length;
            return true;
        }
    }
}
