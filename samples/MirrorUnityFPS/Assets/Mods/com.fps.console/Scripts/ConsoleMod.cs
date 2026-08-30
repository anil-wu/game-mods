using System;
using System.Collections.Generic;
using Game.Mod.Contract;
using Game.Mod.Contract.Wire;
using Game.Mod.Runtime;

namespace Com.Fps.Console
{
    /// <summary>
    /// 控制台 Mod（com.fps.console）：命令注册表 + 文本命令分发。
    /// 命令 = (modId, capabilityId)（§12.11）；其他 Mod 经 `console:register` 能力注册自己的命令，
    /// 客户端 ` 键呼出输入，命令文本 C2S → 服务端解析 → ModCall 执行 → 结果 S2C。
    /// </summary>
    public sealed class ConsoleMod : IMod
    {
        public static readonly ModId ModIdValue = new("com.fps.console");
        public static readonly CapabilityId RegisterCap = new(ModIdValue, "register");

        /// <summary>客户端输出（控制台视图显示）。</summary>
        public static string LastOutput { get; private set; } = "";
        public static long LastOutputTicks { get; private set; }

        /// <summary>静态桥（视图访问）。</summary>
        public static IModContext Context { get; private set; } = null!;

        private static readonly Dictionary<string, (ModId Mod, CapabilityId Cap)> Commands = new();

        public void Register(IModContext context)
        {
            Context = context;
            LastOutput = "";

            context.Mods.Export(RegisterCap, new RegisterHandler(RegisterCommand));
            if (context.HasServer)
                context.Network.RegisterProtocol(new CommandProtocol(), new CommandHandler(context));
            if (context.HasClient)
                context.Network.RegisterProtocol(new OutputProtocol(), new ClientOutputHandler());

            context.Log.Info($"控制台 Mod '{context.Info.Id}' v{context.Info.Version} 已注册");
        }

        public void Unregister(IModContext context)
        {
            Context = null!;
            LastOutput = "";
            Commands.Clear();
        }

        // ---- 命令注册（能力） ----

        public delegate bool RegisterHandler(object? args);

        /// <summary>注册命令：args=[name string, modId string, capabilityId string]。</summary>
        private static bool RegisterCommand(object? args)
        {
            if (args is not object[] a || a.Length < 3) return false;
            if (a[0] is not string name || a[1] is not string mod || a[2] is not string cap) return false;
            if (name.Length == 0 || !ModId.TryParse(mod, out var modId) ||
                !NamespacedId.TryParse(cap, out var capNs)) return false;
            Commands[name] = (modId, new CapabilityId(capNs));
            return true;
        }

        // ---- 命令分发（服务端） ----

        private sealed class CommandHandler : INetworkHandler
        {
            private readonly IModContext _context;
            public CommandHandler(IModContext context) => _context = context;

            public void Handle(in NetworkContext context, in object message)
            {
                var text = ((CommandMessage)message).Text?.Trim() ?? "";
                var result = Execute(_context, text);
                _context.Network.Broadcast(new OutputProtocol().Id, new OutputMessage(result));
            }
        }

        private static string Execute(IModContext context, string text)
        {
            if (text.Length == 0) return "";
            var name = text;
            var args = (object?)null;
            var space = text.IndexOf(' ');
            if (space > 0)
            {
                name = text.Substring(0, space);
                args = text.Substring(space + 1);
            }

            if (!Commands.TryGetValue(name, out var cmd))
                return $"未知命令: {name}";

            try
            {
                // 基础设施委托调用：命令注册即授权，豁免依赖校验（§IModManager.InvokeRegistered）
                var result = context.Mods.InvokeRegistered(cmd.Mod, cmd.Cap, args);
                return $"[{name}] → {(result is null ? "ok" : result.ToString())}";
            }
            catch (Exception e)
            {
                return $"[{name}] 执行失败: {e.Message}";
            }
        }

        private sealed class ClientOutputHandler : INetworkHandler
        {
            public void Handle(in NetworkContext context, in object message)
            {
                if (context.IsServer) return;
                LastOutput = ((OutputMessage)message).Text;
                LastOutputTicks = DateTime.UtcNow.Ticks;
            }
        }
    }

    public readonly struct CommandMessage
    {
        public readonly string Text;
        public CommandMessage(string text) => Text = text;
    }

    public readonly struct OutputMessage
    {
        public readonly string Text;
        public OutputMessage(string text) => Text = text;
    }

    public sealed class CommandProtocol : INetworkProtocol
    {
        public ProtocolId Id => ProtocolId.Of(ConsoleMod.ModIdValue, "command");
        public ushort Version => 1;
        public NetworkDirection Direction => NetworkDirection.ClientToServer;
        public int MaxSize => 256;

        public void Encode(in object message, INetworkWriter w) => w.WriteString(1, ((CommandMessage)message).Text);
        public object Decode(INetworkReader r)
        {
            r.TryReadString(1, out var text);
            return new CommandMessage(text);
        }
    }

    public sealed class OutputProtocol : INetworkProtocol
    {
        public ProtocolId Id => ProtocolId.Of(ConsoleMod.ModIdValue, "output");
        public ushort Version => 1;
        public NetworkDirection Direction => NetworkDirection.ServerToClient;
        public int MaxSize => 512;

        public void Encode(in object message, INetworkWriter w) => w.WriteString(1, ((OutputMessage)message).Text);
        public object Decode(INetworkReader r)
        {
            r.TryReadString(1, out var text);
            return new OutputMessage(text);
        }
    }
}
