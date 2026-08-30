using System.Security.Cryptography;
using RelayServer.Allocation;
using RelayServer.Relay;

// ---------- 配置（环境变量） ----------
var relayPort = int.Parse(Environment.GetEnvironmentVariable("RELAY_PORT") ?? "7777");
var publicHost = Environment.GetEnvironmentVariable("RELAY_PUBLIC_HOST") ?? "127.0.0.1";

var secretB64 = Environment.GetEnvironmentVariable("RELAY_SECRET");
byte[] secret;
if (string.IsNullOrWhiteSpace(secretB64))
{
    secret = RandomNumberGenerator.GetBytes(32);
    Console.WriteLine($"[Relay] 未设置 RELAY_SECRET，已生成临时密钥: {Convert.ToBase64String(secret)}");
}
else
{
    secret = Convert.FromBase64String(secretB64);
}

var idleTimeout = TimeSpan.FromSeconds(int.Parse(Environment.GetEnvironmentVariable("RELAY_IDLE_SECONDS") ?? "60"));
var tokenLifetime = TimeSpan.FromMinutes(int.Parse(Environment.GetEnvironmentVariable("RELAY_TOKEN_MINUTES") ?? "30"));

var rooms = new RoomTable();
var allocation = new AllocationService(secret, tokenLifetime);
var node = new UdpRelayNode(relayPort, secret, rooms, idleTimeout);
node.Log = Console.WriteLine;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

string Endpoint() => $"{publicHost}:{relayPort}";

// ---------- 控制面 HTTP ----------
// 响应统一为管道分隔文本（§6.3）：joinCode|roomId|relayEndpoint|tokenB64|expiryMs——
// 客户端零依赖解析（netstandard2.1 无 System.Text.Json），人类可读；JSON 仅保留在 /health。
string PipeText(AllocationResult r) =>
    $"{r.JoinCode}|{r.RoomId}|{Endpoint()}|{Convert.ToBase64String(r.Token)}|{r.Expiry.ToUnixTimeMilliseconds()}";

app.MapPost("/allocate", () =>
{
    var r = allocation.Allocate();
    return Results.Text(PipeText(r));
});

app.MapPost("/resolve", (ResolveRequest req) =>
{
    if (!allocation.TryResolve(req.JoinCode, out var r))
        return Results.NotFound("invalid join code");
    return Results.Text(PipeText(r));
});

app.MapGet("/health", () => Results.Ok(new { status = "ok", rooms = rooms.Count }));

// ---------- 启动 UDP 数据面 ----------
var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
_ = node.RunAsync(lifetime.ApplicationStopping);

app.Run();

record ResolveRequest(string JoinCode);
