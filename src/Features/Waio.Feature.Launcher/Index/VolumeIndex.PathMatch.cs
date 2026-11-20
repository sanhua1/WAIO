using System.Text;

namespace Waio.Feature.Launcher.Index;

internal sealed partial class VolumeIndex
{
    /// <summary>
    /// 一次搜索里的路径词匹配。完整路径(折叠后)P(r) = P(父) + "\" + 名称,根是挂载路径(如 <c>c:</c>)。
    /// 词出现在 P(r) 里,要么整个落在 P(父) 里(= 父目录匹配,递归),要么跨到最后的 "\名称" 上
    /// (只需要 P(父) 末尾 词长−1 个字节 + "\名称")。所以每条记录的结果按父目录缓存,整遍是线性的,不必拼出完整路径。
    /// <para>缓存每次搜索新建(索引更新拿写锁,和搜索互斥,不会过期)。多线程同时写同一格只会写入相同的值,不用加锁。</para>
    /// </summary>
    private sealed class PathMatcher
    {
        private const int MaxDepth = 256;

        private readonly VolumeIndex _index;
        private readonly IReadOnlyList<byte[]> _terms;
        private readonly sbyte[][] _memo;
        private readonly bool[] _rootMatches;

        public PathMatcher(VolumeIndex index, IReadOnlyList<byte[]> terms)
        {
            _index = index;
            _terms = terms;
            _memo = terms.Count == 0 ? [] : terms.Select(_ => new sbyte[index.Count]).ToArray();
            var root = Encoding.UTF8.GetBytes(index.Root.TrimEnd('\\').ToLowerInvariant());
            _rootMatches = terms.Select(t => root.AsSpan().IndexOf(t) >= 0).ToArray();
            RootPath = root;
        }

        /// <summary>折叠后的挂载路径(不带结尾的 <c>\</c>)。</summary>
        public byte[] RootPath { get; }

        /// <summary>所有路径词都出现在这条记录的完整路径里。</summary>
        public bool Matches(int record)
        {
            for (var i = 0; i < _terms.Count; i++)
            {
                if (!Matches(record, i))
                {
                    return false;
                }
            }

            return true;
        }

        private bool Matches(int record, int term)
        {
            var memo = _memo[term];
            Span<int> chain = stackalloc int[MaxDepth];
            var count = 0;
            var current = record;
            bool result;
            while (true)
            {
                if (current == RootRecord || current < 0 || current >= _index.Count || count == MaxDepth)
                {
                    result = _rootMatches[term];
                    break;
                }

                if (memo[current] != 0)
                {
                    result = memo[current] > 0;
                    break;
                }

                chain[count++] = current;
                current = _index._parent[current];
            }

            // 从靠根的一层往下:父目录匹配了,下面的全都匹配;否则只看跨到自己名称上的那一段
            for (var i = count - 1; i >= 0; i--)
            {
                var r = chain[i];
                result = result || TailContains(r, _terms[term]);
                memo[r] = result ? (sbyte)1 : (sbyte)-1;
            }

            return result;
        }

        /// <summary>词是否出现在"P(父) 的末尾 词长−1 个字节 + \ + 名称"里(必然跨过最后那个 <c>\</c>)。</summary>
        private bool TailContains(int record, ReadOnlySpan<byte> term)
        {
            var name = _index.FoldedName(record);
            var total = term.Length + name.Length;
            Span<byte> buffer = total <= 1024 ? stackalloc byte[total] : new byte[total];
            var end = total;
            name.CopyTo(buffer[(end - name.Length)..]);
            end -= name.Length;
            buffer[--end] = (byte)'\\';
            var current = _index._parent[record];
            for (var guard = 0; end > 0 && guard < MaxDepth; guard++)
            {
                var atRoot = current == RootRecord || current < 0 || current >= _index.Count;
                var part = atRoot ? RootPath : _index.FoldedName(current);
                var take = Math.Min(part.Length, end);
                part[(part.Length - take)..].CopyTo(buffer[(end - take)..end]);
                end -= take;
                if (atRoot || end == 0)
                {
                    break;
                }

                buffer[--end] = (byte)'\\';
                current = _index._parent[current];
            }

            return buffer[end..].IndexOf(term) >= 0;
        }
    }

    /// <summary>折叠后的名称(UTF-8);没有名称的记录返回空。</summary>
    private ReadOnlySpan<byte> FoldedName(int record) =>
        _name[record] < 0 ? [] : _names.At(_name[record], _nameLength[record]);
}
