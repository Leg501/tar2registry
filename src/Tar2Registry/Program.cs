using System.Diagnostics;
using System.Formats.Tar;
using System.Text;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);

var registryUrl = (Environment.GetEnvironmentVariable("REGISTRY_URL") ?? "http://registry:5000").TrimEnd('/');
var registryUser = Environment.GetEnvironmentVariable("REGISTRY_USER") ?? "";
var registryPassword = Environment.GetEnvironmentVariable("REGISTRY_PASSWORD") ?? "";
var appPassword = Environment.GetEnvironmentVariable("APP_PASSWORD") ?? "";
var enableDelete = (Environment.GetEnvironmentVariable("ENABLE_DELETE") ?? "false").Equals("true", StringComparison.OrdinalIgnoreCase);
var maxUploadGb = double.TryParse(Environment.GetEnvironmentVariable("MAX_UPLOAD_GB"), out var g) ? g : 5;
var skopeoPath = Environment.GetEnvironmentVariable("SKOPEO_PATH") ?? "skopeo";
var registryHost = registryUrl.Contains("//") ? registryUrl.Split("//", 2)[1] : registryUrl;

builder.Services.AddDistributedMemoryCache();
builder.Services.AddSession(o => o.Cookie.Name = "t2r");
var app = builder.Build();
app.UseSession();

var http = new HttpClient();
var authHeader = registryUser != "" ? Convert.ToBase64String(Encoding.UTF8.GetBytes($"{registryUser}:{registryPassword}")) : null;

async Task<JsonElement?> RegistryGet(string path)
{
    var req = new HttpRequestMessage(HttpMethod.Get, registryUrl + path);
    if (authHeader != null) req.Headers.Authorization = new("Basic", authHeader);
    var resp = await http.SendAsync(req);
    if (!resp.IsSuccessStatusCode) return null;
    return JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
}

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

app.MapPost("/login", async (HttpContext ctx) =>
{
    var form = await ctx.Request.ReadFormAsync();
    if (form["password"] == appPassword)
    {
        ctx.Session.SetString("ok", "1");
        return Results.Redirect("/");
    }
    return Results.Redirect("/login?e=1");
});

app.MapGet("/logout", (HttpContext ctx) => { ctx.Session.Clear(); return Results.Redirect("/login"); });

// защита: всё, кроме /login, за паролем (если он задан)
app.Use(async (ctx, next) =>
{
    if (appPassword == "" || ctx.Session.GetString("ok") == "1"
        || ctx.Request.Path.StartsWithSegments("/login"))
        await next();
    else
        ctx.Response.Redirect("/login");
});

// ---------- витрина ----------

app.MapGet("/api/catalog", async () =>
{
    var cat = await RegistryGet("/v2/_catalog");
    if (cat is null) return Results.StatusCode(502);
    var repos = new List<object>();
    foreach (var name in cat.Value.GetProperty("repositories").EnumerateArray().Select(e => e.GetString()!))
    {
        var tagsResp = await RegistryGet($"/v2/{name}/tags/list");
        var tags = tagsResp is not null && tagsResp.Value.TryGetProperty("tags", out var t) && t.ValueKind == JsonValueKind.Array
            ? t.EnumerateArray().Select(e => e.GetString()).Where(s => s != null).OrderDescending(StringComparer.Ordinal).ToList()
            : new List<string?>();
        repos.Add(new { name, tags });
    }
    return Results.Json(new { registryHost, enableDelete, repos });
});

// ---------- разбор tar ----------

app.MapPost("/api/inspect", async (IFormFile file) =>
{
    if (file is null || file.Length == 0) return Results.Json(new { error = "нет файла" }, statusCode: 400);
    try
    {
        await using var ms = new MemoryStream();
        await file.CopyToAsync(ms);
        ms.Position = 0;
        await using var tar = new TarReader(ms);
        TarEntry? entry;
        while ((entry = await tar.GetNextEntryAsync()) is not null)
        {
            if (entry.EntryType == TarEntryType.RegularFile && entry.Name.TrimStart('.', '/').TrimStart('/') == "manifest.json")
            {
                using var s = entry.DataStream!;
                var doc = await JsonDocument.ParseAsync(s);
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
    catch (Exception ex)
    {
        return Results.Json(new { error = "файл не похож на docker save tar: " + ex.Message }, statusCode: 400);
    }
}).DisableAntiforgery();

app.MapPost("/api/upload", async (HttpContext ctx) =>
{
    var form = await ctx.Request.ReadFormAsync();
    var file = form.Files["file"];
    var repo = (form["repo"].ToString() ?? "").Trim().Trim('/');
    var tag = form["tag"].ToString().Trim();
    if (string.IsNullOrEmpty(tag)) tag = "latest";
    if (file is null || file.Length == 0 || repo == "")
        return Results.Json(new { error = "нужны файл и путь в реестре" }, statusCode: 400);
    if (file.Length > maxUploadGb * 1024L * 1024L * 1024L)
        return Results.Json(new { error = $"файл больше {maxUploadGb} ГБ" }, statusCode: 400);

    var tmp = Path.Combine(Path.GetTempPath(), "t2r-" + Guid.NewGuid() + ".tar");
    try
    {
        await using (var fs = File.Create(tmp)) await file.CopyToAsync(fs);
        var dest = $"docker://{registryHost}/{repo}:{tag}";
        var psi = new ProcessStartInfo
        {
            FileName = skopeoPath,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        psi.ArgumentList.Add("copy");
        psi.ArgumentList.Add("--dest-tls-verify=false");
        if (authHeader != null) psi.ArgumentList.Add("--dest-creds");
        if (authHeader != null) psi.ArgumentList.Add(registryUser + ":" + registryPassword);
        psi.ArgumentList.Add("docker-archive:" + tmp);
        psi.ArgumentList.Add(dest);
        using var p = Process.Start(psi)!;
        await p.WaitForExitAsync();
        var err = await p.StandardError.ReadToEndAsync();
        return p.ExitCode == 0
            ? Results.Json(new { ok = true, message = $"{repo}:{tag} загружен в реестр" })
            : Results.Json(new { error = "skopeo: " + err[^Math.Min(err.Length, 800)..] }, statusCode: 500);
    }
    catch (Exception ex)
    {
        return Results.Json(new { error = ex.Message }, statusCode: 500);
    }
    finally
    {
        try { File.Delete(tmp); } catch { }
    }
}).DisableAntiforgery();

// ---------- удаление метки ----------

app.MapPost("/api/delete", async (HttpContext ctx) =>
{
    if (!enableDelete) return Results.Json(new { error = "удаление отключено (ENABLE_DELETE=false)" }, statusCode: 403);
    var form = await ctx.Request.ReadFormAsync();
    var repo = form["repo"].ToString().Trim('/');
    var tag = form["tag"].ToString();
    if (repo == "" || tag == "") return Results.Json(new { error = "нужны repo и tag" }, statusCode: 400);

    var req = new HttpRequestMessage(HttpMethod.Get, $"{registryUrl}/v2/{repo}/manifests/{tag}");
    if (authHeader != null) req.Headers.Authorization = new("Basic", authHeader);
    req.Headers.Accept.ParseAdd("application/vnd.docker.distribution.manifest.v2+json");
    var resp = await http.SendAsync(req);
    if (!resp.IsSuccessStatusCode) return Results.Json(new { error = "манифест не найден" }, statusCode: 404);
    var digest = resp.Headers.TryGetValues("Docker-Content-Digest", out var vals) ? vals.FirstOrDefault() : null;
    if (digest is null) return Results.Json(new { error = "реестр не отдал digest" }, statusCode: 502);

    var del = new HttpRequestMessage(HttpMethod.Delete, $"{registryUrl}/v2/{repo}/manifests/{digest}");
    if (authHeader != null) del.Headers.Authorization = new("Basic", authHeader);
    var resp3 = await http.SendAsync(del);
    return resp3.IsSuccessStatusCode
        ? Results.Json(new { ok = true, message = $"{repo}:{tag} помечен удалённым" })
        : Results.Json(new { error = "реестр вернул " + resp3.StatusCode }, statusCode: 502);
}).DisableAntiforgery();

// ---------- статика ----------

app.UseDefaultFiles();
app.UseStaticFiles();

app.Run();
