using System;
using System.Collections.Generic;
using System.Reflection;
using Game.ECS;
using Game.Messaging;
using Game.Mod.Contract;
using Game.ModLoader;

namespace TestRunner
{
    /// <summary>标记一个静态方法为测试。</summary>
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class TestAttribute : Attribute { }

    /// <summary>断言辅助。</summary>
    public static class Assert
    {
        public static void True(bool condition, string message = "")
        {
            if (!condition) throw new Exception(message == "" ? "断言失败" : $"断言失败: {message}");
        }

        public static void Equal<T>(T expected, T actual, string message = "")
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
                throw new Exception($"{message}（期望 {expected}，实际 {actual}）");
        }
    }

    /// <summary>反射发现 [Test] 方法并运行，返回退出码（0=全部通过）。</summary>
    public static class TestRunner
    {
        public static int RunAll()
        {
            int passed = 0, failed = 0;
            var asm = Assembly.GetExecutingAssembly();
            foreach (var type in asm.GetTypes())
            {
                foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (method.GetCustomAttribute<TestAttribute>() is null) continue;
                    try
                    {
                        method.Invoke(null, null);
                        passed++;
                        Console.WriteLine($"PASS  {type.Name}.{method.Name}");
                    }
                    catch (Exception e)
                    {
                        failed++;
                        Console.WriteLine($"FAIL  {type.Name}.{method.Name}: {e.InnerException?.Message ?? e.Message}");
                    }
                }
            }
            Console.WriteLine(failed == 0 ? $"=== ALL PASS ({passed}) ===" : $"=== {failed} FAILED / {passed} PASSED ===");
            return failed == 0 ? 0 : 1;
        }
    }

    /// <summary>静默日志（测试用）。</summary>
    public sealed class SilentLog : ILog
    {
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message) { }
    }

    /// <summary>测试用 IModContext。</summary>
    public sealed class TestModContext : IModContext
    {
        public ModId ModId { get; }
        public ModVersion Version { get; }
        public World World { get; }
        public SystemGroup Systems { get; }
        public MessageBus Messages { get; }
        public ILog Log { get; } = new SilentLog();

        public TestModContext(string modId, string version, World world, SystemGroup systems, MessageBus messages)
        {
            ModId = new ModId(modId);
            Version = ModVersion.Parse(version);
            World = world;
            Systems = systems;
            Messages = messages;
        }
    }
}
