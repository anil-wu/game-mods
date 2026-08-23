using Game.Mod.Contract;
using ModServer.Registry;

// ---------- 配置 ----------
var packagesDir = Environment.GetEnvironmentVariable("MOD_PACKAGES_DIR")
    ?? Path.Combine(AppContext.BaseDirectory, "packages");
var registry = new ModRegistry(packagesDir);

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

// ---------- 上传 ----------
app.MapPost("/api/mods", async (IFormFile file) =>
{
    if (file is null || file.Length == 0)
        return Results.BadRequest(new { error = "缺少文件" });

    const long maxBytes = 512L * 1024 * 1024;
    if (file.Length > maxBytes)
        return Results.BadRequest(new { error = "文件过大" });

    using var ms = new MemoryStream();
    await file.CopyToAsync(ms);
    var data = ms.ToArray();

    ModManifest manifest;
    try { manifest = ModPackageReader.ReadManifest(data); }
    catch (Exception e) { return Results.BadRequest(new { error = $"manifest 无效: {e.Message}" }); }

    registry.Add(manifest, data);
    return Results.Ok(new { modId = manifest.ModId.Value, version = manifest.Version.ToString() });
}).DisableAntiforgery();

// ---------- 列表 ----------
app.MapGet("/api/mods", () =>
    Results.Ok(registry.ListAll().Select(m => new
    {
        modId = m.ModId.Value,
        version = m.Version.ToString(),
        dependencies = m.Dependencies.ToDictionary(k => k.Key.Value, v => v.Value.ToString()),
    })));

app.MapGet("/api/mods/{modId}", (string modId) =>
{
    if (!ModId.TryParse(modId, out var id)) return Results.BadRequest(new { error = "非法 modId" });
    return Results.Ok(registry.ListVersions(id).Select(m => m.Version.ToString()));
});

// ---------- 下载 ----------
app.MapGet("/api/mods/{modId}/{version}/download", (string modId, string version) =>
{
    if (!ModId.TryParse(modId, out var id)) return Results.BadRequest(new { error = "非法 modId" });
    if (!ModVersion.TryParse(version, out var ver)) return Results.BadRequest(new { error = "非法版本" });
    var rec = registry.Get(id, ver);
    if (rec is null) return Results.NotFound(new { error = "Mod 或版本不存在" });
    return Results.File(rec.PackagePath, "application/octet-stream", $"{id.Value}-{ver}.mod");
});

app.MapGet("/api/mods/{modId}/latest/download", (string modId) =>
{
    if (!ModId.TryParse(modId, out var id)) return Results.BadRequest(new { error = "非法 modId" });
    var rec = registry.GetLatest(id);
    if (rec is null) return Results.NotFound(new { error = "Mod 不存在" });
    return Results.File(rec.PackagePath, "application/octet-stream");
});

// ---------- 依赖解析 ----------
app.MapPost("/api/resolve", (ResolveRequest req) =>
{
    if (!ModId.TryParse(req.ModId, out var id)) return Results.BadRequest(new { error = "非法 modId" });
    if (!ModVersion.TryParse(req.Version, out var ver)) return Results.BadRequest(new { error = "非法版本" });
    try
    {
        var closure = registry.Resolve(id, ver);
        return Results.Ok(new
        {
            mods = closure.Select(r => new
            {
                modId = r.Manifest.ModId.Value,
                version = r.Manifest.Version.ToString(),
            })
        });
    }
    catch (Exception e)
    {
        return Results.BadRequest(new { error = e.Message });
    }
});

app.MapGet("/health", () => Results.Ok(new { status = "ok", mods = registry.Count }));

app.Run();

record ResolveRequest(string ModId, string Version);
