using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace AliyaWebLauncher
{
    // 双击即用：定位项目 -> 找/装 Node -> 装依赖 -> 写 .env -> 起服务 -> 打印访问方式 -> 开浏览器
    // 目标环境：Windows 10/11 x64，用系统自带的 csc.exe 编译，不依赖 VS 或 dotnet SDK。
    // 语言版本受 csc v4.0.30319 限制（C# 5），因此这里不使用字符串内插、?. 等新语法。
    class Program
    {
        const string LauncherVersion = "1.4.0";
        const string NodeVersion = "v24.21.0";
        const string NodeFolder = "node-v24.21.0-win-x64";
        const string NpmMirror = "https://registry.npmmirror.com";
        const int DefaultPort = 5000;
        const int PortTries = 10;

        // 依次尝试：官方源不通时自动走国内镜像，避免用户手动改脚本
        static readonly string[] NodeZipUrls = new string[]
        {
            "https://nodejs.org/dist/" + NodeVersion + "/" + NodeFolder + ".zip",
            "https://cdn.npmmirror.com/binaries/node/" + NodeVersion + "/" + NodeFolder + ".zip",
            "https://registry.npmmirror.com/-/binary/node/" + NodeVersion + "/" + NodeFolder + ".zip",
            "https://npmmirror.com/mirrors/node/" + NodeVersion + "/" + NodeFolder + ".zip"
        };

        static string BaseDir;    // 项目根（含 Web/server.js 的那一层）
        static string WebDir;     // <根>/Web
        static string RuntimeDir; // <根>/.node-runtime（Node 不在系统里时的落脚点）
        static Process server;

        static bool NoBrowser, NoPause, SkipInstall, NoDownload, Lan, ShowHelp;
        static string GivenKey;
        static int Port = DefaultPort;

        static int Main(string[] args)
        {
            try { Console.OutputEncoding = Encoding.UTF8; } catch { }
            Console.Title = "AliyaWeb 启动器 v" + LauncherVersion;

            foreach (string a in args)
            {
                string lower = a.ToLower(CultureInfo.InvariantCulture);
                if (lower == "--no-browser") NoBrowser = true;
                else if (lower == "--no-pause") NoPause = true;
                else if (lower == "--skip-install") SkipInstall = true;
                else if (lower == "--no-download") NoDownload = true;
                else if (lower == "--lan") Lan = true;
                else if (lower == "--help" || lower == "-h" || lower == "/?") ShowHelp = true;
                else if (lower.StartsWith("--key=")) GivenKey = a.Substring(6).Trim();
                else if (lower.StartsWith("--port="))
                {
                    int p;
                    if (int.TryParse(a.Substring(7), out p)) Port = p;
                }
            }

            Banner();
            if (ShowHelp) { Help(); PauseIfNeeded(0); return 0; }

            if (!LocateProject())
                return Fail("没找到 Web/server.js。请把本程序和整个项目文件夹放在一起（解压到同一层），再双击。");

            Info("项目位置: " + BaseDir);

            string nodeExe = ResolveNode();
            if (nodeExe == null) return Fail("系统里没有 Node.js，且自动下载失败或被跳过。请安装 Node.js LTS 后重试：https://nodejs.org/zh-cn/download");

            if (!EnsureDependencies(nodeExe))
                return Fail("依赖安装失败，请看上面的 npm 报错。网络受限时可先执行: cd Web && npm install");

            int port = PickPort();
            string host = ResolveHost();

            if (!PrepareEnv(port, host)) return Fail("无法写入 Web/.env，检查该目录是否可写（不要放在只读位置或 C:\\Program Files）。");

            if (!StartServer(nodeExe, port, host)) return Fail("服务启动失败。");

            if (!WaitUntilReady(60, port))
            {
                if (server != null && server.HasExited)
                    return Fail("Node 进程已退出（代码 " + SafeExitCode() + "），服务没能启动。请看上面的日志 —— 常见原因：端口 "
                        + port + " 被占用、.env 里 PORT 不是数字、依赖没装好（Web/node_modules 缺失）。");
                Warn("60 秒内没探测到 /api/test，服务可能还在启动。仍然尝试打开网页，打不开就看上面的日志。");
            }

            PrintAccess(port, host);
            OpenBrowser(port);
            Info("服务运行中。关闭本窗口即可停止（或在网页里继续聊天）。");

            if (server != null && !server.HasExited)
            {
                try { server.WaitForExit(); }
                catch { }
                Info("Node 进程已退出，代码 " + SafeExitCode());
            }

            PauseIfNeeded(0);
            return 0;
        }

        static void Banner()
        {
            Console.WriteLine("==============================================");
            Console.WriteLine("  AliyaWeb 一键启动 v" + LauncherVersion);
            Console.WriteLine("  自动配置环境 -> 启动服务 -> 打开网页");
            Console.WriteLine("==============================================");
        }

        static void Help()
        {
            Console.WriteLine();
            Console.WriteLine("参数：");
            Console.WriteLine("  --lan             允许同一局域网内的其它设备访问（HOST=0.0.0.0）");
            Console.WriteLine("  --port=5000       指定端口，被占用时自动顺延");
            Console.WriteLine("  --key=sk-xxx      免交互传入 DeepSeek API Key");
            Console.WriteLine("  --no-browser      只起服务，不自动打开浏览器");
            Console.WriteLine("  --no-download     不自动下载便携版 Node（系统里没有 node 就直接报错）");
            Console.WriteLine("  --skip-install    跳过 npm install");
            Console.WriteLine("  --no-pause        结束后不等待按键，便于脚本调用");
            Console.WriteLine();
            Console.WriteLine("默认只监听 127.0.0.1，也就是仅本机可访问。");
        }

        // ---------- 定位项目 ----------

        static bool LocateProject()
        {
            string start = AppDomain.CurrentDomain.BaseDirectory;
            if (IsProjectRoot(start)) return Adopt(start);

            // exe 放在 tools\ 之类的子目录里（用户自己编译的场景），往上级找
            string up = start;
            for (int i = 0; i < 2; i++)
            {
                try { up = Path.GetDirectoryName(Path.GetFullPath(up)); }
                catch { up = null; }
                if (up == null || up.Length == 0) break;
                if (IsProjectRoot(up)) return Adopt(up);
            }

            // 容忍 zip 解压后多套一层（GitHub Release 的 zip 常有这种情况）
            foreach (string d in SafeDirectories(start, 0))
            {
                if (IsProjectRoot(d)) return Adopt(d);
                foreach (string e in SafeDirectories(d, 1))
                    if (IsProjectRoot(e)) return Adopt(e);
            }
            return false;
        }

        static bool IsProjectRoot(string dir)
        {
            return dir != null && File.Exists(Path.Combine(dir, "Web", "server.js"));
        }

        static bool Adopt(string dir)
        {
            BaseDir = Path.GetFullPath(dir);
            WebDir = Path.Combine(BaseDir, "Web");
            RuntimeDir = Path.Combine(BaseDir, ".node-runtime");
            return true;
        }

        static IEnumerable<string> SafeDirectories(string parent, int depth)
        {
            List<string> list = new List<string>();
            try
            {
                foreach (string d in Directory.GetDirectories(parent))
                {
                    string name = Path.GetFileName(d);
                    if (name.StartsWith(".")) continue;
                    if (name.Equals("node_modules", StringComparison.OrdinalIgnoreCase)) continue;
                    if (depth > 0 && name.Length > 0 && name[0] == '_') continue;
                    list.Add(d);
                }
            }
            catch { }
            return list;
        }

        // ---------- Node 运行时 ----------

        static string ResolveNode()
        {
            string found = SearchPathFor("node.exe");
            if (found != null) { Info("Node.js: " + found + "  " + NodeVersionProbe(found)); return found; }

            string portable = Path.Combine(RuntimeDir, NodeFolder);
            if (File.Exists(Path.Combine(portable, "node.exe")))
            {
                Info("Node.js: 使用本目录已下载的便携版");
                return Path.Combine(portable, "node.exe");
            }

            if (NoDownload) { Warn("--no-download，且系统里没有 Node.js。"); return null; }

            Warn("系统 PATH 里没有 Node.js，将下载便携版运行时（约 36 MB，只放在项目目录内，不写注册表、不需要管理员权限）。");
            if (!DownloadPortableNode()) return null;
            string node = Path.Combine(portable, "node.exe");
            return File.Exists(node) ? node : null;
        }

        static bool DownloadPortableNode()
        {
            Directory.CreateDirectory(RuntimeDir);
            string zip = Path.Combine(RuntimeDir, NodeFolder + ".zip");
            string target = Path.Combine(RuntimeDir, NodeFolder);

            for (int i = 0; i < NodeZipUrls.Length; i++)
            {
                string url = NodeZipUrls[i];
                Info((i == 0 ? "正在下载 " : "换源重试 ") + url);
                if (!Fetch(url, zip)) continue;
                if (new FileInfo(zip).Length < 1000000L)
                {
                    Warn("下载内容过小（" + new FileInfo(zip).Length + " 字节），判定为不完整，换下一个源。");
                    try { File.Delete(zip); } catch { }
                    continue;
                }

                Info("下载完成，正在解压...");
                try
                {
                    if (Directory.Exists(target)) Directory.Delete(target, true);
                    ZipFile.ExtractToDirectory(zip, RuntimeDir);
                }
                catch (Exception ex)
                {
                    Warn("解压失败: " + ex.Message + "  换下一个源重试。");
                    continue;
                }
                try { File.Delete(zip); } catch { }

                if (!File.Exists(Path.Combine(target, "node.exe")))
                {
                    Warn("解压完成但找不到 node.exe，换下一个源。");
                    continue;
                }
                Ok("便携版 Node 就绪: " + target);
                return true;
            }
            return false;
        }

        static bool Fetch(string url, string into)
        {
            try
            {
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12
                    | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls;

                WebClient wc = new WebClient();
                ManualResetEvent done = new ManualResetEvent(false);
                Exception error = null;
                int lastPct = -10;
                wc.DownloadProgressChanged += delegate(object s, DownloadProgressChangedEventArgs e)
                {
                    if (e.ProgressPercentage >= lastPct + 10)
                    {
                        lastPct = e.ProgressPercentage;
                        Info("  下载中 " + e.ProgressPercentage + "%  (" + (e.BytesReceived / 1048576L)
                            + " / " + (e.TotalBytesToReceive / 1048576L) + " MB)");
                    }
                };
                wc.DownloadFileCompleted += delegate(object s, System.ComponentModel.AsyncCompletedEventArgs e)
                {
                    error = e.Error;
                    done.Set();
                };
                wc.DownloadFileAsync(new Uri(url), into);
                if (!done.WaitOne(600000))
                {
                    try { wc.CancelAsync(); } catch { }
                    Warn("下载超时（10 分钟）。");
                    return false;
                }
                if (error != null)
                {
                    Warn("下载失败: " + error.Message);
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                Warn("下载时异常: " + ex.Message);
                return false;
            }
        }

        static string NodeVersionProbe(string nodeExe)
        {
            try
            {
                string outp = RunCapture(nodeExe, "-v", BaseDir, 8000);
                return outp.Trim();
            }
            catch { return "(版本未知)"; }
        }

        static string SearchPathFor(string exe)
        {
            string path = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (string raw in path.Split(';'))
            {
                string dir = raw.Trim().Trim('"');
                if (dir.Length == 0) continue;
                try
                {
                    string candidate = Path.Combine(dir, exe);
                    if (File.Exists(candidate)) return Path.GetFullPath(candidate);
                }
                catch { }
            }
            return null;
        }

        // ---------- 依赖 ----------

        static bool EnsureDependencies(string nodeExe)
        {
            string express = Path.Combine(WebDir, "node_modules", "express");
            if (Directory.Exists(express))
            {
                Info("依赖已随包提供，跳过安装。");
                return true;
            }
            if (SkipInstall) { Info("--skip-install，跳过依赖安装。"); return true; }

            string npm = SearchPathFor("npm.cmd");
            if (npm == null)
            {
                string beside = Path.Combine(Path.GetDirectoryName(nodeExe), "npm.cmd");
                if (File.Exists(beside)) npm = beside;
            }
            if (npm == null) { Warn("找到 node 但找不到 npm，跳过依赖安装。"); return true; }

            Info("正在安装依赖（express / axios / dotenv / cors），第一次要几十秒...");
            if (NpmInstall(npm, "")) return true;

            Warn("直连默认源安装失败，改用国内镜像 " + NpmMirror + " 重试...");
            if (NpmInstall(npm, " --registry=" + NpmMirror)) return true;

            Warn("两次 npm install 都没成功。可以手动执行: cd Web && npm install --registry=" + NpmMirror);
            return false;
        }

        static bool NpmInstall(string npm, string extraArgs)
        {
            string cmd = "/c \"\"" + npm + "\" install --omit=dev --no-audit --no-fund --loglevel=error" + extraArgs + "\"";
            int code = RunAndWait("cmd.exe", cmd, WebDir, true, 900000);
            if (code != 0) { Warn("npm install 退出码 " + code); return false; }
            return Directory.Exists(Path.Combine(WebDir, "node_modules", "express"));
        }

        // ---------- 端口与监听地址 ----------

        // 用户要的那个端口如果被别的程序占着，往后顺延，而不是直接报错退出。
        // 顺延途中撞见已经在跑的 AliyaWeb，就复用它，否则会起第二个进程抢同一份 sessions.json。
        static int PickPort()
        {
            int preferred = Port;
            int fromEnv;
            if (preferred == DefaultPort && TryReadInt(ReadEnvValue(Path.Combine(WebDir, ".env"), "PORT"), out fromEnv))
                preferred = fromEnv;

            for (int i = 0; i < PortTries; i++)
            {
                int p = preferred + i;
                if (p > 65535) break;
                if (AliyaAlive(p))
                {
                    Info("端口 " + p + " 上已有可用的 AliyaWeb 服务在跑，直接复用。");
                    return p;
                }
                if (!PortInUse(p))
                {
                    if (p != preferred) Warn("端口 " + preferred + " 已被其它程序占用，自动改用 " + p + "。");
                    return p;
                }
            }
            Warn("从 " + preferred + " 起连续 " + PortTries + " 个端口都在使用中，仍按 " + preferred + " 尝试。");
            return preferred;
        }

        static string ResolveHost()
        {
            if (Lan)
            {
                Warn("--lan：服务将对同一局域网内的所有设备开放，用完请关掉本窗口。");
                return "0.0.0.0";
            }
            string fromEnv = ReadEnvValue(Path.Combine(WebDir, ".env"), "HOST");
            if (!string.IsNullOrEmpty(fromEnv)) return fromEnv.Trim();
            return "127.0.0.1";
        }

        // ---------- .env 与密钥 ----------

        static bool PrepareEnv(int port, string host)
        {
            string envFile = Path.Combine(WebDir, ".env");
            string current = ReadEnvValue(envFile, "DEEPSEEK_API_KEY");

            if (current != null && !LooksPlaceholder(current))
            {
                Info("已检测到 API Key: " + Mask(current) + "（不会覆盖 Web/.env）");
                return true;
            }

            string key = GivenKey;
            if (string.IsNullOrEmpty(key)) key = Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY");
            if (string.IsNullOrEmpty(key))
            {
                Console.WriteLine();
                Console.WriteLine("请粘贴你的 DeepSeek API Key（申请地址 https://platform.deepseek.com/api_keys）");
                Console.WriteLine("输入时不会显示字符；直接回车可跳过，跳过后网页能打开但发消息会失败。");
                Console.Write("DEEPSEEK_API_KEY = ");
                key = ReadMasked();
            }
            if (key != null) key = key.Trim();

            if (string.IsNullOrEmpty(key))
            {
                Warn("未填写密钥。稍后可手动把 Web/.env.example 复制为 Web/.env 再填入。");
                if (!File.Exists(envFile)) return WriteEnv(envFile, "", port, host);
                return true;
            }

            if (!key.StartsWith("sk-")) Warn("提示：DeepSeek 的密钥通常以 sk- 开头，你填的不是。仍然按原样写入。");

            bool ok = WriteEnv(envFile, key, port, host);
            if (ok)
            {
                Ok("已写入 " + envFile + "  （" + Mask(key) + "）");
                Warn("重要：这个文件包含你的密钥。不要把它上传、发给别人、或放进压缩包里分享。");
            }
            return ok;
        }

        static bool WriteEnv(string envFile, string key, int port, string host)
        {
            try
            {
                string body = "# 本文件由启动器生成，含 API 密钥，切勿上传或分享\r\n"
                    + "DEEPSEEK_API_KEY=" + key + "\r\n"
                    + "PORT=" + port.ToString(CultureInfo.InvariantCulture) + "\r\n"
                    + "HOST=" + host + "\r\n";
                File.WriteAllText(envFile, body, new UTF8Encoding(false));
                return true;
            }
            catch (Exception ex)
            {
                Warn("写入 .env 失败: " + ex.Message);
                return false;
            }
        }

        static string ReadEnvValue(string envFile, string name)
        {
            try
            {
                if (!File.Exists(envFile)) return null;
                foreach (string line in File.ReadAllLines(envFile))
                {
                    string t = line.Trim();
                    if (t.StartsWith("#") || t.Length == 0) continue;
                    int eq = t.IndexOf('=');
                    if (eq <= 0) continue;
                    if (t.Substring(0, eq).Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
                        return t.Substring(eq + 1).Trim().Trim('"');
                }
            }
            catch { }
            return null;
        }

        static bool LooksPlaceholder(string value)
        {
            string upper = value.ToUpperInvariant();
            return upper.Contains("YOUR") || upper.Contains("XXXX") || value.Contains("在这里填入")
                || value.Contains("你的密钥") || upper.Contains("PLACEHOLDER") || upper.Contains("CHANGEME")
                || value.Trim().Length == 0;
        }

        static bool TryReadInt(string s, out int value)
        {
            value = 0;
            return s != null && int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value)
                && value >= 1 && value <= 65535;
        }

        static string Mask(string key)
        {
            if (string.IsNullOrEmpty(key)) return "(空)";
            if (key.Length <= 8) return key.Substring(0, 1) + "***";
            return key.Substring(0, 6) + "..." + key.Substring(key.Length - 3);
        }

        static string ReadMasked()
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                while (true)
                {
                    ConsoleKeyInfo k = Console.ReadKey(true);
                    if (k.Key == ConsoleKey.Enter) { Console.WriteLine(); break; }
                    if (k.Key == ConsoleKey.Backspace)
                    {
                        if (sb.Length > 0) { sb.Remove(sb.Length - 1, 1); Console.Write("\b \b"); }
                        continue;
                    }
                    if (k.KeyChar >= 33 && k.KeyChar <= 126)
                    {
                        sb.Append(k.KeyChar);
                        Console.Write("*");
                    }
                }
                return sb.ToString();
            }
            catch (InvalidOperationException)
            {
                // 输入被重定向（管道/脚本调用）时 ReadKey 不可用
                string line = Console.ReadLine();
                return line ?? "";
            }
        }

        // ---------- 启动服务 ----------

        static bool StartServer(string nodeExe, int port, string host)
        {
            if (AliyaAlive(port)) return true;

            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(nodeExe, "\"server.js\"");
                psi.WorkingDirectory = WebDir;
                psi.UseShellExecute = false;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.StandardOutputEncoding = Encoding.UTF8;
                psi.StandardErrorEncoding = Encoding.UTF8;
                // 显式传给子进程；dotenv 不会覆盖已存在的 process.env，所以这里的值优先于 .env
                psi.EnvironmentVariables["PORT"] = port.ToString(CultureInfo.InvariantCulture);
                psi.EnvironmentVariables["HOST"] = host;
                server = new Process();
                server.StartInfo = psi;
                server.OutputDataReceived += delegate(object s, DataReceivedEventArgs e) { if (e.Data != null) Console.WriteLine("  [server] " + e.Data); };
                server.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e) { if (e.Data != null) Console.WriteLine("  [server] " + e.Data); };
                server.Start();
                server.BeginOutputReadLine();
                server.BeginErrorReadLine();
                Console.CancelKeyPress += delegate(object s, ConsoleCancelEventArgs e) { StopServer(); e.Cancel = true; };
                AppDomain.CurrentDomain.ProcessExit += delegate { StopServer(); };
                Info("已启动 node server.js（PID " + server.Id + "），监听 " + host + ":" + port);
                return true;
            }
            catch (Exception ex)
            {
                Warn("启动失败: " + ex.Message);
                return false;
            }
        }

        static bool WaitUntilReady(int seconds, int port)
        {
            for (int i = 0; i < seconds; i++)
            {
                if (server != null && server.HasExited) return false;
                if (AliyaAlive(port)) return true;
                Thread.Sleep(500);
            }
            return false;
        }

        // 只看状态码会把「别的程序正好在这个端口上」误判成 AliyaWeb，
        // 所以要求 /api/test 返回我们自己的那段 JSON。
        static bool AliyaAlive(int port)
        {
            try
            {
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(
                    "http://127.0.0.1:" + port + "/api/test");
                req.Timeout = 1500;
                req.ReadWriteTimeout = 1500;
                req.AllowAutoRedirect = false;
                using (HttpWebResponse res = (HttpWebResponse)req.GetResponse())
                {
                    if ((int)res.StatusCode != 200) return false;
                    using (Stream st = res.GetResponseStream())
                    using (StreamReader rd = new StreamReader(st, Encoding.UTF8))
                    {
                        string body = rd.ReadToEnd();
                        return body.Contains("后端API工作正常")
                            || (body.Contains("\"message\"") && body.Contains("\"timestamp\""));
                    }
                }
            }
            catch (WebException) { return false; }
            catch { return false; }
        }

        static bool PortInUse(int port)
        {
            try
            {
                IPGlobalProperties props = IPGlobalProperties.GetIPGlobalProperties();
                foreach (IPEndPoint ep in props.GetActiveTcpListeners())
                    if (ep.Port == port) return true;
                return false;
            }
            catch
            {
                // 拿不到监听表时保守认为空闲，让 Node 自己报 EADDRINUSE
                return false;
            }
        }

        static void StopServer()
        {
            try
            {
                if (server != null && !server.HasExited)
                {
                    server.Kill();
                    server.WaitForExit(5000);
                }
            }
            catch { }
        }

        // ---------- 启动后提示 ----------

        static void PrintAccess(int port, string host)
        {
            bool open = host == "0.0.0.0" || host == "::";
            Console.WriteLine();
            Console.WriteLine("=============== 访问方式 ===============");
            Console.WriteLine("  网页        http://localhost:" + port + "/");
            Console.WriteLine("  健康检查    http://localhost:" + port + "/api/test");
            if (open)
            {
                List<string> ips = LocalIPv4();
                if (ips.Count == 0) Warn("  局域网      没识别到本机 IPv4 地址，请用 ipconfig 查看。");
                foreach (string ip in ips)
                    Console.WriteLine("  局域网      http://" + ip + ":" + port + "/");
            }
            else
            {
                Console.WriteLine("  访问范围    仅本机。需要手机等其它设备访问时，用 AliyaWeb-Launcher.exe --lan 重新启动。");
            }
            Console.WriteLine("---------------- API 接口 ----------------");
            Console.WriteLine("  GET     /api/test                        健康检查");
            Console.WriteLine("  POST    /api/chat                        {\"message\":\"你好\",\"sessionId\":\"可选\"}");
            Console.WriteLine("  GET     /api/session/:sessionId          读取会话与长期记忆");
            Console.WriteLine("  POST    /api/session/:sessionId/export   写入记忆档案");
            Console.WriteLine("  GET     /api/session/:sessionId/export.md 下载 Markdown 档案");
            Console.WriteLine("  DELETE  /api/session/:sessionId          删除会话");
            Console.WriteLine("  命令行示例：");
            Console.WriteLine("    curl -X POST http://localhost:" + port + "/api/chat "
                + "-H \"Content-Type: application/json\" "
                + "-d \"{\\\"message\\\":\\\"你好\\\"}\"");
            Console.WriteLine("========================================");
            Console.WriteLine();
        }

        static List<string> LocalIPv4()
        {
            List<string> list = new List<string>();
            try
            {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    IPInterfaceProperties props = ni.GetIPProperties();
                    if (props == null) continue;
                    foreach (UnicastIPAddressInformation ua in props.UnicastAddresses)
                    {
                        if (ua.Address != null && ua.Address.AddressFamily == AddressFamily.InterNetwork)
                        {
                            byte[] b = ua.Address.GetAddressBytes();
                            if (!(b[0] == 169 && b[1] == 254)) list.Add(ua.Address.ToString());
                        }
                    }
                }
            }
            catch { }
            return list;
        }

        static void OpenBrowser(int port)
        {
            if (NoBrowser) { Info("--no-browser，跳过打开浏览器。"); return; }
            string url = "http://localhost:" + port + "/";
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(url);
                psi.UseShellExecute = true;
                Process.Start(psi);
                Info("已用默认浏览器打开 " + url);
            }
            catch (Exception ex)
            {
                Warn("自动打开浏览器失败: " + ex.Message + "  请手动访问 " + url);
            }
        }

        // ---------- 进程工具 ----------

        static int RunAndWait(string file, string arguments, string workDir, bool echo, int timeoutMs)
        {
            Process p = new Process();
            ProcessStartInfo psi = new ProcessStartInfo(file, arguments);
            psi.WorkingDirectory = workDir;
            psi.UseShellExecute = false;
            psi.RedirectStandardOutput = echo;
            psi.RedirectStandardError = echo;
            p.StartInfo = psi;
            if (echo)
            {
                p.OutputDataReceived += delegate(object s, DataReceivedEventArgs e) { if (e.Data != null) Console.WriteLine("    " + e.Data); };
                p.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e) { if (e.Data != null) Console.WriteLine("    " + e.Data); };
            }
            p.Start();
            if (echo) { p.BeginOutputReadLine(); p.BeginErrorReadLine(); }
            if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } return -1; }
            return p.ExitCode;
        }

        static string RunCapture(string file, string arguments, string workDir, int timeoutMs)
        {
            Process p = new Process();
            ProcessStartInfo psi = new ProcessStartInfo(file, arguments);
            psi.WorkingDirectory = workDir;
            psi.UseShellExecute = false;
            psi.RedirectStandardOutput = true;
            p.StartInfo = psi;
            p.Start();
            string text = p.StandardOutput.ReadToEnd();
            p.WaitForExit(timeoutMs);
            return text;
        }

        static string SafeExitCode()
        {
            try { return server != null ? server.ExitCode.ToString() : "-"; } catch { return "-"; }
        }

        // ---------- 输出 ----------

        static void Info(string m) { Write(m, ConsoleColor.Gray); }
        static void Ok(string m) { Write(m, ConsoleColor.Green); }
        static void Warn(string m) { Write(m, ConsoleColor.Yellow); }
        static void Write(string m, ConsoleColor c)
        {
            ConsoleColor old = Console.ForegroundColor;
            Console.ForegroundColor = c;
            Console.WriteLine(m);
            Console.ForegroundColor = old;
        }

        static int Fail(string m)
        {
            ConsoleColor old = Console.ForegroundColor;
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine();
            Console.WriteLine("X " + m);
            PauseIfNeeded(1);
            Console.ForegroundColor = old;
            return 1;
        }

        static void PauseIfNeeded(int code)
        {
            if (NoPause) return;
            Console.WriteLine();
            try
            {
                Console.WriteLine(code == 0 ? "完成，按任意键关闭本窗口。" : "出错了，按任意键关闭本窗口。");
                Console.ReadKey();
            }
            catch (InvalidOperationException) { }
        }
    }
}
