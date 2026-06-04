using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Forms;
using ClipboardSync.Utils;

namespace ClipboardSync.Core;

public sealed class ClipboardMonitor : IDisposable
{
    private const int WM_CLIPBOARDUPDATE = 0x031D;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    private string _lastHash = string.Empty;
    private bool _skipNextChange;
    private readonly FileLogger _logger;
    private bool _disposed;
    private IntPtr _messageWindowHwnd = IntPtr.Zero;
    private GCHandle _selfHandle;
    private readonly object _hashLock = new();

    public event EventHandler<ClipboardChangedEventArgs>? ClipboardChanged;

    public ClipboardMonitor(FileLogger logger)
    {
        _logger = logger;
    }

    public void Start()
    {
        _logger.Info("Starting clipboard monitor...");
        _selfHandle = GCHandle.Alloc(this, GCHandleType.Normal);
        var hwnd = GetOrCreateMessageWindow();
        if (!AddClipboardFormatListener(hwnd))
        {
            _logger.Error("Failed to add clipboard format listener. Is the session interactive?");
            return;
        }
        _logger.Info("Clipboard monitor started successfully.");
    }

    public void Stop()
    {
        _logger.Info("Stopping clipboard monitor...");
        if (_messageWindowHwnd != IntPtr.Zero)
        {
            RemoveClipboardFormatListener(_messageWindowHwnd);
            DestroyWindow(_messageWindowHwnd);
            _messageWindowHwnd = IntPtr.Zero;
        }
        _logger.Info("Clipboard monitor stopped.");
    }

    private IntPtr GetOrCreateMessageWindow()
    {
        if (_messageWindowHwnd != IntPtr.Zero) return _messageWindowHwnd;

        var className = "ClipboardSyncMsgWindow_" + Guid.NewGuid().ToString("N");
        var wndClass = new WNDCLASS(className);
        wndClass.lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate!);
        wndClass.hInstance = Marshal.GetHINSTANCE(typeof(ClipboardMonitor).Module);

        RegisterClass(ref wndClass);

        _messageWindowHwnd = CreateWindowEx(0, className, null, 0, 0, 0, 0, 0,
            IntPtr.Zero, IntPtr.Zero, wndClass.hInstance, IntPtr.Zero);

        SetWindowLongPtr(_messageWindowHwnd, GWL_USERDATA, GCHandle.ToIntPtr(_selfHandle));

        return _messageWindowHwnd;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_CLIPBOARDUPDATE && !_disposed)
        {
            OnClipboardChanged();
        }
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private readonly WndProcDelegate _wndProcDelegate = WndProcStatic;

    private static IntPtr WndProcStatic(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam)
    {
        var userData = GetWindowLongPtr(hwnd, GWL_USERDATA);
        if (userData == IntPtr.Zero) return DefWindowProc(hwnd, msg, wParam, lParam);

        if (!GCHandle.FromIntPtr(userData).IsAllocated) return DefWindowProc(hwnd, msg, wParam, lParam);
        var target = GCHandle.FromIntPtr(userData).Target;
        if (target is not ClipboardMonitor monitor) return DefWindowProc(hwnd, msg, wParam, lParam);

        return monitor.WndProc(hwnd, msg, wParam, lParam);
    }

    private void OnClipboardChanged()
    {
        if (_skipNextChange)
        {
            _skipNextChange = false;
            return;
        }

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
                if (hash == _lastHash)
                {
                    _logger.Debug("Clipboard hash unchanged, skipping.");
                    return;
                }
                _lastHash = hash;
            }

            _logger.Debug($"Clipboard changed: format={format}, hash={hash[..Math.Min(16, hash.Length)]}...");
            ClipboardChanged?.Invoke(this, new ClipboardChangedEventArgs(hash, format, textContent, imageData, filePaths));
        }
        catch (Exception ex)
        {
            _logger.Error("Error reading clipboard", ex);
        }
    }

    public void UpdateClipboardSilently(string? text, byte[]? image, List<string>? files)
    {
        _skipNextChange = true;
        try
        {
            if (text != null)
            {
                Clipboard.SetText(text);
                lock (_hashLock)
                {
                    _lastHash = ComputeHash(text);
                }
            }
            else if (image != null)
            {
                using var ms = new MemoryStream(image);
                var bmp = new System.Drawing.Bitmap(ms);
                Clipboard.SetImage(bmp);
                lock (_hashLock)
                {
                    _lastHash = ComputeHash(image);
                }
            }
            else if (files != null)
            {
                var collection = new System.Collections.Specialized.StringCollection();
                collection.AddRange(files.ToArray());
                Clipboard.SetFileDropList(collection);
                lock (_hashLock)
                {
                    _lastHash = ComputeHash(string.Join("|", files));
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Error("Error updating clipboard", ex);
            _skipNextChange = false;
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
        if (_selfHandle.IsAllocated) _selfHandle.Free();
    }

    private const int GWL_USERDATA = -21;

    [DllImport("user32.dll")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern ushort RegisterClass(ref WNDCLASS lpWndClass);

    [DllImport("user32.dll")]
    private static extern IntPtr CreateWindowEx(int dwExStyle, string lpClassName, string? lpWindowName,
        int dwStyle, int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu,
        IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASS
    {
        public int style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;

        public WNDCLASS(string className)
        {
            style = 0;
            lpfnWndProc = IntPtr.Zero;
            cbClsExtra = 0;
            cbWndExtra = 0;
            hInstance = IntPtr.Zero;
            hIcon = IntPtr.Zero;
            hCursor = IntPtr.Zero;
            hbrBackground = IntPtr.Zero;
            lpszMenuName = null;
            lpszClassName = className;
        }
    }

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
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
