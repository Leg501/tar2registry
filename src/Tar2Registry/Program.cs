using System.Collections.Concurrent;
using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http.Features;

var builder = WebApplication.CreateBuilder(args);

var registryUrl = (Environment.GetEnvironmentVariable("REGISTRY_URL") ?? "http://registry:5000").TrimEnd('/');
var registryUser = Environment.GetEnvironmentVariable("REGISTRY_USER") ?? "";
var registryPassword = Environment.GetEnvironmentVariable("REGISTRY_PASSWORD") ?? "";
var appPassword = Environment.GetEnvironmentVariable("APP_PASSWORD") ?? "";
var enableDelete = (Environment.GetEnvironmentVariable("ENABLE_DELETE") ?? "false").Equals("true", StringComparison.OrdinalIgnoreCase);
var registryTlsVerify = (Environment.GetEnvironmentVariable("REGISTRY_TLS_VERIFY") ?? "false").Equals("true", StringComparison.OrdinalIgnoreCase);
var maxUploadGb = double.TryParse(Environment.GetEnvironmentVariable("MAX_UPLOAD_GB"), out var g) ? g : 5;
var maxUploadBytes = (long)(maxUploadGb * 1024 * 1024 * 1024);
var skopeoPath = Environment.GetEnvironmentVariable("SKOPEO_PATH") ?? "skopeo";
var registryHost = registryUrl.Contains("//") ? registryUrl.Split("//", 2)[1] : registryUrl;

// лимиты тела запроса: по умолчанию Kestrel режет на ~28 МБ, multipart — на 128 МБ
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = maxUploadBytes + 1024 * 1024);
builder.Services.Configure<FormOptions>(o =>
{
    o.MultipartBodyLengthLimit = maxUploadBytes + 1024 * 1024;
    o.MemoryBufferThreshold = int.MaxValue; // буферизовать multipart на диск, не в RAM
});
builder.Services.AddDistributedMemoryCache();
builder.Services.AddSession(o =>
{
    o.Cookie.Name = "t2r";
    o.Cookie.HttpOnly = true;
    o.Cookie.SameSite = SameSiteMode.Strict; // защита от CSRF
    o.IdleTimeout = TimeSpan.FromHours(12);
});
var app = builder.Build();
app.UseSession();

var http = new HttpClient();
var authHeader = registryUser != "" ? Convert.ToBase64String(Encoding.UTF8.GetBytes($"{registryUser}:{registryPassword}")) : null;

async Task<HttpResponseMessage?> SendOrNull(HttpRequestMessage req)
{
    try { return await http.SendAsync(req); }
    catch (HttpRequestException) { return null; } // реестр недоступен
}

var repoRx = new Regex(@"^[a-z0-9]+([._-][a-z0-9]+)*(/[a-z0-9]+([._-][a-z0-9]+)*)*$", RegexOptions.Compiled);
var tagRx = new Regex(@"^[A-Za-z0-9_][A-Za-z0-9_.-]{0,127}$", RegexOptions.Compiled);

// возвращает клон корня JSON (документ диспозится) и ссылку на следующую страницу из Link
async Task<(JsonElement? Body, string? Next)> RegistryGet(string path)
{
    using var req = new HttpRequestMessage(HttpMethod.Get, registryUrl + path);
    if (authHeader != null) req.Headers.Authorization = new("Basic", authHeader);
    using var resp = await SendOrNull(req);
    if (resp is null || !resp.IsSuccessStatusCode) return (null, null);
    using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
    string? next = null;
    if (resp.Headers.TryGetValues("Link", out var links))
        foreach (var link in links)
            if (link.Contains("rel=\"next\""))
            {
                var a = link.IndexOf('<');
                var b = link.IndexOf('>');
                if (a >= 0 && b > a) next = link[(a + 1)..b];
            }
    return (doc.RootElement.Clone(), next);
}

IResult Html(string body) => Results.Content(body, "text/html; charset=utf-8");

IResult LoginPage(string? error) => Html(
    "<!doctype html><html lang=ru><meta charset=utf-8><meta name=viewport content='width=device-width, initial-scale=1'>"
    + "<title>Вход — tar2registry</title><style>"
    + ":root{--bg:#0d1117;--panel:#161b22;--line:#30363d;--text:#e6edf3;--text-2:#9198a1;--blue:#58a6ff;--green:#238636;--red:#f85149}"
    + "body{font-family:-apple-system,BlinkMacSystemFont,'Segoe UI','Noto Sans',Helvetica,Arial,sans-serif;background:var(--bg);color:var(--text);max-width:340px;margin:12vh auto;padding:0 16px;font-size:14px}"
    + ".card{background:var(--panel);border:1px solid var(--line);border-radius:6px;padding:24px 20px}"
    + "h2{margin:0;font-size:16px}.hint{color:var(--text-2);font-size:13px;margin:2px 0 16px}"
    + "input{width:100%;padding:8px 12px;margin:6px 0 14px;box-sizing:border-box;border-radius:6px;border:1px solid var(--line);background:var(--bg);color:var(--text);font-size:14px}"
    + "input:focus{outline:2px solid var(--blue);outline-offset:-1px}"
    + "button{width:100%;padding:8px;border:none;border-radius:6px;background:var(--green);color:#fff;font-size:14px;font-weight:500;cursor:pointer}"
    + "button:hover{background:#2ea043}"
    + "b{color:var(--red);display:block;margin-bottom:8px;font-weight:500;font-size:13px}</style><body>"
    + "<div class=card><h2>Реестр образов</h2><p class=hint>Вход по паролю</p>"
    + (error == "1" ? "<b>Неверный пароль</b>" : "")
    + "<form method=post><input type=password name=password placeholder=Пароль autofocus><button>Войти</button></form></div></body></html>");

app.MapGet("/login", (string? e) => LoginPage(e));

// примитивный rate-limit входа: растущая задержка после 5 неудач с одного IP
var loginFails = new ConcurrentDictionary<string, (int Count, DateTime Since)>();

app.MapPost("/login", async (HttpContext ctx) =>
{
    var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "?";
    var now = DateTime.UtcNow;
    var attempt = loginFails.GetOrAdd(ip, _ => (0, now));
    if (now - attempt.Since > TimeSpan.FromMinutes(15)) attempt = (0, now);
    if (attempt.Count >= 5) await Task.Delay(Math.Min(attempt.Count - 4, 10) * 2000);

    var form = await ctx.Request.ReadFormAsync();
    var ok = CryptographicOperations.FixedTimeEquals(
        Encoding.UTF8.GetBytes(form["password"].ToString()), Encoding.UTF8.GetBytes(appPassword));
    if (ok)
    {
        loginFails.TryRemove(ip, out _);
        ctx.Session.SetString("ok", "1");
        return Results.Redirect("/");
    }
    loginFails[ip] = (attempt.Count + 1, attempt.Since);
    return Results.Redirect("/login?e=1");
});

app.MapGet("/logout", (HttpContext ctx) => { ctx.Session.Clear(); return Results.Redirect("/login"); });

app.MapGet("/healthz", () => Results.Ok("ok"));

// защита: всё, кроме /login и /healthz, за паролем (если он задан)
app.Use(async (ctx, next) =>
{
    if (appPassword == "" || ctx.Session.GetString("ok") == "1"
        || ctx.Request.Path.StartsWithSegments("/login")
        || ctx.Request.Path.StartsWithSegments("/healthz"))
        await next();
    else
        ctx.Response.Redirect("/login");
});

// ---------- витрина ----------

app.MapGet("/api/catalog", async () =>
{
    // _catalog пагинирован — идём по Link: rel="next"
    var names = new List<string>();
    string? path = "/v2/_catalog?n=1000";
    while (path != null)
    {
        var (body, next) = await RegistryGet(path);
        if (body is null) return Results.StatusCode(502);
        if (body.Value.TryGetProperty("repositories", out var rs) && rs.ValueKind == JsonValueKind.Array)
            names.AddRange(rs.EnumerateArray().Select(e => e.GetString()).Where(s => s != null)!);
        path = next;
    }

    // теги — параллельно, не более 8 запросов к реестру одновременно
    var tagsByRepo = new List<string?>[names.Count];
    using var sem = new SemaphoreSlim(8);
    await Task.WhenAll(names.Select(async (name, i) =>
    {
        await sem.WaitAsync();
        try
        {
            var (tagsResp, _) = await RegistryGet($"/v2/{name}/tags/list");
            tagsByRepo[i] = tagsResp is not null && tagsResp.Value.TryGetProperty("tags", out var t) && t.ValueKind == JsonValueKind.Array
                ? t.EnumerateArray().Select(e => e.GetString()).Where(s => s != null).OrderDescending(StringComparer.Ordinal).ToList()
                : new List<string?>();
        }
        finally { sem.Release(); }
    }));

    var repos = names.Select((name, i) => new { name, tags = tagsByRepo[i] });
    return Results.Json(new { registryHost, enableDelete, repos });
});

// ---------- разбор tar ----------

app.MapPost("/api/inspect", async (HttpContext ctx) =>
{
    var form = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
    var file = form.Files["file"];
    if (file is null || file.Length == 0) return Results.Json(new { error = "нет файла" }, statusCode: 400);
    try
    {
        // Kestrel уже забуферизовал тело на диск — читаем поток напрямую, без копии в RAM
        await using var fs = file.OpenReadStream();
        var head = new byte[2];
        await fs.ReadExactlyAsync(head);
        fs.Position = 0;
        var isGzip = head[0] == 0x1f && head[1] == 0x8b;
        await using var gz = isGzip ? new GZipStream(fs, CompressionMode.Decompress) : null;
        await using var tar = new TarReader(isGzip ? gz! : fs);
        TarEntry? entry;
        while ((entry = await tar.GetNextEntryAsync()) is not null)
        {
            if (entry.EntryType == TarEntryType.RegularFile && entry.Name.TrimStart('.', '/') == "manifest.json")
            {
                using var s = entry.DataStream!;
                using var doc = await JsonDocument.ParseAsync(s);
                var tags = new List<string>();
                var root = doc.RootElement;
                var items = root.ValueKind == JsonValueKind.Array ? root.EnumerateArray().ToList() : [root];
                foreach (var item in items)
                    if (item.TryGetProperty("RepoTags", out var rt) && rt.ValueKind == JsonValueKind.Array)
                        foreach (var tg in rt.EnumerateArray())
                            tags.Add(tg.GetString() ?? "");
                return Results.Json(new { repoTags = tags });
            }
        }
        return Results.Json(new { error = "в архиве нет manifest.json" }, statusCode: 400);
    }
    catch (OperationCanceledException) { return Results.StatusCode(499); }
    catch (Exception ex)
    {
        return Results.Json(new { error = "файл не похож на docker save tar: " + ex.Message }, statusCode: 400);
    }
}).DisableAntiforgery();

// ---------- загрузка ----------

var uploadGate = new SemaphoreSlim(1, 1); // одна загрузка за раз: tar пишется на диск целиком

app.MapPost("/api/upload", async (HttpContext ctx) =>
{
    if (!await uploadGate.WaitAsync(0))
        return Results.Json(new { error = "уже идёт другая загрузка — дождитесь её окончания" }, statusCode: 409);
    var tmp = Path.Combine(Path.GetTempPath(), "t2r-" + Guid.NewGuid() + ".tar");
    string? authFile = null;
    try
    {
        var form = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
        var file = form.Files["file"];
        var repo = form["repo"].ToString().Trim().Trim('/');
        // метки через запятую или пробел: "latest, 2026.11.25-faf0f72"
        var tags = form["tag"].ToString()
            .Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (tags.Count == 0) tags.Add("latest");
        if (file is null || file.Length == 0 || repo == "")
            return Results.Json(new { error = "нужны файл и путь в реестре" }, statusCode: 400);
        if (file.Length > maxUploadBytes)
            return Results.Json(new { error = $"файл больше {maxUploadGb} ГБ" }, statusCode: 400);
        if (!repoRx.IsMatch(repo))
            return Results.Json(new { error = "некорректный путь в реестре: только строчные буквы, цифры и разделители . _ - /" }, statusCode: 400);
        foreach (var tag in tags)
            if (!tagRx.IsMatch(tag))
                return Results.Json(new { error = $"некорректная метка «{tag}»: буквы, цифры, . _ - до 128 символов" }, statusCode: 400);

        await using (var fs = File.Create(tmp)) await file.CopyToAsync(fs, ctx.RequestAborted);

        // креды — во временный authfile, а не в командную строку (видна в ps)
        if (authHeader != null)
        {
            authFile = Path.Combine(Path.GetTempPath(), "t2r-auth-" + Guid.NewGuid() + ".json");
            await File.WriteAllTextAsync(authFile, JsonSerializer.Serialize(new
            {
                auths = new Dictionary<string, object> { [registryHost] = new { auth = authHeader } }
            }));
        }

        foreach (var tag in tags)
        {
            var psi = new ProcessStartInfo
            {
                FileName = skopeoPath,
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = false, // stdout skopeo идёт в лог контейнера
            };
            psi.ArgumentList.Add("copy");
            psi.ArgumentList.Add($"--dest-tls-verify={(registryTlsVerify ? "true" : "false")}");
            if (authFile != null) { psi.ArgumentList.Add("--dest-authfile"); psi.ArgumentList.Add(authFile); }
            psi.ArgumentList.Add("docker-archive:" + tmp);
            psi.ArgumentList.Add($"docker://{registryHost}/{repo}:{tag}");
            using var p = Process.Start(psi)!;
            // читать stderr параллельно с ожиданием, иначе буфер pipe переполнится и процесс зависнет
            var errTask = p.StandardError.ReadToEndAsync();
            try
            {
                await p.WaitForExitAsync(ctx.RequestAborted);
            }
            catch (OperationCanceledException)
            {
                try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
                throw;
            }
            var err = await errTask;
            if (p.ExitCode != 0)
                return Results.Json(new { error = $"skopeo ({repo}:{tag}): " + err[^Math.Min(err.Length, 800)..] }, statusCode: 500);
        }
        return Results.Json(new { ok = true, message = $"{repo}:{string.Join(", ", tags)} загружен в реестр" });
    }
    catch (OperationCanceledException) { return Results.StatusCode(499); }
    catch (Exception ex)
    {
        return Results.Json(new { error = ex.Message }, statusCode: 500);
    }
    finally
    {
        try { File.Delete(tmp); } catch { }
        if (authFile != null) try { File.Delete(authFile); } catch { }
        uploadGate.Release();
    }
}).DisableAntiforgery();

// ---------- удаление метки ----------

app.MapPost("/api/delete", async (HttpContext ctx) =>
{
    if (!enableDelete) return Results.Json(new { error = "удаление отключено (ENABLE_DELETE=false)" }, statusCode: 403);
    var form = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
    var repo = form["repo"].ToString().Trim('/');
    var tag = form["tag"].ToString();
    if (repo == "" || tag == "") return Results.Json(new { error = "нужны repo и tag" }, statusCode: 400);
    if (!repoRx.IsMatch(repo) || !tagRx.IsMatch(tag))
        return Results.Json(new { error = "некорректные repo или tag" }, statusCode: 400);

    var req = new HttpRequestMessage(HttpMethod.Get, $"{registryUrl}/v2/{repo}/manifests/{tag}");
    if (authHeader != null) req.Headers.Authorization = new("Basic", authHeader);
    // docker v2, manifest list, OCI manifest и index — иначе multi-arch/OCI образы не удаляются
    req.Headers.Accept.ParseAdd("application/vnd.docker.distribution.manifest.v2+json");
    req.Headers.Accept.ParseAdd("application/vnd.docker.distribution.manifest.list.v2+json");
    req.Headers.Accept.ParseAdd("application/vnd.oci.image.manifest.v1+json");
    req.Headers.Accept.ParseAdd("application/vnd.oci.image.index.v1+json");
    using var resp = await SendOrNull(req);
    if (resp is null) return Results.Json(new { error = "реестр недоступен" }, statusCode: 502);
    if (!resp.IsSuccessStatusCode) return Results.Json(new { error = "манифест не найден" }, statusCode: 404);
    var digest = resp.Headers.TryGetValues("Docker-Content-Digest", out var vals) ? vals.FirstOrDefault() : null;
    if (digest is null) return Results.Json(new { error = "реестр не отдал digest" }, statusCode: 502);

    using var del = new HttpRequestMessage(HttpMethod.Delete, $"{registryUrl}/v2/{repo}/manifests/{digest}");
    if (authHeader != null) del.Headers.Authorization = new("Basic", authHeader);
    using var resp3 = await SendOrNull(del);
    if (resp3 is null) return Results.Json(new { error = "реестр недоступен" }, statusCode: 502);
    return resp3.IsSuccessStatusCode
        ? Results.Json(new { ok = true, message = $"{repo}:{tag} помечен удалённым" })
        : Results.Json(new { error = "реестр вернул " + resp3.StatusCode }, statusCode: 502);
}).DisableAntiforgery();

// ---------- статика ----------

app.UseDefaultFiles();
app.UseStaticFiles();

app.Run();
