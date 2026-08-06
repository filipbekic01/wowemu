using WowEmu.Data.Db;

namespace WowEmu.Game;

/// <summary>
/// A character's action bars.
/// </summary>
/// <remarks>
/// Port of <c>Player</c>'s <c>m_actionButtons</c>. Nothing about the buttons is validated beyond
/// the button number: the client will happily put a spell it does not know on a bar, and upstream
/// checks that the action exists but not that the player can use it.
/// <para>
/// Sparse rather than a 144-wide array, because almost every character uses a couple of dozen and
/// the packet writes the gaps as zero anyway.
/// </para>
/// </remarks>
public sealed class ActionBar
{
    /// <summary>How many buttons the client has. <c>MAX_ACTION_BUTTONS</c>.</summary>
    public const int MaxButtons = PlayerActionStore.MaxButtons;

    private readonly Dictionary<byte, uint> _buttons = [];

    /// <summary>Every button that has something on it, and its packed action.</summary>
    public IReadOnlyDictionary<byte, uint> Buttons => _buttons;

    public int Count => _buttons.Count;

    /// <summary>What is on one button, packed as the client wants it. Zero for an empty one.</summary>
    public uint this[byte button] => _buttons.GetValueOrDefault(button);

    /// <summary>
    /// Puts something on a button, or clears it.
    /// </summary>
    /// <remarks>
    /// <b>A packed action of zero is a clear, not an action of zero.</b> That is how the client
    /// reports a button being dragged off the bar — there is no separate opcode for it.
    /// </remarks>
    public void Set(byte button, uint packedAction)
    {
        if (button >= MaxButtons)
        {
            return;
        }

        if (packedAction == 0)
        {
            _buttons.Remove(button);

            return;
        }

        _buttons[button] = packedAction;
    }

    /// <summary>Fills the bars from a saved set.</summary>
    public void Restore(IEnumerable<(byte Button, uint Packed)> buttons)
    {
        ArgumentNullException.ThrowIfNull(buttons);

        foreach ((byte button, uint packed) in buttons)
        {
            Set(button, packed);
        }
    }
}
