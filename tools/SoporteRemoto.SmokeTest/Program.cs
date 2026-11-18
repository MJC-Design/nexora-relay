using SoporteRemoto.Engine;
using SoporteRemoto.Protocol;

namespace SoporteRemoto.SmokeTest;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var mode = args.Length > 0 ? args[0].ToLowerInvariant() : "lan";
        var relayUrl = args.Length > 1 ? args[1] : "ws://127.0.0.1:5080";

        try
        {
            if (mode == "relay")
            {
                Console.WriteLine("== Prueba por relay ==");
                await RunAsync(relayUrl, useRelay: true);
            }
            else
            {
                Console.WriteLine("== Prueba por red local ==");
                await RunAsync(null, useRelay: false);
            }
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine("FALLO: " + ex.Message);
            Console.WriteLine(ex);
            return 1;
        }
    }

    private static async Task RunAsync(string? relayUrl, bool useRelay)
    {
        var dir = Path.Combine(Path.GetTempPath(), "SoporteRemotoSmoke_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var sourceFile = Path.Combine(dir, "enviado.txt");
        var uploadTarget = Path.Combine(dir, "subida_al_host.txt");
        var downloadTarget = Path.Combine(dir, "descarga_del_host.txt");

        var content = string.Join("\n",
            Enumerable.Range(0, 8000).Select(i => $"Linea {i} de prueba para SoporteRemoto con contenido variable {i * 17}."));
        await File.WriteAllTextAsync(sourceFile, content);
        var sourceSize = new FileInfo(sourceFile).Length;

        var host = new HostController();
        var hostLogs = new List<string>();
        var uploadDone = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        host.LogMessage += message =>
        {
            lock (hostLogs) hostLogs.Add(message);
            Console.WriteLine("[host] " + message);
            if (message.StartsWith("Archivo recibido", StringComparison.Ordinal))
                uploadDone.TrySetResult(true);
        };

        host.ApproveConnectionAsync = _ => Task.FromResult(true);
        host.SaveFileRequested = _ => Task.FromResult<string?>(uploadTarget);
        host.PickFileToSend = () => Task.FromResult<string?>(sourceFile);

        ClientSession? client = null;
        try
        {
            await host.StartAsync(relayUrl ?? "");

            if (useRelay)
            {
                var deadline = DateTime.UtcNow.AddSeconds(20);
                while (host.RelayId is null && DateTime.UtcNow < deadline)
                    await Task.Delay(200);
                if (host.RelayId is null)
                    throw new Exception("El servidor relay no registró el código.");
                Console.WriteLine($"Código de sesión registrado en el relay: {host.RelayId}");
                if (host.RelayId != host.Code)
                    throw new Exception("El relay no conservó el código generado por el equipo.");
                client = await ClientSession.ConnectRelayAsync(relayUrl!, host.Code, "SmokeTest");
            }
            else
            {
                var found = await LanDiscovery.FindAsync(host.Code, TimeSpan.FromSeconds(5));
                if (found is null)
                    throw new Exception("El descubrimiento en red local no encontró el equipo.");
                Console.WriteLine($"Descubrimiento LAN: {found.Address}:{found.Port} ({found.MachineName}). OK");

                var rejected = false;
                try
                {
                    await ClientSession.ConnectLanAsync("127.0.0.1", host.LanPort, "ZZZZZZZZZ", "SmokeTest");
                }
                catch (InvalidOperationException)
                {
                    rejected = true;
                }
                if (!rejected)
                    throw new Exception("El equipo aceptó un código incorrecto.");
                Console.WriteLine("Código incorrecto rechazado. OK");

                client = await ClientSession.ConnectLanAsync(found.Address, found.Port, host.Code, "SmokeTest");
            }

            client.LogMessage += message => Console.WriteLine("[client] " + message);
            client.SaveFileRequested = _ => Task.FromResult<string?>(downloadTarget);

            Console.WriteLine($"Conectado a {client.HostName} ({client.ScreenWidth}x{client.ScreenHeight}).");

            var frameTcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            client.FrameReceived += bytes => frameTcs.TrySetResult(bytes);
            var frame = await frameTcs.Task.WaitAsync(TimeSpan.FromSeconds(20));
            if (frame.Length < 4 || frame[0] != 0xFF || frame[1] != 0xD8)
                throw new Exception("El fotograma recibido no es un JPEG válido.");
            Console.WriteLine($"Fotograma de pantalla recibido ({frame.Length} bytes). OK");

            await client.SendFileAsync(sourceFile);
            await uploadDone.Task.WaitAsync(TimeSpan.FromSeconds(20));
            if (!File.Exists(uploadTarget))
                throw new Exception("El host no creó el archivo subido.");
            if (new FileInfo(uploadTarget).Length != sourceSize)
                throw new Exception("El tamaño del archivo subido no coincide.");
            if (await File.ReadAllTextAsync(uploadTarget) != content)
                throw new Exception("El contenido del archivo subido no coincide.");
            Console.WriteLine("Subida técnico → host verificada. OK");

            var downloadDone = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            client.TransferCompleted += (_, ok, error) =>
            {
                if (ok) downloadDone.TrySetResult(true);
                else downloadDone.TrySetException(new Exception(error ?? "Recepción incompleta."));
            };

            var pulled = await client.RequestFileFromHostAsync();
            if (!pulled)
                throw new Exception("El host rechazó la solicitud de archivo.");
            await downloadDone.Task.WaitAsync(TimeSpan.FromSeconds(20));
            if (!File.Exists(downloadTarget))
                throw new Exception("El cliente no guardó el archivo descargado.");
            if (await File.ReadAllTextAsync(downloadTarget) != content)
                throw new Exception("El contenido del archivo descargado no coincide.");
            Console.WriteLine("Descarga host → técnico verificada. OK");

            var hostSession = await WaitForSessionAsync(host);
            var chatFromClient = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var chatFromHost = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            hostSession.ChatReceived += (text, _) => chatFromClient.TrySetResult(text);
            client.ChatReceived += (text, _) => chatFromHost.TrySetResult(text);

            client.SendChat("Hola desde el técnico");
            if (await chatFromClient.Task.WaitAsync(TimeSpan.FromSeconds(10)) != "Hola desde el técnico")
                throw new Exception("El host no recibió el mensaje de chat.");
            await hostSession.SendChatAsync("Hola desde el host");
            if (await chatFromHost.Task.WaitAsync(TimeSpan.FromSeconds(10)) != "Hola desde el host")
                throw new Exception("El técnico no recibió la respuesta de chat.");
            Console.WriteLine("Chat bidireccional verificado. OK");

            await client.DisconnectAsync();

            if (useRelay)
            {
                await Task.Delay(800);
                var second = await ClientSession.ConnectRelayAsync(relayUrl!, host.Code, "SmokeTest2");
                var secondFrameTcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
                second.FrameReceived += bytes => secondFrameTcs.TrySetResult(bytes);
                await secondFrameTcs.Task.WaitAsync(TimeSpan.FromSeconds(20));
                second.SaveFileRequested = _ => Task.FromResult<string?>(downloadTarget);
                Console.WriteLine("Segunda sesión con el mismo ID del relay verificada. OK");
                await second.DisconnectAsync();
            }

            await WaitForSessionEndAsync(host);

            var filesTarget = Path.Combine(dir, "archivos_modo_solo.txt");
            host.SaveFileRequested = _ => Task.FromResult<string?>(filesTarget);
            var filesClient = useRelay
                ? await ClientSession.ConnectRelayAsync(relayUrl!, host.Code, "SmokeFiles", SessionMode.Files)
                : await ClientSession.ConnectLanAsync("127.0.0.1", host.LanPort, host.Code, "SmokeFiles", SessionMode.Files);

            try
            {
                var screenFrameSeen = false;
                filesClient.FrameReceived += _ => screenFrameSeen = true;
                if (filesClient.Mode != SessionMode.Files)
                    throw new Exception("La sesión no quedó en modo archivos.");
                if (filesClient.ScreenWidth != 0 || filesClient.ScreenHeight != 0)
                    throw new Exception("El modo archivos informó una resolución de pantalla.");

                var filesHostSession = await WaitForSessionAsync(host);
                if (filesHostSession.Mode != SessionMode.Files)
                    throw new Exception("El host no reconoció el modo archivos.");

                var filesChat = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                filesHostSession.ChatReceived += (text, _) => filesChat.TrySetResult(text);
                filesClient.SendChat("Chat sin compartir pantalla");
                if (await filesChat.Task.WaitAsync(TimeSpan.FromSeconds(10)) != "Chat sin compartir pantalla")
                    throw new Exception("El chat del modo archivos no llegó.");

                await filesClient.SendFileAsync(sourceFile);
                await WaitForFileAsync(filesTarget, sourceSize, 20);
                if (await File.ReadAllTextAsync(filesTarget) != content)
                    throw new Exception("El contenido del archivo en modo archivos no coincide.");

                await Task.Delay(1000);
                if (screenFrameSeen)
                    throw new Exception("El modo archivos recibió fotogramas de pantalla.");

                Console.WriteLine("Modo solo archivos verificado (sin pantalla, chat y transferencia). OK");
                await filesClient.DisconnectAsync();
                await WaitForSessionEndAsync(host);
            }
            finally
            {
                try { await filesClient.DisposeAsync(); } catch { }
            }

            Console.WriteLine("PRUEBA COMPLETA: OK");
        }
        finally
        {
            if (client is not null)
            {
                try { await client.DisposeAsync(); } catch { }
            }
            try { await host.StopAsync(); } catch { }
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    private static async Task<HostSession> WaitForSessionAsync(HostController host)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            var session = host.ActiveSession;
            if (session is not null) return session;
            await Task.Delay(50);
        }
        throw new Exception("El host no registró la sesión activa.");
    }

    private static async Task WaitForSessionEndAsync(HostController host)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (host.ActiveSession is null) return;
            await Task.Delay(50);
        }
        throw new Exception("El host no liberó la sesión anterior.");
    }

    private static async Task WaitForFileAsync(string path, long expectedSize, int timeoutSeconds)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(path) && new FileInfo(path).Length == expectedSize) return;
            await Task.Delay(100);
        }
        throw new Exception("No llegó el archivo esperado: " + Path.GetFileName(path));
    }
}
