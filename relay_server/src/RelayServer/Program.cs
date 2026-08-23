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
app.MapPost("/allocate", () =>
{
    var r = allocation.Allocate();
    return Results.Ok(new
    {
        joinCode = r.JoinCode,
        roomId = r.RoomId,
        relayEndpoint = Endpoint(),
        token = Convert.ToBase64String(r.Token),
        expiresAt = r.Expiry,
    });
});

app.MapPost("/resolve", (ResolveRequest req) =>
{
    if (!allocation.TryResolve(req.JoinCode, out var r))
        return Results.NotFound(new { error = "invalid join code" });
    return Results.Ok(new
    {
        joinCode = r.JoinCode,
        roomId = r.RoomId,
        relayEndpoint = Endpoint(),
        token = Convert.ToBase64String(r.Token),
        expiresAt = r.Expiry,
    });
});

app.MapGet("/health", () => Results.Ok(new { status = "ok", rooms = rooms.Count }));

// ---------- 启动 UDP 数据面 ----------
var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
_ = node.RunAsync(lifetime.ApplicationStopping);

app.Run();

record ResolveRequest(string JoinCode);
