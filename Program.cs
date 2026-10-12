using System.Text.Json.Nodes;

const string PluginName = "MouseTracking";
const string PluginDeveloper = "Ubeow";
const string LookXParameterId = "MouseLookX";
const string LookYParameterId = "MouseLookY";

var commandLineOptions = ParseCommandLine(args);
int port = GetIntOption(commandLineOptions, "port", 8001);
double hz = GetDoubleOption(commandLineOptions, "hz", 30);
double keepAliveIntervalMs = GetDoubleOption(commandLineOptions, "keepalive", 200);
double? lookRadiusPixels = commandLineOptions.ContainsKey("r") ? GetDoubleOption(commandLineOptions, "r", 450) : null;
double headOffsetX = GetDoubleOption(commandLineOptions, "headx", 0.03);
double headOffsetY = GetDoubleOption(commandLineOptions, "heady", 0.25);
// bool debug = commandLineOptions.ContainsKey("debug"); // Bỏ chú thích nếu cần bật chế độ debug
int rotationSign = GetIntOption(commandLineOptions, "rotsign", 1) >= 0 ? 1 : -1;

var fpsSettings = new FpsLookSettings
{
    Sensitivity = GetDoubleOption(commandLineOptions, "sens", 1.0),
    YawMultiplier = GetDoubleOption(commandLineOptions, "yaw", 0.05),
    PitchMultiplier = GetDoubleOption(commandLineOptions, "pitch", 0.05),
    MaxYawDegrees = GetDoubleOption(commandLineOptions, "yawrange", 90),
    MaxPitchDegrees = GetDoubleOption(commandLineOptions, "pitchrange", 60),
    InvertY = commandLineOptions.ContainsKey("invy"),
    DecayDelaySeconds = GetDoubleOption(commandLineOptions, "decaydelay", 0.25),
    DecaySeconds = GetDoubleOption(commandLineOptions, "decaytime", 2.0),
    DecayThreshold = GetDoubleOption(commandLineOptions, "decaysnap", 0.05),
    MouseMode = commandLineOptions.TryGetValue("mode", out var modeArg) ? modeArg.ToLowerInvariant() : "auto",
    RequireHiddenCursor = !commandLineOptions.TryGetValue("requirehidden", out var reqHiddenArg)
                    || !string.Equals(reqHiddenArg, "false", StringComparison.OrdinalIgnoreCase),
    LockThresholdPixels = GetDoubleOption(commandLineOptions, "lockpx", 8),
    ExitThresholdPixels = GetDoubleOption(commandLineOptions, "exitpx", 30),
    EnterThresholdCounts = GetDoubleOption(commandLineOptions, "entercounts", 120),
};

ScreenBounds? screenBoundsOverride = null;
if (commandLineOptions.TryGetValue("screen", out var screenArg))
{
    var parts = screenArg.Split(',');
    var v = new double[4];
    bool ok = parts.Length == 4;
    for (int i = 0; ok && i < 4; i++)
        ok = double.TryParse(parts[i], System.Globalization.NumberStyles.Float,
                             System.Globalization.CultureInfo.InvariantCulture, out v[i]);
    if (ok && v[2] > 0 && v[3] > 0) screenBoundsOverride = new ScreenBounds(v[0], v[1], v[2], v[3]);
    // else Console.WriteLine($"[{PluginName}] --screen sai định dạng, dùng x,y,w,h. Dùng màn hình chính.");
}

string tokenFilePath = Path.Combine(AppContext.BaseDirectory, "vts_token.txt");

WindowsApi.SetProcessDPIAware();// để tọa độ chuột và kích thước màn hình cùng là pixel vật lý
RawMouseInput.StartListening();// luồng đọc chuyển động chuột thô, chỉ gọi một lần (không gọi lại khi reconnect)

using var cts = new CancellationTokenSource();
// Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); }; // Chỉ dùng khi kiểm thử bằng console

// Console.WriteLine($"[{PluginName}] Đang chạy. Nhấn Ctrl+C để thoát.");

while (!cts.IsCancellationRequested)
{
    try
    {
        await RunConnectionSessionAsync(cts.Token);
    }
    catch (OperationCanceledException) when (cts.IsCancellationRequested)
    {
        break;
    }
    catch (Exception ex)
    {
        // Console.WriteLine($"[{PluginName}] Mất kết nối / lỗi: {ex.Message}. Thử lại sau 3 giây...");
        try { await Task.Delay(3000, cts.Token); } catch { break; }
    }
}

// Console.WriteLine($"[{PluginName}] Đã thoát.");
return;

async Task RunConnectionSessionAsync(CancellationToken ct)
{
    using var client = new VTubeStudioClient();
    client.OnVtsMessage = HandleVtsMessage;
    await client.ConnectAsync(port, ct);
    // Console.WriteLine($"[{PluginName}] Đã kết nối VTube Studio (cổng {port}).");

    await AuthenticatePluginAsync(client, ct);
    await EnsureLookParametersAsync(client, ct);

    // Xóa trạng thái cũ của phiên trước, rồi đăng ký ModelMovedEvent
    // VTS gửi dữ liệu ngay sau khi đăng ký và mỗi khi position, size hoặc rotation đổi nên không cần hỏi thêm vị trí model
    PluginState.HasReceivedModelPosition = false;
    await SubscribeToEventAsync(client, "ModelMovedEvent", ct);

    var timer = new PeriodicTimer(TimeSpan.FromSeconds(1.0 / Math.Max(1, hz)));
    var lastParameterSendTime = DateTime.MinValue;
    // var lastDebugTime = DateTime.MinValue; // Bỏ chú thích cùng khối debug bên dưới nếu cần kiểm thử
    double lastLookX = double.NaN, lastLookY = double.NaN;
    const double valueChangeThreshold = 0.001;
    var mouseLook = new FpsMouseLook(fpsSettings);
    var lastUpdateTime = DateTime.UtcNow;

    while (await timer.WaitForNextTickAsync(ct))
    {
        var now = DateTime.UtcNow;

        // Phải gọi mỗi tick (trước mọi lệnh continue) để đọc hết chuyển động chuột thô và tính đúng dt
        double dt = Math.Clamp((now - lastUpdateTime).TotalSeconds, 0.0, 0.25);
        lastUpdateTime = now;
        bool isFps = mouseLook.Update(dt,
            double.IsNaN(lastLookX) ? 0 : lastLookX, double.IsNaN(lastLookY) ? 0 : lastLookY,
            out var fx, out var fy);

        if (!PluginState.HasReceivedModelPosition) continue;
        if (!WindowsApi.GetCursorPos(out var pt)) continue;

        var m = PluginState.CurrentModelPosition;
        var scr = screenBoundsOverride ?? ScreenBounds.GetPrimaryScreenBounds();

        // Khối debug chỉ dùng khi kiểm thử; cần bật thì bỏ chú thích các dòng liên quan
        // if (debug && (now - lastDebugTime).TotalSeconds >= 1)
        // {
        //     lastDebugTime = now;
        //     Console.WriteLine($"[debug] pos=({m.X:F3}, {m.Y:F3}) size={m.Size:F3} rot={m.Rotation:F1}");
        //     Console.WriteLine($"[debug] {mouseLook.DebugStatus} out=({fx:F3}, {fy:F3})");
        // }

        // Hệ -1..1 phụ thuộc kích thước màn hình: X -1..1 => 0..Width, Y -1..1 => 0..Height (Y dương hướng lên)
        // Nên phép xoay phải làm trên pixel, không làm trực tiếp trong hệ -1..1
        double halfScreenWidth = scr.Width / 2.0, halfScreenHeight = scr.Height / 2.0;

        // 1) Tâm đầu khi chưa xoay = vị trí model + độ lệch đầu * size (cùng hệ -1..1) / -74.49664306640625, đổi sang pixel
        //    -74.49664306640625 là default size theo thiết kế của VTS
        double headOffsetXNormalized = headOffsetX * m.Size / -74.49664306640625;
        double headOffsetYNormalized = headOffsetY * m.Size / -74.49664306640625;
        //    Đưa điểm về 0..1 bằng (u + 1) / 2 rồi nhân kích thước màn hình
        double baseHeadX = scr.Left + (m.X + headOffsetXNormalized + 1) / 2.0 * scr.Width;
        double baseHeadY = scr.Top + (1 - m.Y - headOffsetYNormalized) / 2.0 * scr.Height;
        // 2) Độ lệch thêm do xoay, tính trên pixel (Y hướng lên). Đây là vector nên chỉ nhân tỉ lệ,
        //    không cộng 1. Xoay vector độ lệch quanh tâm model
        //    (ngược chiều kim đồng hồ) rồi trừ đi vector chưa xoay
        double headOffsetPixelX = headOffsetXNormalized * halfScreenWidth;
        double headOffsetPixelY = headOffsetYNormalized * halfScreenHeight;
        double rotationRadians = m.Rotation * rotationSign * Math.PI / 180.0;
        double cos = Math.Cos(rotationRadians), sin = Math.Sin(rotationRadians);
        double rotationOffsetX = headOffsetPixelX * cos - headOffsetPixelY * sin - headOffsetPixelX;
        double rotationOffsetY = headOffsetPixelX * sin + headOffsetPixelY * cos - headOffsetPixelY;
        // 3) Vị trí đầu theo pixel (pixel Y đi từ trên xuống nên trừ phần hướng lên)
        double headPositionX = baseHeadX + rotationOffsetX;
        double headPositionY = baseHeadY - rotationOffsetY;
        double dx = pt.X - headPositionX;
        double dy = headPositionY - pt.Y;// pixel Y đi từ trên xuống, param Y đi từ dưới lên
        double lookRadius = lookRadiusPixels ?? 0.45 * scr.Height;
        if (lookRadius < 1) lookRadius = 1;

        double vx = Math.Tanh(dx / lookRadius);
        double vy = Math.Tanh(dy / lookRadius);
        if (isFps) { vx = fx; vy = fy; }// chế độ FPS: dùng giá trị từ chuyển động chuột thô
        // Chỉ gửi khi giá trị đổi, hoặc đủ thời gian keepalive để không bị VTS trả về default
        bool changed = double.IsNaN(lastLookX) ||
                       Math.Abs(vx - lastLookX) > valueChangeThreshold || Math.Abs(vy - lastLookY) > valueChangeThreshold;
        if (!changed && (now - lastParameterSendTime).TotalMilliseconds < keepAliveIntervalMs) continue;

        lastLookX = vx; lastLookY = vy; lastParameterSendTime = now;

        // Gộp cả 2 param trong một request, gửi xong không chờ response
        var data = new JsonObject
        {
            ["faceFound"] = false,
            ["mode"] = "set",
            ["parameterValues"] = new JsonArray
            {
                new JsonObject { ["id"] = LookXParameterId, ["value"] = vx },
                new JsonObject { ["id"] = LookYParameterId, ["value"] = vy },
            }
        };
        await client.SendRequestWithoutWaitingAsync("InjectParameterDataRequest", data, ct);
    }
}

// Xử lý event từ VTS và lỗi của request gửi không chờ phản hồi
void HandleVtsMessage(string? type, JsonNode node)
{
    if (type == "APIError")
    {
        if (!PluginState.HasLoggedApiError)
        {
            PluginState.HasLoggedApiError = true;
            // Console.WriteLine($"[{PluginName}] Lỗi từ VTS: {node["data"]?["message"]}");
        }
        return;
    }

    // modelID rỗng (chưa có model nào được nạp): bỏ qua, không cập nhật
    if (node["data"] is JsonObject evData && evData.ContainsKey("modelID"))
    {
        var modelId = evData["modelID"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(modelId)) return;
    }

    // Event vị trí model được nhận dạng theo dữ liệu (data.modelPosition), không phụ thuộc
    // messageType là "ModelMovedEvent" hay "Event"
    var p = node["data"]?["modelPosition"];
    if (p == null) return;

    var old = PluginState.CurrentModelPosition;
    PluginState.CurrentModelPosition = new VtsModelPosition(
        p["positionX"]?.GetValue<double>() ?? old.X,
        p["positionY"]?.GetValue<double>() ?? old.Y,
        p["size"]?.GetValue<double>() ?? old.Size,
        p["rotation"]?.GetValue<double>() ?? old.Rotation);
    PluginState.HasReceivedModelPosition = true;
}

async Task SubscribeToEventAsync(VTubeStudioClient client, string eventName, CancellationToken ct)
{
    await client.SendRequestAsync("EventSubscriptionRequest", new JsonObject
    {
        ["eventName"] = eventName,
        ["subscribe"] = true,
        ["config"] = new JsonObject(),
    }, ct);

}

async Task AuthenticatePluginAsync(VTubeStudioClient client, CancellationToken ct)
{
    string? token = File.Exists(tokenFilePath) ? File.ReadAllText(tokenFilePath).Trim() : null;

    if (!string.IsNullOrEmpty(token) && await TryAuthenticateWithTokenAsync(client, token, ct))
    {
        // Console.WriteLine($"[{PluginName}] Xác thực bằng token đã lưu.");
        return;
    }

    // Console.WriteLine($"[{PluginName}] Hãy bấm 'Allow' trong popup của VTube Studio...");
    var tokenResp = await client.SendRequestAsync("AuthenticationTokenRequest", new JsonObject
    {
        ["pluginName"] = PluginName,
        ["pluginDeveloper"] = PluginDeveloper,
    }, ct, timeoutSeconds: 60);

    token = tokenResp["data"]?["authenticationToken"]?.GetValue<string>();
    if (string.IsNullOrEmpty(token))
        throw new Exception("Không lấy được token (bạn đã bấm Deny?).");

    File.WriteAllText(tokenFilePath, token);

    if (!await TryAuthenticateWithTokenAsync(client, token, ct))
        throw new Exception("Xác thực thất bại.");

    // Console.WriteLine($"[{PluginName}] Xác thực thành công.");
}

async Task<bool> TryAuthenticateWithTokenAsync(VTubeStudioClient client, string token, CancellationToken ct)
{
    var resp = await client.SendRequestAsync("AuthenticationRequest", new JsonObject
    {
        ["pluginName"] = PluginName,
        ["pluginDeveloper"] = PluginDeveloper,
        ["authenticationToken"] = token,
    }, ct);
    return resp["data"]?["authenticated"]?.GetValue<bool>() ?? false;
}

async Task EnsureLookParametersAsync(VTubeStudioClient client, CancellationToken ct)
{
    foreach (var name in new[] { LookXParameterId, LookYParameterId })
    {
        // Nếu param đã tồn tại VTS trả lỗi, bỏ qua được
        await client.SendRequestAsync("ParameterCreationRequest", new JsonObject
        {
            ["parameterName"] = name,
            ["explanation"] = "Huong nhin cua model toi con tro chuot (tanh)",
            ["min"] = -1,
            ["max"] = 1,
            ["defaultValue"] = 0,
        }, ct);
    }
    // Console.WriteLine($"[{PluginName}] Param sẵn sàng: {LookXParameterId}, {LookYParameterId}");
}

static Dictionary<string, string> ParseCommandLine(string[] a)
{
    var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    foreach (var s in a)
    {
        if (!s.StartsWith("--")) continue;
        var kv = s[2..].Split('=', 2);
        d[kv[0]] = kv.Length > 1 ? kv[1] : "true";
    }
    return d;
}

static int GetIntOption(Dictionary<string, string> d, string k, int def) =>
    d.TryGetValue(k, out var v) && int.TryParse(v, out var r) ? r : def;

static double GetDoubleOption(Dictionary<string, string> d, string k, double def) =>
    d.TryGetValue(k, out var v) &&
    double.TryParse(v, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var r) ? r : def;

sealed record VtsModelPosition(double X, double Y, double Size, double Rotation);

// Trạng thái dùng chung giữa receive loop (ghi) và vòng cập nhật (đọc)
static class PluginState
{
    public static volatile VtsModelPosition CurrentModelPosition = new(0, 0, 1, 0);
    public static volatile bool HasReceivedModelPosition;
    public static volatile bool HasLoggedApiError;
}