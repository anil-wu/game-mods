# com.sample.pushbox 契约文档（Rule 19：契约 = 文档）

> Mod 之间不共享任何数据契约程序集。字节流是唯一事实来源。
> 线格式统一为：字段编号（u16 LE）+ wireType（u8）+ data，可跳读、append-only（§14.11.1）。
> wireType：0=Varint（有符号经 zigzag），1=Fixed32，2=Fixed64，3=LengthDelimited(u32 长度 + bytes)。

## 网络协议

### pushbox:input v1（ClientToServer）

全局 ID：`xxHash32("com.sample.pushbox:input")`

| field | 类型 | 语义 |
|---|---|---|
| 1 | byte (varint) | side：0=Left(A)，1=Right(B) |
| 2 | bool (varint 0/1) | pushing：是否正在推 |

**测试向量**（side=Right, pushing=true）：

```
02 00 00 01        # field 1, wireType 0 (varint), value 1
02 00 00 01 01     # 完整载荷：field1=1, field2=true
   └─ field 2: 02 00 00 01 → fieldId=2, varint, 1
```

完整载荷 hex：`01 00 00 01 02 00 00 01`（field1=Right; field2=true）。

### pushbox:game_won v1（ServerToClient 广播）

全局 ID：`xxHash32("com.sample.pushbox:game_won")`

| field | 类型 | 语义 |
|---|---|---|
| 1 | byte (varint) | winner：0=Left(A)，1=Right(B) |

完整载荷 hex（winner=Left）：`01 00 00 00`。

## 消息（MessageBus 事件，进程内）

### pushbox:game_won v1（Event，Pub/Sub 无返回）

owner：com.sample.pushbox（谁定义谁发布，Rule 11）。
载荷与网络协议 game_won 相同：field 1 = winner (byte)。
需要返回的查询不是消息——走 ModCall（§14.10）。
