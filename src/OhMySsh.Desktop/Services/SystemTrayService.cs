using System.Drawing;
using System.Runtime.InteropServices;
using Forms = System.Windows.Forms;

namespace OhMySsh.Desktop.Services;

/// <summary>Owns the native tray icon and releases its handles when the window closes.</summary>
internal sealed class SystemTrayService : IDisposable
{
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly Forms.ContextMenuStrip _menu;
    private readonly Icon _icon;
    private bool _disposed;

    public SystemTrayService(MainWindow window)
    {
        _icon = CreateIcon();
        _menu = new Forms.ContextMenuStrip();
        _menu.Items.Add("Abrir oh-my-ssh", null, (_, _) => window.Dispatcher.BeginInvoke(window.RestoreFromTray));
        _menu.Items.Add(new Forms.ToolStripSeparator());
        _menu.Items.Add("Sair", null, (_, _) => window.Dispatcher.BeginInvoke(window.Close));

        _notifyIcon = new Forms.NotifyIcon
        {
            Text = "oh-my-ssh · Gerenciador de conexões",
            Icon = _icon,
            ContextMenuStrip = _menu,
            Visible = true
        };
        _notifyIcon.DoubleClick += (_, _) => window.Dispatcher.BeginInvoke(window.RestoreFromTray);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _menu.Dispose();
        _icon.Dispose();
    }

    private static Icon CreateIcon()
    {
        using var bitmap = new Bitmap(32, 32);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.FromArgb(23, 35, 50));
        using var font = new Font("Consolas", 18, FontStyle.Bold, GraphicsUnit.Pixel);
        using var brush = new SolidBrush(Color.FromArgb(210, 226, 249));
        graphics.DrawString(">_", font, brush, 2, 6);
        var handle = bitmap.GetHicon();
        try
        {
            using var borrowedIcon = Icon.FromHandle(handle);
            return (Icon)borrowedIcon.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr iconHandle);
}
