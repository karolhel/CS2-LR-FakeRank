using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using CounterStrikeSharp.API.Core;
using Microsoft.Extensions.Logging;

public class PlayerRankApi : IPlayerRankApi
{
    private static LevelsRanksModuleFakeRank.LevelsRanksModuleFakeRank _core = null!;
    private readonly Dictionary<ulong, (int originalRank, int originalRankType)> _originalRanks = new();

    // In-memory cache of custom ranks (null = player has no custom rank file).
    // Previously the file was read from disk for every player on every server tick.
    private static readonly ConcurrentDictionary<ulong, PlayerRankData?> CustomRankCache = new();

    public PlayerRankApi(LevelsRanksModuleFakeRank.LevelsRanksModuleFakeRank core)
    {
        _core = core;
    }

    public void DisableRank(CCSPlayerController player)
    {
        var steamId = player.SteamID;
        if (!_originalRanks.ContainsKey(steamId))
        {
            _originalRanks[steamId] = (player.CompetitiveRanking, player.CompetitiveRankType);
        }

        LevelsRanksModuleFakeRank.LevelsRanksModuleFakeRank.SetRank(player, 0, 0);
    }

    public void SetCustomRank(CCSPlayerController player, int rank, int rankType)
    {
        var steamId = player.SteamID;
        if (!_originalRanks.ContainsKey(steamId))
        {
            _originalRanks[steamId] = (player.CompetitiveRanking, player.CompetitiveRankType);
        }

        LevelsRanksModuleFakeRank.LevelsRanksModuleFakeRank.SetRank(player, rank, rankType);

        var rankData = new PlayerRankData { Rank = rank, RankType = rankType };
        CustomRankCache[steamId] = rankData;
        SavePlayerRankToFile(steamId, rankData);
    }

    public void ResetRank(CCSPlayerController player)
    {
        var steamId = player.SteamID;
        if (_originalRanks.TryGetValue(steamId, out var originalRank))
        {
            LevelsRanksModuleFakeRank.LevelsRanksModuleFakeRank.SetRank(player, originalRank.originalRank,
                originalRank.originalRankType);

            _originalRanks.Remove(steamId);
        }

        CustomRankCache[steamId] = null;
        DeletePlayerRankFile(steamId);
    }

    public static PlayerRankData? GetCustomRank(ulong steamId)
    {
        return CustomRankCache.GetOrAdd(steamId, LoadPlayerRankFromFile);
    }

    public static void ForgetCached(ulong steamId)
    {
        CustomRankCache.TryRemove(steamId, out _);
    }

    private static void SavePlayerRankToFile(ulong steamId, PlayerRankData rankData)
    {
        try
        {
            File.WriteAllText(GetPlayerRankFilePath(steamId), JsonSerializer.Serialize(rankData));
        }
        catch (Exception e)
        {
            _core.Logger.LogError($"Failed to save custom rank for {steamId}: {e.Message}");
        }
    }

    public static PlayerRankData? LoadPlayerRankFromFile(ulong steamId)
    {
        try
        {
            var filePath = GetPlayerRankFilePath(steamId);
            if (!File.Exists(filePath))
                return null;

            return JsonSerializer.Deserialize<PlayerRankData>(File.ReadAllText(filePath));
        }
        catch (Exception e)
        {
            _core.Logger.LogError($"Failed to load custom rank for {steamId}: {e.Message}");
            return null;
        }
    }

    private static void DeletePlayerRankFile(ulong steamId)
    {
        try
        {
            var filePath = GetPlayerRankFilePath(steamId);
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
        catch (Exception e)
        {
            _core.Logger.LogError($"Failed to delete custom rank for {steamId}: {e.Message}");
        }
    }

    private static string GetPlayerRankFilePath(ulong steamId)
    {
        var dataDirectory = Path.Combine(_core.ModuleDirectory, "PlayerData");
        Directory.CreateDirectory(dataDirectory);

        return Path.Combine(dataDirectory, $"{steamId}_rank.json");
    }
}

public class PlayerRankData
{
    public int Rank { get; set; }
    public int RankType { get; set; }
}
