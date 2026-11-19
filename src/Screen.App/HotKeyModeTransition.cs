namespace Screen.App;

internal readonly record struct HotKeyReservation(
    string Name, HotKeyRegistration Registration, uint Modifiers, uint VirtualKey);

internal static class HotKeyModeTransition
{
    // Keep the hook active until every replacement is reserved. On failure,
    // remove only registrations acquired during this attempt.
    public static bool TryReserveAll(IReadOnlyList<HotKeyReservation> bindings, out string? conflict)
    {
        var acquired = new List<HotKeyRegistration>();
        var committed = false;
        conflict = null;
        try
        {
            foreach (var binding in bindings)
            {
                var wasRegistered = binding.Registration.IsRegistered;
                if (!binding.Registration.TryRegister(binding.Modifiers, binding.VirtualKey))
                {
                    conflict = binding.Name;
                    return false;
                }
                if (!wasRegistered)
                {
                    acquired.Add(binding.Registration);
                }
            }
            committed = true;
            return true;
        }
        finally
        {
            if (!committed)
            {
                foreach (var registration in acquired)
                {
                    registration.Dispose();
                }
            }
        }
    }
}
