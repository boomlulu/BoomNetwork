// DeployProfile — 部署目标配置（本地/远程SSH）+ EditorPrefs 持久化

using System;
using System.Runtime.InteropServices;
using UnityEditor;

namespace BoomNetwork.GM.Editor
{
    public enum DeployProfileType { Local = 0, RemoteSSH = 1 }

    [Serializable]
    public class DeployProfile
    {
        // 标识
        public string Name = "Dev Local";
        public DeployProfileType Type = DeployProfileType.Local;

        // 构建目标（Local 自动检测，Remote 手动选）
        public string TargetOS = "linux";
        public string TargetArch = "amd64";

        // 配置文件（相对 svr/）
        public string ConfigFile = "cmd/framesync/config.yaml";

        // Remote SSH
        public string SshHost = "";
        public string SshPort = "22";
        public string SshUser = "root";
        public string SshKeyPath = "~/.ssh/id_rsa";
        public string RemoteBinaryPath = "/opt/boomnetwork/framesync";
        public string RemoteConfigPath = "/etc/boomnetwork/config.yaml";
        public string SystemdService = ""; // 空 = 用 nohup

        // 健康检查 + Admin 鉴权
        public string HealthUrl = "http://127.0.0.1:9091";
        public string AdminToken = "";
        public int HealthTimeoutSec = 15;

        // ===================== OS/Arch 选项 =====================

        public static readonly string[] OSOptions = { "linux", "darwin", "windows" };
        public static readonly string[] ArchOptions = { "amd64", "arm64" };

        public static string DetectLocalOS()
        {
#if UNITY_EDITOR_WIN
            return "windows";
#elif UNITY_EDITOR_OSX
            return "darwin";
#else
            return "linux";
#endif
        }

        public static string DetectLocalArch()
        {
            var arch = RuntimeInformation.ProcessArchitecture;
            return arch == Architecture.Arm64 ? "arm64" : "amd64";
        }

        // ===================== EditorPrefs 序列化 =====================

        private const string PP = "BoomNetwork.GM.Deploy.";

        public static int GetProfileCount()
        {
            return EditorPrefs.GetInt(PP + "profileCount", 0);
        }

        public static void SetProfileCount(int count)
        {
            EditorPrefs.SetInt(PP + "profileCount", count);
        }

        public static int GetActiveIndex()
        {
            return EditorPrefs.GetInt(PP + "activeProfile", 0);
        }

        public static void SetActiveIndex(int index)
        {
            EditorPrefs.SetInt(PP + "activeProfile", index);
        }

        public static DeployProfile Load(int index)
        {
            string p = PP + $"profile.{index}.";
            var profile = new DeployProfile();
            profile.Name             = EditorPrefs.GetString(p + "name", profile.Name);
            profile.Type             = (DeployProfileType)EditorPrefs.GetInt(p + "type", (int)profile.Type);
            profile.TargetOS         = EditorPrefs.GetString(p + "targetOs", profile.TargetOS);
            profile.TargetArch       = EditorPrefs.GetString(p + "targetArch", profile.TargetArch);
            profile.ConfigFile       = EditorPrefs.GetString(p + "configFile", profile.ConfigFile);
            profile.SshHost          = EditorPrefs.GetString(p + "sshHost", profile.SshHost);
            profile.SshPort          = EditorPrefs.GetString(p + "sshPort", profile.SshPort);
            profile.SshUser          = EditorPrefs.GetString(p + "sshUser", profile.SshUser);
            profile.SshKeyPath       = EditorPrefs.GetString(p + "sshKeyPath", profile.SshKeyPath);
            profile.RemoteBinaryPath = EditorPrefs.GetString(p + "remoteBin", profile.RemoteBinaryPath);
            profile.RemoteConfigPath = EditorPrefs.GetString(p + "remoteCfg", profile.RemoteConfigPath);
            profile.SystemdService   = EditorPrefs.GetString(p + "systemd", profile.SystemdService);
            profile.HealthUrl        = EditorPrefs.GetString(p + "healthUrl", profile.HealthUrl);
            profile.AdminToken       = EditorPrefs.GetString(p + "adminToken", profile.AdminToken);
            profile.HealthTimeoutSec = EditorPrefs.GetInt(p + "healthTimeout", profile.HealthTimeoutSec);
            return profile;
        }

        public static void Save(int index, DeployProfile profile)
        {
            string p = PP + $"profile.{index}.";
            EditorPrefs.SetString(p + "name", profile.Name);
            EditorPrefs.SetInt(p + "type", (int)profile.Type);
            EditorPrefs.SetString(p + "targetOs", profile.TargetOS);
            EditorPrefs.SetString(p + "targetArch", profile.TargetArch);
            EditorPrefs.SetString(p + "configFile", profile.ConfigFile);
            EditorPrefs.SetString(p + "sshHost", profile.SshHost);
            EditorPrefs.SetString(p + "sshPort", profile.SshPort);
            EditorPrefs.SetString(p + "sshUser", profile.SshUser);
            EditorPrefs.SetString(p + "sshKeyPath", profile.SshKeyPath);
            EditorPrefs.SetString(p + "remoteBin", profile.RemoteBinaryPath);
            EditorPrefs.SetString(p + "remoteCfg", profile.RemoteConfigPath);
            EditorPrefs.SetString(p + "systemd", profile.SystemdService);
            EditorPrefs.SetString(p + "healthUrl", profile.HealthUrl);
            EditorPrefs.SetString(p + "adminToken", profile.AdminToken);
            EditorPrefs.SetInt(p + "healthTimeout", profile.HealthTimeoutSec);
        }

        public static void Delete(int index)
        {
            int count = GetProfileCount();
            // 把后面的 profile 往前挪
            for (int i = index; i < count - 1; i++)
            {
                var next = Load(i + 1);
                Save(i, next);
            }
            // 清理最后一个
            ClearKeys(count - 1);
            SetProfileCount(count - 1);

            // 修正 activeIndex
            int active = GetActiveIndex();
            if (active >= count - 1) SetActiveIndex(Math.Max(0, count - 2));
        }

        static void ClearKeys(int index)
        {
            string p = PP + $"profile.{index}.";
            string[] keys = { "name", "type", "targetOs", "targetArch", "configFile",
                "sshHost", "sshPort", "sshUser", "sshKeyPath", "remoteBin", "remoteCfg",
                "systemd", "healthUrl", "adminToken", "healthTimeout" };
            foreach (var k in keys) EditorPrefs.DeleteKey(p + k);
        }

        /// <summary>创建默认 Local profile</summary>
        public static DeployProfile CreateDefaultLocal()
        {
            return new DeployProfile
            {
                Name = "Dev Local",
                Type = DeployProfileType.Local,
                TargetOS = DetectLocalOS(),
                TargetArch = DetectLocalArch(),
                HealthUrl = "http://127.0.0.1:9091",
            };
        }

        /// <summary>创建默认 Remote profile</summary>
        public static DeployProfile CreateDefaultRemote()
        {
            return new DeployProfile
            {
                Name = "Remote Server",
                Type = DeployProfileType.RemoteSSH,
                TargetOS = "linux",
                TargetArch = "amd64",
            };
        }
    }
}
