using System.Collections.Generic;
using Game.ECS;
using UnityEngine;

namespace Com.Fps.Npc
{
    /// <summary>NPC 视图（代码原语）：深色胶囊体，表现只读复制状态。</summary>
    public sealed class NpcView : MonoBehaviour
    {
        private readonly Dictionary<uint, GameObject> _bodies = new();

        private void Update()
        {
            var world = NpcMod.World;
            if (world is null) return;
            foreach (var (entityId, pos) in world.Store<NpcPosition3>().All())
            {
                var e = new Entity(entityId);
                if (world.Has<ReplicaTag>(e)) continue;
                if (!_bodies.TryGetValue(entityId, out var go))
                {
                    go = GameObject.CreatePrimitive(PrimitiveType.Capsule);
                    go.GetComponent<Renderer>().material.color = new Color(0.35f, 0.15f, 0.5f);
                    _bodies[entityId] = go;
                }
                go.transform.position = new Vector3(pos.X, pos.Y, pos.Z);
                var alive = world.TryGet<NpcHealth>(e, out var hp) && hp.Current > 0;
                go.transform.rotation = alive ? Quaternion.identity : Quaternion.Euler(85f, 0f, 0f);
            }
        }
    }
}
