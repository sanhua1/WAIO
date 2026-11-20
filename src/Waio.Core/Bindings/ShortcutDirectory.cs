using Waio.Kit.Input;
using Waio.Kit.Services;

namespace Waio.Core.Bindings;

/// <summary>快捷键占用登记。线程安全。同一个 owner 重复登记同一快捷键视为成功(幂等)。</summary>
public sealed class ShortcutDirectory : IShortcutDirectory
{
    private readonly object _gate = new();
    private readonly Dictionary<KeyChord, string> _owners = [];

    public bool TryClaim(KeyChord chord, string owner, out string? conflictOwner)
    {
        lock (_gate)
        {
            if (_owners.TryGetValue(chord, out var existing) && existing != owner)
            {
                conflictOwner = existing;
                return false;
            }

            _owners[chord] = owner;
            conflictOwner = null;
            return true;
        }
    }

    public void Release(KeyChord chord, string owner)
    {
        lock (_gate)
        {
            if (_owners.TryGetValue(chord, out var existing) && existing == owner)
            {
                _owners.Remove(chord);
            }
        }
    }

    public bool IsClaimed(KeyChord chord)
    {
        lock (_gate)
        {
            return _owners.ContainsKey(chord);
        }
    }
}
