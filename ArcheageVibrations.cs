using System.Text;
using Buttplug.Client;

namespace ArcheageVibrations
{
    internal class Program
    {
        sealed class Pulse
        {
            public double Amount { get; init; }            // 0..1 contribution
            public DateTimeOffset ExpiresAt { get; init; } // when this pulse stops contributing
        }

        static async Task Main()
        {
            // Runtime config
            var serverUri = "ws://127.0.0.1:12345";
            var maxIntensity = 1.0;

            // Client setup
            var client = new ButtplugClient("ArcheageVibrations");

            client.DeviceAdded += (_, e) => Console.WriteLine($"Device connected: {e.Device.Name}");

            client.ServerDisconnect += (_, _) =>
            {
                Console.WriteLine("Intiface disconnected. Make sure the intiface central server is running and restart");
                Environment.Exit(0); // This is shit but I can't be fucked to clean up all the tasks and pause
                return;
            };

            bool hasShownConnectionError = false;

            while (true)
            {
                try
                {
                    var connector = new ButtplugWebsocketConnector(new Uri(serverUri));
                    await client.ConnectAsync(connector);
                    Console.WriteLine("Connected to Buttplug server.");
                    break;
                }
                catch
                {
                    if (!hasShownConnectionError)
                    {
                        hasShownConnectionError = true;
                        Console.WriteLine($"Error connecting to server {serverUri}.");
                        Console.WriteLine("Is Intiface Central running? Or on a different address?");
                        Console.Write("Attempting to connect..");
                    }
                    else
                    {
                        Console.Write(".");
                    }
                    await Task.Delay(1000);
                }
            }

            hasShownConnectionError = false;

            // Outer lifetime loop
            while (true)
            {
                // Wait for a device
                ButtplugClientDevice? device = null;
                Console.WriteLine("Searching for devices..");
                while (device == null)
                {
                    try
                    {
                        await client.StartScanningAsync();
                        await Task.Delay(2000);
                        await client.StopScanningAsync();
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error scanning for devices: {ex.Message}");
                        await Task.Delay(1000);
                    }

                    device = client.Devices.FirstOrDefault();
                    if (device == null)
                    {
                        Console.Write(".");
                        await Task.Delay(1000);
                    }
                }

                Console.WriteLine($"Using device: {device.Name}");

                // Session state
                var pulses = new List<Pulse>();
                var pulsesLock = new object();
                double lastSent = -1;
                const double EPS = 0.01;
                using var sessionCts = new CancellationTokenSource();

                // End this session if device disconnects
                client.DeviceRemoved += OnRemoved;
                void OnRemoved(object? s, dynamic e)
                {
                    try
                    {
                        if (e.Device.Index == device.Index)
                        {
                            Console.WriteLine($"Device removed: {e.Device.Name} (ending session)");
                            sessionCts.Cancel();
                        }
                    }
                    catch
                    {
                        sessionCts.Cancel();
                    }
                }

                var mixerTask = Task.Run(async () =>
                {
                    while (!sessionCts.IsCancellationRequested)
                    {
                        double sum = 0;
                        var now = DateTimeOffset.UtcNow;

                        lock (pulsesLock)
                        {
                            // drop expired
                            for (int i = pulses.Count - 1; i >= 0; i--)
                            {
                                if (pulses[i].ExpiresAt <= now)
                                    pulses.RemoveAt(i);
                            }
                            for (int i = 0; i < pulses.Count; i++)
                                sum += pulses[i].Amount;
                        }

                        var target = Math.Clamp(sum, 0, maxIntensity);

                        if (lastSent < 0 || Math.Abs(target - lastSent) >= EPS)
                        {
                            try
                            {
                                await device.VibrateAsync(target);
                                lastSent = target;
                            }
                            catch
                            {
                                // If the device disappears, bail out to rescan.
                                sessionCts.Cancel();
                                break;
                            }
                        }

                        try { await Task.Delay(50, sessionCts.Token); }
                        catch (OperationCanceledException) { break; }
                    }
                }, sessionCts.Token);

                // Run the command/file loop for this session
                try
                {
                    await RunCommandLoopAsync(client, device, pulses, pulsesLock, () => { lastSent = -1; }, sessionCts.Token);
                }
                catch (OperationCanceledException)
                {
                    // session canceled (device removed or vibrate failed)
                }
                finally
                {
                    // Cleanup for this device session
                    client.DeviceRemoved -= OnRemoved;
                    sessionCts.Cancel();
                    try { await mixerTask; } catch { }
                    try { await client.StopAllDevicesAsync(); } catch { }
                }

                Console.WriteLine("Returning to device search...");
            }
        }

        private static async Task RunCommandLoopAsync(
            ButtplugClient client,
            ButtplugClientDevice device,
            List<Pulse> pulses,
            object pulsesLock,
            Action onStop,
            CancellationToken ct)
        {
            // Resolve mailbox path
            string doc = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            string dir = Path.Combine(doc, "AAClassic", "Addon", "auto_vibrator");
            if (!Directory.Exists(dir))
            {
                Console.WriteLine("Addon path not found. Is the addon installed? or is your addon folder not default?");
                return;
            }

            string path = Path.Combine(dir, "aac-av-mailbox.txt");
            Console.WriteLine($"Ready and awaiting addon output..");
            try { File.WriteAllText(path, ""); } catch { }

            while (!ct.IsCancellationRequested)
            {
                string payload = "";

                // Read
                try
                {
                    using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var sr = new StreamReader(fs, Encoding.UTF8);
                    payload = sr.ReadToEnd();
                }
                catch
                {
                    // file transiently unavailable
                }

                if (!string.IsNullOrWhiteSpace(payload))
                {
                    payload = payload.Trim();
                    if (payload.Length > 0)
                    {
                        // If first char is a sentinel, drop it
                        if (!char.IsLetterOrDigit(payload[0]) && payload.Length > 1)
                            payload = payload.Substring(1);
                    }

                    var parts = payload.Split('|');
                    var tag = parts[0];

                    try
                    {
                        switch (tag)
                        {
                            case "VIBE":
                                float.TryParse(SafeGet(parts, 1), out var intenPct);
                                int.TryParse(SafeGet(parts, 2).Trim('"'), out var durMs);
                                var amount = Math.Clamp(intenPct, 0f, 1f);

                                var pulse = new Pulse
                                {
                                    Amount = amount,
                                    ExpiresAt = DateTimeOffset.UtcNow.AddMilliseconds(Math.Max(0, durMs))
                                };
                                lock (pulsesLock) { pulses.Add(pulse); }

                                Console.WriteLine($"Pulse +{amount:P0} for {(float)durMs / 1000f:0.###}s (stackable).");
                                break;

                            case "STOP":
                                lock (pulsesLock) { pulses.Clear(); }
                                try { await client.StopAllDevicesAsync(); } catch { }
                                onStop();
                                Console.WriteLine("Stopping all devices and clearing pulses.");
                                break;

                            case "CONF":
                                // TODO: parse config
                                break;

                            default:
                                Console.WriteLine($"Unknown command: {payload}");
                                break;
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Command error: {ex.Message}");
                    }
                    finally
                    {
                        try { File.WriteAllText(path, ""); } catch { }
                    }
                }

                try { await Task.Delay(20, ct); }
                catch (OperationCanceledException) { break; }
            }

            static string SafeGet(string[] arr, int idx) => (idx >= 0 && idx < arr.Length) ? arr[idx] : "";
        }
    }
}
