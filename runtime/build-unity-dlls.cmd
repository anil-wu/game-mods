@echo off
REM 构建框架核心库（netstandard2.1）并拷贝到 Unity 工程的 Plugins 目录。
setlocal
cd /d "%~dp0"

set PLUGINS_DIR=Unity\Assets\Plugins
if not exist "%PLUGINS_DIR%" mkdir "%PLUGINS_DIR%"

for %%p in (Game.Mod.Contract Game.ECS Game.Messaging Game.ModLoader) do (
  echo ^>^>^> 构建 %%p (netstandard2.1)
  dotnet build "src\%%p\%%p.csproj" -c Release -f netstandard2.1 >nul
  copy /y "src\%%p\bin\Release\netstandard2.1\%%p.dll" "%PLUGINS_DIR%" >nul
)

echo ^>^>^> 完成，DLL 已拷贝到 %PLUGINS_DIR%
endlocal
