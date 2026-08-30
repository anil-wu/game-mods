# com.game.modstore 契约文档（Rule 19：契约 = 文档）

> Mod 商店消费的"远程契约"是 mod_server 的 HTTP API（非进程内字节流）；
> 导出的 ModCall 能力遵循 §12.11 / 线格式规范（字段编号 + append-only）。

## 1. mod_server HTTP API（消费方视角）

### GET /api/mods

响应：`200 application/json`，数组：

```json
[
  {
    "modId": "com.sample.hello",
    "version": "0.1.0",
    "dependencies": { "com.game.core": ">=1.0.0" }
  }
]
```

### POST /api/resolve

请求：`application/json`：`{ "modId": "com.sample.hello", "version": "0.1.0" }`

响应：依赖闭包（含根 Mod；可选依赖不含在内）：

```json
{ "mods": [ { "modId": "com.game.core", "version": "1.0.0" }, { "modId": "com.sample.hello", "version": "0.1.0" } ] }
```

### GET /api/mods/{modId}/{version}/download

响应：`200 application/octet-stream` = .mod 包（zip）。

**.mod 包格式**：zip 归档，根目录必含 `manifest.json`（Manifest v2），其余为包内文件
（模块 DLL / data / assets，路径与 manifest 中声明一致）。
安装方必须做 zip-slip 校验：条目解包后路径越出目标目录即拒绝。

## 2. 导出能力（ModCall，owner = com.game.modstore）

### modstore:list

- args：无（null）
- 返回：string[]，元素为 `"modId@version"`（如 `"com.sample.hello@0.1.0"`）

### modstore:install_and_start

- args：string，`"modId@version"`
- 返回：bool（true = 已安装并启动；失败详见日志）

## 3. 行为约定

- 安装目录：`{localModsDir}/{modId}/`（Unity 侧为 `persistentDataPath/mods`）。
- 安装幂等：已加载 / 已安装的闭包成员跳过；目标 Mod 最终经 `ModManager.LoadFromDirectory` 启动，依赖递归解析。
- 会话限制（§11.14）：安装注册了网络协议的 Mod 应在离线 / 大厅状态下进行；联网会话中启动协议 Mod 属未定义行为。
