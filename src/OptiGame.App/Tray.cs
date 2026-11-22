using System;
using System.Drawing;
using System.IO;
using System.Windows;

namespace OptiGame.App;

/// <summary>System tray icon (WinForms NotifyIcon is the only built-in tray API).</summary>
internal sealed class Tray : IDisposable
{
    readonly System.Windows.Forms.NotifyIcon _ni;
    readonly System.Windows.Forms.ToolStripMenuItem _auto;

    public event Action? OpenRequested, BoostRequested, ExitRequested;
    public event Action<bool>? AutoToggled;

    public Tray(bool autoBoost)
    {
#pragma warning disable WFO5001
        try { System.Windows.Forms.Application.SetColorMode(System.Windows.Forms.SystemColorMode.System); } catch { }
#pragma warning restore WFO5001
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Open OptiGame+", null, (_, _) => OpenRequested?.Invoke());
        menu.Items.Add("Boost / Restore", null, (_, _) => BoostRequested?.Invoke());
        _auto = new System.Windows.Forms.ToolStripMenuItem("Auto-boost") { Checked = autoBoost, CheckOnClick = true };
        _auto.CheckedChanged += (_, _) => AutoToggled?.Invoke(_auto.Checked);
        menu.Items.Add(_auto);
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitRequested?.Invoke());

        Icon icon;
        var res = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"));
        using (var s = res.Stream)
        {
            var ms = new MemoryStream(); s.CopyTo(ms); ms.Position = 0;
            icon = new Icon(ms, System.Windows.Forms.SystemInformation.SmallIconSize);
        }
        _ni = new System.Windows.Forms.NotifyIcon { Icon = icon, Text = "OptiGame+", Visible = true, ContextMenuStrip = menu };
        _ni.MouseClick += (_, e) => { if (e.Button == System.Windows.Forms.MouseButtons.Left) OpenRequested?.Invoke(); };
        _ni.BalloonTipClicked += (_, _) => OpenRequested?.Invoke();
    }

    public void SetAuto(bool on) { if (_auto.Checked != on) _auto.Checked = on; }

    public void Toast(string title, string text)
    {
        _ni.BalloonTipTitle = title;
        _ni.BalloonTipText = text.Length > 250 ? text[..247] + "…" : text;
        _ni.BalloonTipIcon = System.Windows.Forms.ToolTipIcon.Info;
        _ni.ShowBalloonTip(6000);
    }

    public void Dispose() { _ni.Visible = false; _ni.Dispose(); }
}
