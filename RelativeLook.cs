using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

// Đọc chuyển động chuột thô bằng Raw Input
static class RawMouseInput
{
    const uint WM_INPUT = 0x00FF;
    const uint RID_INPUT = 0x10000003;
    const uint RIDEV_INPUTSINK = 0x00000100;
    static readonly IntPtr HWND_MESSAGE = new IntPtr(-3);

    static long _mouseDeltaX, _mouseDeltaY;
    static WndProcDelegate? _windowProc;
    static readonly IntPtr _inputBuffer = Marshal.AllocHGlobal(128);
    static int _isStarted;

    delegate IntPtr WndProcDelegate(IntPtr h, uint msg, IntPtr w, IntPtr l);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WNDCLASSEX
    {
        public uint cbSize, style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground, lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct RAWINPUTDEVICE { public ushort usUsagePage, usUsage; public uint dwFlags; public IntPtr hwndTarget; }
    [StructLayout(LayoutKind.Sequential)]
    struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern ushort RegisterClassEx(ref WNDCLASSEX c);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr CreateWindowEx(uint ex, string cls, string name, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] static extern IntPtr DefWindowProc(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] d, uint n, uint size);
    [DllImport("user32.dll")] static extern uint GetRawInputData(IntPtr hRaw, uint cmd, IntPtr data, ref uint size, uint hdrSize);
    [DllImport("user32.dll")] static extern int GetMessage(out MSG m, IntPtr h, uint min, uint max);
    [DllImport("user32.dll")] static extern bool TranslateMessage(ref MSG m);
    [DllImport("user32.dll")] static extern IntPtr DispatchMessage(ref MSG m);
    [DllImport("kernel32.dll")] static extern IntPtr GetModuleHandle(string? name);

    public static void StartListening()
    {
        if (Interlocked.Exchange(ref _isStarted, 1) == 1) return;// Không khởi động luồng lần thứ hai
        new Thread(RunMessageLoop) { IsBackground = true, Name = "RawMouseInput" }.Start();
    }

    static void RunMessageLoop()
    {
        _windowProc = HandleWindowMessage;
        var wc = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_windowProc),
            hInstance = GetModuleHandle(null),
            lpszClassName = "MouseLookAtRaw"
        };
        RegisterClassEx(ref wc);
        var hwnd = CreateWindowEx(0, wc.lpszClassName, "", 0, 0, 0, 0, 0, HWND_MESSAGE, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
        var rid = new[] { new RAWINPUTDEVICE { usUsagePage = 1, usUsage = 2, dwFlags = RIDEV_INPUTSINK, hwndTarget = hwnd } };
        if (!RegisterRawInputDevices(rid, 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>()))
            Console.WriteLine("RegisterRawInputDevices thất bại");
        while (GetMessage(out var m, IntPtr.Zero, 0, 0) > 0) { TranslateMessage(ref m); DispatchMessage(ref m); }
    }

    static IntPtr HandleWindowMessage(IntPtr h, uint msg, IntPtr w, IntPtr l)
    {
        if (msg == WM_INPUT)
        {
            uint size = 128;
            // Các offset bên dưới áp dụng cho cấu trúc Raw Input trên Windows x64
            if (GetRawInputData(l, RID_INPUT, _inputBuffer, ref size, 24) != uint.MaxValue
                && Marshal.ReadInt32(_inputBuffer, 0) == 0)// Dữ liệu từ chuột
            {
                ushort flags = (ushort)Marshal.ReadInt16(_inputBuffer, 24);
                if ((flags & 1) == 0)// Bỏ qua tọa độ tuyệt đối
                {
                    Interlocked.Add(ref _mouseDeltaX, Marshal.ReadInt32(_inputBuffer, 36));
                    Interlocked.Add(ref _mouseDeltaY, Marshal.ReadInt32(_inputBuffer, 40));
                }
            }
        }
        return DefWindowProc(h, msg, w, l);
    }

    public static (int dx, int dy) ReadAndResetDeltas() =>
        ((int)Interlocked.Exchange(ref _mouseDeltaX, 0), (int)Interlocked.Exchange(ref _mouseDeltaY, 0));
}

// Các thông số có thể điều chỉnh
sealed class FpsLookSettings
{
    // Góc xoay được tính từ count chuột và hệ số nhạy
    public double Sensitivity = 0.5;
    public double YawMultiplier = 0.05;
    public double PitchMultiplier = 0.05;

    // Giới hạn góc tương ứng với giá trị đầu ra -1..1
    public double MaxYawDegrees = 90;
    public double MaxPitchDegrees = 60;
    public bool InvertY = false;

    // Đưa góc nhìn dần về tâm khi ngừng di chuyển chuột
    public double DecayDelaySeconds = 0.25;
    public double DecaySeconds = 2.0;
    public double DecayThreshold = 0.05;

    // Điều kiện nhận diện chế độ FPS
    public string MouseMode = "auto";
    public bool RequireHiddenCursor = true;
    public double DetectionWindowSeconds = 0.25;
    public double EnterThresholdCounts = 120;
    public double LockThresholdPixels = 8;
    public double ExitThresholdPixels = 30;
    public double EdgeThresholdPixels = 2;

    // Quy đổi count chuột thành góc xoay
    public double YawDegreesPerCount => Sensitivity * YawMultiplier;
    public double PitchDegreesPerCount => Sensitivity * PitchMultiplier;
}

// Nhận diện chế độ chuột và tính góc nhìn
sealed class FpsMouseLook
{
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    struct CURSORINFO { public int cbSize, flags; public IntPtr hCursor; public POINT pt; }
    [DllImport("user32.dll")] static extern bool GetCursorInfo(ref CURSORINFO ci);
    [DllImport("user32.dll")] static extern bool GetClipCursor(out RECT r);

    struct Sample { public double T; public int Dx, Dy, X, Y; public bool Edge; }

    readonly FpsLookSettings _settings;
    readonly Queue<Sample> _recentSamples = new();
    double _elapsedSeconds, _lastMouseMoveTime, _yawDegrees, _pitchDegrees;
    double _yawDecayVelocity, _pitchDecayVelocity;// Vận tốc của quá trình decay, theo thang -1..1
    readonly double _decayFrequency;// Hệ số dùng để tính tốc độ decay

    public bool IsFpsMode { get; private set; }
    public string DebugStatus { get; private set; } = "";

    public FpsMouseLook(FpsLookSettings o)
    {
        _settings = o;
        _decayFrequency = _settings.DecaySeconds > 0 ? CalculateDecayFrequencyFactor(_settings.DecayThreshold) / _settings.DecaySeconds : 0;
    }

    // Tính hệ số sao cho giá trị giảm đến ngưỡng snap trong khoảng thời gian đã chọn
    static double CalculateDecayFrequencyFactor(double snap)
    {
        snap = Math.Clamp(snap, 1e-4, 0.9);
        double lo = 0, hi = 50;
        for (int i = 0; i < 60; i++)
        {
            double mid = (lo + hi) / 2;
            if ((1 + mid) * Math.Exp(-mid) > snap) lo = mid; else hi = mid;
        }
        return (lo + hi) / 2;
    }

    // Cập nhật vị trí và vận tốc của decay sau dt giây
    static void ApplySpringDecay(ref double x, ref double v, double w, double dt)
    {
        double e = Math.Exp(-w * dt), a = w * dt;
        double nx = e * (x * (1 + a) + v * dt);
        double nv = e * (v * (1 - a) - w * w * dt * x);
        x = nx; v = nv;
    }

    /// prevX/prevY là giá trị đã gửi ở lần cập nhật trước
    /// Trả về true khi đang dùng chế độ FPS
    public bool Update(double dt, double prevX, double prevY, out double x, out double y)
    {
        _elapsedSeconds += dt;

        var ci = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
        GetCursorInfo(ref ci);
        GetClipCursor(out var clip);
        var (dx, dy) = RawMouseInput.ReadAndResetDeltas();

        bool hidden = ci.flags == 0;
        int cx = ci.pt.X, cy = ci.pt.Y;
        bool tinyClip = clip.Right - clip.Left <= 4 && clip.Bottom - clip.Top <= 4;// Vùng khóa con trỏ rất nhỏ
        bool edge = !tinyClip && (cx <= clip.Left + _settings.EdgeThresholdPixels || cx >= clip.Right - 1 - _settings.EdgeThresholdPixels
                               || cy <= clip.Top + _settings.EdgeThresholdPixels || cy >= clip.Bottom - 1 - _settings.EdgeThresholdPixels);

        _recentSamples.Enqueue(new Sample { T = _elapsedSeconds, Dx = dx, Dy = dy, X = cx, Y = cy, Edge = edge });
        while (_recentSamples.Count > 1 && _recentSamples.Peek().T < _elapsedSeconds - _settings.DetectionWindowSeconds) _recentSamples.Dequeue();

        // Tổng hợp chuyển động trong khoảng thời gian gần nhất
        double rawPath = 0; bool anyEdge = false;
        int minX = int.MaxValue, maxX = int.MinValue, minY = int.MaxValue, maxY = int.MinValue;
        foreach (var s in _recentSamples)
        {
            rawPath += Math.Sqrt((double)s.Dx * s.Dx + (double)s.Dy * s.Dy);
            anyEdge |= s.Edge;
            minX = Math.Min(minX, s.X); maxX = Math.Max(maxX, s.X);
            minY = Math.Min(minY, s.Y); maxY = Math.Max(maxY, s.Y);
        }
        double span = Math.Max(maxX - minX, maxY - minY);
        var first = _recentSamples.Peek();
        double net = Math.Sqrt(Math.Pow(cx - first.X, 2) + Math.Pow(cy - first.Y, 2));

        // Chọn chế độ hoạt động
        if (_settings.MouseMode == "fps") { if (!IsFpsMode) SwitchToFpsMode(prevX, prevY, false); }
        else if (_settings.MouseMode == "cursor") { IsFpsMode = false; }
        else if (!IsFpsMode)
        {
            bool okHidden = hidden || !_settings.RequireHiddenCursor;
            // Chuột có chuyển động nhưng con trỏ gần như đứng yên, không phải do đẩy vào mép màn hình
            bool locked = tinyClip ? rawPath >= _settings.EnterThresholdCounts * 0.25
                                   : (rawPath >= _settings.EnterThresholdCounts && span <= _settings.LockThresholdPixels && !anyEdge);
            if (okHidden && locked) SwitchToFpsMode(prevX, prevY, true);
        }
        else
        {
            // Không thoát FPS chỉ vì chuột tạm thời đứng yên
            bool shown = _settings.RequireHiddenCursor && !hidden;// Ví dụ: mở menu hoặc chuyển cửa sổ
            bool freed = rawPath > 0 && net >= _settings.ExitThresholdPixels;// Chuột và con trỏ cùng dịch chuyển
            if (shown || freed) { IsFpsMode = false; _recentSamples.Clear(); }
        }

        DebugStatus = $"mode={(IsFpsMode ? "FPS" : "cursor")} hidden={hidden} tinyClip={tinyClip} raw={rawPath:F0} span={span:F0} net={net:F0} edge={anyEdge}";

        if (!IsFpsMode) { x = y = 0; return false; }

        // Cập nhật góc nhìn hoặc đưa góc nhìn dần về tâm
        if (dx != 0 || dy != 0)
        {
            _lastMouseMoveTime = _elapsedSeconds;
            _yawDecayVelocity = _pitchDecayVelocity = 0;// Dừng decay khi chuột di chuyển trở lại
            _yawDegrees += dx * _settings.YawDegreesPerCount;
            _pitchDegrees += dy * _settings.PitchDegreesPerCount;
            _yawDegrees = Math.Clamp(_yawDegrees, -_settings.MaxYawDegrees, _settings.MaxYawDegrees);
            _pitchDegrees = Math.Clamp(_pitchDegrees, -_settings.MaxPitchDegrees, _settings.MaxPitchDegrees);
        }
        else if (_decayFrequency > 0 && _elapsedSeconds - _lastMouseMoveTime > _settings.DecayDelaySeconds)
        {
            // Áp dụng decay riêng cho từng trục trong khoảng -1..1
            double nx = _yawDegrees / _settings.MaxYawDegrees, ny = _pitchDegrees / _settings.MaxPitchDegrees;
            ApplySpringDecay(ref nx, ref _yawDecayVelocity, _decayFrequency, dt);
            ApplySpringDecay(ref ny, ref _pitchDecayVelocity, _decayFrequency, dt);
            if (Math.Abs(nx) < _settings.DecayThreshold) { nx = 0; _yawDecayVelocity = 0; }// Đủ gần tâm thì đặt hẳn về 0
            if (Math.Abs(ny) < _settings.DecayThreshold) { ny = 0; _pitchDecayVelocity = 0; }
            _yawDegrees = nx * _settings.MaxYawDegrees;
            _pitchDegrees = ny * _settings.MaxPitchDegrees;
        }

        x = _yawDegrees / _settings.MaxYawDegrees;
        double py = -_pitchDegrees / _settings.MaxPitchDegrees;// Đảo dấu để quy ước kéo xuống là nhìn xuống
        y = _settings.InvertY ? -py : py;
        return true;
    }

    void SwitchToFpsMode(double prevX, double prevY, bool catchUp)
    {
        IsFpsMode = true;
        _lastMouseMoveTime = _elapsedSeconds;
        _yawDecayVelocity = _pitchDecayVelocity = 0;
        // Giữ hướng nhìn hiện tại khi chuyển sang FPS
        _yawDegrees = Math.Clamp(prevX, -1, 1) * _settings.MaxYawDegrees;
        double py = _settings.InvertY ? -prevY : prevY;
        _pitchDegrees = -Math.Clamp(py, -1, 1) * _settings.MaxPitchDegrees;
        if (catchUp) // Bù lại chuyển động đã ghi nhận lúc nhận diện FPS
        {
            foreach (var s in _recentSamples)
            {
                _yawDegrees += s.Dx * _settings.YawDegreesPerCount;
                _pitchDegrees += s.Dy * _settings.PitchDegreesPerCount;
            }
            _yawDegrees = Math.Clamp(_yawDegrees, -_settings.MaxYawDegrees, _settings.MaxYawDegrees);
            _pitchDegrees = Math.Clamp(_pitchDegrees, -_settings.MaxPitchDegrees, _settings.MaxPitchDegrees);
        }
    }
}