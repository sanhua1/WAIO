namespace Waio.Feature.Clipboard.Domain;

/// <summary>列表里"几分钟前"这类相对时间(纯函数)。</summary>
internal static class RelativeTime
{
    public static string Format(DateTimeOffset at, DateTimeOffset now)
    {
        var span = now - at;
        if (span < TimeSpan.FromMinutes(1))
        {
            return "刚刚"; // 包括时钟回拨造成的负值
        }

        if (span < TimeSpan.FromHours(1))
        {
            return $"{(int)span.TotalMinutes} 分钟前";
        }

        if (span < TimeSpan.FromHours(24))
        {
            return $"{(int)span.TotalHours} 小时前";
        }

        if (span < TimeSpan.FromHours(48))
        {
            return "昨天";
        }

        return at.ToLocalTime().ToString("yyyy-MM-dd");
    }
}
