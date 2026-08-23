# Plugins 目录

此目录存放框架核心库的 **netstandard2.1** 编译产物（供 Unity 2022.3 引用）：

- Game.Mod.Contract.dll
- Game.ECS.dll
- Game.Messaging.dll
- Game.ModLoader.dll

运行 `runtime/build-unity-dlls.sh` 自动构建并拷贝到此处。
这些 DLL 会被 Assembly-CSharp（含 `Assets/Scripts/` 下的引导代码）自动引用。
