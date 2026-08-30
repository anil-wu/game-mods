using System.Collections.Generic;
using Game.ECS;
using UnityEngine;

namespace Com.Fps.Inventory
{
    /// <summary>
    /// 拾取物视图（代码原语）：拾取物立方体 + E 拾取 + 计数 HUD。
    /// 表现只读复制状态；拾取判定/效果在 Server（§11.10/§12.11）。
    /// </summary>
    public sealed class InventoryView : MonoBehaviour
    {
        private readonly Dictionary<uint, GameObject> _items = new();

        private void Update()
        {
            var ctx = InventoryMod.Context;
            if (ctx is null) return;

            SyncItems();
            if (Input.GetKeyDown(KeyCode.E))
                TryPickupNearest();
        }

        private void SyncItems()
        {
            var world = InventoryMod.World;
            if (world is null) return;
            foreach (var (entityId, pos) in world.Store<ItemPosition3>().All())
            {
                var e = new Entity(entityId);
                if (world.Has<ReplicaTag>(e)) continue; // 取服务端权威侧
                if (!_items.TryGetValue(entityId, out var go))
                {
                    go = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    go.transform.localScale = Vector3.one * 0.4f;
                    var kind = world.Get<ItemTag>(e).Kind;
                    go.GetComponent<Renderer>().material.color =
                        kind == ItemKind.Health ? new Color(0.2f, 0.9f, 0.4f) : new Color(0.9f, 0.75f, 0.2f);
                    _items[entityId] = go;
                }
                go.transform.position = new Vector3(pos.X, pos.Y, pos.Z);
                go.transform.Rotate(0f, 60f * Time.deltaTime, 0f);
            }
        }

        private void TryPickupNearest()
        {
            var ctx = InventoryMod.Context;
            var world = InventoryMod.World;
            if (ctx is null || world is null) return;

            var camPos = Camera.main is not null ? Camera.main.transform.position : Vector3.zero;
            uint best = 0;
            var bestDist = 2.5f * 2.5f;
            foreach (var (entityId, pos) in world.Store<ItemPosition3>().All())
            {
                var e = new Entity(entityId);
                if (world.Has<ReplicaTag>(e)) continue;
                var d = (new Vector3(pos.X, pos.Y, pos.Z) - camPos).sqrMagnitude;
                if (d < bestDist)
                {
                    bestDist = d;
                    best = entityId;
                }
            }
            if (best == 0) return;
            if (!ctx.Network.Replication.TryGetNetworkId(new Entity(best), out var netId)) return;
            ctx.Network.SendToServer(new PickupProtocol().Id, new PickupRequest(netId));
        }

        private void OnGUI()
        {
            if (InventoryMod.Context is null) return;
            GUI.Label(new Rect(20, 68, 400, 30),
                $"治疗包 ×{Count(ItemKind.Health)}   弹药箱 ×{Count(ItemKind.Ammo)}    [E] 拾取");

            // 拾取反馈
            if (InventoryMod.LastPicked is { } picked &&
                (System.DateTime.UtcNow.Ticks - InventoryMod.LastPickedTicks) < 1_000_000)
            {
                var text = picked.Kind == ItemKind.Health ? $"+{picked.Amount} 生命" : $"+{picked.Amount} 弹药";
                GUI.Label(new Rect(Screen.width / 2f - 40, Screen.height / 2f + 30, 120, 24), text);
            }
        }

        private static int Count(ItemKind kind) =>
            InventoryMod.Collected.TryGetValue(kind, out var n) ? n : 0;
    }
}
