using Avalonia.Input;
using MioReel.Core.Models;

namespace MioReel.Desktop.Services.Hotkeys;

public static class KeyNameMapper
{
    public static bool IsPureModifierKey(Key key) => key is
        Key.None or
        Key.LeftCtrl or Key.RightCtrl or
        Key.LeftAlt or Key.RightAlt or
        Key.LeftShift or Key.RightShift or
        Key.LWin or Key.RWin or
        Key.CapsLock or Key.NumLock or Key.Scroll;

    public static HotkeyModifiers FromAvaloniaModifiers(KeyModifiers mods)
    {
        var result = HotkeyModifiers.None;
        if (mods.HasFlag(KeyModifiers.Control)) result |= HotkeyModifiers.Ctrl;
        if (mods.HasFlag(KeyModifiers.Alt)) result |= HotkeyModifiers.Alt;
        if (mods.HasFlag(KeyModifiers.Shift)) result |= HotkeyModifiers.Shift;
        if (mods.HasFlag(KeyModifiers.Meta)) result |= HotkeyModifiers.Meta;
        return result;
    }

    public static bool TryFromAvalonia(Key key, KeyModifiers mods, out HotkeyBinding binding)
    {
        if (IsPureModifierKey(key))
        {
            binding = HotkeyBinding.Empty;
            return false;
        }

        var canonical = HotkeyBinding.NormalizeKeyName(key.ToString());
        if (string.IsNullOrWhiteSpace(canonical))
        {
            binding = HotkeyBinding.Empty;
            return false;
        }

        binding = new HotkeyBinding(FromAvaloniaModifiers(mods), canonical);
        return true;
    }

    public static bool TryToWin32VirtualKey(string keyName, out uint vk)
    {
        var key = HotkeyBinding.NormalizeKeyName(keyName);
        if (key.Length == 1 && key[0] is >= 'A' and <= 'Z')
        {
            vk = key[0];
            return true;
        }
        if (key.Length == 2 && key[0] == 'D' && key[1] is >= '0' and <= '9')
        {
            vk = key[1];
            return true;
        }
        if (key.StartsWith("NumPad", StringComparison.Ordinal) && key.Length == 7 && key[6] is >= '0' and <= '9')
        {
            vk = (uint)(0x60 + (key[6] - '0'));
            return true;
        }
        if (key.Length >= 2 && key[0] == 'F' && int.TryParse(key.AsSpan(1), out var fNum) && fNum is >= 1 and <= 24)
        {
            vk = (uint)(0x70 + (fNum - 1));
            return true;
        }

        vk = key switch
        {
            "BackSpace" => 0x08,
            "Tab" => 0x09,
            "Enter" => 0x0D,
            "Escape" => 0x1B,
            "Space" => 0x20,
            "PageUp" => 0x21,
            "PageDown" => 0x22,
            "End" => 0x23,
            "Home" => 0x24,
            "Left" => 0x25,
            "Up" => 0x26,
            "Right" => 0x27,
            "Down" => 0x28,
            "Insert" => 0x2D,
            "Delete" => 0x2E,
            "Multiply" => 0x6A,
            "Add" => 0x6B,
            "Subtract" => 0x6D,
            "Decimal" => 0x6E,
            "Divide" => 0x6F,
            "OemSemicolon" => 0xBA,
            "OemPlus" => 0xBB,
            "OemComma" => 0xBC,
            "OemMinus" => 0xBD,
            "OemPeriod" => 0xBE,
            "OemQuestion" => 0xBF,
            "OemTilde" => 0xC0,
            "OemOpenBrackets" => 0xDB,
            "OemPipe" => 0xDC,
            "OemCloseBrackets" => 0xDD,
            "OemQuotes" => 0xDE,
            _ => 0
        };
        return vk != 0;
    }

    public static bool TryToX11KeySymName(string keyName, out string keysymName)
    {
        var key = HotkeyBinding.NormalizeKeyName(keyName);
        if (key.Length == 1 && key[0] is >= 'A' and <= 'Z')
        {
            keysymName = key.ToLowerInvariant();
            return true;
        }
        if (key.Length == 2 && key[0] == 'D' && key[1] is >= '0' and <= '9')
        {
            keysymName = key[1].ToString();
            return true;
        }
        if (key.StartsWith("NumPad", StringComparison.Ordinal) && key.Length == 7 && key[6] is >= '0' and <= '9')
        {
            keysymName = $"KP_{key[6]}";
            return true;
        }
        if (key.Length >= 2 && key[0] == 'F' && int.TryParse(key.AsSpan(1), out var fNum) && fNum is >= 1 and <= 24)
        {
            keysymName = key;
            return true;
        }

        keysymName = key switch
        {
            "BackSpace" => "BackSpace",
            "Tab" => "Tab",
            "Enter" => "Return",
            "Escape" => "Escape",
            "Space" => "space",
            "PageUp" => "Prior",
            "PageDown" => "Next",
            "End" => "End",
            "Home" => "Home",
            "Left" => "Left",
            "Up" => "Up",
            "Right" => "Right",
            "Down" => "Down",
            "Insert" => "Insert",
            "Delete" => "Delete",
            "OemSemicolon" => "semicolon",
            "OemPlus" => "equal",
            "OemComma" => "comma",
            "OemMinus" => "minus",
            "OemPeriod" => "period",
            "OemQuestion" => "slash",
            "OemTilde" => "grave",
            "OemOpenBrackets" => "bracketleft",
            "OemPipe" => "backslash",
            "OemCloseBrackets" => "bracketright",
            "OemQuotes" => "apostrophe",
            _ => string.Empty
        };
        return !string.IsNullOrEmpty(keysymName);
    }
}
