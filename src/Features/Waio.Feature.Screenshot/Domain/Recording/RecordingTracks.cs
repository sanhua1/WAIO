namespace Waio.Feature.Screenshot.Domain.Recording;

/// <summary>一条音轨里是什么声音。</summary>
public enum AudioTrackKind
{
    System,
    Microphone,

    /// <summary>系统声音 + 麦克风混在一起。</summary>
    Mixed,
}

/// <summary>
/// 录屏的音轨安排(ADR-0030)。区域录屏:两路声音混成一条(ADR-0021)。
/// 全屏录屏:系统声音、麦克风各一条,两路都开时再录一条混音;结束时只保留用户选的那一条,转封装时原样拷贝,不重新编码。
/// </summary>
public static class RecordingTracks
{
    /// <summary>低于这个峰值(约 -50 dB)当作"全程没声音",结束对话框里默认不勾。</summary>
    public const float SilencePeak = 0.003f;

    /// <summary>录制时要写的音轨,顺序就是 MP4 里的音轨顺序(视频轨之后)。</summary>
    public static IReadOnlyList<AudioTrackKind> Layout(bool system, bool microphone, bool separate)
    {
        if (system && microphone)
        {
            return separate ? [AudioTrackKind.System, AudioTrackKind.Microphone, AudioTrackKind.Mixed] : [AudioTrackKind.Mixed];
        }

        return system ? [AudioTrackKind.System] : microphone ? [AudioTrackKind.Microphone] : [];
    }

    /// <summary>
    /// 用户选了保留哪些声音,返回要保留的那条音轨在 <paramref name="layout"/> 里的位置;一条都不留(或录的时候就没有)返回 null。
    /// 两路都留 = 混音那条(两条都保留时合并为一条)。
    /// </summary>
    public static int? Keep(IReadOnlyList<AudioTrackKind> layout, bool system, bool microphone)
    {
        AudioTrackKind? wanted = (system, microphone) switch
        {
            (true, true) => AudioTrackKind.Mixed,
            (true, false) => AudioTrackKind.System,
            (false, true) => AudioTrackKind.Microphone,
            _ => null,
        };
        if (wanted is not { } kind)
        {
            return null;
        }

        var index = IndexOf(layout, kind);
        if (index is null && kind == AudioTrackKind.Mixed)
        {
            // 只录到了其中一路(另一路没开):有哪条留哪条
            index = IndexOf(layout, AudioTrackKind.System) ?? IndexOf(layout, AudioTrackKind.Microphone);
        }

        return index;
    }

    /// <summary>
    /// 分轨录制时某一路声音的临时文件(和画面临时文件放在一起):"X (录制中).mp4" → "X (录制中) 系统声音.mp4"。
    /// 每个文件只有一条轨,合成时按文件挑,不依赖流编号(见 Mp4Remuxer 的说明)。
    /// </summary>
    public static string SidecarPath(string videoPath, AudioTrackKind kind)
    {
        var name = kind switch
        {
            AudioTrackKind.System => "系统声音",
            AudioTrackKind.Microphone => "麦克风",
            _ => "混音",
        };
        return Path.Combine(Path.GetDirectoryName(videoPath) ?? "", $"{Path.GetFileNameWithoutExtension(videoPath)} {name}.mp4");
    }

    /// <summary>一段声音里的最大绝对值。</summary>
    public static float Peak(ReadOnlySpan<float> samples)
    {
        var peak = 0f;
        foreach (var sample in samples)
        {
            peak = Math.Max(peak, Math.Abs(sample));
        }

        return peak;
    }

    private static int? IndexOf(IReadOnlyList<AudioTrackKind> layout, AudioTrackKind kind)
    {
        for (var i = 0; i < layout.Count; i++)
        {
            if (layout[i] == kind)
            {
                return i;
            }
        }

        return null;
    }
}
