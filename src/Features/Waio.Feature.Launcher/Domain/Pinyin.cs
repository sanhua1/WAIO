using System.Reflection;

namespace Waio.Feature.Launcher.Domain;

/// <summary>
/// 汉字转拼音(小写、不带声调,ü 写成 v)。Windows 没有 macOS 的 CFStringTransform 那样的系统接口,
/// 所以用一张从 Unicode Unihan(kMandarin)生成的表(Assets/pinyin.txt,scripts/build-pinyin-table.ps1,ADR-0023)。
/// 表里每个字只有一个最常用的读音;应用名、文件名里常见的多音字在 <see cref="Polyphones"/> 补上其他读音
/// (思路同 MAIO 的 Pinyin.polyphones,字表是这里自己整理的)。
/// </summary>
internal static class Pinyin
{
    private static readonly Lazy<Dictionary<char, string>> Table = new(Load);

    /// <summary>是否是汉字(CJK 基本区 + 扩展 A 区,和拼音表的范围一致)。</summary>
    public static bool IsHan(int codePoint) => codePoint is >= 0x4E00 and <= 0x9FFF or >= 0x3400 and <= 0x4DBF;

    public static bool ContainsHan(ReadOnlySpan<char> text)
    {
        foreach (var c in text)
        {
            if (IsHan(c))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>一个汉字的全部读音,第一个是表里的主读音;不是汉字或表里没有时返回空。</summary>
    public static IReadOnlyList<string> Readings(char han)
    {
        var primary = Table.Value.GetValueOrDefault(han);
        if (!Polyphones.TryGetValue(han, out var extra))
        {
            return primary is null ? [] : [primary];
        }

        var result = new List<string>(extra.Length + 1);
        if (primary is not null)
        {
            result.Add(primary);
        }

        foreach (var reading in extra)
        {
            if (!result.Contains(reading))
            {
                result.Add(reading);
            }
        }

        return result;
    }

    /// <summary>拼音表里有多少个字(测试和日志用)。</summary>
    public static int Count => Table.Value.Count;

    /// <summary>应用名、文件名里常见的多音字:主读音之外的其他读音。</summary>
    internal static readonly Dictionary<char, string[]> Polyphones = new()
    {
        ['乐'] = ["yue"], ['行'] = ["hang"], ['重'] = ["chong"], ['长'] = ["zhang"], ['调'] = ["diao"],
        ['传'] = ["zhuan"], ['藏'] = ["zang"], ['便'] = ["pian"], ['还'] = ["huan"], ['和'] = ["huo", "hu"],
        ['觉'] = ["jiao"], ['曾'] = ["zeng"], ['朝'] = ["zhao"], ['解'] = ["xie"], ['薄'] = ["bao"],
        ['率'] = ["shuai"], ['都'] = ["dou"], ['地'] = ["de"], ['的'] = ["di"], ['得'] = ["dei"],
        ['着'] = ["zhao", "zhuo"], ['了'] = ["liao"], ['系'] = ["ji"], ['区'] = ["ou"], ['单'] = ["shan"],
        ['角'] = ["jue"], ['屏'] = ["bing"], ['盛'] = ["cheng"], ['省'] = ["xing"], ['差'] = ["chai"],
        ['卡'] = ["qia"], ['合'] = ["ge"], ['弹'] = ["dan"], ['参'] = ["shen"], ['校'] = ["jiao"],
        ['奇'] = ["ji"], ['降'] = ["xiang"], ['会'] = ["kuai"], ['露'] = ["lou"], ['色'] = ["shai"],
        ['似'] = ["shi"], ['厦'] = ["sha"], ['折'] = ["she"], ['强'] = ["jiang"], ['壳'] = ["qiao"],
        ['约'] = ["yao"], ['数'] = ["shuo"], ['种'] = ["chong"], ['大'] = ["dai"], ['模'] = ["mu"],
        ['读'] = ["dou"], ['给'] = ["ji"], ['期'] = ["ji"], ['尽'] = ["jin"], ['仇'] = ["qiu"],
        ['否'] = ["pi"], ['更'] = ["geng"], ['柏'] = ["bo"], ['秘'] = ["bi"], ['沈'] = ["chen"],
    };

    private static Dictionary<char, string> Load()
    {
        var table = new Dictionary<char, string>(27_000);
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Waio.Feature.Launcher.pinyin.txt")
            ?? throw new InvalidOperationException("缺少拼音表资源 pinyin.txt");
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            var tab = line.IndexOf('\t');
            if (tab <= 0)
            {
                continue;
            }

            var reading = line[..tab];
            foreach (var c in line.AsSpan(tab + 1))
            {
                table.TryAdd(c, reading);
            }
        }

        return table;
    }
}
