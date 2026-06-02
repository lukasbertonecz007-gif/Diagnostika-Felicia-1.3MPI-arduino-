using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO.Ports;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace FeliciaKLineDiagApp
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }

    public sealed class MainForm : Form
    {
        private const string AppVersion = "0.3";
        private readonly WebBrowser browser;
        private readonly Timer uiTimer;
        private readonly ConcurrentQueue<string> lines = new ConcurrentQueue<string>();
        private readonly StringBuilder partial = new StringBuilder();
        private readonly StringBuilder rawLog = new StringBuilder();
        private SerialPort port;
        private bool ready;
        private int rxChars;
        private int rxLines;
        private string lastRx = "";

        public MainForm()
        {
            Text = "Felicia K-Line Diagnostika v" + AppVersion;
            Width = 1120;
            Height = 760;
            MinimumSize = new Size(920, 620);
            StartPosition = FormStartPosition.CenterScreen;

            browser = new WebBrowser { Dock = DockStyle.Fill, ObjectForScripting = new Bridge(this), ScriptErrorsSuppressed = true };
            browser.DocumentCompleted += delegate
            {
                ready = true;
                Js("setPorts", string.Join("|", SerialPort.GetPortNames()), "COM6");
                Js("setStatus", "Připraveno. Vyber COM port nebo klikni Najít Arduino.", "idle");
                if (SerialPort.GetPortNames().Length == 1) BeginInvoke(new Action(AutoConnect));
            };
            Controls.Add(browser);
            browser.DocumentText = Html;

            uiTimer = new Timer { Interval = 80 };
            uiTimer.Tick += delegate { Flush(); };
            uiTimer.Start();
            FormClosing += delegate { ClosePort(); };
        }

        public string RefreshPorts() { return string.Join("|", SerialPort.GetPortNames()); }

        public void AutoConnect()
        {
            foreach (string p in SerialPort.GetPortNames())
            {
                try
                {
                    Connect(p, "115200");
                    return;
                }
                catch { }
            }
            Status("Arduino nebylo nalezeno.", "bad");
        }

        public void Connect(string name, string baudText)
        {
            if (string.IsNullOrWhiteSpace(name)) { Status("Není vybraný COM port.", "bad"); return; }
            int baud;
            if (!int.TryParse(baudText, out baud)) baud = 115200;
            try
            {
                ClosePort();
                rxChars = rxLines = 0;
                lastRx = "";
                port = new SerialPort(name.Trim(), baud, Parity.None, 8, StopBits.One) { Encoding = Encoding.ASCII, ReadTimeout = 200, WriteTimeout = 1000, DtrEnable = true, RtsEnable = true };
                port.DataReceived += ReadSerial;
                port.Open();
                Js("setConnected", "1", port.PortName, port.BaudRate.ToString(CultureInfo.InvariantCulture));
                Status("Připojeno k " + port.PortName + ".", "good");
                Enqueue("[APP] Připojeno k " + port.PortName);
                Timer t = new Timer { Interval = 900 };
                t.Tick += delegate { t.Stop(); t.Dispose(); SendCommand("?"); };
                t.Start();
            }
            catch (Exception ex)
            {
                ClosePort();
                Status("Připojení selhalo: " + ex.Message, "bad");
            }
        }

        public void Disconnect()
        {
            ClosePort();
            Js("setConnected", "0", "", "");
            Status("Odpojeno.", "idle");
        }

        public void SendCommand(string command)
        {
            if (port == null || !port.IsOpen) { Status("Nejdřív se připoj k Arduinu.", "bad"); return; }
            char c = string.IsNullOrEmpty(command) ? '?' : command[0];
            if (c == 'c' || c == 'C') Js("clearDtc");
            try
            {
                port.Write(new[] { (byte)c }, 0, 1);
                Enqueue("[APP] TX command: " + c);
                if (c == 'f') Status("Čtu paměť závad.", "busy");
                if (c == 'l') Status("Čtu živá data.", "busy");
                if (c == 'c') Status("Mažu paměť závad a ověřuji výsledek.", "busy");
            }
            catch (Exception ex) { Status("Odeslání selhalo: " + ex.Message, "bad"); }
        }

        public void ClearLog()
        {
            rawLog.Length = 0;
            rxChars = rxLines = 0;
            lastRx = "";
            Js("setRxStats", "0", "0", "čeká", "");
        }

        private void ReadSerial(object sender, SerialDataReceivedEventArgs e)
        {
            try
            {
                string text = port.ReadExisting();
                rxChars += text.Length;
                lock (partial)
                {
                    foreach (char ch in text)
                    {
                        if (ch == '\r') continue;
                        if (ch == '\n') { string line = partial.ToString(); partial.Length = 0; if (line.Length > 0) lines.Enqueue(line); }
                        else partial.Append(ch);
                    }
                }
            }
            catch (Exception ex) { lines.Enqueue("[APP] Chyba čtení: " + ex.Message); }
        }

        private void Flush()
        {
            string line;
            int safety = 0;
            while (safety++ < 120 && lines.TryDequeue(out line))
            {
                if (!line.StartsWith("[APP]")) { rxLines++; lastRx = line; }
                rawLog.AppendLine(line);
                Js("appendLog", line);
                Parse(line);
            }
            Js("setRxStats", rxChars.ToString(CultureInfo.InvariantCulture), rxLines.ToString(CultureInfo.InvariantCulture), DateTime.Now.ToString("HH:mm:ss"), lastRx);
        }

        private void Parse(string line)
        {
            string[] p = line.Split('|');
            if (p.Length > 0 && p[0] == "APP_BOOT") { Status("Arduino firmware je připravený.", "good"); return; }
            if (p.Length >= 3 && p[0] == "APP_ID_FIELD") { if (p[1] == "1") Js("setPart", p[2].Trim()); else if (p[1] == "2") Js("setComponent", p[2].Trim()); else Js("setExtra", p[2].Trim()); return; }
            if (p.Length >= 5 && p[0] == "APP_LIVE") { Js("setLive", p[2], p[3], p[4], p[1]); Status("Živá data aktualizována.", "good"); return; }
            if (p.Length >= 4 && p[0] == "APP_DTC") { AddDtc(p[1], p[2], p[3]); Status("Nalezena závada ECU.", "bad"); return; }
            if (p[0] == "APP_DTC_NONE") { Js("setNoFaults"); Status("ECU nehlásí žádné závady.", "good"); return; }
            if (p.Length >= 2 && p[0] == "APP_CLEAR") { Js("setClearResult", "Mazání závad: " + p[1]); Status("Mazání závad: " + p[1], p[1] == "OK" ? "good" : "bad"); return; }
            if (p.Length >= 2 && p[0] == "APP_HW") { Status(p[1] == "OK" ? "K-line převodník vypadá v pořádku." : "Test převodníku selhal.", p[1] == "OK" ? "good" : "bad"); return; }
            if (line.IndexOf("timeout", StringComparison.OrdinalIgnoreCase) >= 0 || line.IndexOf("failed", StringComparison.OrdinalIgnoreCase) >= 0) Status("Komunikace hlásí timeout nebo chybu, koukni do raw logu.", "bad");
        }

        private void AddDtc(string code, string raw, string status)
        {
            string title = Dtc.ContainsKey(code) ? Dtc[code] : "Neznámá VAG závada";
            Js("addDtc", code, title, "raw=" + raw + " status=0x" + status);
        }

        private void Status(string text, string level) { Js("setStatus", text, level); }
        private void Enqueue(string text) { lines.Enqueue(text); }
        private void Js(string name, params object[] args) { if (ready && browser.Document != null) try { browser.Document.InvokeScript(name, args); } catch { } }
        private void ClosePort() { try { if (port != null) { port.DataReceived -= ReadSerial; if (port.IsOpen) port.Close(); port.Dispose(); } } catch { } port = null; }

        private static readonly Dictionary<string, string> Dtc = new Dictionary<string, string>
        {
            { "00513", "Snímač otáček motoru G28" }, { "00518", "Potenciometr škrticí klapky G69" },
            { "00522", "Čidlo teploty chladicí kapaliny G62" }, { "00533", "Regulace volnoběhu" },
            { "00537", "Lambda regulace" }, { "00561", "Přizpůsobení směsi" },
            { "00668", "Napájecí napětí svorka 30" }, { "01087", "Základní nastavení nebylo provedeno" },
            { "01165", "Jednotka škrticí klapky" }, { "01247", "Ventil odvětrání nádrže N80" },
            { "16500", "P0116 čidlo teploty chladicí kapaliny" }, { "16514", "P0130 lambda sonda" },
            { "16555", "P0171 směs příliš chudá" }, { "16556", "P0172 směs příliš bohatá" },
            { "16684", "P0300 náhodné vynechávání zapalování" }, { "16685", "P0301 vynechávání válec 1" },
            { "16686", "P0302 vynechávání válec 2" }, { "16687", "P0303 vynechávání válec 3" },
            { "16688", "P0304 vynechávání válec 4" }, { "16705", "P0321 snímač otáček motoru" },
            { "17978", "P1570 blokování startu imobilizérem" }, { "65535", "Vnitřní chyba řídicí jednotky" }
        };

        private const string Html = @"<!doctype html><html><head><meta http-equiv='X-UA-Compatible' content='IE=edge'><meta charset='utf-8'><title>FeliciaDiag v0.3</title><style>
body{margin:0;font-family:Segoe UI,Arial;background:#eef2f5;color:#182230}.app{display:flex;height:100vh}.nav{width:260px;background:#172232;color:#eaf0f7;padding:14px;box-sizing:border-box}.brand{font-size:26px;font-weight:800}.sub{font-size:12px;color:#9fb1c7;margin-bottom:18px}.nav button{width:100%;margin:5px 0;padding:11px;border:1px solid #33465d;background:#223248;color:white;text-align:left;border-radius:4px;cursor:pointer}.nav button:hover{background:#2b7fc3}.status{margin-top:14px;padding:10px;border:1px solid #3b4d64;background:#101820;font-size:12px}.good{border-color:#2f8f5b;color:#bcf6d1}.bad{border-color:#b94a55;color:#ffd5d8}.busy{border-color:#b9842d;color:#ffe6b5}.main{flex:1;display:flex;flex-direction:column}.top{height:64px;background:white;border-bottom:1px solid #cad4df;display:flex;align-items:center;justify-content:space-between;padding:0 18px}.rx{background:#101820;color:#d9e8f8;padding:8px 18px;font-size:12px}.rx b{color:white}.content{flex:1;overflow:auto;padding:16px}.panel{background:white;border:1px solid #ccd6e0;border-radius:6px;margin-bottom:14px}.head{padding:11px 13px;border-bottom:1px solid #dde5ee;font-weight:700;background:#f8fafc}.body{padding:13px}.grid{display:flex;gap:12px;flex-wrap:wrap}.card{flex:1;min-width:180px;background:#101820;color:white;padding:16px;border-radius:6px}.val{font-size:36px;font-weight:800}.log{height:230px;background:#0d1117;color:#d4dde8;font:12px Consolas,monospace;overflow:auto;padding:10px;white-space:pre-wrap}.dtc{border:1px solid #c77680;background:#fff3f4;color:#7c1f2a;padding:10px;margin:8px 0;border-radius:4px;font:12px Consolas,monospace}.ok{border:1px solid #62a97b;background:#f0fff5;color:#14532d;padding:10px;border-radius:4px}.small{padding:8px 10px;margin:3px;border:1px solid #aebdca;background:#eef3f8;border-radius:4px}.primary{background:#2a7fc6;color:white}.danger{background:#b4232e;color:white}
</style></head><body><div class='app'><div class='nav'><div class='brand'>FeliciaDiag</div><div class='sub'>verze 0.3 / KW1281</div><button onclick='show("main")'>Hlavní obrazovka</button><button onclick='show("faults")'>Paměť závad - 02</button><button onclick='show("live")'>Měřené hodnoty - 08</button><button onclick='show("port")'>Port</button><button onclick='cmd("?")'>Menu Arduina</button><div id='status' class='status'>Startuji...</div></div><div class='main'><div class='top'><div><b id='title'>Hlavní obrazovka</b><div>Škoda Felicia 1.3 MPI / SIMOS 2P / K-line</div></div><div id='conn'>Nepřipojeno</div></div><div class='rx'>Komunikace: <b id='rxState'>čeká</b> | RX znaky: <b id='rxBytes'>0</b> | RX řádky: <b id='rxLines'>0</b> | <span id='rxLast'></span></div><div class='content'><div id='main' class='view'><div class='panel'><div class='head'>Rychlý start</div><div class='body'><button class='small primary' onclick='show("faults");cmd("f")'>Číst závady</button><button class='small danger' onclick='clearCodes()'>Smazat závady</button><button class='small primary' onclick='show("live");cmd("l")'>Živá data</button><button class='small' onclick='cmd("t")'>Test K-line</button></div></div><div class='grid'><div class='card'>Otáčky<br><span id='rpm' class='val'>--</span> rpm</div><div class='card'>Napětí<br><span id='volt' class='val'>--</span> V</div><div class='card'>Klapka<br><span id='thr' class='val'>--</span> deg</div></div><div class='panel'><div class='head'>ECU</div><div class='body'>Číslo: <span id='part'>--</span><br>Komponenta: <span id='comp'>--</span><br>Info: <span id='extra'>--</span></div></div></div><div id='faults' class='view' style='display:none'><div class='panel'><div class='head'>Paměť závad</div><div class='body'><button class='small primary' onclick='cmd("f")'>Číst</button><button class='small danger' onclick='clearCodes()'>Smazat</button><div id='faultList'>Zatím nečteno.</div></div></div></div><div id='live' class='view' style='display:none'><div class='panel'><div class='head'>Živá data</div><div class='body'><button class='small primary' onclick='cmd("l")'>Spustit</button><div class='grid'><div class='card'>Otáčky<br><span id='rpm2' class='val'>--</span></div><div class='card'>Napětí<br><span id='volt2' class='val'>--</span></div><div class='card'>Klapka<br><span id='thr2' class='val'>--</span></div></div></div></div></div><div id='port' class='view' style='display:none'><div class='panel'><div class='head'>Port</div><div class='body'><select id='portSel'></select><input id='baud' value='115200'><button class='small' onclick='refreshPorts()'>Obnovit</button><button class='small primary' onclick='autoConnect()'>Najít Arduino</button><button class='small primary' onclick='connect()'>Připojit</button><button class='small' onclick='disconnect()'>Odpojit</button></div></div></div><div class='panel'><div class='head'>Surový log Arduino / ECU <button class='small' onclick='clearLog()'>Vymazat</button></div><pre id='log' class='log'></pre></div></div></div></div><script>
var logText='';function id(x){return document.getElementById(x)}function esc(s){return String(s).replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;')}function show(v){['main','faults','live','port'].forEach(function(x){id(x).style.display=x==v?'block':'none'});id('title').innerHTML={main:'Hlavní obrazovka',faults:'Paměť závad - 02',live:'Měřené hodnoty - 08',port:'Port'}[v]}function refreshPorts(){setPorts(window.external.RefreshPorts(),'COM6')}function setPorts(list,pref){var s=id('portSel');s.options.length=0;var a=list?String(list).split('|'):[];if(!a.length||a[0]=='')a=['COM6'];a.forEach(function(p,i){var o=document.createElement('option');o.text=o.value=p;s.add(o);if(p==pref)s.selectedIndex=i})}function connect(){window.external.Connect(id('portSel').value,id('baud').value)}function autoConnect(){window.external.AutoConnect()}function disconnect(){window.external.Disconnect()}function cmd(c){window.external.SendCommand(c)}function clearCodes(){if(confirm('Opravdu smazat celou paměť závad ECU?'))cmd('c')}function clearLog(){logText='';id('log').textContent='';window.external.ClearLog()}function setStatus(t,l){id('status').className='status '+(l||'');id('status').textContent=t}function setConnected(on,p,b){id('conn').textContent=on=='1'?'Připojeno: '+p+' / '+b:'Nepřipojeno'}function appendLog(line){logText+=line+'\n';if(logText.length>70000)logText=logText.substr(logText.length-60000);id('log').textContent=logText;id('log').scrollTop=id('log').scrollHeight}function setRxStats(bytes,lines,last,preview){id('rxBytes').textContent=bytes;id('rxLines').textContent=lines;id('rxState').textContent=Number(bytes)>0?'data chodí':'čeká';id('rxLast').textContent=preview||''}function setPart(v){id('part').textContent=v}function setComponent(v){id('comp').textContent=v}function setExtra(v){id('extra').textContent=v}function setLive(r,v,t,s){id('rpm').textContent=id('rpm2').textContent=r;id('volt').textContent=id('volt2').textContent=v;id('thr').textContent=id('thr2').textContent=t}function clearDtc(){id('faultList').innerHTML='Čekám na odpověď ECU...'}function setNoFaults(){id('faultList').innerHTML='<div class="ok">ECU nehlásí žádné závady.</div>'}function setClearResult(t){appendLog('[APP] '+t)}function addDtc(code,title,raw){var d=document.createElement('div');d.className='dtc';d.textContent='VAG kód '+code+'\n'+title+'\n'+raw;var l=id('faultList');if(l.textContent.indexOf('Zatím')>=0)l.innerHTML='';l.appendChild(d)}refreshPorts();</script></body></html>";
    }

    [ComVisible(true)]
    public sealed class Bridge
    {
        private readonly MainForm f;
        public Bridge(MainForm form) { f = form; }
        public string RefreshPorts() { return f.RefreshPorts(); }
        public void AutoConnect() { f.AutoConnect(); }
        public void Connect(string p, string b) { f.Connect(p, b); }
        public void Disconnect() { f.Disconnect(); }
        public void SendCommand(string c) { f.SendCommand(c); }
        public void ClearLog() { f.ClearLog(); }
    }
}
