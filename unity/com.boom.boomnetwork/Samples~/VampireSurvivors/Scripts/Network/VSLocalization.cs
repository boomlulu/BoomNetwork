// BoomNetwork VampireSurvivors Demo — Localization (EN / ZH)

namespace BoomNetwork.Samples.VampireSurvivors
{
    public enum Str
    {
        Title, Host, Port, Connect, Connecting, Connected,
        JoinedRoom, RoomReady, Disconnected,
        Wave, EnemyCount, Bandwidth, Dead, Choosing, LevelUpTitle, Paused, Desync,
        WeaponNew, WeaponUpgrade,
        // Original 4 weapons
        KnifeName, OrbName, LightningName, HolyWaterName,
        // 10 new weapons
        LinkBeamName, HealAuraName, ShieldWallName, ChainLightningPlusName,
        FocusFireName, RevivalTotemName, FrostNovaName, FireTrailName,
        MagnetFieldName, SplitShotName,
        // Boss names
        TwinCoreName, SplitBossName,
        // UI misc
        LangToggle,
        // Lobby
        SoloMode, MultiMode,
    }

    public static class VSLocalization
    {
        static int _lang; // 0=EN, 1=ZH

        public static void SetLanguage(int lang) => _lang = lang;
        public static int Language => _lang;

        public static string Get(Str s) => _table[(int)s][_lang];

        static readonly string[][] _table =
        {
            /* Title              */ new[]{ "Vampire Survivors",          "弹幕生存" },
            /* Host               */ new[]{ "Host",                       "服务器地址" },
            /* Port               */ new[]{ "Port",                       "端口" },
            /* Connect            */ new[]{ "Connect",                    "连接" },
            /* Connecting         */ new[]{ "Connecting…",                "连接中…" },
            /* Connected          */ new[]{ "Connected",                  "已连接" },
            /* JoinedRoom         */ new[]{ "Joined room",                "进入房间" },
            /* RoomReady          */ new[]{ "Room ready — waiting…",      "房间就绪，等待中…" },
            /* Disconnected       */ new[]{ "Disconnected",               "已断开" },
            /* Wave               */ new[]{ "Wave",                       "波次" },
            /* EnemyCount         */ new[]{ "Enemies",                    "敌人" },
            /* Bandwidth          */ new[]{ "BW",                         "带宽" },
            /* Dead               */ new[]{ "DEAD",                       "已阵亡" },
            /* Choosing           */ new[]{ "Choosing upgrade…",          "选择升级中…" },
            /* LevelUpTitle       */ new[]{ "LEVEL UP!  Choose upgrade:", "升级！选择技能：" },
            /* Paused             */ new[]{ "PAUSED",                     "已暂停" },
            /* Desync             */ new[]{ "DESYNC DETECTED",            "检测到不同步" },
            /* WeaponNew          */ new[]{ "(NEW)",                      "（新）" },
            /* WeaponUpgrade      */ new[]{ "Lv{0} → Lv{1}",             "Lv{0} → Lv{1}" },
            /* KnifeName          */ new[]{ "Knife",                      "飞刀" },
            /* OrbName            */ new[]{ "Orb",                        "法球" },
            /* LightningName      */ new[]{ "Lightning",                  "闪电链" },
            /* HolyWaterName      */ new[]{ "Holy Water",                 "圣水" },
            /* LinkBeamName       */ new[]{ "Link Beam",                  "链接光束" },
            /* HealAuraName       */ new[]{ "Heal Aura",                  "治疗光环" },
            /* ShieldWallName     */ new[]{ "Shield Wall",                "战术护盾" },
            /* ChainLightningPlus */ new[]{ "Chain Lightning+",           "连锁闪电+" },
            /* FocusFireName      */ new[]{ "Focus Fire",                 "集火标记" },
            /* RevivalTotemName   */ new[]{ "Revival Totem",              "复活图腾" },
            /* FrostNovaName      */ new[]{ "Frost Nova",                 "冰冻新星" },
            /* FireTrailName      */ new[]{ "Fire Trail",                 "火焰轨迹" },
            /* MagnetFieldName    */ new[]{ "Magnet Field",               "磁力场" },
            /* SplitShotName      */ new[]{ "Split Shot",                 "分裂弹" },
            /* TwinCoreName       */ new[]{ "Twin Core",                  "双核 Boss" },
            /* SplitBossName      */ new[]{ "Split Boss",                 "分裂 Boss" },
            /* LangToggle         */ new[]{ "中文",                        "EN" },
            /* SoloMode           */ new[]{ "Solo",                        "单人模式" },
            /* MultiMode          */ new[]{ "Multiplayer",                 "多人匹配" },
        };
    }
}
