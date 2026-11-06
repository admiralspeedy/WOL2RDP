using System.Diagnostics;
using System.Drawing;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace WOL2RDP;

internal static class Program
{
    // WinForms uses a single-threaded apartment for its UI and Windows dialogs.
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new WakeForm());
    }
}

internal sealed class WakeForm : Form
{
    // UDP port 9 is the conventional Wake-on-LAN discard port. The listener
    // identifies the VM by MAC address inside the packet, not by the UDP port.
    private const int WolUdpPort = 9;
    // Give the VM time to boot and bring GNOME Remote Login online.
    private static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(120);

    // Form controls remain on the UI thread; awaited network calls keep the
    // indeterminate progress indicator responsive while we wait for Ubuntu.
    private readonly Label status = new();
    private readonly Label details = new();
    private readonly ProgressBar spinner = new();
    private readonly Button cancel = new();
    private readonly CancellationTokenSource cancellation = new();

    // Construct the small, fixed-size connection window programmatically;
    // no WinForms designer-generated files are needed in the repository.
    public WakeForm()
    {
        Text = "WOL2RDP";
        ClientSize = new Size(380, 166);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = true;
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 10);

        status.SetBounds(22, 20, 334, 26);
        status.Text = "Preparing connection…";
        status.Font = new Font("Segoe UI", 11, FontStyle.Bold);

        details.SetBounds(22, 52, 334, 38);
        details.Text = "Reading configuration…";
        details.ForeColor = SystemColors.GrayText;

        spinner.SetBounds(22, 102, 334, 15);
        // Marquee represents an unknown boot duration, not a percentage.
        spinner.Style = ProgressBarStyle.Marquee;
        spinner.MarqueeAnimationSpeed = 25;

        cancel.SetBounds(252, 127, 104, 29);
        cancel.Text = "Cancel";
        cancel.Click += (_, _) => { cancellation.Cancel(); cancel.Enabled = false; };

        Controls.AddRange(new Control[] { status, details, spinner, cancel });
        // Start after the window appears so the user sees progress immediately.
        Shown += async (_, _) => await RunAsync();
        FormClosing += (_, _) => cancellation.Cancel();
    }

    // Main sequence: load configuration -> read the signed RDP profile ->
    // send WoL -> poll for the RDP TCP listener -> launch mstsc.exe.
    private async Task RunAsync()
    {
        try
        {
            HelperConfig config = await LoadConfigAsync(cancellation.Token);
            // Keep all user-editable files next to the executable, regardless
            // of the current working directory or how the app is launched.
            string rdpFile = Path.Combine(AppContext.BaseDirectory, config.RdpFile);
            if (!File.Exists(rdpFile))
                throw new FileNotFoundException($"RDP file '{config.RdpFile}' was not found beside the application.", rdpFile);

            // File.ReadAllLines detects the UTF-16 BOM used by signed RDP profiles.
            string[] lines = await File.ReadAllLinesAsync(rdpFile, cancellation.Token);
            (string host, int port) = ReadDestination(lines);

            status.Text = "Sending wake packet…";
            details.Text = $"WoL → {config.WolDestination}";
            // WoL does not tell us when the guest is ready. We send the packet
            // once, then independently wait for the RDP endpoint to accept TCP.
            SendWol(config.MacAddress, config.WolDestination, WolUdpPort);

            status.Text = "Waiting for RDP…";
            Stopwatch watch = Stopwatch.StartNew();
            // Poll repeatedly rather than sleeping a fixed number of seconds:
            // an already-running VM connects quickly, while a cold boot gets
            // the full MaxWait deadline.
            while (watch.Elapsed < MaxWait)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                details.Text = $"Checking {host}:{port}  •  {Math.Max(0, (int)(MaxWait - watch.Elapsed).TotalSeconds)}s left";
                using var client = new TcpClient();
                try
                {
                    using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
                    // Limit each attempt so an unreachable endpoint cannot
                    // hang the UI or consume the entire 120-second deadline.
                    attempt.CancelAfter(TimeSpan.FromSeconds(2));
                    await client.ConnectAsync(host, port, attempt.Token);
                    if (client.Connected)
                    {
                        status.Text = "RDP is ready!";
                        details.Text = "Opening your signed RDP connection…";
                        spinner.Style = ProgressBarStyle.Continuous;
                        spinner.Value = 100;
                        // Open the ORIGINAL RDP file as-is. Its settings and
                        // digital signature are never rewritten by this app.
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = Path.Combine(Environment.SystemDirectory, "mstsc.exe"),
                            UseShellExecute = false,
                            ArgumentList = { rdpFile }
                        });
                        Close();
                        return;
                    }
                }
                catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
                {
                    // Expected while the guest is starting.
                }
                catch (SocketException)
                {
                    // Expected while the guest is starting.
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException)
                {
                    // A transient network failure: try again until the deadline.
                }

                // Respect cancellation between attempts as well.
                await Task.Delay(TimeSpan.FromSeconds(2), cancellation.Token);
            }
            throw new TimeoutException($"No RDP connection at {host}:{port} after {(int)MaxWait.TotalSeconds} seconds. The wake packet was sent; verify the target is online and check the address in your RDP file.");
        }
        // Cancel closes the form without reporting a connection failure.
        catch (OperationCanceledException)
        {
            Close();
        }
        catch (Exception ex)
        {
            spinner.Style = ProgressBarStyle.Continuous;
            spinner.Value = 0;
            status.Text = "Could not connect";
            details.Text = ex.Message;
            details.ForeColor = Color.Firebrick;
            details.AutoEllipsis = true;
            cancel.Enabled = true;
            cancel.Text = "Close";
            cancel.Click += (_, _) => Close();
            MessageBox.Show(this, ex.Message, "WOL2RDP", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    // Parse a deliberately simple key=value file. Blank lines and lines
    // beginning with # or ; are comments. All three keys are required.
    private static async Task<HelperConfig> LoadConfigAsync(CancellationToken token)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "config.cfg");
        if (!File.Exists(path))
            throw new FileNotFoundException("Missing config.cfg. Put it beside WOL2RDP.exe.", path);

        string[] lines = await File.ReadAllLinesAsync(path, token);
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < lines.Length; index++)
        {
            string line = lines[index].Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith(';'))
                continue;

            // Split on the FIRST = so a value can contain later = characters.
            int separator = line.IndexOf('=');
            if (separator < 1)
                throw new InvalidDataException($"Invalid config.cfg line {index + 1}: expected key=value.");

            string key = line[..separator].Trim();
            string value = line[(separator + 1)..].Trim();
            if (!new[] { "wolDestination", "macAddress", "rdpFile" }.Contains(key, StringComparer.OrdinalIgnoreCase))
                throw new InvalidDataException($"Unknown config.cfg setting '{key}' on line {index + 1}.");
            if (!values.TryAdd(key, value))
                throw new InvalidDataException($"Duplicate config.cfg setting '{key}' on line {index + 1}.");
        }

        string Setting(string key) => values.TryGetValue(key, out string? value) ? value : "";
        var config = new HelperConfig
        {
            WolDestination = Setting("wolDestination"),
            MacAddress = Setting("macAddress"),
            RdpFile = Setting("rdpFile")
        };
        if (string.IsNullOrWhiteSpace(config.WolDestination) ||
            string.IsNullOrWhiteSpace(config.MacAddress) || string.IsNullOrWhiteSpace(config.RdpFile))
            throw new InvalidDataException("config.cfg needs wolDestination, macAddress, and rdpFile.");

        // Use an explicit IPv4 destination, typically a subnet broadcast
        // address (for example 192.168.3.255 on a 192.168.0.0/22 LAN).
        if (!IPAddress.TryParse(config.WolDestination, out var dest) ||
            dest.AddressFamily != AddressFamily.InterNetwork)
            throw new InvalidDataException("wolDestination must be an IPv4 address.");

        string[] macParts = config.MacAddress.Split(':', '-');
        if (macParts.Length != 6 || macParts.Any(part => part.Length != 2 ||
            !byte.TryParse(part, System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out _)))
            throw new InvalidDataException("macAddress must be six hexadecimal octets, in AA:BB:CC:DD:EE:FF format.");

        // Only accept a filename next to the executable, not a directory
        // traversal or arbitrary filesystem path.
        if (config.RdpFile != Path.GetFileName(config.RdpFile) ||
            !config.RdpFile.EndsWith(".rdp", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("rdpFile must be an .rdp filename located beside the EXE.");

        return config;
    }

    // RDP profiles store settings as "name:type:value" lines. We need only
    // the server address and optional TCP port for the readiness probe; mstsc
    // will handle all other settings when it opens the original file.
    private static (string Host, int Port) ReadDestination(string[] lines)
    {
        string? address = null;
        int port = 3389; // Standard RDP port if the profile omits one.
        foreach (string raw in lines)
        {
            string line = raw.Trim();
            if (line.StartsWith("full address:s:", StringComparison.OrdinalIgnoreCase))
                address = line["full address:s:".Length..].Trim();
            else if (line.StartsWith("server port:i:", StringComparison.OrdinalIgnoreCase))
            {
                string value = line["server port:i:".Length..].Trim();
                if (!int.TryParse(value, out port) || port is < 1 or > 65535)
                    throw new InvalidDataException("Invalid server port in RDP file.");
            }
        }
        if (string.IsNullOrWhiteSpace(address))
            throw new InvalidDataException("The configured RDP file must contain a 'full address:s:' setting.");

        // An explicit hostname:port overrides the separate server port setting.
        Match match = Regex.Match(address, @"^(.+):(\d+)$");
        if (match.Success && !address.StartsWith('['))
        {
            address = match.Groups[1].Value;
            if (!int.TryParse(match.Groups[2].Value, out port) || port is < 1 or > 65535)
                throw new InvalidDataException("Invalid port in RDP destination.");
        }
        if (address.Contains(' ') || address.Contains('\r') || address.Contains('\n'))
            throw new InvalidDataException("Invalid hostname in RDP file.");
        return (address, port);
    }

    // A WoL magic packet is six bytes of FF followed by sixteen copies of
    // the target six-byte MAC. Unraid's WoL listener then maps that MAC to
    // the configured VM and starts it.
    private static void SendWol(string mac, string broadcast, int port)
    {
        string[] components = mac.Split(':', '-');
        if (components.Length != 6) throw new FormatException("Invalid WoL MAC address.");
        byte[] hardware = components.Select(v => Convert.ToByte(v, 16)).ToArray();
        byte[] magic = new byte[102]; // 6 + (16 * 6) bytes.
        Array.Fill<byte>(magic, 0xff, 0, 6);
        for (int i = 0; i < 16; i++) Buffer.BlockCopy(hardware, 0, magic, 6 + i * 6, 6);
        using var udp = new UdpClient();
        // Required to send to a subnet broadcast address.
        udp.EnableBroadcast = true;
        udp.Send(magic, magic.Length, new IPEndPoint(IPAddress.Parse(broadcast), port));
    }
}

// In-memory representation of the three supported config.cfg entries.
internal sealed class HelperConfig
{
    public string WolDestination { get; set; } = "";
    public string MacAddress { get; set; } = "";
    public string RdpFile { get; set; } = "";
}
