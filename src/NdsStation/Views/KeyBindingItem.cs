using System.ComponentModel;
using NdsStation.Emulation;

namespace NdsStation.Views;

public sealed class KeyBindingItem : INotifyPropertyChanged
{
    private int _virtualKey;
    private bool _waiting;

    public KeyBindingItem(DsKey key, int virtualKey)
    {
        Key = key;
        _virtualKey = virtualKey;
    }

    public DsKey Key { get; }
    public string Label => InputMap.KeyLabel(Key);

    public int VirtualKey
    {
        get => _virtualKey;
        set { _virtualKey = value; Raise(); }
    }

    public bool Waiting
    {
        get => _waiting;
        set { _waiting = value; Raise(); }
    }

    public string KeyName => _waiting ? "Premi un tasto…" : _virtualKey == 0 ? "—" : InputMap.VirtualKeyName(_virtualKey);

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(KeyName)));
}
