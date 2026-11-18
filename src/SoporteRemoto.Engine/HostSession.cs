using System.Diagnostics;
using SoporteRemoto.Protocol;

namespace SoporteRemoto.Engine;

public sealed class HostSession : IVideoSession
{
    private const int MaxCaptureWidth = 1440;
    private const long JpegQuality = 55;
    private const int TargetFps = 12;
    private const int FileChunkSize = 64 * 1024;

    private readonly IConnection _conn;
    private readonly HostController _controller;
    private readonly ConnectionRequest _request;
    private readonly CancellationTokenSource _cts;
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Dictionary<int, IncomingFile> _incoming = new();
    private readonly Dictionary<int, TaskCompletionSource<bool>> _pendingAccepts = new();
    private readonly object _fileGate = new();
    private readonly object _cleanupGate = new();
    private readonly object _audioGate = new();
    private readonly object _videoGate = new();

    private ScreenCapturer? _capturer;
    private AudioCall? _audio;
    private VideoCall? _video;
    private Task? _screenTask;
    private Task? _receiveTask;
    private int _nextFileId;
    private int _sendingFile;
    private int _pullRunning;
    private bool _cleaned;
    private bool _localAudio;
    private bool _remoteAudio;
    private bool _localVideo;
    private bool _remoteVideo;

    public Task Closed => _closed.Task;
    public string ClientName => _request.ClientName;
    public string RemoteAddress => _request.RemoteAddress;
    public SessionMode Mode { get; }
    public string PeerName => ClientName;
    public bool IsHost => true;
    public bool IsAudioLocal
    {
        get { lock (_audioGate) return _localAudio; }
    }
    public MediaDeviceInfo? SelectedCamera => MediaDevices.DefaultCamera;
    public MediaDeviceInfo? SelectedMicrophone => MediaDevices.DefaultMicrophone;

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

    private HostSession(
        IConnection conn, HostController controller, ConnectionRequest request,
        SessionMode mode, CancellationToken outer)
    {
        _conn = conn;
        _controller = controller;
        _request = request;
        Mode = mode;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
    }

    public static async Task<HostSession?> TryStartAsync(
        IConnection conn, ControlMessage connect, string remote, HostController controller, CancellationToken ct)
    {
        if (!controller.TryReserveSession())
        {
            await TrySendAsync(conn,
                new ControlMessage { Type = "connectResult", Ok = false, Message = "El equipo ya está en una sesión remota." }, ct);
            return null;
        }

        var started = false;
        try
        {
            if (!string.Equals(SessionCode.Normalize(connect.Code), controller.Code, StringComparison.Ordinal))
            {
                controller.Log("Intento de conexión con un código incorrecto.");
                await TrySendAsync(conn,
                    new ControlMessage { Type = "connectResult", Ok = false, Message = "Código incorrecto." }, ct);
                return null;
            }

            var request = new ConnectionRequest(
                string.IsNullOrWhiteSpace(connect.ClientName) ? "Equipo desconocido" : connect.ClientName!,
                remote,
                SessionModeExtensions.FromWire(connect.Mode));

            if (controller.ApproveConnectionAsync is not null)
            {
                bool approved;
                try { approved = await controller.ApproveConnectionAsync(request); }
                catch { approved = false; }

                if (!approved)
                {
                    await TrySendAsync(conn,
                        new ControlMessage { Type = "connectResult", Ok = false, Message = "El usuario del equipo remoto rechazó la conexión." }, ct);
                    return null;
                }
            }

            var session = new HostSession(conn, controller, request, request.Mode, ct);
            try
            {
                await session.StartAsync();
                started = true;
                return session;
            }
            catch
            {
                session.Cleanup("No se pudo iniciar la sesión.");
                throw;
            }
        }
        catch (Exception ex)
        {
            controller.Log("Error al iniciar sesión: " + ex.Message);
            return null;
        }
        finally
        {
            if (!started) controller.FailSessionReservation();
        }
    }

    private async Task StartAsync()
    {
        try
        {
            if (Mode == SessionMode.Screen)
                _capturer = new ScreenCapturer();

            var result = new ControlMessage
            {
                Type = "connectResult",
                Ok = true,
                ScreenWidth = _capturer?.SourceWidth ?? 0,
                ScreenHeight = _capturer?.SourceHeight ?? 0,
                HostName = DeviceInfo.Name,
            };

            await _conn.SendAsync(MessageType.Control, result.ToUtf8(), _cts.Token);

            _controller.AttachSession(this, $"{ClientName} ({RemoteAddress})");
            _controller.Log(Mode switch
            {
                SessionMode.Files => $"Sesión de archivos iniciada con {ClientName}.",
                SessionMode.Video => $"Videollamada iniciada con {ClientName}.",
                _ => $"Sesión iniciada con {ClientName}.",
            });

            _receiveTask = Task.Run(() => ReceiveLoopAsync(_cts.Token));
            if (Mode == SessionMode.Screen)
                _screenTask = Task.Run(() => ScreenLoopAsync(_cts.Token));
        }
        catch
        {
            _cts.Cancel();
            _cts.Dispose();
            _capturer?.Dispose();
            throw;
        }
    }

    public async Task EndAsync(string message = "El equipo remoto finalizó la sesión.")
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await _conn.SendAsync(MessageType.Control,
                new ControlMessage { Type = "disconnect", Message = message }.ToUtf8(), timeout.Token);
        }
        catch { }

        Cleanup(message);
    }

    private async Task ScreenLoopAsync(CancellationToken ct)
    {
        var interval = TimeSpan.FromMilliseconds(1000.0 / TargetFps);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var started = Stopwatch.GetTimestamp();

                var jpeg = _capturer!.CaptureJpeg(MaxCaptureWidth, JpegQuality);
                if (jpeg is not null)
                    await _conn.SendAsync(MessageType.ScreenFrame, jpeg, ct);

                var (cursorX, cursorY) = InputSender.GetCursorPosition();
                await _conn.SendAsync(MessageType.Control,
                    new ControlMessage { Type = "cursor", CursorX = cursorX, CursorY = cursorY }.ToUtf8(), ct);

                var wait = interval - Stopwatch.GetElapsedTime(started);
                if (wait > TimeSpan.FromMilliseconds(2))
                    await Task.Delay(wait, ct);
                else
                    await Task.Yield();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Cleanup("Se perdió la conexión: " + ex.Message);
        }
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
                    Cleanup("El equipo remoto cerró la conexión.");
                    return;
                }

                switch (frame.Value.Type)
                {
                    case MessageType.Control:
                        var msg = ControlMessage.FromUtf8(frame.Value.Payload.Span);
                        if (msg is not null) await HandleControlAsync(msg);
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
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Cleanup("Error de sesión: " + ex.Message);
        }
    }

    private async Task HandleControlAsync(ControlMessage msg)
    {
        switch (msg.Type)
        {
            case "input":
                if (Mode == SessionMode.Screen) HandleInput(msg);
                break;

            case "chat":
                ChatReceived?.Invoke(msg.Text ?? "", msg.From);
                _controller.Log($"Chat de {msg.From ?? "la otra persona"}: {msg.Text}");
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

            case "filePullRequest":
                _ = HandlePullAsync();
                break;

            case "disconnect":
                Cleanup(msg.Message ?? "El equipo remoto finalizó la sesión.");
                break;
        }
    }

    private static void HandleInput(ControlMessage msg)
    {
        switch (msg.InputType)
        {
            case "move":
                InputSender.Move(msg.X ?? 0, msg.Y ?? 0);
                break;
            case "button":
                InputSender.Button(msg.Button ?? 0, msg.Down ?? false, msg.X ?? 0, msg.Y ?? 0);
                break;
            case "wheel":
                InputSender.Wheel(msg.WheelDelta ?? 0, msg.X ?? 0, msg.Y ?? 0);
                break;
            case "key":
                InputSender.Key(msg.Vk ?? 0, msg.Down ?? false);
                break;
            case "text":
                InputSender.Text(msg.Text ?? "");
                break;
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
                _controller.Log($"{info.Name}: el equipo remoto rechazó la transferencia.");
                return;
            }

            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
            var buffer = new byte[FileChunkSize];
            long totalSent = 0;
            while (true)
            {
                var read = await stream.ReadAsync(buffer, _cts.Token);
                if (read <= 0) break;
                var chunk = FileChunkFrame.Build(id, buffer.AsSpan(0, read));
                await _conn.SendAsync(MessageType.FileChunk, chunk, _cts.Token);
                totalSent += read;
            }

            await SendControlSafeAsync(new ControlMessage { Type = "fileComplete", FileId = id });
            _controller.Log($"Archivo enviado: {info.Name} ({FormatSize(totalSent)}).");
        }
        finally
        {
            lock (_fileGate) _pendingAccepts.Remove(id);
            Interlocked.Exchange(ref _sendingFile, 0);
        }
    }

    private async Task HandlePullAsync()
    {
        if (Interlocked.CompareExchange(ref _pullRunning, 1, 0) != 0) return;

        try
        {
            if (_controller.PickFileToSend is null)
            {
                await SendControlSafeAsync(new ControlMessage
                {
                    Type = "filePullResult",
                    Ok = false,
                    Message = "El equipo remoto no permite solicitudes de archivos.",
                });
                return;
            }

            var path = await _controller.PickFileToSend();
            if (string.IsNullOrWhiteSpace(path))
            {
                await SendControlSafeAsync(new ControlMessage
                {
                    Type = "filePullResult",
                    Ok = false,
                    Message = "El envío fue cancelado en el equipo remoto.",
                });
                return;
            }

            await SendControlSafeAsync(new ControlMessage { Type = "filePullResult", Ok = true });
            await SendFileAsync(path);
        }
        catch (Exception ex)
        {
            _controller.Log("Error enviando archivo: " + ex.Message);
            await SendControlSafeAsync(new ControlMessage { Type = "filePullResult", Ok = false, Message = ex.Message });
        }
        finally
        {
            Interlocked.Exchange(ref _pullRunning, 0);
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
            if (_controller.SaveFileRequested is not null)
                saveTo = await _controller.SaveFileRequested(new FileOffer(fileId, name, size));
        }
        catch { }

        if (string.IsNullOrWhiteSpace(saveTo))
        {
            _controller.Log($"Transferencia rechazada: {name}.");
            await SendControlSafeAsync(new ControlMessage { Type = "fileAccept", FileId = fileId, Accepted = false });
            return;
        }

        try
        {
            var stream = new FileStream(saveTo, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            lock (_fileGate) _incoming[fileId] = new IncomingFile { Stream = stream, Size = size, Name = name };
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
        lock (_fileGate) _incoming.TryGetValue(fileId, out file);
        if (file is null) return;

        var data = payload.Slice(4);
        await file.Stream.WriteAsync(data, ct);
        file.Received += data.Length;

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
        _controller.Log($"Archivo recibido: {file.Name} ({FormatSize(file.Received)}).");
    }

    private void ResolveFileAccept(ControlMessage msg)
    {
        var id = msg.FileId ?? 0;
        TaskCompletionSource<bool>? tcs;
        lock (_fileGate) _pendingAccepts.Remove(id, out tcs);
        tcs?.TrySetResult(msg.Accepted == true);
    }

    public async Task SendChatAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        await SendControlSafeAsync(new ControlMessage
        {
            Type = "chat",
            Text = text.Trim(),
            From = DeviceInfo.Name,
        });
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

        _controller.Log("Llamada de audio iniciada.");
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
        _controller.Log("Llamada de audio finalizada.");
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
            _controller.Log("La otra persona inició una llamada de audio.");
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

        _controller.Log("La otra persona finalizó la llamada de audio.");
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

        _controller.Log("Videollamada iniciada.");
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
        _controller.Log("Videollamada finalizada.");
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

        _controller.Log("La otra persona inició la videollamada.");
        VideoStateChanged?.Invoke(_localVideo, true);
    }

    private void HandleRemoteVideoStop()
    {
        lock (_videoGate)
        {
            if (!_remoteVideo) return;
            _remoteVideo = false;
        }

        _controller.Log("La otra persona finalizó la videollamada.");
        VideoStateChanged?.Invoke(_localVideo, false);
        try { RemoteVideoEnded?.Invoke(); } catch { }
    }

    public Task EndSessionAsync() => EndAsync();

    private async Task SendControlSafeAsync(ControlMessage message)
    {
        try
        {
            await _conn.SendAsync(MessageType.Control, message.ToUtf8(), _cts.Token);
        }
        catch (OperationCanceledException) { }
        catch { }
    }

    private static async Task TrySendAsync(IConnection conn, ControlMessage message, CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            await conn.SendAsync(MessageType.Control, message.ToUtf8(), timeout.Token);
        }
        catch { }
    }

    private void Cleanup(string reason)
    {
        lock (_cleanupGate)
        {
            if (_cleaned) return;
            _cleaned = true;
        }

        try { _cts.Cancel(); } catch { }
        try { _capturer?.Dispose(); } catch { }
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

        _controller.ReleaseSession(this);
        _controller.Log("Sesión finalizada: " + reason);
        _closed.TrySetResult();
        try { SessionEnded?.Invoke(reason); } catch { }
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:0.0} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):0.0} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):0.00} GB";
    }

    private sealed class IncomingFile
    {
        public required FileStream Stream { get; init; }
        public long Size { get; init; }
        public string Name { get; init; } = "";
        public long Received { get; set; }
    }
}
