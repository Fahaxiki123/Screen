namespace Screen.App;

/// <summary>
/// Keeps the existing shortcut registered until Windows accepts its replacement.
/// Alternating IDs let us reserve a new shortcut without an unregister/rollback gap.
/// </summary>
internal sealed class HotKeyRegistration(
    int firstId,
    int secondId,
    Func<int, uint, uint, bool> register,
    Action<int> unregister) : IDisposable
{
    private int? _activeId;
    private uint _modifiers;
    private uint _virtualKey;

    public bool IsActiveId(int id) => _activeId == id;

    public bool TryRegister(uint modifiers, uint virtualKey)
    {
        if (_activeId is not null && _modifiers == modifiers && _virtualKey == virtualKey)
        {
            return true;
        }
        var candidateId = _activeId == firstId ? secondId : firstId;
        if (!register(candidateId, modifiers, virtualKey))
        {
            return false;
        }
        if (_activeId is { } previousId)
        {
            unregister(previousId);
        }
        _activeId = candidateId;
        _modifiers = modifiers;
        _virtualKey = virtualKey;
        return true;
    }

    public void Dispose()
    {
        if (_activeId is { } id)
        {
            unregister(id);
            _activeId = null;
        }
    }
}
