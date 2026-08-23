namespace Game.Mod.Contract
{
    /// <summary>
    /// 文件清单条目。
    /// </summary>
    public sealed class FileEntry
    {
        /// <summary>包内相对路径，也是资源寻址路径。</summary>
        public string Path { get; set; } = "";

        public FileType Type { get; set; }

        public long Size { get; set; }

        public string Sha256 { get; set; } = "";

        public override string ToString() => $"{Path} ({Type}, {Size} bytes)";
    }

}
