namespace Waio.Feature.Screenshot.Domain;

/// <summary>截图历史的搜索和分组(MAIO HistorySearch)。</summary>
public static class HistorySearch
{
    public sealed record DayGroup(DateTime Day, IReadOnlyList<ScreenshotRecord> Records);

    /// <summary>按识别出的文字、来源程序、日期(如 2026-11-03)搜索,不区分大小写;多个词都要命中。</summary>
    public static IReadOnlyList<ScreenshotRecord> Filter(IReadOnlyList<ScreenshotRecord> records, string query)
    {
        var words = query.ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
        {
            return records;
        }

        return [.. records.Where(record =>
        {
            var haystack = string.Join('\n', record.OcrText ?? "", record.SourceApp ?? "", record.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd"))
                .ToLowerInvariant();
            return words.All(haystack.Contains);
        })];
    }

    /// <summary>按天(本地时间)分组,新的在前。</summary>
    public static IReadOnlyList<DayGroup> GroupByDay(IEnumerable<ScreenshotRecord> records)
    {
        var groups = new List<DayGroup>();
        List<ScreenshotRecord>? current = null;
        var day = DateTime.MinValue;
        foreach (var record in records.OrderByDescending(r => r.CreatedAt))
        {
            var date = record.CreatedAt.ToLocalTime().Date;
            if (current is null || date != day)
            {
                day = date;
                current = [];
                groups.Add(new DayGroup(day, current));
            }

            current.Add(record);
        }

        return groups;
    }
}

/// <summary>
/// 重新编辑已有截图时,原图放在屏幕的哪里(MAIO editContext):按 1:1 像素显示,放不下(超过显示器的 90%)时缩小;
/// 尽量以 <c>center</c>(贴图中心)为中心,但不超出显示器。
/// </summary>
/// <param name="Rect">原图显示的位置(全局物理像素)。</param>
/// <param name="Zoom">每个屏幕像素对应原图的多少个像素(≥ 1;1 表示 1:1)。</param>
public sealed record EditPlacement(PxRect Rect, double Zoom)
{
    public static EditPlacement For((int Width, int Height) image, PxRect display, PxPoint? center)
    {
        var zoom = Math.Max(1, Math.Max(image.Width / (display.Width * 0.9), image.Height / (display.Height * 0.9)));
        var width = Math.Max(1, (int)Math.Round(image.Width / zoom));
        var height = Math.Max(1, (int)Math.Round(image.Height / zoom));
        var middle = center ?? new PxPoint(display.X + display.Width / 2, display.Y + display.Height / 2);
        var x = Math.Clamp(middle.X - width / 2, display.X, display.Right - width);
        var y = Math.Clamp(middle.Y - height / 2, display.Y, display.Bottom - height);
        return new EditPlacement(new PxRect(x, y, width, height), zoom);
    }
}
