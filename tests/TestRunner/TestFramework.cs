using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

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

        public static void Throws<T>(Action action, string message = "") where T : Exception
        {
            try
            {
                action();
            }
            catch (T)
            {
                return;
            }
            catch (Exception e)
            {
                throw new Exception($"{message}（期望 {typeof(T).Name}，实际 {e.GetType().Name}: {e.Message}）");
            }
            throw new Exception($"{message}（期望 {typeof(T).Name}，但未抛异常）");
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

    /// <summary>仓库根目录定位（从 bin 目录上溯）。</summary>
    public static class RepoPaths
    {
        public static string Root { get; } = FindRoot();

        public static string ModDir(string modId) => Path.Combine(Root, "mods", modId);

        public static string ModJson(string modId) => File.ReadAllText(Path.Combine(ModDir(modId), "mod.json"));

        private static string FindRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "README.md")))
                dir = dir.Parent;
            return dir?.FullName ?? throw new Exception("无法定位仓库根目录");
        }
    }
}
