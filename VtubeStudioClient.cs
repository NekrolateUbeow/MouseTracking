using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;

sealed class VTubeStudioClient : IDisposable
{
    readonly ClientWebSocket _ws = new();
    readonly ConcurrentDictionary<string, TaskCompletionSource<JsonNode>> _pending = new();
    readonly SemaphoreSlim _sendLock = new(1, 1);

    public Action<string?, JsonNode>? OnVtsMessage;

    public async Task ConnectAsync(int port, CancellationToken ct)
    {
        await _ws.ConnectAsync(new Uri($"ws://localhost:{port}"), ct);
        _ = Task.Run(() => ReceiveMessagesAsync(ct));
    }

    static string BuildApiRequest(string messageType, JsonObject? data, string id)
    {
        var msg = new JsonObject
        {
            ["apiName"] = "VTubeStudioPublicAPI",
            ["apiVersion"] = "1.0",
            ["requestID"] = id,
            ["messageType"] = messageType,
        };
        if (data != null) msg["data"] = data;
        return msg.ToJsonString();
    }

    async Task SendWebSocketMessageAsync(string json, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        await _sendLock.WaitAsync(ct);
        try { await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct); }
        finally { _sendLock.Release(); }
    }

    // Gửi yêu cầu và chờ phản hồi
    public async Task<JsonNode> SendRequestAsync(string messageType, JsonObject? data,
        CancellationToken ct, int timeoutSeconds = 5)
    {
        string id = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<JsonNode>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        try
        {
            await SendWebSocketMessageAsync(BuildApiRequest(messageType, data, id), ct);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
            using var reg = timeout.Token.Register(() => tcs.TrySetCanceled());
            return await tcs.Task;
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    // Gửi mà không chờ phản hồi (dùng cho inject); phản hồi được xử lý trong OnVtsMessage
    public Task SendRequestWithoutWaitingAsync(string messageType, JsonObject? data, CancellationToken ct) =>
        SendWebSocketMessageAsync(BuildApiRequest(messageType, data, "nw"), ct);

    async Task ReceiveMessagesAsync(CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        try
        {
            while (!ct.IsCancellationRequested && _ws.State == WebSocketState.Open)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult res;
                do
                {
                    res = await _ws.ReceiveAsync(buffer, ct);
                    if (res.MessageType == WebSocketMessageType.Close) return;
                    ms.Write(buffer, 0, res.Count);
                } while (!res.EndOfMessage);

                var node = JsonNode.Parse(ms.ToArray());
                if (node == null) continue;

                var type = node["messageType"]?.GetValue<string>();

                // Sự kiện (messageType không phải "...Response" hoặc "APIError") luôn được chuyển đến OnVtsMessage,
                // kể cả khi requestID trùng với yêu cầu đang chờ, để tránh nhầm sự kiện thành phản hồi
                bool isResponse = type != null && (type.EndsWith("Response") || type == "APIError");
                if (!isResponse)
                {
                    try { OnVtsMessage?.Invoke(type, node); } catch { }
                    continue;
                }

                var reqId = node["requestID"]?.GetValue<string>();
                if (reqId != null && _pending.TryRemove(reqId, out var tcs))
                {
                    tcs.TrySetResult(node);
                    continue;
                }

                // Không có yêu cầu nào chờ phản hồi (inject không chờ): chỉ cần xử lý lỗi
                if (type == "APIError")
                {
                    try { OnVtsMessage?.Invoke(type, node); } catch { }
                }
            }
        }
        catch { /* Kết nối đã đóng; xử lý trong finally. */ }
        finally
        {
            foreach (var kv in _pending)
                kv.Value.TrySetException(new IOException("Kết nối VTube Studio đã đóng."));
        }
    }

    public void Dispose()
    {
        try { _ws.Dispose(); } catch { }
    }
}