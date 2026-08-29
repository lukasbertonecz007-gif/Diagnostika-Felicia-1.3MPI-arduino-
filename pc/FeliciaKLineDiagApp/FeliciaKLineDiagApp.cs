using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.IO.Ports;
using System.Runtime.InteropServices;
using System.Security.Permissions;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace FeliciaKLineDiagApp
{
    static class Program
    {
        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        [STAThread]
        static void Main()
        {
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) =>
            {
                try
                {
                    File.AppendAllText("error_crash.log", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " [UI Exception] " + e.Exception + "\r\n");
                }
                catch { }
            };
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                try
                {
                    File.AppendAllText("error_crash.log", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " [Domain Exception] " + e.ExceptionObject + "\r\n");
                }
                catch { }
            };

            try
            {
                if (Environment.OSVersion.Version.Major >= 6)
                {
                    SetProcessDPIAware();
                }
            }
            catch { }

            try
            {
                string appName = Path.GetFileName(Application.ExecutablePath);
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Internet Explorer\Main\FeatureControl\FEATURE_BROWSER_EMULATION"))
                {
                    if (key != null)
                    {
                        key.SetValue(appName, 11001, RegistryValueKind.DWord);
                    }
                }
            }
            catch { }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }

    [ComVisible(true)]
    [PermissionSet(SecurityAction.Demand, Name = "FullTrust")]
    public class MainForm : Form
    {
        private readonly WebBrowser browser;
        private SerialPort port;
        private readonly object portLock = new object();
        private readonly System.Windows.Forms.Timer timer;
        private readonly Queue<string> lines = new Queue<string>();
        private readonly StringBuilder rxBuffer = new StringBuilder();
        private long totalBytes = 0;
        private long totalLines = 0;
        private string lastLine = "";
        private bool ready = false;
        private Thread readThread = null;
        private volatile bool isReading = false;

        public MainForm()
        {
            Text = "FeliciaDiag V0.5.1 - Škoda Felicia 1.3 MPI Diagnostika";
            Size = new Size(1440, 960);
            MinimumSize = new Size(1180, 760);
            StartPosition = FormStartPosition.CenterScreen;
            WindowState = FormWindowState.Maximized;
            BackColor = Color.FromArgb(15, 23, 42);

            browser = new WebBrowser
            {
                Dock = DockStyle.Fill,
                IsWebBrowserContextMenuEnabled = false,
                WebBrowserShortcutsEnabled = false,
                AllowWebBrowserDrop = false,
                ScriptErrorsSuppressed = true,
                ObjectForScripting = new Bridge(this),
                ScrollBarsEnabled = false
            };

            browser.DocumentCompleted += (s, e) =>
            {
                ready = true;
                RefreshPorts();
            };

            Controls.Add(browser);
            browser.DocumentText = Html;

            timer = new System.Windows.Forms.Timer { Interval = 30 };
            timer.Tick += (s, e) => ProcessLines();
            timer.Start();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            try
            {
                timer.Stop();
                ClosePort();
            }
            catch { }
            base.OnFormClosing(e);
        }

        private static Dictionary<string, string> GetFriendlyPortNames()
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using (RegistryKey sc = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DEVICEMAP\SERIALCOMM"))
                {
                    if (sc != null)
                    {
                        foreach (string name in sc.GetValueNames())
                        {
                            string port = sc.GetValue(name) as string;
                            if (!string.IsNullOrEmpty(port))
                            {
                                string desc = name;
                                if (desc.StartsWith(@"\Device\", StringComparison.OrdinalIgnoreCase))
                                    desc = desc.Substring(8);
                                map[port] = desc;
                            }
                        }
                    }
                }
            }
            catch { }
            return map;
        }

        public string RefreshPorts()
        {
            try
            {
                string[] ports = SerialPort.GetPortNames();
                Array.Sort(ports);
                var friendly = GetFriendlyPortNames();
                List<string> list = new List<string>();
                foreach (string p in ports)
                {
                    string desc = friendly.ContainsKey(p) ? friendly[p] : "";
                    if (!string.IsNullOrEmpty(desc))
                        list.Add(p + "::" + p + " (" + desc + ")");
                    else
                        list.Add(p + "::" + p);
                }
                string joined = string.Join("|", list);
                string pref = ports.Length > 0 ? ports[0] : "COM6";
                Js("setPorts", joined, pref);
                return joined;
            }
            catch (Exception ex)
            {
                Status("Chyba při hledání portů: " + ex.Message, "bad");
                return "";
            }
        }

        public void Connect(string portName, string baudStr)
        {
            Disconnect();

            if (string.IsNullOrEmpty(portName)) portName = "COM6";
            if (portName.Contains(" ")) portName = portName.Substring(0, portName.IndexOf(' '));
            if (portName.Contains(":")) portName = portName.Substring(0, portName.IndexOf(':'));
            portName = portName.Trim();

            int baud = 115200;
            int.TryParse(baudStr, out baud);
            if (baud <= 0) baud = 115200;

            try
            {
                lock (portLock)
                {
                    port = new SerialPort(portName, baud, Parity.None, 8, StopBits.One)
                    {
                        DtrEnable = true,
                        RtsEnable = false, // CRITICAL FOR ESP32: RTS=false allows CPU to run instead of bootloader reset!
                        Handshake = Handshake.None,
                        Encoding = Encoding.UTF8,
                        ReadTimeout = 1500,
                        WriteTimeout = 1500,
                        ReadBufferSize = 16384,
                        WriteBufferSize = 16384
                    };

                    port.Open();
                    try { port.DiscardInBuffer(); } catch { }
                    try { port.DiscardOutBuffer(); } catch { }
                }

                isReading = true;
                readThread = new Thread(ReadLoop)
                {
                    IsBackground = true,
                    Name = "SerialReader"
                };
                readThread.Start();

                Js("setConnected", "1", portName, baud.ToString());
                Status("Připojeno k " + portName + " (" + baud + " bd). ESP32 / Arduino je připraveno.", "good");
                Enqueue("[APP] Otevřen port " + portName + " (" + baud + " bd)");

                Thread t = new Thread(() =>
                {
                    try
                    {
                        Thread.Sleep(200);
                        SendCommand("\n");
                        Thread.Sleep(100);
                        SendCommand("?");
                    }
                    catch { }
                })
                {
                    IsBackground = true
                };
                t.Start();
            }
            catch (Exception ex)
            {
                Disconnect();
                Status("Nelze otevřít " + portName + ": " + ex.Message, "bad");
                Enqueue("[APP] Chyba otevření portu: " + ex.Message);
            }
        }

        private void ReadLoop()
        {
            while (isReading)
            {
                try
                {
                    SerialPort p = port;
                    if (p != null && p.IsOpen && p.BytesToRead > 0)
                    {
                        string text = p.ReadExisting();
                        if (!string.IsNullOrEmpty(text))
                        {
                            totalBytes += text.Length;
                            lock (rxBuffer)
                            {
                                rxBuffer.Append(text);
                                string all = rxBuffer.ToString();
                                int lastNl = all.LastIndexOf('\n');
                                if (lastNl >= 0)
                                {
                                    string full = all.Substring(0, lastNl);
                                    rxBuffer.Remove(0, lastNl + 1);

                                    string[] split = full.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.RemoveEmptyEntries);
                                    lock (lines)
                                    {
                                        foreach (string line in split)
                                        {
                                            if (!string.IsNullOrEmpty(line))
                                            {
                                                totalLines++;
                                                lastLine = line;
                                                lines.Enqueue(line);
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
                catch { }
                Thread.Sleep(10);
            }
        }

        public void ResetDevice()
        {
            lock (portLock)
            {
                if (port != null && port.IsOpen)
                {
                    try
                    {
                        // Pulse DTR / RTS to trigger hardware reset
                        port.DtrEnable = false;
                        port.RtsEnable = true;
                        Thread.Sleep(100);
                        port.DtrEnable = true;
                        port.RtsEnable = false;
                        Status("Odeslán HW reset signál (DTR/RTS)...", "busy");
                        Enqueue("[APP] Odeslán hardware reset do mikrokontroléru.");
                        Thread.Sleep(400);
                        SendCommand("?");
                    }
                    catch (Exception ex)
                    {
                        Status("Chyba při HW resetu: " + ex.Message, "bad");
                    }
                }
                else
                {
                    Status("Port není připojen.", "bad");
                }
            }
        }

        private void StopReader()
        {
            isReading = false;
            Thread thread = readThread;
            readThread = null;

            if (thread != null && thread != Thread.CurrentThread)
            {
                try
                {
                    if (thread.IsAlive) thread.Join(500);
                }
                catch { }
            }
        }

        public void Disconnect()
        {
            StopReader();
            lock (portLock)
            {
                try
                {
                    if (port != null)
                    {
                        if (port.IsOpen)
                        {
                            port.DiscardInBuffer();
                            port.DiscardOutBuffer();
                            port.Close();
                        }
                        port.Dispose();
                    }
                }
                catch { }
                port = null;
            }

            Js("setConnected", "0", "", "");
            Status("Odpojeno.", "idle");
            Enqueue("[APP] Port byl odpojen.");
        }

        public void AutoConnect()
        {
            string[] ports = SerialPort.GetPortNames();
            if (ports.Length == 0)
            {
                Status("Nebyl nalezen žádný COM port.", "bad");
                return;
            }

            Array.Sort(ports);
            Connect(ports[0], "115200");
        }

        public void SendCommand(string cmd)
        {
            lock (portLock)
            {
                if (port == null || !port.IsOpen)
                {
                    Status("Port není připojen! Nejprve klikni na Připojit.", "bad");
                    return;
                }

                try
                {
                    port.Write(cmd);
                    Status("Odeslán příkaz: " + cmd, "busy");
                    Enqueue("[TX] " + cmd);
                }
                catch (Exception ex)
                {
                    Status("Chyba odeslání příkazu: " + ex.Message, "bad");
                    Enqueue("[APP] Chyba zápisu: " + ex.Message);
                }
            }
        }

        public void ClearLog()
        {
            lock (lines) { lines.Clear(); }
            lock (rxBuffer) { rxBuffer.Length = 0; }
            totalBytes = 0;
            totalLines = 0;
            lastLine = "";
            Js("setRxStats", "0", "0", "--", "--");
        }

        private void ProcessLines()
        {
            int count = 0;
            while (count < 40)
            {
                string line = null;
                lock (lines)
                {
                    if (lines.Count > 0) line = lines.Dequeue();
                }
                if (line == null) break;
                count++;

                Js("appendLog", line);
                Parse(line);
            }

            string preview = lastLine;
            if (preview.Length > 55) preview = preview.Substring(0, 52) + "...";
            Js("setRxStats", totalBytes.ToString(), totalLines.ToString(), lastLine, preview);
        }

        private void Parse(string line)
        {
            if (string.IsNullOrEmpty(line)) return;

            if (line.StartsWith("APP_BOOT|"))
            {
                Status("Arduino/ESP32 je připraveno.", "good");
                return;
            }

            if (line.StartsWith("APP_ID_FIELD|"))
            {
                string[] p = line.Split('|');
                if (p.Length >= 3)
                {
                    if (p[1] == "1") Js("setPart", p[2]);
                    else if (p[1] == "2") Js("setComponent", p[2]);
                    else if (p[1] == "3") Js("setExtra", p[2]);
                    else if (p[1] == "4") Js("setExtra4", p[2]);
                }
                return;
            }

            if (line.StartsWith("APP_ID_DONE"))
            {
                Status("Identifikace ECU byla úspěšně načtena z řídicí jednotky.", "good");
                return;
            }

            if (line.StartsWith("APP_LIVE_FIELD|"))
            {
                string[] p = line.Split('|');
                if (p.Length >= 7)
                {
                    Js("setLiveField", p[1], p[2], p[3], p[4], p[5], p[6]);
                    Status("Čtení měřicích bloků SIMOS 2P běží (vzorek #" + p[1] + ").", "good");
                }
                return;
            }

            if (line.StartsWith("APP_LIVE|"))
            {
                string[] p = line.Split('|');
                if (p.Length >= 5)
                {
                    string temp = p.Length > 5 ? p[5] : "--";
                    string inj = p.Length > 6 ? p[6] : "--";
                    string lambda = p.Length > 7 ? p[7] : "--";
                    Js("setLive", p[1], p[2], p[3], p[4], temp, inj, lambda);
                    Status("Čtení živých dat běží (vzorek #" + p[1] + ").", "good");
                }
                return;
            }

            if (line.StartsWith("APP_LIVE_DONE"))
            {
                Status("Cyklus čtení živých dat byl dokončen.", "good");
                Js("setLiveDone");
                return;
            }

            if (line.StartsWith("APP_ADP|"))
            {
                string[] p = line.Split('|');
                if (p.Length >= 2)
                {
                    if (p[1] == "OK") { Status("Základní nastavení škrticí klapky proběhlo úspěšně (ADP OK)!", "good"); Js("setAdpResult", "OK"); }
                    else if (p[1] == "REFUSED") { Status("ECU odmítla základní nastavení klapky (zkontroluj teplotu motoru >80°C a pedál v klidu).", "bad"); Js("setAdpResult", "REFUSED"); }
                    else { Status("Chyba při základním nastavení klapky (" + p[1] + ").", "bad"); Js("setAdpResult", p[1]); }
                }
                return;
            }

            if (line.StartsWith("APP_DTC|"))
            {
                string[] p = line.Split('|');
                if (p.Length >= 4) AddDtc(p[1], p[2], p[3]);
                return;
            }

            if (line == "APP_DTC_NONE")
            {
                Js("setNoFaults");
                Status("V paměti závad nejsou žádné chyby.", "good");
                return;
            }

            if (line == "APP_DTC_REFUSED")
            {
                Status("ECU odmítla čtení závad.", "bad");
                return;
            }

            if (line.StartsWith("APP_DTC_DONE|"))
            {
                Status("Čtení paměti závad dokončeno.", "good");
                return;
            }

            if (line.StartsWith("APP_CLEAR|"))
            {
                string[] p = line.Split('|');
                if (p.Length >= 2)
                {
                    if (p[1] == "OK") { Status("Paměť závad byla úspěšně smazána.", "good"); Js("setClearResult", "Závady smazány."); }
                    else if (p[1] == "REFUSED") Status("ECU odmítla smazání paměti závad.", "bad");
                    else if (p[1] == "TIMEOUT") Status("Timeout při mazání paměti závad.", "bad");
                }
                return;
            }

            if (line.StartsWith("APP_HW|"))
            {
                string[] p = line.Split('|');
                if (p.Length >= 2)
                {
                    Status(p[1] == "OK" ? "K-line převodník je 100% v pořádku!" : "Test převodníku selhal.", p[1] == "OK" ? "good" : "bad");
                }
                return;
            }

            if (line.IndexOf("timeout", StringComparison.OrdinalIgnoreCase) >= 0 || line.IndexOf("failed", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                Status("Komunikace hlásí timeout nebo chybu, koukni do raw logu.", "bad");
            }
        }

        private void AddDtc(string code, string raw, string status)
        {
            string title = Dtc.ContainsKey(code) ? Dtc[code] : "Neznámá VAG závada";
            Js("addDtc", code, title, "raw=" + raw + " status=0x" + status);
        }

        private void Status(string text, string level) { Js("setStatus", text, level); }
        private void Enqueue(string text) { lock (lines) { lines.Enqueue(text); } }

        private void Js(string name, params object[] args)
        {
            try
            {
                if (this.IsDisposed || !this.IsHandleCreated) return;
                if (this.InvokeRequired)
                {
                    this.BeginInvoke(new Action(() => Js(name, args)));
                    return;
                }

                if (ready && browser != null && !browser.IsDisposed && browser.Document != null)
                {
                    browser.Document.InvokeScript(name, args);
                }
            }
            catch { }
        }

        private void ClosePort()
        {
            StopReader();
            lock (portLock)
            {
                try
                {
                    if (port != null)
                    {
                        if (port.IsOpen)
                        {
                            port.DiscardInBuffer();
                            port.DiscardOutBuffer();
                            port.Close();
                        }
                        port.Dispose();
                    }
                }
                catch { }
                port = null;
            }
        }

        private static readonly Dictionary<string, string> Dtc = new Dictionary<string, string>
        {
            { "00513", "Snímač otáček motoru G28" },
            { "00518", "Potenciometr škrticí klapky G69" },
            { "00522", "Čidlo teploty chladicí kapaliny G62" },
            { "00533", "Regulace volnoběhu" },
            { "00537", "Lambda regulace" },
            { "00561", "Přizpůsobení směsi" },
            { "00668", "Napájecí napětí svorka 30" },
            { "01087", "Základní nastavení nebylo provedeno" },
            { "01165", "Jednotka škrticí klapky" },
            { "01247", "Ventil odvětrání nádrže N80" },
            { "16500", "P0116 čidlo teploty chladicí kapaliny" },
            { "16514", "P0130 lambda sonda" },
            { "16555", "P0171 směs příliš chudá" },
            { "16556", "P0172 směs příliš bohatá" },
            { "16684", "P0300 náhodné vynechávání zapalování" },
            { "16685", "P0301 vynechávání válec 1" },
            { "16686", "P0302 vynechávání válec 2" },
            { "16687", "P0303 vynechávání válec 3" },
            { "16688", "P0304 vynechávání válec 4" },
            { "16705", "P0321 snímač otáček motoru" },
            { "17978", "P1570 blokování startu imobilizérem" },
            { "65535", "Vnitřní chyba řídicí jednotky" }
        };

        private const string Html = @"<!doctype html>
<html>
<head>
<meta http-equiv=""Content-Type"" content=""text/html; charset=utf-8"">
<meta http-equiv=""X-UA-Compatible"" content=""IE=edge"">
<title>FeliciaDiag V0.5.1</title>
<style>
* { box-sizing: border-box; user-select: none; -webkit-user-select: none; }
body { margin: 0; font-family: 'Segoe UI', Tahoma, Arial, sans-serif; background: #0b1120; color: #f1f5f9; overflow: hidden; height: 100vh; font-size: 17px; }
.app { display: flex; height: 100vh; width: 100vw; overflow: hidden; }

/* LEVÝ NAVIGAČNÍ PANEL */
.nav { width: 330px; background: #131d31; color: #eaf0f7; padding: 22px 18px; box-sizing: border-box; display: flex; flex-direction: column; border-right: 2px solid #1e293b; flex-shrink: 0; }
.brand { font-size: 31px; font-weight: 800; color: #38bdf8; letter-spacing: -0.5px; }
.sub { font-size: 16px; color: #94a3b8; margin-top: 4px; margin-bottom: 24px; font-weight: 500; }
.nav-group { margin-bottom: 18px; }
.nav-title { font-size: 11px; text-transform: uppercase; letter-spacing: 1px; color: #64748b; font-weight: 700; margin-bottom: 8px; padding-left: 6px; }
.nav button { width: 100%; margin: 6px 0; padding: 15px 18px; border: 1px solid #334155; background: #1e293b; color: #f8fafc; text-align: left; border-radius: 8px; cursor: pointer; font-size: 17px; font-weight: 600; font-family: inherit; transition: all 0.15s ease; outline: none; }
.nav button:hover { background: #2563eb; border-color: #3b82f6; color: #ffffff; }
.nav button.active { background: #2563eb; border-color: #60a5fa; color: #ffffff; box-shadow: 0 4px 12px rgba(37,99,235,0.35); }
.nav-icon { margin-right: 10px; font-size: 18px; display: inline-block; vertical-align: middle; }

.status { margin-top: auto; padding: 14px; border: 2px solid #334155; background: #090e17; font-size: 14px; border-radius: 8px; word-break: break-word; font-weight: 600; line-height: 1.4; }
.good { border-color: #059669; color: #34d399; background: rgba(16,185,129,0.12); }
.bad { border-color: #dc2626; color: #f87171; background: rgba(239,68,68,0.12); }
.busy { border-color: #d97706; color: #fbbf24; background: rgba(245,158,11,0.12); }
.idle { border-color: #334155; color: #94a3b8; }

/* HLAVNÍ PRAVÁ ČÁST */
.main { flex: 1; display: flex; flex-direction: column; overflow: hidden; background: #090e17; height: 100vh; }
.top { background: #111a2e; border-bottom: 2px solid #1e293b; display: flex; align-items: center; justify-content: space-between; padding: 17px 28px; flex-shrink: 0; }
.top-title { font-size: 23px; font-weight: 800; color: #f8fafc; }
.top-sub { font-size: 15px; color: #94a3b8; margin-top: 2px; }

.port-bar { display: flex; align-items: center; gap: 10px; background: #1e293b; padding: 8px 14px; border-radius: 8px; border: 1px solid #334155; }
.port-bar label { font-size: 13px; font-weight: 700; color: #cbd5e1; }
.port-bar select { padding: 8px 12px; font-size: 15px; font-weight: 700; border-radius: 6px; border: 1px solid #475569; background: #0f172a; color: #f8fafc; cursor: pointer; min-width: 150px; font-family: inherit; }

.rx { background: #060911; color: #94a3b8; padding: 8px 24px; font-size: 13px; border-bottom: 1px solid #1e293b; font-weight: 500; flex-shrink: 0; }
.rx b { color: #f8fafc; font-size: 14px; }

/* ROLOVATELNÝ OBSAH AKTIVNÍ ZÁLOŽKY */
.content { flex: 1; overflow-y: auto; padding: 20px 24px; }
.panel { background: #111a2e; border: 1px solid #1e293b; border-radius: 10px; margin-bottom: 18px; box-shadow: 0 4px 6px -1px rgba(0, 0, 0, 0.2); }
.head { padding: 14px 18px; border-bottom: 1px solid #1e293b; font-size: 17px; font-weight: 700; background: #172238; color: #f8fafc; display: flex; align-items: center; justify-content: space-between; }
.body { padding: 20px; }

/* TLAČÍTKA A OVLÁDACÍ PRVKY */
.btn { padding: 13px 20px; margin: 4px; border: 1px solid #334155; background: #1e293b; color: #f1f5f9; border-radius: 6px; cursor: pointer; font-size: 17px; font-weight: 700; font-family: inherit; transition: all 0.15s ease; display: inline-block; vertical-align: middle; line-height: normal; text-align: center; white-space: nowrap; }
.btn:hover { background: #334155; color: #ffffff; border-color: #475569; }
.btn-lg { padding: 14px 22px; font-size: 15px; border-radius: 8px; }
.btn-primary { background: #2563eb; color: #ffffff; border-color: #1d4ed8; }
.btn-primary:hover { background: #1d4ed8; border-color: #1e40af; }
.btn-danger { background: #dc2626; color: #ffffff; border-color: #b91c1c; }
.btn-danger:hover { background: #b91c1c; border-color: #991b1b; }
.btn-success { background: #059669; color: #ffffff; border-color: #047857; }
.btn-success:hover { background: #047857; border-color: #065f46; }
.btn-warning { background: #d97706; color: #ffffff; border-color: #b45309; }
.btn-warning:hover { background: #b45309; border-color: #92400e; }

.btn-icon { display: inline-block; margin-right: 8px; font-size: 16px; vertical-align: -1px; line-height: 1; }

/* KARTY INFORMACÍ O ECU */
.info-table { width: 100%; border-collapse: collapse; margin-top: 6px; font-size: 16px; }
.info-table td { padding: 14px 16px; border-bottom: 1px solid #1e293b; }
.info-label { width: 260px; font-weight: 600; color: #94a3b8; font-size: 15px; }
.info-value { font-weight: 700; color: #38bdf8; font-size: 18px; }

/* KARTY ŽIVÝCH DAT (VERTIKÁLNÍ ŘAZENÍ) */
.live-item { background: #111a2e; border: 1px solid #1e293b; border-radius: 8px; padding: 14px 20px; margin-bottom: 12px; display: flex; align-items: center; justify-content: space-between; gap: 16px; transition: border-color 0.15s ease; }
.live-item:hover { border-color: #3b82f6; }
.live-item.verified { border-left: 5px solid #10b981; }
.live-item.unverified { border-left: 5px solid #f59e0b; }
.live-info { flex: 1; }
.live-title { font-size: 16px; font-weight: 700; color: #f8fafc; display: flex; align-items: center; gap: 8px; }
.live-meta { font-size: 13px; color: #94a3b8; margin-top: 3px; }
.live-val { font-size: 22px; font-weight: 800; color: #38bdf8; font-family: Consolas, monospace; min-width: 150px; text-align: right; }
.live-group { background: #111a2e; border: 1px solid #1e293b; border-radius: 8px; margin-bottom: 14px; overflow: hidden; }
.live-group-title { padding: 12px 16px; background: #172238; color: #f8fafc; font-size: 17px; font-weight: 700; border-bottom: 1px solid #1e293b; }
.live-row { display: flex; justify-content: space-between; gap: 18px; padding: 11px 16px; border-bottom: 1px solid #1e293b; }
.live-row:last-child { border-bottom: none; }
.live-row-label { color: #cbd5e1; font-size: 16px; font-weight: 600; }
.live-row-value { color: #38bdf8; font-family: Consolas, monospace; font-size: 17px; font-weight: 700; text-align: right; }
.legacy-live { display: none; }

.badge { display: inline-flex; align-items: center; gap: 4px; padding: 4px 10px; border-radius: 6px; font-size: 12px; font-weight: 700; }
.badge-verified { background: rgba(16,185,129,0.15); color: #34d399; border: 1px solid #059669; }
.badge-unverified { background: rgba(245,158,11,0.15); color: #fbbf24; border: 1px solid #d97706; }

/* KARTY KONFIGURACE A ADAPTACE */
.cfg-card { background: #0f172a; border: 1px solid #1e293b; border-radius: 8px; padding: 18px 20px; margin-bottom: 16px; }
.cfg-head { font-size: 17px; font-weight: 700; color: #f8fafc; margin-bottom: 6px; display: flex; align-items: center; justify-content: space-between; }
.cfg-desc { font-size: 14px; color: #94a3b8; line-height: 1.5; margin-bottom: 14px; }
.cfg-cond { background: #080d18; border: 1px solid #1e293b; border-radius: 6px; padding: 10px 14px; font-size: 13px; color: #cbd5e1; margin-bottom: 14px; }
.cfg-cond b { color: #fbbf24; }

/* CHYBOVÉ KÓDY (DTC) */
.dtc { border: 2px solid #ef4444; background: rgba(239,68,68,0.12); color: #fecaca; padding: 16px; margin: 12px 0; border-radius: 8px; font-family: Consolas, monospace; font-size: 16px; line-height: 1.5; }
.dtc-code { font-size: 20px; font-weight: 800; color: #f87171; margin-bottom: 4px; }
.dtc-desc { font-size: 16px; font-weight: 700; color: #ffffff; margin-bottom: 6px; }
.dtc-meta { font-size: 13px; color: #cbd5e1; }

.ok-box { border: 2px solid #10b981; background: rgba(16,185,129,0.12); color: #a7f3d0; padding: 18px 22px; border-radius: 8px; font-size: 17px; font-weight: 700; display: flex; align-items: center; gap: 12px; }
.empty-box { border: 1px dashed #475569; background: #0d1526; color: #94a3b8; padding: 22px; border-radius: 8px; text-align: center; font-size: 16px; }

/* SPODNÍ FIXNÍ KOMUNIKAČNÍ ZÁZNAMNÍK */
.bottom-log { flex-shrink: 0; background: #111a2e; border-top: 2px solid #1e293b; display: flex; flex-direction: column; }
.bottom-log-head { padding: 11px 20px; font-size: 16px; font-weight: 700; background: #172238; color: #f8fafc; display: flex; align-items: center; justify-content: space-between; border-bottom: 1px solid #1e293b; }
.log-fixed { height: 230px; margin: 0; background: #05080e; color: #38bdf8; font-family: Consolas, monospace; font-size: 16px; overflow-y: auto; padding: 14px 18px; white-space: pre-wrap; line-height: 1.55; border: none; border-radius: 0; }
</style>
</head>
<body>
<div class=""app"">
    <!-- LEVÁ NAVIGACE -->
    <div class=""nav"">
        <div class=""brand"">FeliciaDiag</div>
        <div class=""sub"">Verze V0.5.1 / ESP32 & Arduino</div>
        
        <div class=""nav-group"">
            <div class=""nav-title"">Diagnostika</div>
            <button id=""nav-main"" class=""active"" onclick=""show('main')""><span class=""nav-icon"">&#127968;</span> Hlavní přehled</button>
            <button id=""nav-faults"" onclick=""show('faults')""><span class=""nav-icon"">&#128269;</span> Paměť závad (02)</button>
            <button id=""nav-live"" onclick=""show('live')""><span class=""nav-icon"">&#128202;</span> Živá data (08)</button>
            <button id=""nav-config"" onclick=""show('config')""><span class=""nav-icon"">&#9881;</span> Konfigurace & adaptace</button>
            <button id=""nav-ecu"" onclick=""show('ecu')""><span class=""nav-icon"">&#128196;</span> Informace o ECU (01)</button>
        </div>

        <div class=""nav-group"">
            <div class=""nav-title"">Hardware a sběrnice</div>
            <button id=""nav-port"" onclick=""show('port')""><span class=""nav-icon"">&#128268;</span> Nastavení portu</button>
            <button onclick=""cmd('?')"" style=""margin-top: 10px; background: #0f172a;""><span class=""nav-icon"">&#10067;</span> Menu zařízení</button>
        </div>

        <div id=""status"" class=""status idle"">Startuji aplikaci...</div>
    </div>

    <!-- HLAVNÍ OBSAH -->
    <div class=""main"">
        <!-- HORNÍ LIŠTA -->
        <div class=""top"">
            <div>
                <div id=""title"" class=""top-title"">Hlavní přehled</div>
                <div class=""top-sub"">Škoda Felicia 1.3 MPI / SIMOS 2P / K-line diagnostika & adaptace</div>
            </div>
            <div class=""port-bar"">
                <label>PORT:</label>
                <select id=""portSelTop"" onchange=""onPortChanged(this.value)""></select>
                <button class=""btn"" onclick=""refreshPorts()""><span class=""btn-icon"" style=""margin-right:6px; font-size:14px;"">&#128259;</span>Obnovit</button>
                <button id=""btnConnectTop"" class=""btn btn-primary"" onclick=""toggleConnect()""><span class=""btn-icon"" style=""margin-right:6px; font-size:14px;"">&#9889;</span>Připojit</button>
                <span id=""connBadge"" style=""font-size: 14px; font-weight: 700; padding: 6px 12px; border-radius: 6px; background: rgba(239,68,68,0.2); color: #fca5a5;"">&#9679; Nepřipojeno</span>
            </div>
        </div>

        <!-- RX MONITOR LIŠTA -->
        <div class=""rx"">
            <span id=""rxDot"" style=""display:inline-block; width:10px; height:10px; border-radius:50%; background:#475569; margin-right:8px; vertical-align:middle; transition: all 0.1s ease;""></span>
            Komunikace: <b id=""rxState"">čeká</b> &nbsp;|&nbsp; 
            RX znaky: <b id=""rxBytes"">0</b> &nbsp;|&nbsp; 
            RX řádky: <b id=""rxLines"">0</b> &nbsp;|&nbsp; 
            Poslední zpráva: <span id=""rxLast"">--</span>
        </div>

        <!-- OBLAST PRO ZVOLENOU ZÁLOŽKU -->
        <div class=""content"">
            
            <!-- 1. HLAVNÍ PŘEHLED -->
            <div id=""main"" class=""view"">
                <div class=""panel"">
                    <div class=""head"">Rychlé diagnostické a konfigurační akce</div>
                    <div class=""body"">
                        <button class=""btn btn-lg btn-primary"" onclick=""show('faults');cmd('f')""><span class=""btn-icon"">&#128269;</span>Vyčíst paměť závad</button>
                        <button class=""btn btn-lg btn-success"" onclick=""show('live');startLiveData()""><span class=""btn-icon"">&#128202;</span>Živá data (08)</button>
                        <button class=""btn btn-lg btn-warning"" onclick=""show('config');runThrottleAdaptation()""><span class=""btn-icon"">&#9881;</span>Adaptace klapky (098)</button>
                        <button class=""btn btn-lg btn-danger"" onclick=""clearCodes()""><span class=""btn-icon"">&#128465;</span>Smazat paměť závad</button>
                        <button class=""btn btn-lg"" onclick=""cmd('t')""><span class=""btn-icon"">&#9889;</span>Test K-line linky</button>
                        <button class=""btn btn-lg"" onclick=""show('ecu');readEcuInfo()"" style=""background: #334155;""><span class=""btn-icon"">&#128196;</span>Informace o ECU</button>
                        <button class=""btn btn-lg"" onclick=""show('port')"" style=""background: #334155;""><span class=""btn-icon"">&#128268;</span>Nastavení portu</button>
                    </div>
                </div>

                <div class=""panel"">
                    <div class=""head"">Návod k použití a přehled možností</div>
                    <div class=""body"" style=""line-height: 1.6; font-size: 15px; color: #cbd5e1;"">
                        <div style=""display: flex; gap: 16px; flex-wrap: wrap;"">
                            <div style=""flex: 1; min-width: 260px; background: #080d18; padding: 16px; border-radius: 8px; border: 1px solid #1e293b;"">
                                <b style=""color: #38bdf8; font-size: 16px;"">&#128268; 1. Připojení k vozidlu</b>
                                <p style=""margin: 8px 0 0 0;"">Připojte K-line adaptér k OBD-II konektoru Felicie (Pin 7 = K-line, Pin 4/5 = GND, Pin 16 = +12V) a zapněte zapalování.</p>
                            </div>
                            <div style=""flex: 1; min-width: 260px; background: #080d18; padding: 16px; border-radius: 8px; border: 1px solid #1e293b;"">
                                <b style=""color: #38bdf8; font-size: 16px;"">&#9881; 2. Konfigurace a adaptace</b>
                                <p style=""margin: 8px 0 0 0;"">K dispozici je zdokumentované základní nastavení škrticí klapky (098). Ostatní zápisové úkony se nezobrazují, dokud nebudou pro tuto jednotku bezpečně ověřené.</p>
                            </div>
                            <div style=""flex: 1; min-width: 260px; background: #080d18; padding: 16px; border-radius: 8px; border: 1px solid #1e293b;"">
                                <b style=""color: #38bdf8; font-size: 16px;"">&#128220; 3. Záznamník komunikace</b>
                                <p style=""margin: 8px 0 0 0;"">Na spodku obrazovky vidíte kompletní komunikaci mezi Arduinem/ESP32 a řídicí jednotkou v reálném čase.</p>
                            </div>
                        </div>
                    </div>
                </div>
            </div>

            <!-- 2. PAMĚŤ ZÁVAD -->
            <div id=""faults"" class=""view"" style=""display:none"">
                <div class=""panel"">
                    <div class=""head"">
                        <span>Paměť závad řídicí jednotky (Funkce 02)</span>
                        <div>
                            <button class=""btn btn-primary"" onclick=""cmd('f')""><span class=""btn-icon"" style=""margin-right:6px; font-size:14px;"">&#128269;</span>Vyčíst závady</button>
                            <button class=""btn btn-danger"" onclick=""clearCodes()""><span class=""btn-icon"" style=""margin-right:6px; font-size:14px;"">&#128465;</span>Smazat závady</button>
                        </div>
                    </div>
                    <div class=""body"">
                        <div id=""faultList"">
                            <div class=""empty-box"">Paměť závad zatím nebyla načtena. Klikni na tlačítko <b>Vyčíst závady</b>.</div>
                        </div>
                    </div>
                </div>
            </div>

            <!-- 3. ŽIVÁ DATA / BLOKY MĚŘENÝCH HODNOT (FUNKCE 08) -->
            <div id=""live"" class=""view"" style=""display:none"">
                <div class=""panel"">
                    <div class=""head"">
                        <span>Měřené hodnoty a živá data (Funkce 08)</span>
                        <div>
                            <button class=""btn btn-success"" onclick=""startLiveData()""><span class=""btn-icon"" style=""margin-right:6px; font-size:14px;"">&#9654;</span>Spustit čtení živých dat</button>
                            <button class=""btn"" onclick=""cmd('?')"" style=""background: #334155;""><span class=""btn-icon"" style=""margin-right:6px; font-size:14px;"">&#9209;</span>Zastavit</button>
                        </div>
                    </div>
                    <div class=""body"">
                        <div id=""liveStatusBox"" style=""margin-bottom: 18px;"">
                            <div class=""empty-box"" style=""padding: 14px 18px;"">ℹ️ Čtou se pouze zdokumentované měřicí skupiny řídicí jednotky <b>Siemens SIMOS 2P</b>. Žádné hodnoty se do ECU nezapisují.</div>
                        </div>
                        <div id=""liveGroups""></div>
                    </div>
                    <div class=""body legacy-live"">
                        <div id=""liveStatusBox"" style=""margin-bottom: 18px;"">
                            <div class=""empty-box"" style=""padding: 14px 18px;"">ℹ️ Pro zahájení kontinuálního čtení hodnot z motoru klikněte na <b>Spustit čtení živých dat</b>.</div>
                        </div>

                        <!-- 1. OTÁČKY MOTORU (OVĚŘENO) -->
                        <div class=""live-item verified"">
                            <div class=""live-info"">
                                <div class=""live-title"">
                                    <span>Otáčky motoru (G28)</span>
                                    <span class=""badge badge-verified"">&#9989; Ověřeno na vozidle</span>
                                </div>
                                <div class=""live-meta"">Skupina 003, Pole 1 &bull; VAG KW1281 vzorec: raw &times; 32</div>
                            </div>
                            <div class=""live-val"" id=""live_rpm"">-- ot/min</div>
                        </div>

                        <!-- 2. NAPĚTÍ PALUBNÍ SÍTĚ (OVĚŘENO) -->
                        <div class=""live-item verified"">
                            <div class=""live-info"">
                                <div class=""live-title"">
                                    <span>Napětí akumulátoru / palubní sítě (Svorka 30)</span>
                                    <span class=""badge badge-verified"">&#9989; Ověřeno na vozidle</span>
                                </div>
                                <div class=""live-meta"">Skupina 005, Pole 2 &bull; VAG KW1281 vzorec: raw &times; 0.1</div>
                            </div>
                            <div class=""live-val"" id=""live_volt"">-- V</div>
                        </div>

                        <!-- 3. ÚHEL ŠKRTICÍ KLAPKY (OVĚŘENO) -->
                        <div class=""live-item verified"">
                            <div class=""live-info"">
                                <div class=""live-title"">
                                    <span>Úhel otevření škrticí klapky (G69)</span>
                                    <span class=""badge badge-verified"">&#9989; Ověřeno na vozidle</span>
                                </div>
                                <div class=""live-meta"">Skupina 003, Pole 3 &bull; VAG KW1281 vzorec: raw &times; 0.5</div>
                            </div>
                            <div class=""live-val"" id=""live_thr"">-- °</div>
                        </div>

                        <!-- 4. TEPLOTA CHLADICÍ KAPALINY (NEOVĚŘENO) -->
                        <div class=""live-item unverified"">
                            <div class=""live-info"">
                                <div class=""live-title"">
                                    <span>&#9888; Teplota chladicí kapaliny motoru (G62)</span>
                                    <span class=""badge badge-unverified"">&#9888; Neověřeno na vozidle</span>
                                </div>
                                <div class=""live-meta"">Skupina 001, Pole 2 &bull; VAG KW1281 snímač teploty chlazení</div>
                            </div>
                            <div class=""live-val"" id=""live_temp"">-- °C</div>
                        </div>

                        <!-- 5. DOBA VSTŘIKU (NEOVĚŘENO) -->
                        <div class=""live-item unverified"">
                            <div class=""live-info"">
                                <div class=""live-title"">
                                    <span>&#9888; Doba vstřiku paliva (Vstřikovače N30–N33)</span>
                                    <span class=""badge badge-unverified"">&#9888; Neověřeno na vozidle</span>
                                </div>
                                <div class=""live-meta"">Skupina 002, Pole 1 &bull; Doba otevření vstřikovacích ventilů</div>
                            </div>
                            <div class=""live-val"" id=""live_inj"">-- ms</div>
                        </div>

                        <!-- 6. LAMBDA REGULACE (NEOVĚŘENO) -->
                        <div class=""live-item unverified"">
                            <div class=""live-info"">
                                <div class=""live-title"">
                                    <span>&#9888; Napětí / integrátor lambda sondy (G39)</span>
                                    <span class=""badge badge-unverified"">&#9888; Neověřeno na vozidle</span>
                                </div>
                                <div class=""live-meta"">Skupina 001, Pole 3 &bull; Napětí kyslíkového snímače (0.00 – 1.00 V)</div>
                            </div>
                            <div class=""live-val"" id=""live_lambda"">-- V</div>
                        </div>

                        <!-- 7. ZÁTĚŽ MOTORU (NEOVĚŘENO) -->
                        <div class=""live-item unverified"">
                            <div class=""live-info"">
                                <div class=""live-title"">
                                    <span>&#9888; Vypočtená zátěž motoru</span>
                                    <span class=""badge badge-unverified"">&#9888; Neověřeno na vozidle</span>
                                </div>
                                <div class=""live-meta"">Skupina 002, Pole 2 &bull; Relativní zatížení motoru jednotkou SIMOS 2P</div>
                            </div>
                            <div class=""live-val"" id=""live_load"">-- %</div>
                        </div>

                        <!-- 8. PŘEDSTIH ZÁŽEHU (NEOVĚŘENO) -->
                        <div class=""live-item unverified"">
                            <div class=""live-info"">
                                <div class=""live-title"">
                                    <span>&#9888; Úhel předstihu zážehu</span>
                                    <span class=""badge badge-unverified"">&#9888; Neověřeno na vozidle</span>
                                </div>
                                <div class=""live-meta"">Skupina 003, Pole 4 &bull; Předstih zážehu v úhlových stupních před HÚ</div>
                            </div>
                            <div class=""live-val"" id=""live_ign"">-- °</div>
                        </div>

                        <!-- 9. TEPLOTA NASÁVANÉHO VZDUCHU (NEOVĚŘENO) -->
                        <div class=""live-item unverified"">
                            <div class=""live-info"">
                                <div class=""live-title"">
                                    <span>&#9888; Teplota nasávaného vzduchu (G42)</span>
                                    <span class=""badge badge-unverified"">&#9888; Neověřeno na vozidle</span>
                                </div>
                                <div class=""live-meta"">Skupina 004, Pole 3 &bull; Snímač teploty vzduchu v sacím potrubí</div>
                            </div>
                            <div class=""live-val"" id=""live_airtemp"">-- °C</div>
                        </div>

                        <!-- 10. ADAPTACE VOLNOBĚHU (NEOVĚŘENO) -->
                        <div class=""live-item unverified"">
                            <div class=""live-info"">
                                <div class=""live-title"">
                                    <span>&#9888; Adaptace volnoběhu / poloha regulátoru (V60)</span>
                                    <span class=""badge badge-unverified"">&#9888; Neověřeno na vozidle</span>
                                </div>
                                <div class=""live-meta"">Skupina 004, Pole 2 &bull; Poloha servomotoru klapky volnoběhu</div>
                            </div>
                            <div class=""live-val"" id=""live_idle"">-- %</div>
                        </div>

                        <!-- 11. RYCHLOST VOZIDLA (NEOVĚŘENO) -->
                        <div class=""live-item unverified"">
                            <div class=""live-info"">
                                <div class=""live-title"">
                                    <span>&#9888; Rychlost vozidla</span>
                                    <span class=""badge badge-unverified"">&#9888; Neověřeno na vozidle</span>
                                </div>
                                <div class=""live-meta"">Skupina 005, Pole 1 &bull; Rychlostní impulsy pro řídicí jednotku</div>
                            </div>
                            <div class=""live-val"" id=""live_speed"">-- km/h</div>
                        </div>

                        <!-- 12. VENTIL ODVĚTRÁNÍ NÁDRŽE N80 (NEOVĚŘENO) -->
                        <div class=""live-item unverified"">
                            <div class=""live-info"">
                                <div class=""live-title"">
                                    <span>&#9888; Ventil odvětrání palivové nádrže (N80)</span>
                                    <span class=""badge badge-unverified"">&#9888; Neověřeno na vozidle</span>
                                </div>
                                <div class=""live-meta"">Skupina 006, Pole 2 &bull; Střída ventilu regenerace nádobky s aktivním uhlím EVAP</div>
                            </div>
                            <div class=""live-val"" id=""live_evap"">-- %</div>
                        </div>

                        <!-- 13. SPÍNAČ VOLNOBĚHU KLAPKY F60 (NEOVĚŘENO) -->
                        <div class=""live-item unverified"">
                            <div class=""live-info"">
                                <div class=""live-title"">
                                    <span>&#9888; Spínač volnoběhu klapky (F60)</span>
                                    <span class=""badge badge-unverified"">&#9888; Neověřeno na vozidle</span>
                                </div>
                                <div class=""live-meta"">Skupina 098, Pole 4 &bull; Stav sepnutí spínače volnoběhu klapky</div>
                            </div>
                            <div class=""live-val"" id=""live_f60"">--</div>
                        </div>

                    </div>
                </div>
            </div>

            <!-- 4. KONFIGURACE A ADAPTACE (FUNKCE 04 / 10 / 03 / 07) -->
            <div id=""config"" class=""view"" style=""display:none"">
                <div class=""panel"">
                    <div class=""head"">
                        <span>Ověřené servisní funkce jednotky SIMOS 2P</span>
                    </div>
                    <div class=""body"">
                        <div id=""cfgStatusBox"" style=""margin-bottom: 18px;"">
                            <div class=""empty-box"" style=""padding: 14px 18px;"">ℹ️ Zobrazené úkony odpovídají dílenské příručce Felicia 1.3 MPI se SIMOS 2P.</div>
                        </div>

                        <div class=""cfg-card"">
                            <div class=""cfg-head"">
                                <span>&#9881; Základní nastavení škrticí klapky — skupina 098</span>
                                <button class=""btn btn-warning"" onclick=""runThrottleAdaptation()""><span class=""btn-icon"">&#9654;</span>Spustit nastavení 098</button>
                            </div>
                            <div class=""cfg-desc"">
                                Zdokumentovaný servisní postup pro seřízení pohonu škrticí klapky po jeho vyčištění nebo opravě. Funkce čte výsledek přímo z ECU; při odmítnutí se do nastavení nic nedopisuje.
                            </div>
                            <div class=""cfg-cond"">
                                <b>Než začneš:</b> motor vypnutý, zapalování zapnuté, pedál plynu volný, motor zahřátý nad 80 °C a paměť závad bez aktuálních chyb. Po úspěchu počkej 20 s, ukonči komunikaci a vypni zapalování alespoň na 30 s.
                            </div>
                        </div>

                        <div class=""cfg-card"">
                            <div class=""cfg-head""><span>&#128274; Záměrně nenabízené funkce</span></div>
                            <div class=""cfg-desc"">
                                Párování imobilizéru patří samostatné jednotce 25 a vyžaduje správné přihlášení. Kódování výbavy ani hromadné mazání adaptací tato motorová komunikace nemají spolehlivě ověřené. Test akčních členů také není v této verzi nabízen, dokud nebude ověřena kompletní bezpečná sekvence pro SIMOS 2P.
                            </div>
                        </div>
                    </div>
                    <div class=""body legacy-live"">

                        <div id=""cfgStatusBox"" style=""margin-bottom: 18px;"">
                            <div class=""empty-box"" style=""padding: 14px 18px;"">ℹ️ Vyberte požadovanou servisní operaci níže. Ujistěte se, že je zapnuté zapalování.</div>
                        </div>

                        <!-- 1. ZÁKLADNÍ NASTAVENÍ KLAPKY -->
                        <div class=""cfg-card"">
                            <div class=""cfg-head"">
                                <span>&#9881; 1. Základní nastavení škrticí klapky (Funkce 04 - Skupina 098)</span>
                                <button class=""btn btn-warning"" onclick=""runThrottleAdaptation()""><span class=""btn-icon"">&#9654;</span>Provést adaptaci klapky</button>
                            </div>
                            <div class=""cfg-desc"">
                                Provede kalibraci dorazů a servomotoru klapky V60 / potenciometru G69. Provádí se vždy po vyčištění klapky, výměně baterie nebo odstranění kolísání volnoběhu.
                            </div>
                            <div class=""cfg-cond"">
                                <b>Podmínky:</b> Teplota chlazení &gt; 80 °C, motor vypnutý, zapalování ZAPNUTO, pedál plynu v klidu, paměť závad bez chyb.
                            </div>
                        </div>

                        <!-- 2. RESET ADAPTAČNÍCH HODNOT -->
                        <div class=""cfg-card"">
                            <div class=""cfg-head"">
                                <span>&#128465; 2. Vymazání adaptačních hodnot ECU (Funkce 10 - Kanál 00)</span>
                                <button class=""btn btn-danger"" onclick=""runResetAdaptations()""><span class=""btn-icon"">&#128465;</span>Vymazat adaptace (Tovární reset)</button>
                            </div>
                            <div class=""cfg-desc"">
                                Vymaže naučené dlouhodobé korekce směsi, lambda regulace a polohy klapky z paměti RAM/EEPROM řídicí jednotky. Doporučuje se provést před adaptací klapky nebo po výměně čidel.
                            </div>
                            <div class=""cfg-cond"">
                                <b>Doporučený postup:</b> 1. Vymazat paměť závad &rarr; 2. Vymazat adaptace &rarr; 3. Vypnout zapalování na 30 s &rarr; 4. Provést základní nastavení 098.
                            </div>
                        </div>

                        <!-- 3. TEST AKČNÍCH ČLENŮ -->
                        <div class=""cfg-card"">
                            <div class=""cfg-head"">
                                <span>&#9889; 3. Test akčních členů (Funkce 03)</span>
                                <button class=""btn btn-primary"" onclick=""runActuatorTests()""><span class=""btn-icon"">&#9889;</span>Spustit test akčních členů</button>
                            </div>
                            <div class=""cfg-desc"">
                                Postupně aktivuje výstupy řídicí jednotky pro ověření jejich elektrické funkčnosti (ventil odvětrání nádrže N80, relé palivového čerpadla, servomotor klapky V60).
                            </div>
                            <div class=""cfg-cond"">
                                <b>Podmínky:</b> Motor vypnutý, zapalování ZAPNUTO. Během testu budete slyšet cvakání a bzučení jednotlivých prvků.
                            </div>
                        </div>

                        <!-- 4. PŘIZPŮSOBENÍ IMOBILIZÉRU -->
                        <div class=""cfg-card"">
                            <div class=""cfg-head"">
                                <span>&#128273; 4. Přizpůsobení k imobilizéru (Adresa 25 / Kanál 00)</span>
                                <button class=""btn"" onclick=""runImmoAdaptation()"" style=""background: #334155;""><span class=""btn-icon"">&#128273;</span>Spárovat ECU s imobilizérem</button>
                            </div>
                            <div class=""cfg-desc"">
                                Provede vzájemné spárování a synchronizaci řídicí jednotky motoru SIMOS 2P s jednotkou imobilizéru (využívá se při výměně motorové jednotky nebo immoboxu).
                            </div>
                        </div>

                        <!-- 5. KÓDOVÁNÍ ŘÍDICÍ JEDNOTKY -->
                        <div class=""cfg-card"">
                            <div class=""cfg-head"">
                                <span>&#128196; 5. Kódování výbavy ECU (Funkce 07)</span>
                                <span style=""font-family: Consolas, monospace; font-size: 16px; color: #38bdf8; font-weight: 700;"">Aktuální kód: 00000</span>
                            </div>
                            <div class=""cfg-desc"">
                                Nastavuje konfiguraci výbavy vozidla (standardně <b>00000</b> pro vozy s manuální převodovkou a bez klimatizace).
                            </div>
                        </div>

                    </div>
                </div>
            </div>

            <!-- 5. IDENTIFIKACE ECU -->
            <div id=""ecu"" class=""view"" style=""display:none"">
                <div class=""panel"">
                    <div class=""head"">
                        <span>Podrobné informace o řídicí jednotce (Funkce 01)</span>
                        <button class=""btn btn-primary"" onclick=""readEcuInfo()""><span class=""btn-icon"" style=""margin-right:6px; font-size:14px;"">&#128269;</span>Načíst identifikaci z ECU</button>
                    </div>
                    <div class=""body"">
                        <div id=""ecuStatusBox"" style=""margin-bottom: 16px;"">
                            <div class=""empty-box"">ℹ️ Údaje se načítají přímo z reálné řídicí jednotky motoru přes protokol KW1281.<br>Pro načtení údajů z auta klikněte na tlačítko <b>Načíst identifikaci z ECU</b> výše.</div>
                        </div>
                        <table class=""info-table"">
                            <tr>
                                <td class=""info-label"">Vozidlo:</td>
                                <td style=""font-size: 17px; font-weight: 700; color: #ffffff;"">Škoda Felicia 1.3 MPI</td>
                            </tr>
                            <tr>
                                <td class=""info-label"">Číslo dílu VAG (Pole 1):</td>
                                <td class=""info-value"" id=""part2"" style=""color:#94a3b8;"">-- (nenačteno)</td>
                            </tr>
                            <tr>
                                <td class=""info-label"">Systém / Komponenta (Pole 2):</td>
                                <td class=""info-value"" id=""comp2"" style=""color:#94a3b8;"">-- (nenačteno)</td>
                            </tr>
                            <tr>
                                <td class=""info-label"">Verze softwaru (Pole 3):</td>
                                <td class=""info-value"" id=""extra2"" style=""color:#94a3b8;"">-- (nenačteno)</td>
                            </tr>
                            <tr>
                                <td class=""info-label"">Doplňkové kódování (Pole 4):</td>
                                <td class=""info-value"" id=""extra4"" style=""color:#94a3b8;"">--</td>
                            </tr>
                            <tr>
                                <td class=""info-label"">Diagnostický protokol:</td>
                                <td style=""font-size: 16px; color: #cbd5e1;"">VAG KW1281 (K-line 9600 baud, 5-baud slow init adresa 0x01)</td>
                            </tr>
                        </table>
                    </div>
                </div>
            </div>

            <!-- 6. NASTAVENÍ PORTU -->
            <div id=""port"" class=""view"" style=""display:none"">
                <div class=""panel"">
                    <div class=""head"">Nastavení a správa sériového COM portu & Hardware (ESP32 / Arduino)</div>
                    <div class=""body"">
                        <div style=""background: #0f172a; border: 1px solid #1e293b; border-radius: 8px; padding: 14px 18px; margin-bottom: 20px; line-height: 1.5; font-size: 14px;"">
                            <b style=""color: #38bdf8;"">ℹ️ Podpora pro ESP32 a Arduino:</b><br>
                            - <b>ESP32-C3:</b> Standardní rychlost <b>115200 baud</b>, RX = GPIO 5, TX = GPIO 4.<br>
                            - <b>Arduino Nano / Uno:</b> Rychlost <b>115200 baud</b>, AltSoftSerial RX = D8, TX = D9.<br>
                            - Pokud se zařízení nechce spojit, klikněte na <b>HW Reset zařízení</b> níže pro restart procesoru.
                        </div>

                        <div style=""margin-bottom: 20px; font-size: 16px;"">
                            <label style=""font-weight: 700; display: inline-block; width: 180px;"">Vyber COM port:</label>
                            <select id=""portSel"" style=""padding: 10px 14px; font-size: 15px; min-width: 320px; border-radius: 6px; border: 1px solid #475569; background: #0f172a; color: white;"" onchange=""onPortChanged(this.value)""></select>
                            <button class=""btn"" onclick=""refreshPorts()""><span class=""btn-icon"" style=""margin-right:6px; font-size:14px;"">&#128259;</span>Obnovit seznam</button>
                        </div>
                        <div style=""margin-bottom: 20px; font-size: 16px;"">
                            <label style=""font-weight: 700; display: inline-block; width: 180px;"">Rychlost (Baud):</label>
                            <select id=""baud"" style=""padding: 10px 14px; width: 220px; font-size: 15px; background: #0f172a; color: white; border: 1px solid #334155; border-radius: 6px;"">
                                <option value=""115200"" selected>115200 (ESP32 & Arduino standard)</option>
                                <option value=""57600"">57600</option>
                                <option value=""38400"">38400</option>
                                <option value=""9600"">9600</option>
                                <option value=""230400"">230400</option>
                            </select>
                        </div>
                        <div style=""margin-top: 24px; padding-top: 18px; border-top: 1px solid #1e293b;"">
                            <button class=""btn btn-lg btn-primary"" onclick=""connect()""><span class=""btn-icon"">&#9889;</span>Připojit k vybranému portu</button>
                            <button class=""btn btn-lg btn-danger"" onclick=""disconnect()""><span class=""btn-icon"">&#10060;</span>Odpojit</button>
                            <button class=""btn btn-lg btn-warning"" onclick=""resetDevice()""><span class=""btn-icon"">&#128260;</span>HW Reset zařízení (ESP32 / Arduino)</button>
                            <button class=""btn btn-lg"" onclick=""autoConnect()""><span class=""btn-icon"">&#128269;</span>Automaticky najít zařízení</button>
                        </div>
                    </div>
                </div>
            </div>

        </div>

        <!-- SPODNÍ FIXNÍ ZÁZNAMNÍK KOMUNIKACE (VIDITELNÝ STÁLE NA SPODKU) -->
        <div class=""bottom-log"" id=""bottomLogPanel"">
            <div class=""bottom-log-head"">
                <span>&#128220; Komunikační záznamník (Arduino / ECU)</span>
                <button class=""btn"" style=""padding: 5px 14px; font-size: 13px; margin: 0;"" onclick=""clearLog()""><span class=""btn-icon"" style=""margin-right:5px; font-size:13px;"">&#128465;</span>Vymazat záznamník</button>
            </div>
            <pre id=""log"" class=""log-fixed""></pre>
        </div>

    </div>
</div>

<script>
var logText = '';
var isConnected = false;
var currentSelectedPort = 'COM6';

function id(x) { return document.getElementById(x); }

function show(v) {
    var views = ['main', 'faults', 'live', 'config', 'ecu', 'port'];
    for (var i = 0; i < views.length; i++) {
        var x = views[i];
        var el = id(x);
        if (el) el.style.display = (x == v) ? 'block' : 'none';
        
        var navBtn = id('nav-' + x);
        if (navBtn) {
            if (x == v) navBtn.className = 'active';
            else navBtn.className = '';
        }
    }

    var titles = {
        main: 'Hlavní přehled',
        faults: 'Paměť závad řídicí jednotky (02)',
        live: 'Měřené hodnoty a živá data (08)',
        config: 'Konfigurace a adaptace řídicí jednotky (04 / 10)',
        ecu: 'Informace o řídicí jednotce (01)',
        port: 'Nastavení sériového portu'
    };
    if (id('title')) id('title').innerHTML = titles[v] || 'Diagnostika';
}

function onPortChanged(v) {
    currentSelectedPort = v;
    var s1 = id('portSelTop');
    var s2 = id('portSel');
    if (s1 && s1.value != v) s1.value = v;
    if (s2 && s2.value != v) s2.value = v;
}

function refreshPorts() {
    var res = window.external.RefreshPorts();
    setPorts(res, currentSelectedPort);
}

function setPorts(list, pref) {
    var rawList = list ? String(list).split('|').filter(function(x) { return x && x.length > 0; }) : [];
    var known = {};
    var ports = [];
    
    for (var i = 0; i < rawList.length; i++) {
        var parts = rawList[i].split('::');
        var pName = parts[0];
        var pLabel = parts.length > 1 ? parts[1] : pName;
        if (!known[pName]) {
            known[pName] = true;
            ports.push({ name: pName, label: pLabel, detected: true });
        }
    }
    
    for (var j = 1; j <= 16; j++) {
        var c = 'COM' + j;
        if (!known[c]) {
            known[c] = true;
            ports.push({ name: c, label: c, detected: false });
        }
    }
    
    if (!pref && ports.length > 0) pref = ports[0].name;
    currentSelectedPort = pref;
    
    var selectIds = ['portSelTop', 'portSel'];
    for (var k = 0; k < selectIds.length; k++) {
        var s = id(selectIds[k]);
        if (!s) continue;
        s.options.length = 0;
        for (var m = 0; m < ports.length; m++) {
            var item = ports[m];
            var o = document.createElement('option');
            o.value = item.name;
            o.text = item.label;
            s.add(o);
            if (item.name == pref) s.selectedIndex = m;
        }
    }
}

function setConnected(conn, port, baud) {
    isConnected = (conn == '1' || conn == 1 || conn == true);
    var b1 = id('btnConnectTop');
    var badge = id('connBadge');
    if (isConnected) {
        if (b1) {
            b1.className = 'btn btn-danger';
            b1.innerHTML = '<span class=""btn-icon"" style=""margin-right:6px; font-size:14px;"">&#10060;</span>Odpojit';
        }
        if (badge) {
            badge.style.background = 'rgba(16,185,129,0.2)';
            badge.style.color = '#34d399';
            badge.innerHTML = '● Připojeno k ' + port + ' (' + baud + ' bd)';
        }
    } else {
        if (b1) {
            b1.className = 'btn btn-primary';
            b1.innerHTML = '<span class=""btn-icon"" style=""margin-right:6px; font-size:14px;"">&#9889;</span>Připojit';
        }
        if (badge) {
            badge.style.background = 'rgba(239,68,68,0.2)';
            badge.style.color = '#fca5a5';
            badge.innerHTML = '● Nepřipojeno';
        }
    }
}

function setStatus(text, level) {
    var s = id('status');
    if (!s) return;
    s.className = 'status ' + (level || 'idle');
    s.textContent = text;
}

function toggleConnect() {
    if (isConnected) {
        disconnect();
    } else {
        connect();
    }
}

function connect() {
    var p = currentSelectedPort || (id('portSelTop') ? id('portSelTop').value : 'COM6');
    var b = id('baud') ? id('baud').value : '115200';
    window.external.Connect(p, b);
}

function resetDevice() {
    window.external.ResetDevice();
}

function autoConnect() {
    window.external.AutoConnect();
}

function disconnect() {
    window.external.Disconnect();
}

function cmd(c) {
    window.external.SendCommand(c);
}

function clearCodes() {
    if (confirm('Opravdu chcete vymazat paměť závad řídicí jednotky motoru?')) {
        cmd('c');
    }
}

function readEcuInfo() {
    var box = id('ecuStatusBox');
    if (box) {
        box.innerHTML = '<div class=""busy"" style=""padding: 14px 18px; border-radius: 8px;"">⏳ <b>Probíhá inicializace a čtení identifikačních polí z ECU...</b><br>Vyčkejte prosím 2-3 sekundy.</div>';
    }
    cmd('i');
}

function clearLog() {
    logText = '';
    var l1 = id('log');
    if (l1) l1.textContent = '';
    window.external.ClearLog();
}

function runThrottleAdaptation() {
    if (confirm('Spustit základní nastavení škrticí klapky (Funkce 04, Skupina 098)?\n\nUjistěte se, že:\n- Teplota motoru je > 80 °C\n- Motor neběží a zapalování je ZAPNUTO\n- Lanko plynu je volné')) {
        var box = id('cfgStatusBox');
        if (box) {
            box.innerHTML = '<div class=""busy"" style=""padding: 14px 18px; border-radius: 8px;"">⏳ <b>Probíhá základní nastavení škrticí klapky (098)...</b><br>ECU nastavuje dorazy servomotoru V60. Vyčkejte cca 5 sekund.</div>';
        }
        cmd('a');
    }
}

function setAdpResult(res) {
    var box = id('cfgStatusBox');
    if (!box) return;
    if (res == 'OK') {
        box.innerHTML = '<div class=""ok-box"" style=""padding: 14px 18px;""><span style=""font-size:24px;"">&#9989;</span> <div><b>Základní nastavení škrticí klapky bylo úspěšně dokončeno (ADP OK)!</b><div style=""font-size:13px;color:#a7f3d0;margin-top:3px;"">Nyní vypněte zapalování na 30 sekund pro uložení hodnot do paměti ECU.</div></div></div>';
    } else if (res == 'REFUSED') {
        box.innerHTML = '<div class=""dtc""><div class=""dtc-code"">&#9888; ECU odmítla základní nastavení (REFUSED)</div><div class=""dtc-desc"">Zkontrolujte, zda je motor zahřátý nad 80 °C, motor neběží a lanko plynu není napnuté.</div></div>';
    } else {
        box.innerHTML = '<div class=""dtc""><div class=""dtc-code"">&#9888; Chyba komunikace při adaptaci: ' + res + '</div></div>';
    }
}

function startLiveData() {
    var box = id('liveStatusBox');
    if (box) {
        box.innerHTML = '<div class=""busy"" style=""padding: 14px 18px; border-radius: 8px;"">⏳ <b>Inicializuji KW1281 a načítám měřicí bloky SIMOS 2P...</b><br>První hodnoty se zobrazí během několika sekund.</div>';
    }
    var groups = id('liveGroups');
    if (groups) groups.innerHTML = '';
    cmd('l');
}

var simosGroups = {
    '001': 'Základní hodnoty motoru', '003': 'Volnoběh a škrticí klapka',
    '005': 'Napájení a teploty', '007': 'Provoz škrticí klapky',
    '008': 'Stabilizace volnoběhu', '009': 'Spotřeba vzduchu při volnoběhu',
    '010': 'Lambda regulace a aktivní uhlí', '011': 'Vstřikování a lambda korekce',
    '012': 'Hodnoty lambda regulace', '015': 'Regulace klepání — válce 1/4',
    '016': 'Regulace klepání — válce 2/3', '017': 'Signály snímačů klepání',
    '019': 'Provozní stav klimatizace', '020': 'Stav lambda regulace',
    '021': 'Nastavení škrticí klapky', '097': 'Napětí potenciometrů klapky',
    '099': 'Kontrola lambda regulace'
};

var simosFields = {
    '001_1': 'Otáčky motoru (G28)', '001_2': 'Teplota chladicí kapaliny (G62)', '001_3': 'Napětí lambda sondy (G39)', '001_4': 'Stav lambda regulace',
    '003_1': 'Otáčky motoru', '003_2': 'Požadované volnoběžné otáčky', '003_3': 'Úhel škrticí klapky (G69)', '003_4': 'Střída ovládání klapky (V60)',
    '005_1': 'Otáčky motoru', '005_2': 'Napětí ECU / akumulátoru', '005_3': 'Teplota chladicí kapaliny', '005_4': 'Teplota nasávaného vzduchu (G42)',
    '007_1': 'Úhel škrticí klapky', '007_4': 'Provozní stav klapky',
    '008_1': 'Otáčky motoru', '008_2': 'Požadované volnoběžné otáčky', '008_3': 'Regulátor volnoběhu', '008_4': 'Stav klapky',
    '009_1': 'Regulátor volnoběhu', '009_2': 'Spotřeba vzduchu', '009_3': 'Teplota chladicí kapaliny', '009_4': 'Otáčky motoru',
    '010_1': 'Lambda regulace', '010_2': 'Napětí lambda sondy', '010_3': 'Střída ventilu aktivního uhlí', '010_4': 'Lambda korekce při aktivním uhlí',
    '011_1': 'Doba vstřiku', '011_2': 'Lambda hodnota pro volnoběh', '011_3': 'Lambda hodnota při částečném zatížení', '011_4': 'Stav ventilu aktivního uhlí',
    '012_1': 'Otáčky motoru', '012_3': 'Lambda regulace', '012_4': 'Napětí lambda sondy',
    '015_1': 'Otáčky motoru', '015_3': 'Omezení předstihu klepáním — válce 1/4', '015_4': 'Omezení předstihu klepáním — válce 2/3',
    '016_1': 'Otáčky motoru', '016_3': 'Omezení předstihu klepáním — válce 2/3', '016_4': 'Omezení předstihu klepáním — válce 1/4',
    '017_1': 'Signál snímače klepání', '017_2': 'Signál snímače klepání', '017_3': 'Signál snímače klepání', '017_4': 'Signál snímače klepání',
    '019_1': 'Otáčky motoru', '019_3': 'Kompresor klimatizace', '019_4': 'Stav klimatizace',
    '020_1': 'Otáčky motoru', '020_3': 'Teplota chladicí kapaliny', '020_4': 'Stav lambda regulace',
    '021_1': 'Stav ovládání škrticí klapky', '021_2': 'Minimální poloha nastavovače', '021_3': 'Nouzová poloha nastavovače', '021_4': 'Maximální poloha nastavovače',
    '097_1': 'Potenciometr klapky — dolní mez', '097_2': 'Potenciometr nastavovače — dolní mez', '097_3': 'Potenciometr klapky — horní mez', '097_4': 'Potenciometr nastavovače — horní mez',
    '099_1': 'Otáčky motoru', '099_2': 'Teplota chladicí kapaliny', '099_3': 'Lambda regulace', '099_4': 'Stav lambda regulace'
};

function padGroup(group) {
    var result = String(parseInt(group, 10));
    while (result.length < 3) result = '0' + result;
    return result;
}

function fmt(value, decimals) { return Number(value).toFixed(decimals); }

function decodeKwp1281(formula, a, b) {
    formula = Number(formula); a = Number(a); b = Number(b);
    var product = a * b;
    switch (formula) {
        case 1: return fmt(product * 0.2, 0) + ' ot/min';
        case 2: return fmt(product * 0.002, 1) + ' %';
        case 3: return fmt(product * 0.002, 1) + ' °';
        case 4: return fmt(Math.abs(b - 127) * 0.01 * a, 1) + (b > 127 ? ' ° po HÚ' : ' ° před HÚ');
        case 5: return fmt(product * 0.1 - a * 10, 1) + ' °C';
        case 6: case 21: return fmt(product * 0.001, 3) + ' V';
        case 7: return fmt(product * 0.01, 1) + ' km/h';
        case 8: return fmt(product * 0.1, 1);
        case 9: return fmt((b - 127) * 0.02 * a, 1) + ' °';
        case 10: return b === 0 ? 'studený' : 'zahřátý';
        case 11: return fmt(1 + a * (b - 128) * 0.0001, 3) + ' λ';
        case 12: return fmt(product * 0.001, 3) + ' Ω';
        case 13: return fmt((b - 127) * 0.001 * a, 3) + ' mm';
        case 14: return fmt(product * 0.005, 3) + ' bar';
        case 15: case 22: return fmt(product * 0.01, 2) + ' ms';
        case 16: return 'stav 0x' + a.toString(16).toUpperCase() + ' / 0x' + b.toString(16).toUpperCase();
        case 17: return String.fromCharCode(a) + String.fromCharCode(b);
        case 18: return fmt(product * 0.04, 1) + ' mbar';
        case 19: return fmt(product * 0.01, 2) + ' l';
        case 20: return fmt(a * (b - 128) / 128, 1) + ' %';
        case 23: return fmt(a * b / 256, 1) + ' %';
        case 24: return fmt(product * 0.001, 3) + ' A';
        case 25: return fmt(((b * 256) + a) / 182, 1) + ' g/h';
        case 26: return (b - a) + ' °C';
        case 27: return fmt(Math.abs(b - 128) * 0.01 * a, 1) + (b < 128 ? ' ° po HÚ' : ' ° před HÚ');
        case 31: return fmt(a * b / 2560, 1) + ' °C';
        case 32: return (b > 128 ? b - 256 : b).toString();
        case 33: return fmt(a === 0 ? 100 * b : 100 * b / a, 1) + ' %';
        case 35: return fmt(product * 0.01, 2) + ' l/h';
        case 39: return fmt(a * b / 256, 1) + ' mg/h';
        case 47: return ((b - 128) * a) + ' ms';
        case 48: case 54: return String(a * 256 + b);
        case 49: return fmt(a * b / 4, 1) + ' mg/h';
        case 53: return fmt((b - 128) * 1.4222 + a * 0.006, 2) + ' g/s';
        case 60: return fmt((a * 256 + b) * 0.01, 2) + ' s';
        default: return 'surová A=' + a + ', B=' + b + ' (formát ' + formula + ')';
    }
}

function setLiveField(sample, group, cell, formula, a, b) {
    var groupCode = padGroup(group);
    var key = groupCode + '_' + cell;
    var groupId = 'live_group_' + groupCode;
    var groupBox = id(groupId);
    if (!groupBox) {
        groupBox = document.createElement('div');
        groupBox.className = 'live-group';
        groupBox.id = groupId;
        var title = document.createElement('div');
        title.className = 'live-group-title';
        title.textContent = 'Skupina ' + groupCode + ' — ' + (simosGroups[groupCode] || 'měřené hodnoty ECU');
        groupBox.appendChild(title);
        id('liveGroups').appendChild(groupBox);
    }
    var rowId = groupId + '_cell_' + cell;
    var row = id(rowId);
    if (!row) {
        row = document.createElement('div');
        row.className = 'live-row';
        row.id = rowId;
        var label = document.createElement('div');
        label.className = 'live-row-label';
        label.textContent = simosFields[key] || ('Pole ' + cell + ' (skupina ' + groupCode + ')');
        var value = document.createElement('div');
        value.className = 'live-row-value';
        value.id = rowId + '_value';
        row.appendChild(label);
        row.appendChild(value);
        groupBox.appendChild(row);
    }
    id(rowId + '_value').textContent = decodeKwp1281(formula, a, b);
    var box = id('liveStatusBox');
    if (box) {
        box.innerHTML = '<div class=""ok-box"" style=""padding: 12px 18px;""><span style=""font-size:22px;"">🔄</span> <div><b>Čtou se měřicí bloky SIMOS 2P.</b> Přijat vzorek #' + sample + '.</div></div>';
    }
}

function setLiveDone() {
    var box = id('liveStatusBox');
    if (box) {
        box.innerHTML = '<div class=""empty-box"" style=""padding: 12px 18px;"">✅ Cyklus čtení živých dat byl dokončen. Pro další čtení klikněte znovu na <b>Spustit čtení živých dat</b>.</div>';
    }
}

function appendLog(line) {
    logText += line + '\n';
    if (logText.length > 70000) {
        logText = logText.substr(logText.length - 60000);
    }
    var l1 = id('log');
    if (l1) {
        l1.textContent = logText;
        l1.scrollTop = l1.scrollHeight;
    }
}

var rxTimer = null;
function setRxStats(bytes, lines, last, preview) {
    if (id('rxBytes')) id('rxBytes').textContent = bytes;
    if (id('rxLines')) id('rxLines').textContent = lines;
    if (id('rxState')) id('rxState').textContent = (Number(bytes) > 0) ? 'aktivní příjem' : 'čeká';
    if (id('rxLast')) id('rxLast').textContent = preview || '--';

    var dot = id('rxDot');
    if (dot) {
        dot.style.background = '#10b981';
        if (rxTimer) clearTimeout(rxTimer);
        rxTimer = setTimeout(function() {
            if (dot) dot.style.background = '#475569';
        }, 120);
    }
}

function setPart(v) {
    var el = id('part2');
    if (el) { el.textContent = v; el.style.color = '#38bdf8'; }
    updateEcuSuccess();
}

function setComponent(v) {
    var el = id('comp2');
    if (el) { el.textContent = v; el.style.color = '#38bdf8'; }
    updateEcuSuccess();
}

function setExtra(v) {
    var el = id('extra2');
    if (el) { el.textContent = v; el.style.color = '#38bdf8'; }
    updateEcuSuccess();
}

function setExtra4(v) {
    var el = id('extra4');
    if (el) { el.textContent = v; el.style.color = '#38bdf8'; }
    updateEcuSuccess();
}

function updateEcuSuccess() {
    var box = id('ecuStatusBox');
    if (box) {
        box.innerHTML = '<div class=""ok-box"" style=""padding: 14px 18px;""><span style=""font-size:24px;"">&#9989;</span> <div><b>Identifikace byla úspěšně načtena z řídicí jednotky ECU!</b><div style=""font-size:13px;color:#a7f3d0;margin-top:3px;"">Data byla přijata v reálném čase přes protokol VAG KW1281.</div></div></div>';
    }
}

function clearDtc() {
    var l = id('faultList');
    if (l) l.innerHTML = '<div class=""empty-box"">Čekám na odpověď z řídicí jednotky...</div>';
}

function setNoFaults() {
    var l = id('faultList');
    if (l) l.innerHTML = '<div class=""ok-box""><span style=""font-size:24px;"">&#9989;</span> <div><b>V paměti závad nejsou žádné chyby!</b><div style=""font-size:13px;color:#6ee7b7;margin-top:3px;"">Řídicí jednotka SIMOS 2P vrátila kód FF FF 88 (bez závad).</div></div></div>';
}

function setClearResult(t) {
    appendLog('[APP] ' + t);
}

function addDtc(code, title, raw) {
    var l = id('faultList');
    if (!l) return;
    if (l.textContent.indexOf('Zatím') >= 0 || l.textContent.indexOf('Čekám') >= 0 || l.textContent.indexOf('nejsou') >= 0) {
        l.innerHTML = '';
    }
    var d = document.createElement('div');
    d.className = 'dtc';
    d.innerHTML = '<div class=""dtc-code"">&#9888; VAG kód ' + code + '</div><div class=""dtc-desc"">' + title + '</div><div class=""dtc-meta"">Parametry: ' + raw + '</div>';
    l.appendChild(d);
}

refreshPorts();
</script>
</body>
</html>";
    }

    [ComVisible(true)]
    public sealed class Bridge
    {
        private readonly MainForm f;
        public Bridge(MainForm form) { f = form; }
        public void Connect(string p, string b) { f.Connect(p, b); }
        public void Disconnect() { f.Disconnect(); }
        public void ResetDevice() { f.ResetDevice(); }
        public void AutoConnect() { f.AutoConnect(); }
        public void SendCommand(string c) { f.SendCommand(c); }
        public string RefreshPorts() { return f.RefreshPorts(); }
        public void ClearLog() { f.ClearLog(); }
    }
}
