using System.Text.RegularExpressions;

namespace SoporteRemoto.Android;

public static class EngineMessages
{
    private static readonly Dictionary<string, string> Exact = new(StringComparer.Ordinal)
    {
        ["El equipo ya está en una sesión remota."] = "Eng_AlreadySession",
        ["Intento de conexión con un código incorrecto."] = "Eng_WrongCodeAttempt",
        ["Código incorrecto."] = "Eng_WrongCode",
        ["Equipo desconocido"] = "Eng_UnknownDevice",
        ["Equipo remoto"] = "Eng_RemoteDevice",
        ["Red local"] = "Eng_Lan",
        ["la otra persona"] = "Eng_OtherPerson",
        ["El usuario del equipo remoto rechazó la conexión."] = "Eng_Rejected",
        ["No se pudo iniciar la sesión."] = "Eng_StartFailed",
        ["El equipo remoto finalizó la sesión."] = "Eng_RemoteEnded",
        ["El equipo remoto cerró la conexión."] = "Eng_RemoteClosed",
        ["Se desactivaron las conexiones."] = "Eng_ConnectionsDisabled",
        ["Conexiones desactivadas."] = "Eng_Disabled",
        ["El equipo remoto no permite solicitudes de archivos."] = "Eng_PullNotAllowed",
        ["El envío fue cancelado en el equipo remoto."] = "Eng_PullCancelledRemote",
        ["Ya hay una transferencia de archivos en curso."] = "Eng_FileBusy",
        ["El archivo ya no existe."] = "Eng_FileGone",
        ["El equipo remoto no respondió a la transferencia."] = "Eng_FileNoAnswer",
        ["No se encontró ningún micrófono conectado."] = "Eng_NoMic",
        ["Llamada de audio iniciada."] = "Eng_AudioStarted",
        ["Llamada de audio finalizada."] = "Eng_AudioEnded",
        ["La otra persona inició una llamada de audio."] = "Eng_AudioRemoteStart",
        ["La otra persona finalizó la llamada de audio."] = "Eng_AudioRemoteStop",
        ["Buscando el equipo en la red local…"] = "Eng_Searching",
        ["No se encontró el equipo en la red local y no hay servidor de Internet configurado."] = "Eng_NoRelay",
        ["No está en la red local; conectando por Internet…"] = "Eng_TryingInternet",
        ["El equipo remoto no respondió a tiempo."] = "Eng_NoAnswer",
        ["La conexión fue rechazada."] = "Eng_RejectedRemote",
        ["Error del servidor."] = "Eng_ServerGenericError",
        ["La conexión se cerró."] = "Eng_ConnectionClosed",
        ["Solicitud reemplazada."] = "Eng_PullReplaced",
        ["El equipo remoto no respondió a la solicitud de archivo."] = "Eng_PullNoAnswer",
        ["El archivo llegó incompleto."] = "Eng_FileIncomplete",
        ["El técnico finalizó la sesión."] = "Eng_TechEnded",
        ["El técnico cerró la sesión."] = "Eng_TechClosed",
        ["Sesión cerrada."] = "Eng_SessionClosed",
        ["El código no es válido. Debe tener 9 letras o números."] = "Eng_InvalidCode",
        ["En línea"] = "Eng_RelayOnline",
        ["Conectando al servidor…"] = "Eng_RelayConnecting",
        ["Sin conexión al servidor"] = "Eng_RelayOffline",
        ["Sin servidor configurado"] = "Eng_RelayNoServer",
        ["Desactivado"] = "Eng_RelayDisabled",
        ["Screen sharing is not supported on this platform."] = "Eng_NoScreenSupport",
        ["Remote control is not supported on this platform."] = "Eng_NoInputSupport",
        ["Audio calls are not supported on this platform."] = "Eng_NoAudioSupport",
        ["No microphone tool found. Install PulseAudio (parec) or ALSA (arecord)."] = "Eng_NoMicTool",
        ["No audio playback tool found. Install PulseAudio (pacat) or ALSA (aplay)."] = "Eng_NoPlaybackTool",
        ["Videollamada iniciada."] = "Eng_VideoStarted",
        ["Videollamada finalizada."] = "Eng_VideoEnded",
        ["La otra persona inició la videollamada."] = "Eng_VideoRemoteStart",
        ["La otra persona finalizó la videollamada."] = "Eng_VideoRemoteStop",
        ["No se encontró ninguna cámara conectada."] = "Eng_NoCamera",
        ["No se encontró ffmpeg. Instálalo para usar la videollamada."] = "Eng_NoFfmpeg",
        ["ffmpeg finalizó sin enviar video."] = "Eng_FfmpegNoVideo",
    };

    private const string ListeningPrefix = "Escuchando conexiones de red local en el puerto ";
    private const string SessionCodePrefix = "Código de sesión: ";
    private const string NewCodePrefix = "Nuevo código de sesión: ";
    private const string DiscoveryErrorPrefix = "No se pudo activar la búsqueda en red local: ";
    private const string RelayConnectedPrefix = "Conectado al servidor de Internet. Código: ";
    private const string ServerErrorPrefix = "Servidor: ";
    private const string LanErrorPrefix = "Error de red local: ";
    private const string IncomingPrefix = "Conexión entrante desde ";
    private const string LocalErrorPrefix = "Error en conexión local: ";
    private const string FileSessionPrefix = "Sesión de archivos iniciada con ";
    private const string SessionStartedPrefix = "Sesión iniciada con ";
    private const string SessionEndedPrefix = "Sesión finalizada: ";
    private const string SessionErrorPrefix = "Sesión: ";
    private const string StartErrorPrefix = "Error al iniciar sesión: ";
    private const string ConnectionLostPrefix = "Se perdió la conexión: ";
    private const string SessionFailurePrefix = "Error de sesión: ";
    private const string ChatPrefix = "Chat de ";
    private const string FileSentPrefix = "Archivo enviado: ";
    private const string FileReceivedPrefix = "Archivo recibido: ";
    private const string SendFileErrorPrefix = "Error enviando archivo: ";
    private const string TransferRejectedPrefix = "Transferencia rechazada: ";
    private const string AudioStartErrorPrefix = "No se pudo iniciar la llamada de audio: ";
    private const string AudioPlayErrorPrefix = "No se pudo reproducir el audio: ";
    private const string ConnectFailedPrefix = "No se pudo conectar. Verifica que el código sea correcto y que el otro equipo esté en línea. ";
    private const string RelayConnectErrorPrefix = "No se pudo conectar al servidor: ";
    private const string ConnectionErrorPrefix = "Error de conexión: ";
    private const string VideoSessionPrefix = "Videollamada iniciada con ";
    private const string CameraErrorPrefix = "No se pudo iniciar la cámara: ";
    private const string MicrophoneErrorPrefix = "No se pudo iniciar el micrófono: ";

    private static readonly Regex LanUnreachable =
        new(@"^No se pudo alcanzar (.+):(\d+) en la red local\.$", RegexOptions.CultureInvariant);

    private static readonly Regex FoundLan =
        new(@"^Equipo encontrado en la red local \((.+)\)\.$", RegexOptions.CultureInvariant);

    private static readonly Regex ChatMessage =
        new(@"^Chat de (.+?): (.*)$", RegexOptions.Singleline | RegexOptions.CultureInvariant);

    private static readonly Regex FileWithSize =
        new(@"^(.+) \(([^()]*)\)\.$", RegexOptions.CultureInvariant);

    public static string Translate(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return message ?? string.Empty;
        var text = message.Trim();

        if (Exact.TryGetValue(text, out var key))
            return Localization.Get(key);

        if (text.StartsWith(ListeningPrefix, StringComparison.Ordinal))
            return Localization.Format("Eng_Listening", text[ListeningPrefix.Length..].TrimEnd('.'));

        if (text.StartsWith(SessionCodePrefix, StringComparison.Ordinal))
            return Localization.Format("Eng_SessionCode", text[SessionCodePrefix.Length..]);

        if (text.StartsWith(NewCodePrefix, StringComparison.Ordinal))
            return Localization.Format("Eng_NewCode", text[NewCodePrefix.Length..]);

        if (text.StartsWith(DiscoveryErrorPrefix, StringComparison.Ordinal))
            return Localization.Format("Eng_DiscoveryError", text[DiscoveryErrorPrefix.Length..]);

        if (text.StartsWith(RelayConnectedPrefix, StringComparison.Ordinal))
            return Localization.Format("Eng_RelayConnected", text[RelayConnectedPrefix.Length..]);

        if (text.StartsWith(ServerErrorPrefix, StringComparison.Ordinal))
            return Localization.Format("Eng_ServerError", text[ServerErrorPrefix.Length..]);

        if (text.StartsWith(LanErrorPrefix, StringComparison.Ordinal))
            return Localization.Format("Eng_LanError", text[LanErrorPrefix.Length..]);

        if (text.StartsWith(IncomingPrefix, StringComparison.Ordinal))
            return Localization.Format("Eng_Incoming", text[IncomingPrefix.Length..].TrimEnd('.'));

        if (text.StartsWith(LocalErrorPrefix, StringComparison.Ordinal))
            return Localization.Format("Eng_LocalError", text[LocalErrorPrefix.Length..]);

        if (text.StartsWith(FileSessionPrefix, StringComparison.Ordinal))
            return Localization.Format("Eng_FileSessionStarted", text[FileSessionPrefix.Length..].TrimEnd('.'));

        if (text.StartsWith(SessionStartedPrefix, StringComparison.Ordinal))
            return Localization.Format("Eng_SessionStarted", text[SessionStartedPrefix.Length..].TrimEnd('.'));

        if (text.StartsWith(SessionEndedPrefix, StringComparison.Ordinal))
            return Localization.Format("Eng_SessionEnded", Translate(text[SessionEndedPrefix.Length..]));

        if (text.StartsWith(StartErrorPrefix, StringComparison.Ordinal))
            return Localization.Format("Eng_StartError", text[StartErrorPrefix.Length..]);

        if (text.StartsWith(SessionErrorPrefix, StringComparison.Ordinal))
            return Localization.Format("Eng_SessionError", text[SessionErrorPrefix.Length..]);

        if (text.StartsWith(ConnectionLostPrefix, StringComparison.Ordinal))
            return Localization.Format("Eng_ConnectionLost", Translate(text[ConnectionLostPrefix.Length..]));

        if (text.StartsWith(SessionFailurePrefix, StringComparison.Ordinal))
            return Localization.Format("Eng_SessionFailure", Translate(text[SessionFailurePrefix.Length..]));

        if (text.StartsWith(ChatPrefix, StringComparison.Ordinal))
        {
            var chat = ChatMessage.Match(text);
            if (chat.Success)
                return Localization.Format("Eng_ChatFrom", Translate(chat.Groups[1].Value), chat.Groups[2].Value);
        }

        if (text.StartsWith(FileSentPrefix, StringComparison.Ordinal))
        {
            var rest = text[FileSentPrefix.Length..];
            var sized = FileWithSize.Match(rest);
            return sized.Success
                ? Localization.Format("Eng_FileSentSize", sized.Groups[1].Value, sized.Groups[2].Value)
                : Localization.Format("Eng_FileSent", rest.TrimEnd('.'));
        }

        if (text.StartsWith(FileReceivedPrefix, StringComparison.Ordinal))
        {
            var rest = text[FileReceivedPrefix.Length..];
            var sized = FileWithSize.Match(rest);
            return sized.Success
                ? Localization.Format("Eng_FileReceivedSize", sized.Groups[1].Value, sized.Groups[2].Value)
                : Localization.Format("Eng_FileReceived", rest.TrimEnd('.'));
        }

        if (text.StartsWith(SendFileErrorPrefix, StringComparison.Ordinal))
            return Localization.Format("Eng_SendFileError", text[SendFileErrorPrefix.Length..]);

        if (text.StartsWith(TransferRejectedPrefix, StringComparison.Ordinal))
            return Localization.Format("Eng_TransferRejected",
                text[TransferRejectedPrefix.Length..].TrimEnd('.'));

        if (text.StartsWith(AudioStartErrorPrefix, StringComparison.Ordinal))
            return Localization.Format("Eng_AudioStartError", Translate(text[AudioStartErrorPrefix.Length..]));

        if (text.StartsWith(AudioPlayErrorPrefix, StringComparison.Ordinal))
            return Localization.Format("Eng_AudioPlayError", text[AudioPlayErrorPrefix.Length..]);

        if (text.StartsWith(ConnectFailedPrefix, StringComparison.Ordinal))
            return Localization.Format("Eng_ConnectFailed", text[ConnectFailedPrefix.Length..]);

        if (text.StartsWith(RelayConnectErrorPrefix, StringComparison.Ordinal))
            return Localization.Format("Eng_RelayConnectError", text[RelayConnectErrorPrefix.Length..]);

        if (text.StartsWith(ConnectionErrorPrefix, StringComparison.Ordinal))
            return Localization.Format("Eng_ConnectionError", Translate(text[ConnectionErrorPrefix.Length..]));

        if (text.StartsWith(VideoSessionPrefix, StringComparison.Ordinal))
            return Localization.Format("Eng_VideoSessionStarted", text[VideoSessionPrefix.Length..].TrimEnd('.'));

        if (text.StartsWith(CameraErrorPrefix, StringComparison.Ordinal))
            return Localization.Format("Eng_CameraStartError", Translate(text[CameraErrorPrefix.Length..]));

        if (text.StartsWith(MicrophoneErrorPrefix, StringComparison.Ordinal))
            return Localization.Format("Eng_MicStartError", Translate(text[MicrophoneErrorPrefix.Length..]));

        var unreachable = LanUnreachable.Match(text);
        if (unreachable.Success)
            return Localization.Format("Eng_LanUnreachable",
                unreachable.Groups[1].Value, unreachable.Groups[2].Value);

        var found = FoundLan.Match(text);
        if (found.Success)
            return Localization.Format("Eng_FoundLan", found.Groups[1].Value);

        return text;
    }

    public static string TranslateRelayStatus(string? status)
    {
        return status switch
        {
            "En línea" => Localization.Get("Eng_RelayOnline"),
            "Conectando al servidor…" => Localization.Get("Eng_RelayConnecting"),
            "Sin conexión al servidor" => Localization.Get("Eng_RelayOffline"),
            "Sin servidor configurado" => Localization.Get("Eng_RelayNoServer"),
            "Desactivado" => Localization.Get("Eng_RelayDisabled"),
            _ => Translate(status),
        };
    }
}
