using System.Globalization;
using System.Text.Json;
using System.Threading.Channels;
using Llampec.Devices.Logitech;
using Llampec.Platform;

return await Probe.RunAsync(args);

internal static class Probe
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static async Task<int> RunAsync(string[] args)
    {
        using var stop = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; stop.Cancel(); };
        Console.CancelKeyPress += cancel;
        try
        {
            var options = Options.Parse(args);
            if (options.Command == "help") { PrintHelp(); return 0; }
            // Parse the shortcut before opening or configuring any hardware.
            var shortcut = options.Shortcut is null ? null : new KeyboardShortcut(options.Shortcut);
            var devices = WindowsHidTransport.EnumerateLogitech();
            if (options.Command == "list")
            {
                Console.WriteLine(JsonSerializer.Serialize(devices.Select((device, index) => new { Index = index, Device = device }), JsonOptions));
                return 0;
            }
            if (devices.Count == 0)
                throw new IOException("No present Logitech vendor HID collection was found. Wake/connect the mouse and run 'list' again.");
            if (options.Device is null && devices.Count != 1)
                throw new ArgumentException($"Found {devices.Count} collections. Run 'list', then choose --device N explicitly.");
            int selected = options.Device ?? 0;
            if (selected < 0 || selected >= devices.Count) throw new ArgumentException("--device is outside the current 'list' indices.");
            HidDeviceInfo info = devices[selected];
            Console.WriteLine($"Opening {info.ProductName ?? "Logitech"} (PID {info.ProductId:X4}), usage {info.UsagePage:X4}:{info.Usage:X4}, device index {options.Slot:X2}.");
            await using var client = new HidppClient(WindowsHidTransport.Open(info), options.Slot);
            await using var device = await LogitechDevice.CreateAsync(client, stop.Token);
            PrintState(device, full: options.Command == "inspect");
            if (options.Command == "inspect") return 0;

            // Bounded event delivery: never execute a keyboard action on the HID
            // reader, which also has to service restoration and settings replies.
            var presses = Channel.CreateBounded<bool>(new BoundedChannelOptions(64)
            {
                SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.DropOldest
            });
            try
            {
                if (options.Dpi is { } dpi) await device.SetDpiAsync(dpi, stop.Token);
                if (options.WheelMode is { } mode)
                {
                    byte modeByte = mode == "free" ? (byte)1 : (byte)2;
                    byte threshold = mode switch { "free" => 0, "ratchet" => 255, _ => options.Threshold ?? 25 };
                    await device.SetSmartShiftAsync(modeByte, threshold, stop.Token);
                }
                if (options.InvertVertical is not null || options.InvertHorizontal is not null)
                    await device.SetWheelInversionAsync(options.InvertVertical, options.InvertHorizontal, stop.Token);
                if (options.WatchThumb || shortcut is not null)
                    await device.DivertThumbAsync(pressed => presses.Writer.TryWrite(pressed), ct: stop.Token);

                Console.WriteLine("Applied state:");
                PrintState(device, full: false);
                Console.WriteLine($"Trial active for {options.Seconds} seconds. Ctrl+C restores the saved settings and exits.");
                if (options.WatchThumb || shortcut is not null)
                    Console.WriteLine($"Press the physical thumb button. Action: {shortcut?.Text ?? "log press/release only"}.");
                using var trial = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                trial.CancelAfter(TimeSpan.FromSeconds(options.Seconds));
                int downCount = 0, upCount = 0;
                try
                {
                    while (!trial.IsCancellationRequested)
                    {
                        var ready = presses.Reader.WaitToReadAsync(trial.Token).AsTask();
                        if (await Task.WhenAny(ready, client.Completion) == client.Completion)
                            throw new IOException("The mouse connection ended during the trial. Reconnect and run inspect before retrying.", client.ConnectionError);
                        if (!await ready) break;
                        while (presses.Reader.TryRead(out bool pressed))
                        {
                            Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} thumb {(pressed ? "down" : "up")}");
                            if (pressed) { downCount++; shortcut?.Send(); } else upCount++;
                        }
                    }
                }
                catch (OperationCanceledException) when (trial.IsCancellationRequested) { }
                Console.WriteLine($"Observed thumb transitions: {downCount} down, {upCount} up.");
            }
            finally
            {
                Console.WriteLine("Restoring settings changed by this trial...");
                using var restore = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                await device.RestoreAsync(restore.Token);
                Console.WriteLine("Restoration verified.");
            }
            return 0;
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { return 130; }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.ToString());
            return 1;
        }
        finally { Console.CancelKeyPress -= cancel; }
    }

    private static void PrintState(LogitechDevice device, bool full)
    {
        if (full) { Console.WriteLine(JsonSerializer.Serialize(device, JsonOptions)); return; }
        Console.WriteLine($"{device.Name}; DPI={device.Dpi?.Current.ToString() ?? "unavailable"}; "
            + $"wheel mode={device.SmartShift?.Mode}, threshold={device.SmartShift?.Threshold}; "
            + $"vertical invert={(device.VerticalWheelMode is byte mode ? ((mode & 4) != 0).ToString() : "unavailable")}; "
            + $"horizontal invert={device.HorizontalWheelState?.Inverted.ToString() ?? "unavailable"}.");
    }

    private static void PrintHelp() => Console.WriteLine("""
        Llampec Logitech hardware prototype (Windows ARM64 / x64)
          list
          inspect [--device N] [--slot 255]
          trial [--device N] [--slot 255] [--seconds 30]
                [--dpi 1200] [--watch-thumb | --thumb-shortcut Win+Tab]
                [--wheel-mode ratchet|free|auto] [--smartshift-threshold 25]
                [--invert-vertical true|false] [--invert-horizontal true|false]

        list/inspect only query the device. trial restores changed settings on normal
        exit or Ctrl+C. Keep the mouse connected until restoration completes. Some
        wheel settings survive process termination; forced termination cannot restore.
        Close other HID++ configurators before a trial. Shortcuts target the foreground
        application; elevated windows can reject them. Slot 255 is direct Bluetooth;
        receiver slots 1-6 require an explicit --slot and are not yet hardware-validated.
        """);

    private sealed record Options(string Command, int? Device, byte Slot, int Seconds, ushort? Dpi,
        bool WatchThumb, string? Shortcut, string? WheelMode, byte? Threshold, bool? InvertVertical, bool? InvertHorizontal)
    {
        public static Options Parse(string[] args)
        {
            string command = args.Length == 0 ? "inspect" : args[0].ToLowerInvariant();
            if (command is "--help" or "-h" or "help") return new("help", null, 255, 30, null, false, null, null, null, null, null);
            if (command is not ("list" or "inspect" or "trial")) throw new ArgumentException("Expected list, inspect or trial. Use --help for syntax.");
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int index = 1; index < args.Length; index++)
            {
                string key = args[index];
                if (values.ContainsKey(key)) throw new ArgumentException($"Duplicate option: {key}");
                if (key == "--watch-thumb") { values.Add(key, "true"); continue; }
                if (key is not ("--device" or "--slot" or "--seconds" or "--dpi" or "--thumb-shortcut" or "--wheel-mode"
                    or "--smartshift-threshold" or "--invert-vertical" or "--invert-horizontal"))
                    throw new ArgumentException($"Unknown option: {key}");
                if (++index >= args.Length) throw new ArgumentException($"Missing value for {key}");
                values.Add(key, args[index]);
            }
            if (command != "trial" && values.Keys.Any(key => key is not ("--device" or "--slot")))
                throw new ArgumentException("Mutation/watch options require the explicit 'trial' command.");
            string? Value(string name) => values.GetValueOrDefault(name);
            int? Integer(string name) => Value(name) is { } value ? int.Parse(value, NumberStyles.None, CultureInfo.InvariantCulture) : null;
            bool? Boolean(string name) => Value(name) is { } value ? bool.Parse(value) : null;
            int slot = Integer("--slot") ?? 255, seconds = Integer("--seconds") ?? 30;
            if (slot != 255 && slot is not (>= 1 and <= 6)) throw new ArgumentException("--slot must be 255 (direct) or 1-6 (receiver).");
            if (seconds is < 1 or > 3600) throw new ArgumentException("--seconds must be 1-3600.");
            string? mode = Value("--wheel-mode");
            if (mode is not (null or "ratchet" or "free" or "auto")) throw new ArgumentException("--wheel-mode must be ratchet, free or auto.");
            int? threshold = Integer("--smartshift-threshold");
            if (threshold is not null && (threshold is < 1 or > 50 || mode != "auto"))
                throw new ArgumentException("--smartshift-threshold must be 1-50 and requires --wheel-mode auto.");
            int? dpi = Integer("--dpi");
            if (dpi is not null && dpi is not (> 0 and <= ushort.MaxValue)) throw new ArgumentException("Invalid DPI value.");
            return new(command, Integer("--device"), (byte)slot, seconds, (ushort?)dpi,
                values.ContainsKey("--watch-thumb"), Value("--thumb-shortcut"), mode, (byte?)threshold,
                Boolean("--invert-vertical"), Boolean("--invert-horizontal"));
        }
    }
}
