namespace Waio.Feature.Screenshot.Infrastructure.Recording;

/// <summary>
/// MP4 转封装(ADR-0030):把几个 MP4 里的全部轨合并成一个新的 MP4,压缩数据原样拷贝,不解码、不重新编码,所以很快。
/// Source Reader 不设输出格式时给的就是文件里的压缩样本;Sink Writer 的流不设输入格式时直接写压缩样本。
/// 全屏录屏结束后,用它把画面临时文件和用户选的那条声音的临时文件合成最终文件。
/// <para>
/// 为什么按文件挑、而不是从一个多轨文件里按流编号挑:2026-11-12 真机发现,硬件编码录出的多轨分段 MP4,
/// Source Reader 的流编号和轨道顺序对不上(第 0 号流是声音),挑出来的文件没有画面。每个临时文件只有一条轨就不会拿错。
/// </para>
/// </summary>
internal static class Mp4Remuxer
{
    /// <summary>把 <paramref name="sources"/> 的全部轨按时间交错写进 <paramref name="destination"/>(扩展名 .mp4)。在调用线程上同步执行。</summary>
    public static void Remux(string destination, IReadOnlyList<string> sources)
    {
        if (sources.Count == 0)
        {
            throw new ArgumentException("至少要有一个文件", nameof(sources));
        }

        Com.EnsureMta();
        Com.Check(Mf.MFStartup(Mf.Version, 0), "启动 Media Foundation");
        var inputs = new List<Input>();
        nint writer = 0;
        try
        {
            if (File.Exists(destination))
            {
                File.Delete(destination);
            }

            Com.Check(Mf.MFCreateSinkWriterFromURL(destination, 0, 0, out writer), "创建 MP4 文件");
            foreach (var source in sources)
            {
                inputs.Add(Input.Open(source, writer));
            }

            Com.Check(Mf.BeginWriting(writer), "开始写 MP4");
            foreach (var input in inputs)
            {
                input.ReadNext();
            }

            // 每次写时间最早的那个样本(声音和画面交错,Sink Writer 不用缓存一整条轨)
            while (inputs.Where(i => i.Pending != 0).MinBy(i => i.Time) is { } next)
            {
                Com.Check(Mf.WriteSample(writer, next.Output[next.Stream], next.Pending), "写入 MP4");
                next.ReadNext();
            }

            Com.Check(Mf.Finalize(writer), "完成 MP4");
        }
        finally
        {
            foreach (var input in inputs)
            {
                input.Dispose();
            }

            Com.Release(ref writer);
        }
    }

    /// <summary>文件里有几条轨。日志和测试用。</summary>
    public static int StreamCount(string path)
    {
        Com.EnsureMta();
        Com.Check(Mf.MFStartup(Mf.Version, 0), "启动 Media Foundation");
        nint reader = 0;
        try
        {
            Com.Check(Mf.MFCreateSourceReaderFromURL(path, 0, out reader), "打开 MP4");
            return (int)CountStreams(reader);
        }
        finally
        {
            Com.Release(ref reader);
        }
    }

    private static uint CountStreams(nint reader)
    {
        var count = 0u;
        while (Mf.GetCurrentMediaType(reader, count, out var type) >= 0)
        {
            Com.Release(ref type);
            count++;
        }

        return count;
    }

    /// <summary>一个输入文件:Source Reader、它的流到输出流的对应、下一个待写的样本。</summary>
    private sealed class Input : IDisposable
    {
        private nint _reader;
        private int _remaining;

        private Input(nint reader, Dictionary<uint, uint> output)
        {
            _reader = reader;
            Output = output;
            _remaining = output.Count;
        }

        public Dictionary<uint, uint> Output { get; }

        /// <summary>读到、还没写的样本(0 = 这个文件读完了)。</summary>
        public nint Pending;

        public uint Stream { get; private set; }

        public long Time { get; private set; }

        public static Input Open(string path, nint writer)
        {
            nint reader = 0;
            try
            {
                Com.Check(Mf.MFCreateSourceReaderFromURL(path, 0, out reader), "打开录屏临时文件");
                Com.Check(Mf.SetStreamSelection(reader, Mf.AllStreams, true), "选择轨道");
                var output = new Dictionary<uint, uint>();
                var streams = CountStreams(reader);
                for (var stream = 0u; stream < streams; stream++)
                {
                    nint type = 0;
                    try
                    {
                        Com.Check(Mf.GetCurrentMediaType(reader, stream, out type), "读取轨道格式");
                        Com.Check(Mf.AddStream(writer, type, out var index), "添加轨道");
                        output[stream] = index;
                    }
                    finally
                    {
                        Com.Release(ref type);
                    }
                }

                var input = new Input(reader, output);
                reader = 0;
                return input;
            }
            finally
            {
                Com.Release(ref reader);
            }
        }

        public void ReadNext()
        {
            Com.Release(ref Pending);
            while (_remaining > 0)
            {
                Com.Check(Mf.ReadSample(_reader, out var stream, out var flags, out var time, out var sample), "读取录屏数据");
                if ((flags & Mf.ReaderError) != 0)
                {
                    Com.Release(ref sample);
                    throw new RecordingException("读取录屏数据出错");
                }

                if ((flags & Mf.ReaderEndOfStream) != 0)
                {
                    _remaining--;
                }

                if (sample != 0 && Output.ContainsKey(stream))
                {
                    Pending = sample;
                    Stream = stream;
                    Time = time;
                    return;
                }

                Com.Release(ref sample);
            }
        }

        public void Dispose()
        {
            Com.Release(ref Pending);
            Com.Release(ref _reader);
        }
    }
}
