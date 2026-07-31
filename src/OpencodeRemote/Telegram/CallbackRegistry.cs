using System.Security.Cryptography;
using OpencodeRemote.Telegram.Models;

namespace OpencodeRemote.Telegram;

public sealed class CallbackRegistry
{
    private sealed record Entry(CallbackAction Action, DateTimeOffset Expires, string Group);

    internal sealed record ReservedEntry(string Key, CallbackAction Action, DateTimeOffset Expires, string Group);

    internal sealed record Reservation(CallbackAction Action, IReadOnlyList<ReservedEntry> Entries);

    private readonly Lock _gate = new();
    private readonly Dictionary<string, Entry> _actions = [];

    public string Add(CallbackAction action, TimeSpan? lifetime = null, string? group = null)
    {
        lock (_gate)
        {
            RemoveExpired();
            var key = CreateKey();
            _actions[key] = new Entry(
                action,
                DateTimeOffset.UtcNow.Add(lifetime ?? TimeSpan.FromHours(1)),
                group ?? key);
            return key;
        }
    }

    internal string CreateGroup() => CreateKey();

    internal bool TryReserve(string key, out Reservation? reservation)
    {
        lock (_gate)
        {
            reservation = null;
            RemoveExpired();
            if (!_actions.TryGetValue(key, out var selected))
            {
                return false;
            }

            var entries = _actions
                .Where(item => item.Value.Group == selected.Group)
                .Select(item => new ReservedEntry(
                    item.Key,
                    item.Value.Action,
                    item.Value.Expires,
                    item.Value.Group))
                .ToArray();
            foreach (var entry in entries)
            {
                _actions.Remove(entry.Key);
            }

            reservation = new Reservation(selected.Action, entries);
            return true;
        }
    }

    internal bool Restore(Reservation reservation)
    {
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            var restored = false;
            foreach (var entry in reservation.Entries.Where(entry => entry.Expires >= now))
            {
                restored |= _actions.TryAdd(
                    entry.Key,
                    new Entry(entry.Action, entry.Expires, entry.Group));
            }
            return restored;
        }
    }

    private static string CreateKey() => Convert.ToHexString(RandomNumberGenerator.GetBytes(6));

    private void RemoveExpired()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var key in _actions.Where(item => item.Value.Expires < now).Select(item => item.Key).ToArray())
        {
            _actions.Remove(key);
        }
    }
}
