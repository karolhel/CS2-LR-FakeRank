using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.UserMessages;
using CounterStrikeSharp.API.Modules.Utils;
using LevelsRanksApi;
using Microsoft.Extensions.Logging;

namespace LevelsRanksModuleFakeRank
{
    [MinimumApiVersion(80)]
    public class LevelsRanksModuleFakeRank : BasePlugin
    {
        public override string ModuleName => "[LR] Module - FakeRank by Dz!ad3k";
        public override string ModuleVersion => "1.0.8";
        public override string ModuleAuthor => "ABKAM designed by RoadSide Romeo & Wend4r, fixed by Dz!ad3k";

        private Dictionary<int, (int competitiveRanking, int competitiveRankType)>? _ranksConfig;
        private readonly Dictionary<string, (int competitiveRanking, int competitiveRankType)> _playerRanks = new();
        private ILevelsRanksApi? _api;
        private readonly PluginCapability<ILevelsRanksApi> _apiCapability = new("levels_ranks");
        private IPlayerRankApi? _playerRankApi;
        private readonly PluginCapability<IPlayerRankApi> _playerRankApiCapability = new("PLAYER_RANK_API");
        private readonly Dictionary<string, int> _lastKnownLevels = new();

        // Players we already warned about (not present in LR OnlineUsers) - prevents log spam every second.
        private readonly HashSet<string> _missingWarned = new();

        private const float UpdateInterval = 1.0f;

        // Set when clients should be told to (re)display ranks on the scoreboard. Handled at most once per
        // UpdateInterval - sending the reveal every tick (original behaviour) made the icons flicker.
        private bool _revealPending;

        public override void Load(bool hotReload)
        {
            _playerRankApi = new PlayerRankApi(this);
            Capabilities.RegisterPluginCapability(_playerRankApiCapability, () => _playerRankApi);

            RegisterEventHandler<EventPlayerDisconnect>(OnPlayerDisconnect);
            RegisterEventHandler<EventPlayerConnectFull>((_, _) =>
            {
                // Give LR Core a moment to load the player, then refresh the scoreboard for everyone.
                AddTimer(3.0f, () => _revealPending = true);
                return HookResult.Continue;
            });
            RegisterEventHandler<EventRoundStart>((_, _) =>
            {
                _revealPending = true;
                return HookResult.Continue;
            });
        }

        public override void OnAllPluginsLoaded(bool hotReload)
        {
            base.OnAllPluginsLoaded(hotReload);

            try
            {
                _api = _apiCapability.Get();
            }
            catch (Exception e)
            {
                Logger.LogError($"Failed to get Levels Ranks API: {e.Message}");
            }

            if (_api == null)
            {
                Logger.LogError("Levels Ranks API is currently unavailable. FakeRank module is disabled.");
                return;
            }

            CreateRanksConfig();
            _ranksConfig = LoadRanksConfig();

            // The game resets the scoreboard rank fields on its own, so they must be re-applied every tick
            // (only when they differ - cheap, no disk access). LR levels are fetched once per second.
            RegisterListener<Listeners.OnTick>(ApplyRanks);
            AddTimer(UpdateInterval, () =>
            {
                FetchPlayerRanks();
                if (_revealPending)
                {
                    _revealPending = false;
                    RevealRanksToAll();
                }
            }, TimerFlags.REPEAT);
        }

        private static IEnumerable<CCSPlayerController> GetRealPlayers()
        {
            return Utilities.GetPlayers().Where(player =>
                player is { IsValid: true, IsBot: false, IsHLTV: false } &&
                player.Connected == 0 && // 0 = fully connected (enum member name differs between CSS versions)
                player.SteamID != 0 &&
                player.TeamNum != (int)CsTeam.Spectator);
        }

        private void FetchPlayerRanks()
        {
            if (_api == null) return;

            foreach (var player in GetRealPlayers())
            {
                var steamId = _api.ConvertToSteamId(player.SteamID);

                if (!_api.OnlineUsers.TryGetValue(steamId, out var onlineUser))
                {
                    // LR Core has not loaded this player (yet). Warn once instead of every second.
                    if (_missingWarned.Add(steamId))
                    {
                        Logger.LogWarning(
                            $"Player {player.PlayerName} ({steamId}) is not in LR OnlineUsers - rank will be applied once LR Core loads the player.");
                    }

                    continue;
                }

                if (_missingWarned.Remove(steamId))
                {
                    Logger.LogInformation($"Player {player.PlayerName} ({steamId}) loaded by LR Core, applying fake rank.");
                }

                var currentLevelId = onlineUser.Rank;

                if (_lastKnownLevels.TryGetValue(steamId, out var lastLevel) && currentLevelId == lastLevel)
                    continue;

                if (_ranksConfig != null && _ranksConfig.TryGetValue(currentLevelId, out var rankInfo))
                {
                    _playerRanks[steamId] = rankInfo;
                    _lastKnownLevels[steamId] = currentLevelId;
                    _revealPending = true;
                }
            }
        }

        private HookResult OnPlayerDisconnect(EventPlayerDisconnect @event, GameEventInfo info)
        {
            var player = @event.Userid;
            if (player == null || !player.IsValid || player.IsBot || player.SteamID == 0)
                return HookResult.Continue;

            var steamId64 = player.SteamID;
            PlayerRankApi.ForgetCached(steamId64);

            if (_api != null)
            {
                var steamId = _api.ConvertToSteamId(steamId64);
                _playerRanks.Remove(steamId);
                _lastKnownLevels.Remove(steamId);
                _missingWarned.Remove(steamId);
            }

            return HookResult.Continue;
        }

        // Scoreboard reveal: "FakeRanks - Reveal All" (Metamod) on TAB, plus RevealRanksToAll() on join,
        // round start and level change. Never per tick - that made the rank icons flicker.
        private void ApplyRanks()
        {
            if (_api == null) return;

            foreach (var player in GetRealPlayers())
            {
                var steamId64 = player.SteamID;

                int rank;
                int rankType;

                // Custom rank (set via PLAYER_RANK_API) - cached in memory, no disk access.
                var customRank = PlayerRankApi.GetCustomRank(steamId64);
                if (customRank != null)
                {
                    rank = customRank.Rank;
                    rankType = customRank.RankType;
                }
                else if (_playerRanks.TryGetValue(_api.ConvertToSteamId(steamId64), out var rankInfo))
                {
                    rank = rankInfo.competitiveRanking;
                    rankType = rankInfo.competitiveRankType;
                }
                else
                {
                    continue;
                }

                SetRank(player, rank, rankType);
            }
        }

        private static void RevealRanksToAll()
        {
            var filter = new RecipientFilter();
            foreach (var player in Utilities.GetPlayers())
            {
                if (player is { IsValid: true, IsBot: false, IsHLTV: false } && player.Connected == 0)
                    filter.Add(player);
            }

            if (filter.Count > 0)
                UserMessage.FromId(350).Send(filter);
        }

        // Sets the scoreboard rank and marks the fields as changed so the engine networks them to all clients.
        internal static void SetRank(CCSPlayerController player, int rank, int rankType)
        {
            if (player.CompetitiveRankType == (sbyte)rankType && player.CompetitiveRanking == rank &&
                player.CompetitiveWins == 777)
                return;

            player.CompetitiveRankType = (sbyte)rankType;
            player.CompetitiveRanking = rank;
            player.CompetitiveWins = 777;

            Utilities.SetStateChanged(player, "CCSPlayerController", "m_iCompetitiveRankType");
            Utilities.SetStateChanged(player, "CCSPlayerController", "m_iCompetitiveRanking");
            Utilities.SetStateChanged(player, "CCSPlayerController", "m_iCompetitiveWins");
        }

        private void CreateRanksConfig()
        {
            var configDirectory = Path.Combine(Application.RootDirectory, "configs/plugins/LevelsRanks");
            var filePath = Path.Combine(configDirectory, "settings_fakerank.json");

            if (!File.Exists(filePath))
            {
                var defaultConfig = new
                {
                    LR_FakeRank = new
                    {
                        Type = "1",
                        FakeRank = new Dictionary<string, string>
                        {
                            { "1", "1" },
                            { "2", "2" },
                            { "3", "3" },
                            { "4", "4" },
                            { "5", "5" },
                            { "6", "6" },
                            { "7", "7" },
                            { "8", "8" },
                            { "9", "9" },
                            { "10", "10" },
                            { "11", "11" },
                            { "12", "12" },
                            { "13", "13" },
                            { "14", "14" },
                            { "15", "15" },
                            { "16", "16" },
                            { "17", "17" },
                            { "18", "18" }
                        }
                    }
                };

                Directory.CreateDirectory(configDirectory);
                var json = JsonSerializer.Serialize(defaultConfig, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(filePath, json);
            }
        }

        private Dictionary<int, (int competitiveRanking, int competitiveRankType)> LoadRanksConfig()
        {
            var configDirectory = Path.Combine(Application.RootDirectory, "configs/plugins/LevelsRanks");
            var filePath = Path.Combine(configDirectory, "settings_fakerank.json");

            var json = File.ReadAllText(filePath);
            var config = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, object>>>(json);

            var ranks = new Dictionary<int, (int competitiveRanking, int competitiveRankType)>();

            if (config != null && config.TryGetValue("LR_FakeRank", out var fakeRankSection) &&
                fakeRankSection.TryGetValue("FakeRank", out var fakeRanksObject))
            {
                if (fakeRanksObject is JsonElement fakeRanksElement)
                {
                    int rankType;
                    if (fakeRankSection.TryGetValue("Type", out var typeValue) &&
                        typeValue is JsonElement typeElement &&
                        typeElement.GetString() is string typeString && int.TryParse(typeString, out var type))
                    {
                        switch (type)
                        {
                            case 1:
                                rankType = 12;
                                break;
                            case 2:
                                rankType = 7;
                                break;
                            case 3:
                                rankType = 11;
                                break;
                            default:
                                rankType = 12;
                                break;
                        }
                    }
                    else
                    {
                        rankType = 12;
                    }

                    foreach (var rank in fakeRanksElement.EnumerateObject())
                    {
                        if (int.TryParse(rank.Name, out var level) &&
                            rank.Value.GetString() is string competitiveRankingString &&
                            int.TryParse(competitiveRankingString, out var competitiveRanking))
                        {
                            ranks[level] = (competitiveRanking, rankType);
                        }
                    }
                }
            }

            return ranks;
        }
    }
}
