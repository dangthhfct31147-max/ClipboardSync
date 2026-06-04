using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Forms;
using ClipboardSync;
using ClipboardSync.Utils;

namespace ClipboardSync.Core;

public sealed class ClipboardMonitor : IDisposable
{
    private const int WM_CLIPBOARDUPDATE = 0x031D;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    private string _lastHash = string.Empty;
    private bool _isApplyingRemote;
    private readonly FileLogger _logger;
    private bool _disposed;
    private Thread? _clipboardThread;
    private readonly object _hashLock = new();
    private readonly object _debounceLock = new();
    private DateTime _lastChangeTime = DateTime.MinValue;
    private string _debounceHash = string.Empty;
    private const int DebounceMs = 150;

    public event EventHandler<ClipboardChangedEventArgs>? ClipboardChanged;

    public ClipboardMonitor(AppConfig config, FileLogger logger)
    {
        _logger = logger;
    }

    public void Start()
    {
        _logger.Info("Starting clipboard monitor...");

        _clipboardThread = new Thread(ClipboardThreadMain)
        {
            IsBackground = true,
            Name = "ClipboardSync.ClipboardPump"
        };
        _clipboardThread.SetApartmentState(ApartmentState.STA);
        _clipboardThread.Start();
    }

    private void ClipboardThreadMain()
    {
        var form = new HiddenClipboardForm(this, _logger);
        Application.Run(form);
    }

    [DllImport("user32.dll")]
    private static extern bool PostThreadMessage(int threadId, int msg, IntPtr wParam, IntPtr lParam);

    public void Stop()
    {
        _logger.Info("Stopping clipboard monitor...");

        if (_clipboardThread != null && _clipboardThread.IsAlive)
        {
            PostThreadMessage(_clipboardThread.ManagedThreadId, 0x0010, IntPtr.Zero, IntPtr.Zero);
            if (!_clipboardThread.Join(3000))
            {
                _logger.Warn("Clipboard thread did not exit cleanly.");
            }
        }

        _logger.Info("Clipboard monitor stopped.");
    }

    internal void OnClipboardUpdate()
    {
        if (_isApplyingRemote) return;

        try
        {
            string hash;
            ClipboardFormat format;
            string? textContent = null;
            byte[]? imageData = null;
            List<string>? filePaths = null;

            if (Clipboard.ContainsText())
            {
                textContent = Clipboard.GetText();
                if (string.IsNullOrEmpty(textContent)) return;
                hash = ComputeHash(textContent);
                format = ClipboardFormat.Text;
            }
            else if (Clipboard.ContainsImage())
            {
                try
                {
                    using var bmp = new System.Drawing.Bitmap(Clipboard.GetImage()!);
                    using var ms = new MemoryStream();
                    bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                    imageData = ms.ToArray();
                    if (imageData.Length == 0) return;
                    hash = ComputeHash(imageData);
                    format = ClipboardFormat.Image;
                }
                catch (Exception ex)
                {
                    _logger.Warn($"Failed to read clipboard image: {ex.Message}");
                    return;
                }
            }
            else if (Clipboard.ContainsFileDropList())
            {
                var files = Clipboard.GetFileDropList();
                if (files.Count == 0) return;
                filePaths = files.Cast<string>().ToList();
                hash = ComputeHash(string.Join("|", filePaths));
                format = ClipboardFormat.Files;
            }
            else
            {
                return;
            }

            lock (_hashLock)
            {
                if (hash == _lastHash) return;
                _lastHash = hash;
            }

            lock (_debounceLock)
            {
                var now = DateTime.UtcNow;
                if (hash == _debounceHash && (now - _lastChangeTime).TotalMilliseconds < DebounceMs) return;
                _debounceHash = hash;
                _lastChangeTime = now;
            }

            var args = new ClipboardChangedEventArgs(hash, format, textContent, imageData, filePaths);
            ClipboardChanged?.Invoke(this, args);
        }
        catch (Exception ex)
        {
            _logger.Error("Error reading clipboard", ex);
        }
    }

    public void UpdateClipboardSilently(string? text, byte[]? image, List<string>? files)
    {
        _isApplyingRemote = true;
        try
        {
            if (text != null)
            {
                Clipboard.SetText(text);
                lock (_hashLock) { _lastHash = ComputeHash(text); }
            }
            else if (image != null)
            {
                using var ms = new MemoryStream(image);
                var bmp = new System.Drawing.Bitmap(ms);
                Clipboard.SetImage(bmp);
                lock (_hashLock) { _lastHash = ComputeHash(image); }
            }
            else if (files != null)
            {
                var collection = new System.Collections.Specialized.StringCollection();
                collection.AddRange(files.ToArray());
                Clipboard.SetFileDropList(collection);
                lock (_hashLock) { _lastHash = ComputeHash(string.Join("|", files)); }
            }
        }
        catch (Exception ex)
        {
            _logger.Error("Error updating clipboard", ex);
            _isApplyingRemote = false;
        }
        finally
        {
            _isApplyingRemote = false;
        }
    }

    private static string ComputeHash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private static string ComputeHash(byte[] data) =>
        Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    public string LastHash
    {
        get { lock (_hashLock) return _lastHash; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }

    private sealed class HiddenClipboardForm : Form
    {
        private const int WM_QUIT_FORM = 0x0010;
        private readonly ClipboardMonitor _owner;
        private readonly FileLogger _logger;
        private bool _addedListener;

        public HiddenClipboardForm(ClipboardMonitor owner, FileLogger logger)
        {
            _owner = owner;
            _logger = logger;
            Text = "ClipboardSync";
            ShowInTaskbar = false;
            WindowState = FormWindowState.Minimized;
            Size = new System.Drawing.Size(1, 1);
            SetVisibleCore(false);

            if (!AddClipboardFormatListener(Handle))
            {
                var err = Marshal.GetLastWin32Error();
                _logger.Error($"Failed to add clipboard format listener. Win32 error: {err}");
                return;
            }

            _addedListener = true;
            _logger.Info("Clipboard monitor started (hidden Form on STA thread).");
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_CLIPBOARDUPDATE)
            {
                _owner.OnClipboardUpdate();
            }
            else if (m.Msg == WM_QUIT_FORM)
            {
                Close();
                return;
            }
            base.WndProc(ref m);
        }

        protected override void Dispose(bool disposing)
        {
            if (_addedListener && Handle != IntPtr.Zero)
            {
                RemoveClipboardFormatListener(Handle);
            }
            base.Dispose(disposing);
        }
    }
}

public sealed class ClipboardChangedEventArgs : EventArgs
{
    public string Hash { get; }
    public ClipboardFormat Format { get; }
    public string? TextContent { get; }
    public byte[]? ImageData { get; }
    public List<string>? FilePaths { get; }

    public ClipboardChangedEventArgs(string hash, ClipboardFormat format, string? text, byte[]? image, List<string>? files)
    {
        Hash = hash;
        Format = format;
        TextContent = text;
        ImageData = image;
        FilePaths = files;
    }
}
