// DeployTool — 构建/部署/验证引擎
// 使用 Process + EditorApplication.update 轮询，不阻塞主线程

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace BoomNetwork.GM.Editor
{
    public enum DeployStage
    {
        Idle, Building, Uploading, Stopping, Starting, Verifying, Done, Failed
    }

    // Upload 子阶段（远程 SSH）
    enum UploadSubStage { Backup, ScpBinary, ScpConfig }

    public class DeployTool
    {
        public DeployStage Stage { get; private set; } = DeployStage.Idle;
        public bool IsRunning => Stage != DeployStage.Idle && Stage != DeployStage.Done && Stage != DeployStage.Failed;

        private readonly List<string> _logLines = new List<string>();
        private readonly ConcurrentQueue<string> _logQueue = new ConcurrentQueue<string>();
        private const int MAX_LOG_LINES = 500;

        private Process _activeProcess;
        private DeployProfile _profile;
        private string _serverPath; // svr/ 的绝对路径
        private string _builtBinaryPath; // 构建产物路径

        // Upload 子状态
        private UploadSubStage _uploadSub;

        // Verify 状态
        private AdminClient _verifyClient;
        private double _verifyDeadline;
        private double _nextVerifyPoll;

        // 回调
        private Action _onRepaint;

        public DeployTool(Action onRepaint)
        {
            _onRepaint = onRepaint;
        }

        public void Dispose()
        {
            KillProcess();
            _verifyClient?.Dispose();
        }

        public IReadOnlyList<string> LogLines => _logLines;

        public void ClearLog()
        {
            _logLines.Clear();
            while (_logQueue.TryDequeue(out _)) { }
        }

        // ===================== 入口 =====================

        public void StartDeploy(DeployProfile profile, string serverPath)
        {
            if (IsRunning) return;

            _profile = profile;
            _serverPath = serverPath;
            _builtBinaryPath = null;

            ClearLog();
            Log($"[Deploy] Profile: {profile.Name} ({profile.Type})");
            Log($"[Deploy] Target: {GetEffectiveOS()}/{GetEffectiveArch()}");
            Log($"[Deploy] Config: {profile.ConfigFile}");

            // 验证字段
            var err = ValidateProfile();
            if (err != null)
            {
                Log($"[Error] {err}");
                SetStage(DeployStage.Failed);
                return;
            }

            StartBuild();
        }

        // ===================== 每帧 Tick =====================

        public void Tick()
        {
            // drain 日志队列
            bool dirty = false;
            while (_logQueue.TryDequeue(out var line))
            {
                _logLines.Add(line);
                if (_logLines.Count > MAX_LOG_LINES) _logLines.RemoveAt(0);
                dirty = true;
            }

            if (Stage == DeployStage.Verifying)
            {
                TickVerify();
                dirty = true;
            }

            // 检查 Process 退出
            if (_activeProcess != null)
            {
                bool exited;
                try { exited = _activeProcess.HasExited; }
                catch { exited = true; } // 无关联进程时视为已退出

                if (exited)
                {
                    int code = 0;
                    try { code = _activeProcess.ExitCode; } catch { code = -1; }
                    _activeProcess.Dispose();
                    _activeProcess = null;
                    OnProcessExit(code);
                    dirty = true;
                }
            }

            if (dirty) _onRepaint?.Invoke();
        }

        // ===================== Build 阶段 =====================

        void StartBuild()
        {
            SetStage(DeployStage.Building);

            string os = GetEffectiveOS();
            string arch = GetEffectiveArch();
            string ext = os == "windows" ? ".exe" : "";
            string distDir = Path.Combine(_serverPath, "dist", $"{os}_{arch}");
            Directory.CreateDirectory(distDir);
            _builtBinaryPath = Path.Combine(distDir, "framesync" + ext);

            // git hash
            string gitHash = RunCapture(ResolveToolPath("git"), $"-C \"{_serverPath}\" rev-parse --short HEAD");
            string buildTime = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
            string ldflags = $"-s -w -X main.BuildHash={gitHash} -X main.BuildTime={buildTime}";

            string args = $"build -o \"{_builtBinaryPath}\" -ldflags=\"{ldflags}\" ./cmd/framesync/";
            Log($"[Build] GOOS={os} GOARCH={arch} CGO_ENABLED=0 go {args}");

            var env = new Dictionary<string, string>
            {
                ["GOOS"] = os,
                ["GOARCH"] = arch,
                ["CGO_ENABLED"] = "0",
            };
            LaunchProcess(ResolveToolPath("go"), args, _serverPath, env);
        }

        // ===================== Upload 阶段（远程 SSH）=====================

        void StartUpload()
        {
            SetStage(DeployStage.Uploading);
            _uploadSub = UploadSubStage.Backup;

            // 备份旧二进制
            string remoteBin = _profile.RemoteBinaryPath;
            string cmd = $"[ -f {remoteBin} ] && mv {remoteBin} {remoteBin}.bak || true";
            Log($"[Upload] SSH backup: {remoteBin} → {remoteBin}.bak");
            LaunchSsh(cmd);
        }

        void ContinueUpload()
        {
            switch (_uploadSub)
            {
                case UploadSubStage.Backup:
                    _uploadSub = UploadSubStage.ScpBinary;
                    Log($"[Upload] SCP binary → {_profile.SshUser}@{_profile.SshHost}:{_profile.RemoteBinaryPath}");
                    LaunchScp(_builtBinaryPath, _profile.RemoteBinaryPath);
                    break;

                case UploadSubStage.ScpBinary:
                    // chmod +x
                    string chmodCmd = $"chmod +x {_profile.RemoteBinaryPath}";
                    _uploadSub = UploadSubStage.ScpConfig;
                    string localConfig = Path.Combine(_serverPath, _profile.ConfigFile);
                    if (File.Exists(localConfig))
                    {
                        Log($"[Upload] SCP config → {_profile.SshUser}@{_profile.SshHost}:{_profile.RemoteConfigPath}");
                        // chmod + scp config 合并：先 chmod 再 scp
                        LaunchSsh(chmodCmd);
                        // 这里简化：chmod 完成后 ScpConfig 阶段 scp config
                    }
                    else
                    {
                        Log($"[Upload] Config not found locally: {localConfig}, skipping");
                        LaunchSsh(chmodCmd); // 只 chmod
                    }
                    break;

                case UploadSubStage.ScpConfig:
                    string cfgPath = Path.Combine(_serverPath, _profile.ConfigFile);
                    if (File.Exists(cfgPath))
                    {
                        // scp config 作为最后一步，完成后进入 Stop
                        LaunchScp(cfgPath, _profile.RemoteConfigPath);
                        _uploadSub = (UploadSubStage)99; // sentinel: next exit → Stop
                    }
                    else
                    {
                        StartStop();
                    }
                    break;

                default:
                    // sentinel → 进入 Stop
                    StartStop();
                    break;
            }
        }

        // ===================== Stop 阶段 =====================

        void StartStop()
        {
            SetStage(DeployStage.Stopping);

            if (_profile.Type == DeployProfileType.Local)
            {
                Log("[Stop] Killing local server...");
                StopLocal();
                // StopLocal 是同步的，直接进入 Start
                StartStart();
            }
            else
            {
                string cmd;
                if (!string.IsNullOrEmpty(_profile.SystemdService))
                {
                    cmd = $"sudo systemctl stop {_profile.SystemdService} || true";
                    Log($"[Stop] SSH: systemctl stop {_profile.SystemdService}");
                }
                else
                {
                    string binName = Path.GetFileName(_profile.RemoteBinaryPath);
                    cmd = $"pkill -f '{binName}' || true";
                    Log($"[Stop] SSH: pkill -f '{binName}'");
                }
                LaunchSsh(cmd);
            }
        }

        void StopLocal()
        {
            // 复用 ServerWindow 的 kill 逻辑
            try
            {
                string ports = "9000,9091"; // 默认端口
                string killCmd = $"lsof -ti:{ports} -sTCP:LISTEN | sort -u | xargs kill -9 2>/dev/null; sleep 0.3; " +
                                 $"lsof -ti:{ports} -sTCP:LISTEN | sort -u | xargs kill -9 2>/dev/null";

                var isWindows = Application.platform == RuntimePlatform.WindowsEditor;
                if (isWindows)
                {
                    killCmd = "taskkill /F /IM framesync.exe 2>nul";
                    RunSync("cmd.exe", $"/c {killCmd}");
                }
                else
                {
                    RunSync("/bin/bash", $"-c \"{killCmd}\"");
                }
                Log("[Stop] Local server killed");
            }
            catch (Exception e)
            {
                Log($"[Stop] Warning: {e.Message}");
            }
        }

        // ===================== Start 阶段 =====================

        void StartStart()
        {
            SetStage(DeployStage.Starting);

            if (_profile.Type == DeployProfileType.Local)
            {
                Log("[Start] Launching local server...");
                StartLocal();
                // 直接进入 Verify
                StartVerify();
            }
            else
            {
                string cmd;
                string bin = _profile.RemoteBinaryPath;
                string cfg = _profile.RemoteConfigPath;

                if (!string.IsNullOrEmpty(_profile.SystemdService))
                {
                    cmd = $"sudo systemctl start {_profile.SystemdService}";
                    Log($"[Start] SSH: systemctl start {_profile.SystemdService}");
                }
                else
                {
                    cmd = $"nohup {bin} -config {cfg} > /tmp/framesync.log 2>&1 &";
                    Log($"[Start] SSH: nohup {bin} -config {cfg}");
                }
                LaunchSsh(cmd);
            }
        }

        void StartLocal()
        {
            string cfgPath = Path.Combine(_serverPath, _profile.ConfigFile);
            string runCmd;

            if (Application.platform == RuntimePlatform.WindowsEditor)
            {
                runCmd = $"start \"\" \"{_builtBinaryPath}\" -config \"{cfgPath}\"";
                Process.Start(new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c {runCmd}",
                    UseShellExecute = false, CreateNoWindow = true,
                });
            }
            else
            {
                // macOS / Linux: 在新终端窗口启动
                runCmd = $"\"{_builtBinaryPath}\" -config \"{cfgPath}\"";
                if (Application.platform == RuntimePlatform.OSXEditor)
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "osascript",
                        Arguments = $"-e 'tell application \"Terminal\" to do script \"{runCmd}\"'",
                        UseShellExecute = false, CreateNoWindow = true,
                    });
                }
                else
                {
                    // Linux: nohup 后台
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "/bin/bash",
                        Arguments = $"-c \"nohup {runCmd} > /tmp/framesync.log 2>&1 &\"",
                        UseShellExecute = false, CreateNoWindow = true,
                    });
                }
            }
            Log($"[Start] Server launched: {_builtBinaryPath}");
        }

        // ===================== Verify 阶段 =====================

        void StartVerify()
        {
            SetStage(DeployStage.Verifying);
            _verifyClient?.Dispose();
            _verifyClient = new AdminClient(_profile.HealthUrl, 2000);
            _verifyDeadline = EditorApplication.timeSinceStartup + _profile.HealthTimeoutSec;
            _nextVerifyPoll = EditorApplication.timeSinceStartup + 2.0; // 等 2 秒再开始检查
            Log($"[Health] Checking {_profile.HealthUrl} (timeout {_profile.HealthTimeoutSec}s)...");
        }

        void TickVerify()
        {
            if (EditorApplication.timeSinceStartup < _nextVerifyPoll) return;
            _nextVerifyPoll = EditorApplication.timeSinceStartup + 2.0;

            var result = _verifyClient.FetchHealth();
            if (result.IsOnline)
            {
                Log($"[Health] OK — rooms:{result.Rooms} players:{result.Players} up:{result.Uptime}");
                _verifyClient.Dispose();
                _verifyClient = null;
                SetStage(DeployStage.Done);
                return;
            }

            if (EditorApplication.timeSinceStartup > _verifyDeadline)
            {
                Log($"[Health] TIMEOUT — server did not respond within {_profile.HealthTimeoutSec}s");
                _verifyClient.Dispose();
                _verifyClient = null;
                SetStage(DeployStage.Failed);
            }
        }

        // ===================== 状态机转移 =====================

        void OnProcessExit(int exitCode)
        {
            if (exitCode != 0 && Stage != DeployStage.Stopping)
            {
                // Stopping 阶段 exitCode != 0 可以容忍（进程可能已经不在）
                Log($"[Error] Process exited with code {exitCode}");
                SetStage(DeployStage.Failed);
                return;
            }

            switch (Stage)
            {
                case DeployStage.Building:
                    if (exitCode != 0) { SetStage(DeployStage.Failed); return; }
                    Log($"[Build] Done → {_builtBinaryPath}");
                    if (_profile.Type == DeployProfileType.RemoteSSH)
                        StartUpload();
                    else
                        StartStop();
                    break;

                case DeployStage.Uploading:
                    ContinueUpload();
                    break;

                case DeployStage.Stopping:
                    StartStart();
                    break;

                case DeployStage.Starting:
                    StartVerify();
                    break;
            }
        }

        // ===================== 验证 =====================

        string ValidateProfile()
        {
            if (string.IsNullOrEmpty(_serverPath))
                return "Server path is empty (set in Dashboard Config)";
            if (!Directory.Exists(_serverPath))
                return $"Server path not found: {_serverPath}";
            if (_profile.Type == DeployProfileType.RemoteSSH)
            {
                if (string.IsNullOrEmpty(_profile.SshHost))
                    return "SSH Host is required for remote deploy";
                if (string.IsNullOrEmpty(_profile.SshUser))
                    return "SSH User is required for remote deploy";
                if (string.IsNullOrEmpty(_profile.RemoteBinaryPath))
                    return "Remote binary path is required";
            }
            return null;
        }

        // ===================== Process 辅助 =====================

        void LaunchProcess(string fileName, string args, string workDir, Dictionary<string, string> envExtra = null)
        {
            KillProcess();

            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = args,
                WorkingDirectory = workDir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            if (envExtra != null)
            {
                foreach (var kv in envExtra)
                    psi.EnvironmentVariables[kv.Key] = kv.Value;
            }

            _activeProcess = new Process { StartInfo = psi };
            _activeProcess.OutputDataReceived += (s, e) => { if (e.Data != null) _logQueue.Enqueue(e.Data); };
            _activeProcess.ErrorDataReceived += (s, e) => { if (e.Data != null) _logQueue.Enqueue(e.Data); };
            try
            {
                _activeProcess.Start();
                _activeProcess.BeginOutputReadLine();
                _activeProcess.BeginErrorReadLine();
            }
            catch (Exception ex)
            {
                Log($"[Error] Failed to start process '{fileName}': {ex.Message}");
                _activeProcess = null;
                SetStage(DeployStage.Failed);
            }
        }

        void LaunchSsh(string remoteCmd)
        {
            string keyPath = ExpandPath(_profile.SshKeyPath);
            string args = $"-i \"{keyPath}\" -p {_profile.SshPort} " +
                          $"-o StrictHostKeyChecking=no -o ConnectTimeout=10 " +
                          $"{_profile.SshUser}@{_profile.SshHost} \"{remoteCmd}\"";
            LaunchProcess("/usr/bin/ssh", args, _serverPath);
        }

        void LaunchScp(string localPath, string remotePath)
        {
            string keyPath = ExpandPath(_profile.SshKeyPath);
            string args = $"-i \"{keyPath}\" -P {_profile.SshPort} " +
                          $"-o StrictHostKeyChecking=no " +
                          $"\"{localPath}\" {_profile.SshUser}@{_profile.SshHost}:\"{remotePath}\"";
            LaunchProcess("/usr/bin/scp", args, _serverPath);
        }

        static string ExpandPath(string path) =>
            path.Replace("~", System.Environment.GetFolderPath(
                System.Environment.SpecialFolder.UserProfile));

        /// <summary>在常见安装路径中查找可执行文件，解决 Unity 子进程 PATH 受限问题</summary>
        static string ResolveToolPath(string name)
        {
            string[] searchDirs =
            {
                "/opt/homebrew/bin",    // Apple Silicon Homebrew
                "/usr/local/bin",       // Intel Homebrew / 手动安装
                "/usr/local/go/bin",    // 官方 Go 安装包
                "/usr/bin",
                "/bin",
            };
            foreach (var dir in searchDirs)
            {
                var full = Path.Combine(dir, name);
                if (File.Exists(full)) return full;
            }
            return name; // 找不到时回退，让系统自己报错
        }

        void KillProcess()
        {
            if (_activeProcess != null)
            {
                try
                {
                    if (!_activeProcess.HasExited)
                        _activeProcess.Kill();
                }
                catch { }
                try { _activeProcess.Dispose(); } catch { }
            }
            _activeProcess = null;
        }

        static string RunCapture(string fileName, string args)
        {
            try
            {
                var p = Process.Start(new ProcessStartInfo
                {
                    FileName = fileName, Arguments = args,
                    UseShellExecute = false, RedirectStandardOutput = true,
                    CreateNoWindow = true,
                });
                string output = p.StandardOutput.ReadToEnd().Trim();
                p.WaitForExit(3000);
                return output;
            }
            catch { return "unknown"; }
        }

        static void RunSync(string fileName, string args)
        {
            var p = Process.Start(new ProcessStartInfo
            {
                FileName = fileName, Arguments = args,
                UseShellExecute = false, RedirectStandardOutput = true,
                CreateNoWindow = true,
            });
            p?.WaitForExit(5000);
        }

        void SetStage(DeployStage stage)
        {
            Stage = stage;
            if (stage == DeployStage.Done) Log("[Deploy] Completed successfully!");
            else if (stage == DeployStage.Failed) Log("[Deploy] FAILED");
        }

        void Log(string msg)
        {
            string ts = DateTime.Now.ToString("HH:mm:ss");
            _logQueue.Enqueue($"{ts} {msg}");
        }

        // ===================== 配置工具 =====================

        /// <summary>从 config.yaml 模板创建新环境配置</summary>
        public static string CreateEnvConfig(string serverPath, string envName)
        {
            string template = Path.Combine(serverPath, "cmd", "framesync", "config.yaml");
            string configsDir = Path.Combine(serverPath, "configs");
            Directory.CreateDirectory(configsDir);
            string target = Path.Combine(configsDir, $"config.{envName}.yaml");
            if (File.Exists(target)) return null; // 已存在
            File.Copy(template, target);
            return target;
        }

        /// <summary>生成 systemd service 文件</summary>
        public static string GenSystemdService(string serverPath, DeployProfile profile)
        {
            string configsDir = Path.Combine(serverPath, "configs");
            Directory.CreateDirectory(configsDir);
            string serviceName = string.IsNullOrEmpty(profile.SystemdService) ? "boomnetwork" : profile.SystemdService;
            string path = Path.Combine(configsDir, $"{serviceName}.service");

            string content = $@"[Unit]
Description=BoomNetwork Framesync Server
After=network.target

[Service]
ExecStart={profile.RemoteBinaryPath} -config {profile.RemoteConfigPath}
Restart=on-failure
RestartSec=5s
StandardOutput=journal
StandardError=journal
LimitNOFILE=65536

[Install]
WantedBy=multi-user.target
";
            File.WriteAllText(path, content);
            return path;
        }

        string GetEffectiveOS() =>
            _profile.Type == DeployProfileType.Local ? DeployProfile.DetectLocalOS() : _profile.TargetOS;

        string GetEffectiveArch() =>
            _profile.Type == DeployProfileType.Local ? DeployProfile.DetectLocalArch() : _profile.TargetArch;
    }
}
