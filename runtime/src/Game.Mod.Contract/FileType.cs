namespace Game.Mod.Contract
{
    /// <summary>
    /// 包内文件类型，决定加载管线。
    /// </summary>
    public enum FileType
    {
        /// <summary>程序集 (.dll)，反射加载并扫描 IMod 入口。</summary>
        Code,

        /// <summary>数据/配置文件 (JSON 等)，按 Schema 解析注册内容定义。</summary>
        Data,

        /// <summary>资源 (贴图/模型/音频/bundle)，交给资产管线。</summary>
        Asset
    }

}
