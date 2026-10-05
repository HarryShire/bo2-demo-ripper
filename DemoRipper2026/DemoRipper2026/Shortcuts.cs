using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace DemoRipper2026
{
    /// <summary>
    /// Keyboard shortcuts you can change, set under Offsets and maintenance. The
    /// numpad * and - read "Set Start time to Current Demo Time" and "Set End time to Current Demo Time".
    /// System-wide hotkeys, so they work with BO2 in front; the numpad keys stay the defaults.
    /// </summary>
    public partial class MainWindow
    {
        private static readonly uint[] DefaultVk = { VK_DIVIDE, VK_MULTIPLY, VK_SUBTRACT, VK_ADD };
        private static readonly int[] HotkeyIds = { HotkeyRecord, HotkeyMarkIn, HotkeyMarkOut, HotkeyGoToStart };
        private static readonly string[] HotkeyLabels =
            { "record / stop / save", "Set Start time to Current Demo Time", "Set End time to Current Demo Time", "go to the start time" };
        private readonly uint[] _hotVk = (uint[])DefaultVk.Clone();

        private TextBox[] KeyBoxes { get { return new[] { KeyRecordBox, KeyStartBox, KeyEndBox, KeyGoToBox }; } }

        private void LoadHotkeySetting(string k, string v)
        {
            int i = k == "keyrecord" ? 0 : k == "keystart" ? 1 : k == "keyend" ? 2 : k == "keygoto" ? 3 : -1;
            uint vk;
            if (i >= 0 && uint.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out vk) && vk > 0 && vk < 256) _hotVk[i] = vk;
        }

        public static string KeyName(uint vk)
        {
            switch (vk)
            {
                case 0x6F: return "Numpad /";
                case 0x6A: return "Numpad *";
                case 0x6D: return "Numpad -";
                case 0x6B: return "Numpad +";
                case 0x6E: return "Numpad .";
            }
            if (vk >= 0x60 && vk <= 0x69) return "Numpad " + (vk - 0x60);
            Key key = KeyInterop.KeyFromVirtualKey((int)vk);
            string n = key.ToString();
            if (n.Length == 2 && n[0] == 'D' && char.IsDigit(n[1])) return n.Substring(1);
            return n;
        }

        /// <summary>(Re)registers all four with the keys chosen now; says which another program holds.</summary>
        private void RegisterAllHotkeys()
        {
            if (_hwnd == null) return;
            foreach (int id in _hotkeys) UnregisterHotKey(_hwnd.Handle, id);
            _hotkeys.Clear();

            var failed = new List<string>();
            for (int i = 0; i < 4; i++) RegisterOrReport(HotkeyIds[i], _hotVk[i], KeyName(_hotVk[i]), failed);

            var parts = new List<string>();
            for (int i = 0; i < 4; i++) parts.Add(KeyName(_hotVk[i]) + "  " + HotkeyLabels[i]);
            HotkeyText.Text = string.Join("     ", parts) + "     (work in game)";
            HotkeyText.TextWrapping = TextWrapping.Wrap;

            TextBox[] boxes = KeyBoxes;
            for (int i = 0; i < 4; i++)
            {
                boxes[i].Text = KeyName(_hotVk[i]);
                boxes[i].PreviewKeyDown -= KeyBox_PreviewKeyDown;
                boxes[i].PreviewKeyDown += KeyBox_PreviewKeyDown;
            }
            if (failed.Count > 0)
                StatusText.Text = string.Join(", ", failed) + " already taken by another program. Pick other keys under Offsets and maintenance.";
        }

        private void KeyBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            var box = (TextBox)sender;
            int i = int.Parse((string)box.Tag, CultureInfo.InvariantCulture);
            e.Handled = true;
            Key key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key == Key.Escape || key == Key.Tab) { box.Text = KeyName(_hotVk[i]); return; }
            if (key == Key.LeftShift || key == Key.RightShift || key == Key.LeftCtrl || key == Key.RightCtrl
                || key == Key.LeftAlt || key == Key.RightAlt || key == Key.LWin || key == Key.RWin) return;
            uint vk = (uint)KeyInterop.VirtualKeyFromKey(key);
            if (vk == 0) return;
            for (int j = 0; j < 4; j++)
                if (j != i && _hotVk[j] == vk) { StatusText.Text = KeyName(vk) + " is already " + HotkeyLabels[j] + "."; return; }
            _hotVk[i] = vk;
            RegisterAllHotkeys();
            SaveSettings();
            StatusText.Text = KeyName(vk) + " now: " + HotkeyLabels[i] + ".";
        }

        private void KeyDefaults_Click(object sender, RoutedEventArgs e)
        {
            Array.Copy(DefaultVk, _hotVk, 4);
            RegisterAllHotkeys();
            SaveSettings();
            StatusText.Text = "Shortcuts back on the numpad.";
        }
    }
}
