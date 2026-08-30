using System;
using Game.ECS;
using Game.Messaging;
using Game.Mod.Contract;
using Game.Mod.Contract.Wire;

namespace Game.Mod.Runtime
{
    /// <summary>
    /// IModContext 的运行时实现：向 Mod 注入带 Scope 的能力门面（§5）。
    /// 所有门面绑定当前 ModObject：归属可追踪、卸载可强制清理（§12.10）。
    /// Context 在卸载后失效（§7 热卸载流程）。
    /// </summary>
    internal sealed class ModContextImpl : IModContext
    {
        private bool _valid = true;

        public IModInfo Info { get; }
        public IModManager Mods { get; }
        public IModResourceScope Resources { get; }
        public IModPoolScope Pool { get; }
        public IEcsContext Ecs { get; }
        public INetworkContext Network { get; }
        public IUiContext Ui { get; }
        public IServiceContext Services { get; }
        public IMessageContext Messages { get; }
        public bool HasClient { get; }
        public bool HasServer { get; }
        public IClientContext? Client { get; }
        public IServerContext? Server { get; }
        public ILog Log { get; }

        public ModContextImpl(
            ModInfo info,
            ModManager manager,
            ServiceRegistry services,
            World world,
            SystemGroup systems,
            MessageBus bus,
            IModResourceScope resources,
            IModPoolScope pool,
            RuntimeRole role,
            ILog log)
        {
            Info = info;
            Resources = resources;
            Pool = pool;
            HasClient = role != RuntimeRole.Server;
            HasServer = role != RuntimeRole.Client;
            Client = HasClient ? new ClientContextImpl() : null;
            Server = HasServer ? new ServerContextImpl() : null;
            Log = log;

            Messages = new MessageContextFacade(this, info.Id, bus);
            Services = new ServiceContextFacade(this, info.Id, services);
            Ecs = new EcsContextFacade(this, info.Id, world, systems);
            Network = new NetworkContextFacade(this, info.Id, services);
            Ui = new UiContextFacade(this, info.Id, services);
            Mods = new ModsFacade(this, info.Id, manager);
        }

        internal void Invalidate() => _valid = false;

        internal void ThrowIfInvalid()
        {
            if (!_valid)
                throw new ModStateException($"Mod '{Info.Id}' 的 Context 已失效（Mod 已卸载）");
        }

        private sealed class ClientContextImpl : IClientContext { }
        private sealed class ServerContextImpl : IServerContext { }
    }

    // ---- 门面：全部绑定 owner，归属可追踪（§12.10 合法通道） ----

    internal sealed class MessageContextFacade : IMessageContext
    {
        private readonly ModContextImpl _ctx;
        private readonly ModId _owner;
        private readonly MessageBus _bus;

        public MessageContextFacade(ModContextImpl ctx, ModId owner, MessageBus bus)
        {
            _ctx = ctx; _owner = owner; _bus = bus;
        }

        public PayloadWriter CreateWriter() => new();

        public void Publish(MessageId id, int version, in PayloadBuffer payload)
        {
            _ctx.ThrowIfInvalid();
            _bus.Publish(_owner, id, version, in payload);
        }

        public void Subscribe(MessageId id, MessageHandler handler)
        {
            _ctx.ThrowIfInvalid();
            _bus.Subscribe(_owner, id, handler);
        }
    }

    internal sealed class ServiceContextFacade : IServiceContext
    {
        private readonly ModContextImpl _ctx;
        private readonly ModId _owner;
        private readonly ServiceRegistry _services;

        public ServiceContextFacade(ModContextImpl ctx, ModId owner, ServiceRegistry services)
        {
            _ctx = ctx; _owner = owner; _services = services;
        }

        public void Register(ServiceId id, object service)
        {
            _ctx.ThrowIfInvalid();
            _services.Register(_owner, id, service);
        }

        public void Unregister(ServiceId id)
        {
            _ctx.ThrowIfInvalid();
            _services.Unregister(id);
        }

        public object Get(ServiceId id)
        {
            _ctx.ThrowIfInvalid();
            return _services.Get(id);
        }

        public bool TryGet(ServiceId id, out object service)
        {
            _ctx.ThrowIfInvalid();
            return _services.TryGet(id, out service);
        }
    }

    internal sealed class EcsContextFacade : IEcsContext
    {
        private readonly ModContextImpl _ctx;
        private readonly ModId _owner;
        private readonly SystemGroup _systems;

        public World World { get; }

        public EcsContextFacade(ModContextImpl ctx, ModId owner, World world, SystemGroup systems)
        {
            _ctx = ctx; _owner = owner; World = world; _systems = systems;
        }

        public void RegisterComponent(Type componentType)
        {
            _ctx.ThrowIfInvalid();
            if (!typeof(IComponent).IsAssignableFrom(componentType))
                throw new ArgumentException($"'{componentType}' 未实现 IComponent", nameof(componentType));
            World.RegisterComponent(componentType);
        }

        public void RegisterSystem(ISystem system, SystemSide side)
        {
            _ctx.ThrowIfInvalid();
            _systems.Add(system, side, _owner);
        }

        public Entity CreateEntity()
        {
            _ctx.ThrowIfInvalid();
            return World.CreateEntity(_owner);
        }
    }

    internal sealed class NetworkContextFacade : INetworkContext
    {
        private readonly ModContextImpl _ctx;
        private readonly ModId _owner;
        private readonly ServiceRegistry _services;

        public NetworkContextFacade(ModContextImpl ctx, ModId owner, ServiceRegistry services)
        {
            _ctx = ctx; _owner = owner; _services = services;
            Replication = new ReplicationContextFacade(ctx, owner, services);
        }

        private INetworkRuntime Runtime
        {
            get
            {
                _ctx.ThrowIfInvalid();
                if (_services.TryGet(WellKnownServices.NetworkRuntime, out var svc))
                    return (INetworkRuntime)svc;
                throw new NoServiceException(WellKnownServices.NetworkRuntime);
            }
        }

        public bool IsActive =>
            _services.TryGet(WellKnownServices.NetworkRuntime, out var svc) && ((INetworkRuntime)svc).IsActive;

        public IReplicationContext Replication { get; }

        public void RegisterProtocol(INetworkProtocol protocol, INetworkHandler? handler) =>
            Runtime.RegisterProtocol(_owner, protocol, handler);

        public void UnregisterProtocol(ProtocolId id) => Runtime.UnregisterProtocol(_owner, id);

        public void SendToServer(ProtocolId id, object message) => Runtime.SendToServer(_owner, id, message);

        public void SendToClient(int connectionId, ProtocolId id, object message) =>
            Runtime.SendToClient(_owner, connectionId, id, message);

        public void Broadcast(ProtocolId id, object message) => Runtime.Broadcast(_owner, id, message);

        public void Start(NetworkConfig config) => Runtime.Start(config);

        public void Stop() => Runtime.Stop();
    }

    /// <summary>Replication 门面：绑定 owner 后路由到 Network.Mod 的复制运行时（§11.10）。</summary>
    internal sealed class ReplicationContextFacade : IReplicationContext
    {
        private readonly ModContextImpl _ctx;
        private readonly ModId _owner;
        private readonly ServiceRegistry _services;

        public ReplicationContextFacade(ModContextImpl ctx, ModId owner, ServiceRegistry services)
        {
            _ctx = ctx; _owner = owner; _services = services;
        }

        private INetworkRuntime Runtime
        {
            get
            {
                _ctx.ThrowIfInvalid();
                if (_services.TryGet(WellKnownServices.NetworkRuntime, out var svc))
                    return (INetworkRuntime)svc;
                throw new NoServiceException(WellKnownServices.NetworkRuntime);
            }
        }

        public void RegisterArchetype(ArchetypeId id, IComponentCodec[] codecs) =>
            Runtime.RegisterArchetype(_owner, id, codecs);

        public uint SpawnReplicated(Game.ECS.Entity entity, ArchetypeId id) =>
            Runtime.SpawnReplicated(_owner, entity, id);

        public void DespawnReplicated(Game.ECS.Entity entity) =>
            Runtime.DespawnReplicated(_owner, entity);

        public bool TryGetEntity(uint networkId, out Game.ECS.Entity entity) =>
            Runtime.TryGetEntity(networkId, out entity);

        public bool TryGetServerEntity(uint networkId, out Game.ECS.Entity entity) =>
            Runtime.TryGetServerEntity(networkId, out entity);

        public bool TryGetNetworkId(Game.ECS.Entity entity, out uint networkId) =>
            Runtime.TryGetNetworkId(entity, out networkId);
    }

    internal sealed class UiContextFacade : IUiContext
    {
        private readonly ModContextImpl _ctx;
        private readonly ModId _owner;
        private readonly ServiceRegistry _services;

        public UiContextFacade(ModContextImpl ctx, ModId owner, ServiceRegistry services)
        {
            _ctx = ctx; _owner = owner; _services = services;
        }

        private IWindowManager Windows
        {
            get
            {
                _ctx.ThrowIfInvalid();
                if (_services.TryGet(WellKnownServices.WindowManager, out var svc))
                    return (IWindowManager)svc;
                throw new NoServiceException(WellKnownServices.WindowManager);
            }
        }

        public void RegisterWindow(WindowDescriptor descriptor)
        {
            if (descriptor.Id.Mod != _owner)
                throw new ModStateException($"Mod '{_owner}' 不能注册其他 Mod 命名空间的窗口 '{descriptor.Id}'");
            Windows.RegisterWindow(_owner, descriptor);
        }

        public IWindowHandle Open(WindowId id, WindowOptions options = default) => Windows.Open(_owner, id, options);

        public void Close(WindowId id) => Windows.Close(_owner, id);

        public bool IsOpen(WindowId id) =>
            _services.TryGet(WellKnownServices.WindowManager, out var svc) && ((IWindowManager)svc).IsOpen(id);
    }

    internal sealed class ModsFacade : IModManager
    {
        private readonly ModContextImpl _ctx;
        private readonly ModId _caller;
        private readonly ModManager _manager;

        public ModsFacade(ModContextImpl ctx, ModId caller, ModManager manager)
        {
            _ctx = ctx; _caller = caller; _manager = manager;
        }

        public ModObject? Get(ModId id) => _manager.Get(id);
        public bool IsLoaded(ModId id) => _manager.IsLoaded(id);
        public ModObject Load(ModId id) => _manager.Load(id);
        public ModObject LoadFromDirectory(string modDirectory) => _manager.LoadFromDirectory(modDirectory);
        public ModManifest RegisterDirectory(string modDirectory) => _manager.RegisterDirectory(modDirectory);
        public void Unload(ModId id) => _manager.Unload(id);
        public System.Collections.Generic.List<string> UnloadAll() => _manager.UnloadAll();

        public void Export(CapabilityId id, CapabilityHandler handler)
        {
            _ctx.ThrowIfInvalid();
            _manager.Export(_caller, id, handler);
        }

        public PayloadBuffer Call(ModId target, CapabilityId id, in PayloadBuffer args)
        {
            _ctx.ThrowIfInvalid();
            return _manager.Call(_caller, target, id, in args);
        }

        public PayloadBuffer InvokeRegistered(ModId target, CapabilityId id, in PayloadBuffer args)
        {
            _ctx.ThrowIfInvalid();
            return _manager.InvokeRegistered(_caller, target, id, in args);
        }
    }
}
