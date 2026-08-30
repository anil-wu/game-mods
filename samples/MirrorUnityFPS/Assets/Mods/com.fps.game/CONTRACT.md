# com.fps.game 契约文档（Rule 19：契约 = 文档）

## 1. 定位

**主 Mod / 启动 Mod**：FPS 游戏的入口与身份。依赖全部组件 Mod（§12 依赖闭包），
装它一个 = 装整个游戏；Register = 启动，Unregister = 退出（§7 卸载镜像销毁全部组件）。

## 2. 导出能力（ModCall）

### game:status
- args：无
- 返回：string（"running" / "stopped"）

## 3. 依赖（Manifest 声明，由 ModResolver 拓扑）

```
com.fps.game
├── com.game.core / com.game.network
├── com.fps.player    （移动/生命/生成）
├── com.fps.weapon    （射击/换弹/命中）
├── com.fps.inventory （拾取/背包）
├── com.fps.npc       （敌对 NPC）
├── com.fps.mapgen    （程序化地图）
└── com.fps.console   （控制台命令）
```
