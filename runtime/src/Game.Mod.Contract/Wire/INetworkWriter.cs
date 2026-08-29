namespace Game.Mod.Contract.Wire
{
    /// <summary>
    /// 安全写入原语（§11.4 / §14.11）：字段编号 + 边界检查。
    /// 消息、ModCall、网络协议共用同一套线格式（Rule 14）：
    /// 每个字段 = fieldId(u16) + wireType(u8) + data，可跳读、append-only 演化（§14.11.1）。
    /// </summary>
    public interface INetworkWriter
    {
        void WriteInt32(int fieldId, int value);
        void WriteUInt32(int fieldId, uint value);
        void WriteInt64(int fieldId, long value);
        void WriteUInt64(int fieldId, ulong value);
        void WriteFloat(int fieldId, float value);
        void WriteBool(int fieldId, bool value);
        void WriteByte(int fieldId, byte value);
        void WriteString(int fieldId, string value);
        void WriteBytes(int fieldId, byte[] value);
    }

    /// <summary>
    /// 安全读取原语（§14.11.2）：buffer 上的游标，不是完整反序列化。
    /// 消费方只解析自己关心的字段，未知字段按长度 O(1) 跳过；
    /// 字段缺失（对方旧版本）返回 false——append-only 演化由机制保证。
    /// </summary>
    public interface INetworkReader
    {
        bool TryReadInt32(int fieldId, out int value);
        bool TryReadUInt32(int fieldId, out uint value);
        bool TryReadInt64(int fieldId, out long value);
        bool TryReadUInt64(int fieldId, out ulong value);
        bool TryReadFloat(int fieldId, out float value);
        bool TryReadBool(int fieldId, out bool value);
        bool TryReadByte(int fieldId, out byte value);
        bool TryReadString(int fieldId, out string value);
        bool TryReadBytes(int fieldId, out byte[] value);
    }
}
