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

        public MainForm()
        {
            Text = "FeliciaDiag V0.2A - Škoda Felicia 1.3 MPI Diagnostika";
            Size = new Size(1280, 860);
            MinimumSize = new Size(1024, 700);
            StartPosition = FormStartPosition.CenterScreen;
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

            timer = new System.Windows.Forms.Timer { Interval = 40 };
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

        public void Connect(string portName, string baudStr)
        {
            int baud;
            if (!int.TryParse(baudStr, out baud) || baud <= 0) baud = 115200;

            try
            {
                ClosePort();
                lock (portLock)
                {
                    port = new SerialPort(portName, baud, Parity.None, 8, StopBits.One)
                    {
                        DtrEnable = true,
                        RtsEnable = true,
                        ReadTimeout = 1000,
                        WriteTimeout = 1000,
                        ReadBufferSize = 8192,
                        WriteBufferSize = 8192
                    };
                    port.DataReceived += ReadSerial;
                    port.Open();
                }

                Js("setConnected", "1", portName, baud.ToString());
                Status("Připojeno k " + portName + " (" + baud + " bd)", "good");
                Enqueue("[APP] Otevřen port " + portName + " na rychlosti " + baud + " bd");

                new Thread(() =>
                {
                    try
                    {
                        Thread.Sleep(400);
                        SendCommand("?");
                    }
                    catch { }
                })
                {
                    IsBackground = true
                }.Start();
            }
            catch (Exception ex)
            {
                Js("setConnected", "0", "", "");
                Status("Chyba při otevírání portu: " + ex.Message, "bad");
                Enqueue("[CHYBA] " + ex.Message);
            }
        }

        public void Disconnect()
        {
            ClosePort();
            Js("setConnected", "0", "", "");
            Status("Odpojeno", "idle");
            Enqueue("[APP] Port byl odpojen.");
        }

        public void AutoConnect()
        {
            string[] names = SerialPort.GetPortNames();
            Array.Sort(names);
            if (names.Length == 0)
            {
                Status("Nenalezen žádný COM port.", "bad");
                return;
            }

            string picked = names[names.Length - 1];
            Status("Automaticky vybrán port " + picked + ", připojuji...", "busy");
            Connect(picked, "115200");
        }

        public void SendCommand(string cmd)
        {
            lock (portLock)
            {
                if (port == null || !port.IsOpen)
                {
                    Status("Port není otevřený.", "bad");
                    return;
                }

                try
                {
                    port.Write(cmd + "\n");
                    Enqueue("[TX] " + cmd);
                    Status("Odeslán příkaz: " + cmd, "busy");
                }
                catch (Exception ex)
                {
                    Status("Chyba zápisu do portu: " + ex.Message, "bad");
                    Enqueue("[CHYBA TX] " + ex.Message);
                }
            }
        }

        public string RefreshPorts()
        {
            string[] names = SerialPort.GetPortNames();
            Array.Sort(names);
            string res = string.Join("|", names);
            string pref = names.Length > 0 ? names[0] : "COM6";
            Js("setPorts", res, pref);
            return res;
        }

        public void ClearLog()
        {
            Enqueue("[APP] Log byl vymazán.");
        }

        private void ReadSerial(object sender, SerialDataReceivedEventArgs e)
        {
            try
            {
                lock (portLock)
                {
                    if (port == null || !port.IsOpen) return;
                    string text = port.ReadExisting();
                    if (string.IsNullOrEmpty(text)) return;
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
            catch { }
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
            lock (portLock)
            {
                try
                {
                    if (port != null)
                    {
                        port.DataReceived -= ReadSerial;
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
<title>FeliciaDiag V0.2A</title>
<style>
* { box-sizing: border-box; user-select: none; -webkit-user-select: none; }
body { margin: 0; font-family: 'Segoe UI', Tahoma, Arial, sans-serif; background: #0b1120; color: #f1f5f9; overflow: hidden; height: 100vh; font-size: 15px; }
.app { display: flex; height: 100vh; width: 100vw; overflow: hidden; }

/* LEVÝ NAVIGAČNÍ PANEL */
.nav { width: 300px; background: #131d31; color: #eaf0f7; padding: 20px 16px; box-sizing: border-box; display: flex; flex-direction: column; border-right: 2px solid #1e293b; flex-shrink: 0; }
.brand { font-size: 28px; font-weight: 800; color: #38bdf8; letter-spacing: -0.5px; }
.sub { font-size: 14px; color: #94a3b8; margin-top: 4px; margin-bottom: 24px; font-weight: 500; }
.nav-group { margin-bottom: 18px; }
.nav-title { font-size: 11px; text-transform: uppercase; letter-spacing: 1px; color: #64748b; font-weight: 700; margin-bottom: 8px; padding-left: 6px; }
.nav button { width: 100%; margin: 5px 0; padding: 13px 16px; border: 1px solid #334155; background: #1e293b; color: #f8fafc; text-align: left; border-radius: 8px; cursor: pointer; font-size: 15px; font-weight: 600; font-family: inherit; transition: all 0.15s ease; outline: none; }
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
.top { background: #111a2e; border-bottom: 2px solid #1e293b; display: flex; align-items: center; justify-content: space-between; padding: 14px 24px; flex-shrink: 0; }
.top-title { font-size: 20px; font-weight: 800; color: #f8fafc; }
.top-sub { font-size: 13px; color: #94a3b8; margin-top: 2px; }

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
.btn { padding: 11px 18px; margin: 4px; border: 1px solid #334155; background: #1e293b; color: #f1f5f9; border-radius: 6px; cursor: pointer; font-size: 15px; font-weight: 700; font-family: inherit; transition: all 0.15s ease; display: inline-block; vertical-align: middle; line-height: normal; text-align: center; white-space: nowrap; }
.btn:hover { background: #334155; color: #ffffff; border-color: #475569; }
.btn-lg { padding: 14px 22px; font-size: 15px; border-radius: 8px; }
.btn-primary { background: #2563eb; color: #ffffff; border-color: #1d4ed8; }
.btn-primary:hover { background: #1d4ed8; border-color: #1e40af; }
.btn-danger { background: #dc2626; color: #ffffff; border-color: #b91c1c; }
.btn-danger:hover { background: #b91c1c; border-color: #991b1b; }
.btn-success { background: #059669; color: #ffffff; border-color: #047857; }
.btn-success:hover { background: #047857; border-color: #065f46; }

.btn-icon { display: inline-block; margin-right: 10px; font-size: 17px; vertical-align: -1px; line-height: 1; }

/* KARTY INFORMACÍ O ECU */
.info-table { width: 100%; border-collapse: collapse; margin-top: 6px; font-size: 16px; }
.info-table td { padding: 14px 16px; border-bottom: 1px solid #1e293b; }
.info-label { width: 240px; font-weight: 600; color: #94a3b8; font-size: 15px; }
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

.badge { display: inline-flex; align-items: center; gap: 4px; padding: 4px 10px; border-radius: 6px; font-size: 12px; font-weight: 700; }
.badge-verified { background: rgba(16,185,129,0.15); color: #34d399; border: 1px solid #059669; }
.badge-unverified { background: rgba(245,158,11,0.15); color: #fbbf24; border: 1px solid #d97706; }

/* CHYBOVÉ KÓDY (DTC) */
.dtc { border: 2px solid #ef4444; background: rgba(239,68,68,0.12); color: #fecaca; padding: 16px; margin: 12px 0; border-radius: 8px; font-family: Consolas, monospace; font-size: 16px; line-height: 1.5; }
.dtc-code { font-size: 20px; font-weight: 800; color: #f87171; margin-bottom: 4px; }
.dtc-desc { font-size: 16px; font-weight: 700; color: #ffffff; margin-bottom: 6px; }
.dtc-meta { font-size: 13px; color: #cbd5e1; }

.ok-box { border: 2px solid #10b981; background: rgba(16,185,129,0.12); color: #a7f3d0; padding: 18px 22px; border-radius: 8px; font-size: 17px; font-weight: 700; display: flex; align-items: center; gap: 12px; }
.empty-box { border: 1px dashed #475569; background: #0d1526; color: #94a3b8; padding: 22px; border-radius: 8px; text-align: center; font-size: 16px; }

/* SPODNÍ FIXNÍ KOMUNIKAČNÍ ZÁZNAMNÍK */
.bottom-log { flex-shrink: 0; background: #111a2e; border-top: 2px solid #1e293b; display: flex; flex-direction: column; }
.bottom-log-head { padding: 8px 18px; font-size: 14px; font-weight: 700; background: #172238; color: #f8fafc; display: flex; align-items: center; justify-content: space-between; border-bottom: 1px solid #1e293b; }
.log-fixed { height: 160px; margin: 0; background: #05080e; color: #38bdf8; font-family: Consolas, monospace; font-size: 13px; overflow-y: auto; padding: 10px 16px; white-space: pre-wrap; line-height: 1.45; border: none; border-radius: 0; }
</style>
</head>
<body>
<div class=""app"">
    <!-- LEVÁ NAVIGACE -->
    <div class=""nav"">
        <div class=""brand"">FeliciaDiag</div>
        <div class=""sub"">Verze V0.2A / KW1281</div>
        
        <div class=""nav-group"">
            <div class=""nav-title"">Diagnostika</div>
            <button id=""nav-main"" class=""active"" onclick=""show('main')""><span class=""nav-icon"">&#127968;</span> Hlavní přehled</button>
            <button id=""nav-faults"" onclick=""show('faults')""><span class=""nav-icon"">&#128269;</span> Paměť závad (02)</button>
            <button id=""nav-live"" onclick=""show('live')""><span class=""nav-icon"">&#128202;</span> Živá data (08)</button>
            <button id=""nav-ecu"" onclick=""show('ecu')""><span class=""nav-icon"">&#128196;</span> Informace o ECU (01)</button>
        </div>

        <div class=""nav-group"">
            <div class=""nav-title"">Komunikace a hardware</div>
            <button id=""nav-port"" onclick=""show('port')""><span class=""nav-icon"">&#9881;</span> Nastavení portu</button>
            <button onclick=""cmd('?')"" style=""margin-top: 10px; background: #0f172a;""><span class=""nav-icon"">&#10067;</span> Menu Arduina</button>
        </div>

        <div id=""status"" class=""status idle"">Startuji aplikaci...</div>
    </div>

    <!-- HLAVNÍ OBSAH -->
    <div class=""main"">
        <!-- HORNÍ LIŠTA -->
        <div class=""top"">
            <div>
                <div id=""title"" class=""top-title"">Hlavní přehled</div>
                <div class=""top-sub"">Škoda Felicia 1.3 MPI / SIMOS 2P / K-line diagnostika</div>
            </div>
            <div class=""port-bar"">
                <label>PORT:</label>
                <select id=""portSelTop"" onchange=""onPortChanged(this.value)""></select>
                <button class=""btn"" onclick=""refreshPorts()"" title=""Znovu prohledat porty""><span class=""btn-icon"" style=""margin-right:6px; font-size:14px;"">&#128259;</span>Obnovit</button>
                <button id=""btnConnectTop"" class=""btn btn-primary"" onclick=""toggleConnect()""><span class=""btn-icon"" style=""margin-right:6px; font-size:14px;"">&#9889;</span>Připojit</button>
                <span id=""connBadge"" style=""font-size: 14px; font-weight: 700; padding: 6px 12px; border-radius: 6px; background: rgba(239,68,68,0.2); color: #fca5a5;"">&#9679; Nepřipojeno</span>
            </div>
        </div>

        <!-- RX MONITOR LIŠTA -->
        <div class=""rx"">
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
                    <div class=""head"">Rychlé diagnostické akce</div>
                    <div class=""body"">
                        <button class=""btn btn-lg btn-primary"" onclick=""show('faults');cmd('f')""><span class=""btn-icon"">&#128269;</span>Vyčíst paměť závad</button>
                        <button class=""btn btn-lg btn-success"" onclick=""show('live');startLiveData()""><span class=""btn-icon"">&#128202;</span>Živá data (08)</button>
                        <button class=""btn btn-lg btn-danger"" onclick=""clearCodes()""><span class=""btn-icon"">&#128465;</span>Smazat paměť závad</button>
                        <button class=""btn btn-lg"" onclick=""cmd('t')""><span class=""btn-icon"">&#9889;</span>Test K-line linky</button>
                        <button class=""btn btn-lg"" onclick=""show('ecu');readEcuInfo()"" style=""background: #334155;""><span class=""btn-icon"">&#128196;</span>Informace o ECU</button>
                        <button class=""btn btn-lg"" onclick=""show('port')"" style=""background: #334155;""><span class=""btn-icon"">&#9881;</span>Nastavení portu</button>
                    </div>
                </div>

                <div class=""panel"">
                    <div class=""head"">Návod k použití a stav připojení</div>
                    <div class=""body"" style=""line-height: 1.6; font-size: 15px; color: #cbd5e1;"">
                        <div style=""display: flex; gap: 16px; flex-wrap: wrap;"">
                            <div style=""flex: 1; min-width: 260px; background: #080d18; padding: 16px; border-radius: 8px; border: 1px solid #1e293b;"">
                                <b style=""color: #38bdf8; font-size: 16px;"">&#128268; 1. Připojení k vozidlu</b>
                                <p style=""margin: 8px 0 0 0;"">Připojte K-line převodník k OBD-II konektoru Felicie (Pin 7 = K-line, Pin 4/5 = GND, Pin 16 = +12V) a zapněte zapalování.</p>
                            </div>
                            <div style=""flex: 1; min-width: 260px; background: #080d18; padding: 16px; border-radius: 8px; border: 1px solid #1e293b;"">
                                <b style=""color: #38bdf8; font-size: 16px;"">&#128269; 2. Paměť závad a živá data</b>
                                <p style=""margin: 8px 0 0 0;"">Kliknutím na <b>Vyčíst paměť závad</b> nebo <b>Živá data</b> proběhne inicializace KW1281 (9600 bd) a načtení diagnostických bloků z motorové jednotky SIMOS 2P.</p>
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

            <!-- 4. IDENTIFIKACE ECU -->
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

            <!-- 5. NASTAVENÍ PORTU -->
            <div id=""port"" class=""view"" style=""display:none"">
                <div class=""panel"">
                    <div class=""head"">Nastavení a správa sériového COM portu</div>
                    <div class=""body"">
                        <div style=""margin-bottom: 20px; font-size: 16px;"">
                            <label style=""font-weight: 700; display: inline-block; width: 160px;"">Vyber port:</label>
                            <select id=""portSel"" style=""padding: 10px 14px; font-size: 15px; min-width: 220px; border-radius: 6px; border: 1px solid #475569; background: #0f172a; color: white;"" onchange=""onPortChanged(this.value)""></select>
                            <button class=""btn"" onclick=""refreshPorts()""><span class=""btn-icon"" style=""margin-right:6px; font-size:14px;"">&#128259;</span>Obnovit seznam</button>
                        </div>
                        <div style=""margin-bottom: 20px; font-size: 16px;"">
                            <label style=""font-weight: 700; display: inline-block; width: 160px;"">Rychlost (Baud):</label>
                            <input id=""baud"" value=""115200"" style=""padding: 9px 14px; width: 150px; font-size: 15px; background: #0f172a; color: white; border: 1px solid #334155; border-radius: 6px;"">
                        </div>
                        <div style=""margin-top: 24px; padding-top: 18px; border-top: 1px solid #1e293b;"">
                            <button class=""btn btn-lg btn-primary"" onclick=""connect()""><span class=""btn-icon"">&#9889;</span>Připojit k vybranému portu</button>
                            <button class=""btn btn-lg btn-danger"" onclick=""disconnect()""><span class=""btn-icon"">&#10060;</span>Odpojit</button>
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
    var views = ['main', 'faults', 'live', 'ecu', 'port'];
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
        var p = rawList[i];
        if (!known[p]) {
            known[p] = true;
            ports.push({ name: p, detected: true });
        }
    }
    
    for (var j = 1; j <= 16; j++) {
        var c = 'COM' + j;
        if (!known[c]) {
            known[c] = true;
            ports.push({ name: c, detected: false });
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
            o.text = item.detected ? (item.name + ' (detekováno)') : item.name;
            s.add(o);
            if (item.name == pref) s.selectedIndex = m;
        }
    }
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

function autoConnect() {
    window.external.AutoConnect();
}

function disconnect() {
    window.external.Disconnect();
}

function cmd(c) {
    window.external.SendCommand(c);
}

function startLiveData() {
    var box = id('liveStatusBox');
    if (box) {
        box.innerHTML = '<div class=""busy"" style=""padding: 14px 18px; border-radius: 8px;"">⏳ <b>Inicializuji KW1281 a spouštím kontinuální čtení živých dat (Funkce 08)...</b><br>Vyčkejte 2-3 sekundy na navázání komunikace s ECU.</div>';
    }
    cmd('l');
}

function setLive(sample, rpm, volt, thr, temp, inj, lambda) {
    if (id('live_rpm')) id('live_rpm').textContent = (rpm !== undefined && rpm !== '') ? (rpm + ' ot/min') : '--';
    if (id('live_volt')) id('live_volt').textContent = (volt !== undefined && volt !== '') ? (volt + ' V') : '--';
    if (id('live_thr')) id('live_thr').textContent = (thr !== undefined && thr !== '') ? (thr + ' °') : '--';
    if (id('live_temp')) id('live_temp').textContent = (temp && temp !== '--') ? (temp + ' °C') : '--';
    if (id('live_inj')) id('live_inj').textContent = (inj && inj !== '--') ? (inj + ' ms') : '--';
    if (id('live_lambda')) id('live_lambda').textContent = (lambda && lambda !== '--') ? (lambda + ' V') : '--';
    
    var box = id('liveStatusBox');
    if (box) {
        box.innerHTML = '<div class=""ok-box"" style=""padding: 12px 18px;""><span style=""font-size:22px;"">🔄</span> <div><b>Aktivní přenos živých dat z ECU!</b> (přijat vzorek #' + sample + ')</div></div>';
    }
}

function setLiveDone() {
    var box = id('liveStatusBox');
    if (box) {
        box.innerHTML = '<div class=""empty-box"" style=""padding: 12px 18px;"">✅ Cyklus čtení živých dat byl dokončen. Pro další čtení klikněte znovu na <b>Spustit čtení živých dat</b>.</div>';
    }
}

function readEcuInfo() {
    id('part2').textContent = 'Čtu z ECU...';
    id('part2').style.color = '#fbbf24';
    id('comp2').textContent = 'Čtu z ECU...';
    id('comp2').style.color = '#fbbf24';
    id('extra2').textContent = 'Čtu z ECU...';
    id('extra2').style.color = '#fbbf24';
    if (id('extra4')) {
        id('extra4').textContent = 'Čtu z ECU...';
        id('extra4').style.color = '#fbbf24';
    }
    var box = id('ecuStatusBox');
    if (box) {
        box.innerHTML = '<div class=""busy"" style=""padding: 14px 18px; border-radius: 8px;"">⏳ <b>Navazuji spojení s ECU (5-baud slow init na 0x01)...</b><br>Inicializuji motorovou jednotku a čtu identifikační bloky KW1281. Vyčkejte 2-3 sekundy.</div>';
    }
    cmd('i');
}

function clearCodes() {
    if (confirm('Opravdu chcete odeslat příkaz pro smazání celé paměti závad řídicí jednotky ECU?')) {
        cmd('c');
    }
}

function clearLog() {
    logText = '';
    if (id('log')) id('log').textContent = '';
    window.external.ClearLog();
}

function setStatus(t, l) {
    var s = id('status');
    if (!s) return;
    s.className = 'status ' + (l || 'idle');
    s.textContent = t;
}

function setConnected(on, p, b) {
    isConnected = (on == '1');
    var btn = id('btnConnectTop');
    var badge = id('connBadge');
    if (isConnected) {
        if (btn) {
            btn.innerHTML = '<span class=""btn-icon"" style=""margin-right:6px; font-size:14px;"">&#10060;</span>Odpojit';
            btn.className = 'btn btn-danger';
        }
        if (badge) {
            badge.innerHTML = '&#9679; Připojeno: ' + p + ' (' + b + ' bd)';
            badge.style.background = 'rgba(16,185,129,0.2)';
            badge.style.color = '#34d399';
        }
        onPortChanged(p);
    } else {
        if (btn) {
            btn.innerHTML = '<span class=""btn-icon"" style=""margin-right:6px; font-size:14px;"">&#9889;</span>Připojit';
            btn.className = 'btn btn-primary';
        }
        if (badge) {
            badge.innerHTML = '&#9679; Nepřipojeno';
            badge.style.background = 'rgba(239,68,68,0.2)';
            badge.style.color = '#fca5a5';
        }
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

function setRxStats(bytes, lines, last, preview) {
    if (id('rxBytes')) id('rxBytes').textContent = bytes;
    if (id('rxLines')) id('rxLines').textContent = lines;
    if (id('rxState')) id('rxState').textContent = (Number(bytes) > 0) ? 'aktivní příjem' : 'čeká';
    if (id('rxLast')) id('rxLast').textContent = preview || '--';
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
        public void AutoConnect() { f.AutoConnect(); }
        public void SendCommand(string c) { f.SendCommand(c); }
        public string RefreshPorts() { return f.RefreshPorts(); }
        public void ClearLog() { f.ClearLog(); }
    }
}