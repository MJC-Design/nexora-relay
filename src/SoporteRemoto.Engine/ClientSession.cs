using System.Threading.Channels;
using SoporteRemoto.Protocol;

namespace SoporteRemoto.Engine;

public sealed class ClientSession : IVideoSession, IAsyncDisposable
{
    private const int FileChunkSize = 64 * 1024;
    private const int ProgressIntervalMs = 150;

    public event Action<byte[]>? FrameReceived;
    public event Action<int, int>? CursorChanged;
    public event Action<string>? Disconnected;
    public event Action<int, long, long>? TransferProgress;
    public event Action<int, bool, string?>? TransferCompleted;
    public event Action<long, long>? OutgoingProgress;
    public event Action<bool, string?>? PullRequestResult;
    public event Action<string>? LogMessage;
    public event Action<string, string?>? ChatReceived;
    public event Action<bool, bool>? AudioStateChanged;
    public event Action<string>? AudioError;
    public event Action<bool, bool>? VideoStateChanged;
    public event Action<byte[]>? LocalVideoFrame;
    public event Action<byte[]>? RemoteVideoFrame;
    public event Action<string>? VideoError;
    public event Action? RemoteVideoEnded;
    public event Action<string>? SessionEnded;
    public event Action? VideoInvited;
    public event Action? VideoAccepted;
    public event Action? VideoRejected;

    public Func<FileOffer, Task<string?>>? SaveFileRequested { get; set; }

    public string HostName { get; }
    public string PeerName => HostName;
    public bool IsHost => false;
    public bool IsAudioLocal
    {
        get { lock (_audioGate) return _localAudio; }
    }
    public MediaDeviceInfo? SelectedCamera => MediaDevices.DefaultCamera;
    public MediaDeviceInfo? SelectedMicrophone => MediaDevices.DefaultMicrophone;
    public int ScreenWidth { get; }
    public int ScreenHeight { get; }
    public SessionMode Mode { get; }
    public Task Closed => _closed.Task;
    public bool IsClosed => _closed.Task.IsCompleted;

    private readonly IConnection _conn;
    private readonly CancellationTokenSource _cts;
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Channel<ControlMessage> _outbox;
    private readonly Dictionary<int, IncomingFile> _incoming = new();
    private readonly Dictionary<int, TaskCompletionSource<bool>> _pendingAccepts = new();
    private readonly object _fileGate = new();
    private readonly object _cleanupGate = new();
    private readonly object _audioGate = new();
    private readonly object _videoGate = new();

    private TaskCompletionSource<(bool Ok, string? Message)>? _pullTcs;
    private AudioCall? _audio;
    private VideoCall? _video;
    private int _nextFileId;
    private int _sendingFile;
    private bool _cleaned;
    private bool _disposed;
    private bool _localAudio;
    private bool _remoteAudio;
    private bool _localVideo;
    private bool _remoteVideo;

    private ClientSession(IConnection conn, ControlMessage result, SessionMode mode)
    {
        _conn = conn;
        Mode = mode;
        HostName = string.IsNullOrWhiteSpace(result.HostName) ? "Equipo remoto" : result.HostName!;
        ScreenWidth = result.ScreenWidth ?? 0;
        ScreenHeight = result.ScreenHeight ?? 0;
        _cts = new CancellationTokenSource();
        _outbox = Channel.CreateUnbounded<ControlMessage>(new UnboundedChannelOptions { SingleReader = true });

        _ = OutboxLoopAsync(_cts.Token);
        _ = ReceiveLoopAsync(_cts.Token);
    }

    public static async Task<ClientSession> ConnectLanAsync(
        string ip, int port, string code, string clientName,
        SessionMode mode = SessionMode.Screen, CancellationToken ct = default)
    {
        TcpConnection conn;
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                conn = await TcpConnection.ConnectAsync(ip, port, timeout.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException($"No se pudo alcanzar {ip}:{port} en la red local.");
            }
        }
        return await EstablishAsync(conn, code, clientName, mode, ct);
    }

    public static async Task<ClientSession> ConnectRelayAsync(
        string serverUrl, string code, string clientName,
        SessionMode mode = SessionMode.Screen, CancellationToken ct = default)
    {
        RelayClientConnection conn;
        try
        {
            conn = await RelayClientConnection.ConnectAsync(serverUrl, code, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new IOException("No se pudo conectar al servidor: " + ex.Message, ex);
        }
        return await EstablishAsync(conn, code, clientName, mode, ct);
    }

    /// <summary>
    /// Conecta solo con el código: primero busca el equipo en la red local y,
    /// si no aparece, usa el servidor de Internet.
    /// </summary>
    public static async Task<ClientSession> ConnectWithCodeAsync(
        string relayUrl, string code, string clientName, SessionMode mode = SessionMode.Screen,
        Action<string>? log = null, CancellationToken ct = default)
    {
        var normalized = SessionCode.Normalize(code);
        if (normalized.Length != SessionCode.Length)
            throw new InvalidOperationException("El código no es válido. Debe tener 9 letras o números.");

        log?.Invoke("Buscando el equipo en la red local…");
        var found = await LanDiscovery.FindAsync(normalized, TimeSpan.FromSeconds(2), ct);
        if (found is not null)
        {
            log?.Invoke($"Equipo encontrado en la red local ({found.Address}).");
            return await ConnectLanAsync(found.Address, found.Port, normalized, clientName, mode, ct);
        }

        if (string.IsNullOrWhiteSpace(relayUrl))
            throw new IOException(
                "No se encontró el equipo en la red local y no hay servidor de Internet configurado.");

        log?.Invoke("No está en la red local; conectando por Internet…");
        try
        {
            return await ConnectRelayAsync(relayUrl, normalized, clientName, mode, ct);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or RelayException)
        {
            throw new IOException(
                "No se pudo conectar. Verifica que el código sea correcto y que el otro equipo esté en línea. " +
                ex.Message, ex);
        }
    }

    private static async Task<ClientSession> EstablishAsync(
        IConnection conn, string code, string clientName, SessionMode mode, CancellationToken ct)
    {
        var normalized = SessionCode.Normalize(code);
        try
        {
            await conn.SendAsync(MessageType.Control,
                new ControlMessage
                {
                    Type = "connect",
                    Code = normalized,
                    ClientName = clientName,
                    Mode = mode.ToWire(),
                }.ToUtf8(), ct);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(90));

            while (true)
            {
                ReceivedFrame? frame;
                try
                {
                    frame = await conn.ReceiveAsync(timeout.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new TimeoutException("El equipo remoto no respondió a tiempo.");
                }

                if (frame is null)
                    throw new IOException("El equipo remoto cerró la conexión.");

                if (frame.Value.Type != MessageType.Control) continue;

                var msg = ControlMessage.FromUtf8(frame.Value.Payload.Span);
                if (msg is null) continue;

                switch (msg.Type)
                {
                    case "connectResult":
                        if (msg.Ok == true) return new ClientSession(conn, msg, mode);
                        throw new InvalidOperationException(msg.Message ?? "La conexión fue rechazada.");
                    case "error":
                        throw new IOException(msg.Message ?? "Error del servidor.");
                    case "disconnect":
                        throw new IOException(msg.Message ?? "La conexión se cerró.");
                }
            }
        }
        catch
        {
            try { await conn.DisposeAsync(); } catch { }
            throw;
        }
    }

    public void SendInputMove(int x, int y) =>
        Queue(new ControlMessage { Type = "input", InputType = "move", X = x, Y = y });

    public void SendInputButton(int button, bool down, int x, int y) =>
        Queue(new ControlMessage { Type = "input", InputType = "button", Button = button, Down = down, X = x, Y = y });

    public void SendInputWheel(int delta, int x, int y) =>
        Queue(new ControlMessage { Type = "input", InputType = "wheel", WheelDelta = delta, X = x, Y = y });

    public void SendInputKey(int vk, bool down) =>
        Queue(new ControlMessage { Type = "input", InputType = "key", Vk = vk, Down = down });

    public void SendTextInput(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        Queue(new ControlMessage { Type = "input", InputType = "text", Text = text });
    }

    public void SendChat(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        Queue(new ControlMessage { Type = "chat", Text = text.Trim(), From = DeviceInfo.Name });
    }

    public IReadOnlyList<MediaDeviceInfo> GetCameraDevices() => MediaDevices.GetCameras();

    public IReadOnlyList<MediaDeviceInfo> GetMicrophoneDevices() => MediaDevices.GetMicrophones();

    public bool SelectCamera(MediaDeviceInfo device)
    {
        MediaDevices.DefaultCamera = device;
        lock (_videoGate)
        {
            if (_video is null || !_localVideo) return true;
            try
            {
                _video.Restart(device);
                return true;
            }
            catch (Exception ex)
            {
                VideoError?.Invoke("No se pudo iniciar la cámara: " + ex.Message);
                return false;
            }
        }
    }

    public bool SelectMicrophone(MediaDeviceInfo device)
    {
        MediaDevices.DefaultMicrophone = device;
        lock (_audioGate)
        {
            if (_audio is null || !_localAudio) return true;
            try
            {
                _audio.RestartCapture(device);
                return true;
            }
            catch (Exception ex)
            {
                AudioError?.Invoke("No se pudo iniciar el micrófono: " + ex.Message);
                return false;
            }
        }
    }

    public void StartAudioCall()
    {
        lock (_audioGate)
        {
            if (_localAudio) return;
            try
            {
                _audio ??= CreateAudio();
                _audio.EnsurePlayback();
                if (!_audio.TryStartCapture(MediaDevices.DefaultMicrophone))
                    throw new InvalidOperationException("No se encontró ningún micrófono conectado.");
                _localAudio = true;
            }
            catch (Exception ex)
            {
                _audio?.StopAll();
                AudioError?.Invoke("No se pudo iniciar la llamada de audio: " + ex.Message);
                return;
            }
        }

        LogMessage?.Invoke("Llamada de audio iniciada.");
        _ = SendControlSafeAsync(new ControlMessage { Type = "audioStart" });
        AudioStateChanged?.Invoke(true, _remoteAudio);
    }

    public void StopAudioCall()
    {
        lock (_audioGate)
        {
            if (!_localAudio) return;
            _localAudio = false;
            _remoteAudio = false;
            _audio?.StopAll();
        }

        _ = SendControlSafeAsync(new ControlMessage { Type = "audioStop" });
        LogMessage?.Invoke("Llamada de audio finalizada.");
        AudioStateChanged?.Invoke(false, false);
    }

    private AudioCall CreateAudio()
    {
        var audio = new AudioCall();
        audio.Captured += OnAudioCaptured;
        return audio;
    }

    private void OnAudioCaptured(byte[] data)
    {
        var ct = _cts.Token;
        if (ct.IsCancellationRequested) return;
        _ = SendAudioFrameAsync(data, ct);
    }

    private async Task SendAudioFrameAsync(byte[] data, CancellationToken ct)
    {
        try { await _conn.SendAsync(MessageType.AudioFrame, data, ct); } catch { }
    }

    private void HandleRemoteAudioStart()
    {
        var started = false;
        lock (_audioGate)
        {
            if (_remoteAudio) return;
            try
            {
                _audio ??= CreateAudio();
                _audio.EnsurePlayback();
                _remoteAudio = true;
                started = true;
            }
            catch (Exception ex)
            {
                AudioError?.Invoke("No se pudo reproducir el audio: " + ex.Message);
            }
        }

        if (started)
        {
            LogMessage?.Invoke("La otra persona inició una llamada de audio.");
            AudioStateChanged?.Invoke(_localAudio, true);
        }
    }

    private void HandleRemoteAudioStop()
    {
        lock (_audioGate)
        {
            if (!_remoteAudio) return;
            _remoteAudio = false;
            if (!_localAudio) _audio?.StopAll();
        }

        LogMessage?.Invoke("La otra persona finalizó la llamada de audio.");
        AudioStateChanged?.Invoke(_localAudio, false);
    }

    public void StartVideo()
    {
        lock (_videoGate)
        {
            if (_localVideo) return;
            try
            {
                _video ??= CreateVideo();
                _video.Start(MediaDevices.DefaultCamera);
                _localVideo = true;
            }
            catch (Exception ex)
            {
                _video?.Dispose();
                _video = null;
                VideoError?.Invoke("No se pudo iniciar la cámara: " + ex.Message);
                return;
            }
        }

        LogMessage?.Invoke("Videollamada iniciada.");
        _ = SendControlSafeAsync(new ControlMessage { Type = "videoStart" });
        VideoStateChanged?.Invoke(true, _remoteVideo);
    }

    public void StopVideo()
    {
        lock (_videoGate)
        {
            if (!_localVideo) return;
            _localVideo = false;
            _remoteVideo = false;
            _video?.Stop();
        }

        _ = SendControlSafeAsync(new ControlMessage { Type = "videoStop" });
        LogMessage?.Invoke("Videollamada finalizada.");
        VideoStateChanged?.Invoke(false, false);
    }

    public void SendVideoInvite() =>
        _ = SendControlSafeAsync(new ControlMessage { Type = "videoInvite" });

    public void RespondVideoInvite(bool accepted) =>
        _ = SendControlSafeAsync(new ControlMessage { Type = accepted ? "videoAccept" : "videoReject" });

    private VideoCall CreateVideo()
    {
        var video = new VideoCall();
        video.Captured += OnVideoCaptured;
        video.Error += OnVideoError;
        return video;
    }

    private void OnVideoCaptured(byte[] jpeg)
    {
        var ct = _cts.Token;
        if (ct.IsCancellationRequested) return;
        try { LocalVideoFrame?.Invoke(jpeg); } catch { }
        _ = SendVideoFrameAsync(jpeg, ct);
    }

    private void OnVideoError(string message)
    {
        try { VideoError?.Invoke(message); } catch { }
    }

    private async Task SendVideoFrameAsync(byte[] jpeg, CancellationToken ct)
    {
        try { await _conn.SendAsync(MessageType.VideoFrame, jpeg, ct); } catch { }
    }

    private void HandleRemoteVideoStart()
    {
        lock (_videoGate)
        {
            if (_remoteVideo) return;
            _remoteVideo = true;
        }

        LogMessage?.Invoke("La otra persona inició la videollamada.");
        VideoStateChanged?.Invoke(_localVideo, true);
    }

    private void HandleRemoteVideoStop()
    {
        lock (_videoGate)
        {
            if (!_remoteVideo) return;
            _remoteVideo = false;
        }

        LogMessage?.Invoke("La otra persona finalizó la videollamada.");
        VideoStateChanged?.Invoke(_localVideo, false);
        try { RemoteVideoEnded?.Invoke(); } catch { }
    }

    public Task SendChatAsync(string text)
    {
        SendChat(text);
        return Task.CompletedTask;
    }

    public Task EndSessionAsync() => DisconnectAsync();

    private void Queue(ControlMessage message) => _outbox.Writer.TryWrite(message);

    public async Task<bool> RequestFileFromHostAsync(CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource<(bool Ok, string? Message)>(TaskCreationOptions.RunContinuationsAsynchronously);
        Interlocked.Exchange(ref _pullTcs, tcs)?.TrySetResult((false, "Solicitud reemplazada."));

        await SendControlSafeAsync(new ControlMessage { Type = "filePullRequest" });

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
            timeout.CancelAfter(TimeSpan.FromMinutes(5));
            var result = await tcs.Task.WaitAsync(timeout.Token);
            return result.Ok;
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException("El equipo remoto no respondió a la solicitud de archivo.");
        }
        finally
        {
            Interlocked.CompareExchange(ref _pullTcs, null, tcs);
        }
    }

    public async Task SendFileAsync(string path)
    {
        if (Interlocked.CompareExchange(ref _sendingFile, 1, 0) != 0)
            throw new InvalidOperationException("Ya hay una transferencia de archivos en curso.");

        var id = Interlocked.Increment(ref _nextFileId);
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_fileGate) _pendingAccepts[id] = tcs;

        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) throw new FileNotFoundException("El archivo ya no existe.", path);

            await SendControlSafeAsync(new ControlMessage
            {
                Type = "fileOffer",
                FileId = id,
                FileName = info.Name,
                FileSize = info.Length,
            });

            bool accepted;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                timeout.CancelAfter(TimeSpan.FromMinutes(2));
                accepted = await tcs.Task.WaitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException("El equipo remoto no respondió a la transferencia.");
            }

            if (!accepted)
            {
                LogMessage?.Invoke($"{info.Name}: el equipo remoto rechazó la transferencia.");
                return;
            }

            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
            var buffer = new byte[FileChunkSize];
            long sent = 0;
            var lastReport = Environment.TickCount64;
            while (true)
            {
                var read = await stream.ReadAsync(buffer, _cts.Token);
                if (read <= 0) break;
                var chunk = FileChunkFrame.Build(id, buffer.AsSpan(0, read));
                await _conn.SendAsync(MessageType.FileChunk, chunk, _cts.Token);
                sent += read;

                var now = Environment.TickCount64;
                if (now - lastReport >= ProgressIntervalMs)
                {
                    lastReport = now;
                    OutgoingProgress?.Invoke(sent, info.Length);
                }
            }

            await SendControlSafeAsync(new ControlMessage { Type = "fileComplete", FileId = id });
            OutgoingProgress?.Invoke(sent, info.Length);
            LogMessage?.Invoke($"Archivo enviado: {info.Name}.");
        }
        finally
        {
            lock (_fileGate) _pendingAccepts.Remove(id);
            Interlocked.Exchange(ref _sendingFile, 0);
        }
    }

    private async Task OutboxLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var message in _outbox.Reader.ReadAllAsync(ct))
            {
                try
                {
                    await _conn.SendAsync(MessageType.Control, message.ToUtf8(), ct);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    Cleanup("Se perdió la conexión: " + ex.Message, notify: true);
                    break;
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var frame = await _conn.ReceiveAsync(ct);
                if (frame is null)
                {
                    Cleanup("El equipo remoto cerró la conexión.", notify: true);
                    return;
                }

                switch (frame.Value.Type)
                {
                    case MessageType.ScreenFrame:
                        FrameReceived?.Invoke(frame.Value.Payload.ToArray());
                        break;

                    case MessageType.FileChunk:
                        await HandleFileChunkAsync(frame.Value.Payload, ct);
                        break;

                    case MessageType.AudioFrame:
                        _audio?.Play(frame.Value.Payload.Span);
                        break;

                    case MessageType.VideoFrame:
                        RemoteVideoFrame?.Invoke(frame.Value.Payload.ToArray());
                        break;

                    case MessageType.Control:
                        var msg = ControlMessage.FromUtf8(frame.Value.Payload.Span);
                        if (msg is not null) await HandleControlAsync(msg);
                        break;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Cleanup("Error de conexión: " + ex.Message, notify: true);
        }
    }

    private async Task HandleControlAsync(ControlMessage msg)
    {
        switch (msg.Type)
        {
            case "cursor":
                CursorChanged?.Invoke(msg.CursorX ?? 0, msg.CursorY ?? 0);
                break;

            case "chat":
                ChatReceived?.Invoke(msg.Text ?? "", msg.From);
                break;

            case "audioStart":
                HandleRemoteAudioStart();
                break;

            case "audioStop":
                HandleRemoteAudioStop();
                break;

            case "videoStart":
                HandleRemoteVideoStart();
                break;

            case "videoStop":
                HandleRemoteVideoStop();
                break;

            case "videoInvite":
                VideoInvited?.Invoke();
                break;

            case "videoAccept":
                VideoAccepted?.Invoke();
                break;

            case "videoReject":
                VideoRejected?.Invoke();
                break;

            case "fileOffer":
                _ = HandleIncomingFileAsync(msg);
                break;

            case "fileComplete":
                await FinalizeIncomingAsync(msg.FileId ?? 0);
                break;

            case "fileAccept":
                ResolveFileAccept(msg);
                break;

            case "filePullResult":
                _pullTcs?.TrySetResult((msg.Ok == true, msg.Message));
                PullRequestResult?.Invoke(msg.Ok == true, msg.Message);
                break;

            case "log":
                LogMessage?.Invoke(msg.Message ?? "");
                break;

            case "disconnect":
                Cleanup(msg.Message ?? "El equipo remoto finalizó la sesión.", notify: true);
                break;

            case "error":
                Cleanup(msg.Message ?? "Error del servidor.", notify: true);
                break;
        }
    }

    private async Task HandleIncomingFileAsync(ControlMessage offer)
    {
        var fileId = offer.FileId ?? 0;
        var name = Path.GetFileName(offer.FileName ?? "archivo");
        long size = offer.FileSize ?? 0;

        string? saveTo = null;
        try
        {
            if (SaveFileRequested is not null)
                saveTo = await SaveFileRequested(new FileOffer(fileId, name, size));
        }
        catch { }

        if (string.IsNullOrWhiteSpace(saveTo))
        {
            await SendControlSafeAsync(new ControlMessage { Type = "fileAccept", FileId = fileId, Accepted = false });
            return;
        }

        try
        {
            var stream = new FileStream(saveTo, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            lock (_fileGate)
            {
                _incoming[fileId] = new IncomingFile
                {
                    Stream = stream,
                    Size = size,
                    Name = name,
                    LastReport = Environment.TickCount64,
                };
            }
            await SendControlSafeAsync(new ControlMessage { Type = "fileAccept", FileId = fileId, Accepted = true });
        }
        catch (Exception ex)
        {
            await SendControlSafeAsync(new ControlMessage
            {
                Type = "fileAccept",
                FileId = fileId,
                Accepted = false,
                Message = ex.Message,
            });
        }
    }

    private async Task HandleFileChunkAsync(ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        var fileId = FileChunkFrame.ReadFileId(payload.Span);
        if (fileId < 0) return;

        IncomingFile? file;
        lock (_fileGate)
        {
            _incoming.TryGetValue(fileId, out file);
        }
        if (file is null) return;

        var data = payload.Slice(4);
        await file.Stream.WriteAsync(data, ct);
        file.Received += data.Length;

        var now = Environment.TickCount64;
        if (now - file.LastReport >= ProgressIntervalMs)
        {
            file.LastReport = now;
            TransferProgress?.Invoke(fileId, file.Received, file.Size);
        }

        if (file.Size > 0 && file.Received >= file.Size)
            await FinalizeIncomingAsync(fileId);
    }

    private async Task FinalizeIncomingAsync(int fileId)
    {
        IncomingFile? file;
        lock (_fileGate)
        {
            _incoming.Remove(fileId, out file);
        }
        if (file is null) return;

        try { await file.Stream.FlushAsync(); } catch { }
        try { file.Stream.Dispose(); } catch { }

        var ok = file.Size <= 0 || file.Received >= file.Size;
        TransferProgress?.Invoke(fileId, file.Received, Math.Max(file.Size, file.Received));
        TransferCompleted?.Invoke(fileId, ok, ok ? null : "El archivo llegó incompleto.");
        LogMessage?.Invoke($"Archivo recibido: {file.Name}.");
    }

    private void ResolveFileAccept(ControlMessage msg)
    {
        var id = msg.FileId ?? 0;
        TaskCompletionSource<bool>? tcs;
        lock (_fileGate) _pendingAccepts.Remove(id, out tcs);
        tcs?.TrySetResult(msg.Accepted == true);
    }

    private async Task SendControlSafeAsync(ControlMessage message)
    {
        try
        {
            await _conn.SendAsync(MessageType.Control, message.ToUtf8(), _cts.Token);
        }
        catch (OperationCanceledException) { }
        catch { }
    }

    public async Task DisconnectAsync()
    {
        if (_cleaned) return;
        const string reason = "El técnico finalizó la sesión.";
        try
        {
            await SendControlSafeAsync(new ControlMessage { Type = "disconnect", Message = reason });
        }
        catch { }
        Cleanup(reason, notify: false);
        await DisposeAsync();
    }

    private void Cleanup(string reason, bool notify)
    {
        lock (_cleanupGate)
        {
            if (_cleaned) return;
            _cleaned = true;
        }

        try { _cts.Cancel(); } catch { }
        _outbox.Writer.TryComplete();
        try { _audio?.Dispose(); } catch { }
        try { _video?.Dispose(); } catch { }
        _audio = null;
        _video = null;

        IncomingFile[] files;
        TaskCompletionSource<bool>[] pending;
        lock (_fileGate)
        {
            files = _incoming.Values.ToArray();
            _incoming.Clear();
            pending = _pendingAccepts.Values.ToArray();
            _pendingAccepts.Clear();
        }

        foreach (var file in files)
        {
            try { file.Stream.Dispose(); } catch { }
        }
        foreach (var tcs in pending)
            tcs.TrySetResult(false);

        Interlocked.Exchange(ref _pullTcs, null)?.TrySetResult((false, reason));
        _closed.TrySetResult();
        try { SessionEnded?.Invoke(reason); } catch { }

        if (notify)
        {
            try { Disconnected?.Invoke(reason); } catch { }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        if (!_cleaned)
        {
            try
            {
                await SendControlSafeAsync(new ControlMessage { Type = "disconnect", Message = "El técnico cerró la sesión." });
            }
            catch { }
        }

        Cleanup("Sesión cerrada.", notify: false);

        try { _cts.Cancel(); } catch { }
        _outbox.Writer.TryComplete();
        try { await _conn.DisposeAsync(); } catch { }
        _cts.Dispose();
    }

    private sealed class IncomingFile
    {
        public required FileStream Stream { get; init; }
        public long Size { get; init; }
        public string Name { get; init; } = "";
        public long Received { get; set; }
        public long LastReport { get; set; }
    }
}
