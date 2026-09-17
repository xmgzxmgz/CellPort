namespace CellPort.Core.Services;

/// <summary>
/// 极简 BER-TLV 解析器，覆盖 eUICC ES10 响应用到的子集：
/// 单字节 tag（5A / A0 / E3 / 4F / 90~95 / 5C / 80 等）与
/// 多字节 tag（BF2D / BF3E / 9F70 等），短格式与 1~3 字节长格式。
/// 行为参照 lpac 的 euicc_derutil_unpack 系列函数。
/// </summary>
public static class BerTlv
{
    /// <summary>一个 TLV 节点。</summary>
    /// <param name="Tag">tag 值（多字节 tag 按大端合并为一个 int）。</param>
    /// <param name="Value">value 字节。</param>
    /// <param name="HeaderLength">tag + length 的总字节数。</param>
    public sealed record Node(int Tag, IReadOnlyList<byte> Value, int HeaderLength)
    {
        /// <summary>该节点占用的总字节数（含头）。</summary>
        public int TotalLength => HeaderLength + Value.Count;
    }

    public static IReadOnlyList<Node> Parse(byte[] data) => Parse(data, 0, data.Length);

    public static IReadOnlyList<Node> Parse(byte[] data, int offset, int count)
    {
        var nodes = new List<Node>();
        var end = offset + count;
        var i = offset;

        while (i < end)
        {
            // ---- tag ----
            var tagStart = i;
            int tag = data[i];
            i++;
            if ((tag & 0x1F) == 0x1F)
            {
                // 多字节 tag：后续字节最高位为 0 时结束。
                // 限制最多再读 3 字节，防御畸形数据。
                for (var k = 0; k < 3 && i < end; k++)
                {
                    var b = data[i];
                    i++;
                    tag = (tag << 8) | b;
                    if ((b & 0x80) == 0)
                    {
                        break;
                    }
                }
            }

            if (i >= end)
            {
                break; // 只有 tag 没有长度，畸形
            }

            // ---- length ----
            var l0 = data[i];
            i++;
            int len;
            if (l0 < 0x80)
            {
                len = l0;
            }
            else
            {
                var longBytes = l0 & 0x7F;
                if (longBytes is 0 or > 3 || i + longBytes > end)
                {
                    break; // 不定长(0x80)或超出支持范围
                }
                len = 0;
                for (var k = 0; k < longBytes; k++)
                {
                    len = (len << 8) | data[i + k];
                }
                i += longBytes;
            }

            if (len < 0 || i + len > end)
            {
                break; // 长度越界，丢弃尾部
            }

            var value = new byte[len];
            Array.Copy(data, i, value, 0, len);

            nodes.Add(new Node(tag, value, i - tagStart));
            i += len;
        }

        return nodes;
    }

    /// <summary>在节点列表中查找指定 tag 的第一个节点。</summary>
    public static Node? Find(IReadOnlyList<Node> nodes, int tag)
    {
        foreach (var n in nodes)
        {
            if (n.Tag == tag)
            {
                return n;
            }
        }
        return null;
    }

    /// <summary>把 node.value 再解析一层。</summary>
    public static IReadOnlyList<Node> Children(Node node) => Parse(node.Value.ToArray(), 0, node.Value.Count);

    /// <summary>把 value 当作无符号大端整数（用于 9F70 状态、95 类别等短枚举）。</summary>
    public static long ToLong(IReadOnlyList<byte> value)
    {
        long v = 0;
        for (var i = 0; i < value.Count; i++)
        {
            v = (v << 8) | value[i];
        }
        return v;
    }
}
