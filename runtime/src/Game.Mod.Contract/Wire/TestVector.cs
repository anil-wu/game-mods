using System;

namespace Game.Mod.Contract.Wire
{
    /// <summary>
    /// 契约测试向量（§14.11.3）：owner Mod 随版本发布的"样例字段 + 规范编码字节"。
    ///
    /// 无共享 DLL 模式（Rule 19）下，消费方各自重复定义自己的解析器——这带来解析发散风险
    /// （owner 改了字节布局，消费方手写解析器静默失效）。测试向量是关键补偿机制：
    /// 消费方 CI 用自己的解析器解析 <see cref="Bytes"/>，应解出 <see cref="Sample"/> 中
    /// （自己关心的）字段；两端对同一字节解出一致结果，即"可重复定义"未漂移。
    ///
    /// 第一方核心 Mod 可直接在共享测试程序集中跑（如 tests/TestRunner/TestVectorTests）；
    /// UGC 场景下向量应序列化进包（样例 + 字节 hex），消费方 CI 离线校验。
    /// </summary>
    public sealed class TestVector
    {
        /// <summary>契约标识（如 "position_row"），对应契约文档（CONTRACT.md）中的行/消息名。</summary>
        public readonly string ContractId;

        /// <summary>样例字段值（owner 语义，编码前的字段元组）。</summary>
        public readonly object?[] Sample;

        /// <summary>规范编码字节（owner 用自己的 encoder 产出；正确解析器应从中解出 Sample）。</summary>
        public readonly byte[] Bytes;

        public TestVector(string contractId, object?[] sample, byte[] bytes)
        {
            ContractId = contractId ?? throw new ArgumentNullException(nameof(contractId));
            Sample = sample ?? throw new ArgumentNullException(nameof(sample));
            Bytes = bytes ?? throw new ArgumentNullException(nameof(bytes));
        }

        /// <summary>用 DataCodec 规范编码一个样例元组，构造向量（owner 侧）。</summary>
        public static TestVector Of(string contractId, object?[] sample)
            => new(contractId, sample, DataCodec.Write(sample).ToArray());

        /// <summary>解码 <see cref="Bytes"/> 回字段元组——消费方解析器（TryRead）的输入。</summary>
        public object?[] DecodeFields()
            => DataCodec.Read(new PayloadReader(new PayloadBuffer(Bytes)));

        public override string ToString() => $"TestVector({ContractId}, {Sample.Length} fields, {Bytes.Length} bytes)";
    }
}
