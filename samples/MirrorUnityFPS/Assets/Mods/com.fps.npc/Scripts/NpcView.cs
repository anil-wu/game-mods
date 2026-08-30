using System.Collections.Generic;
using Game.ECS;
using Game.Mod.Contract;
using UnityEngine;

namespace Com.Fps.Npc
{
    /// <summary>NPC 视图（忠实资源版）：半人马模型，经所属 Mod 的 ResourceScope 加载（Rule 3/§8.4）。</summary>
    public sealed class NpcView : MonoBehaviour
    {
        private static readonly AssetId Centaur = new(NpcMod.ModIdValue,
            "Model/True_Fantastic_Creatures/Centaur/Prefabs/Centaur");

        private readonly Dictionary<uint, GameObject> _bodies = new();
        private GameObject? _prefab;

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
                    go = Spawn();
                    go.name = $"Npc_{entityId}";
                    _bodies[entityId] = go;
                }
                go.transform.position = new Vector3(pos.X, pos.Y, pos.Z);
                var alive = world.TryGet<NpcHealth>(e, out var hp) && hp.Current > 0;
                go.transform.rotation = alive ? Quaternion.Euler(0f, 180f, 0f) : Quaternion.Euler(85f, 180f, 0f);
            }
        }

        private GameObject Spawn()
        {
            if (_prefab is null)
            {
                try { _prefab = NpcMod.Context?.Resources.Load(Centaur) as GameObject; }
                catch (System.Exception) { _prefab = null; }
            }
            return _prefab is not null
                ? Object.Instantiate(_prefab)
                : GameObject.CreatePrimitive(PrimitiveType.Capsule); // bundle 缺失回退
        }
    }
}
