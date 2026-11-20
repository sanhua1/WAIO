using Waio.Kit.Features;
using Waio.Kit.Services;

namespace Waio.Core.Storage;

/// <summary>功能私有数据目录:%LOCALAPPDATA%\WAIO\data\&lt;featureId&gt;\。创建时确保目录存在。</summary>
public sealed class FeatureStorage : IFeatureStorage
{
    public FeatureStorage(FeatureId id, AppPaths paths)
    {
        DataDirectory = paths.FeatureDataDirectory(id);
        Directory.CreateDirectory(DataDirectory);
    }

    public string DataDirectory { get; }
}
