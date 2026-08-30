namespace Game.ECS
{
    /// <summary>
    /// 复制副本标记（§11.10）：Client 侧经 Replication 创建的实体。
    /// Host 单 World 下区分双端实体——服务端权威查询/统计必须排除 ReplicaTag；
    /// 纯 Client 环境下全部实体都是副本。
    /// </summary>
    public struct ReplicaTag : IComponent { }
}
