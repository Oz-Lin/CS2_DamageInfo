﻿using System.Data;
using System.Text.Json.Serialization;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Modules.Admin;
using Microsoft.Extensions.Logging;

namespace K4ryuuDamageInfo
{
	public sealed class PluginConfig : BasePluginConfig
	{
		[JsonPropertyName("round-end-summary")]
		public bool RoundEndSummary { get; set; } = true;

		[JsonPropertyName("round-end-summary-allow-death-print")]
		public bool AlowDeathPrint { get; set; } = true;

		[JsonPropertyName("round-end-summary-show-only-killer")]
		public bool ShowOnlyKiller { get; set; } = false;

		[JsonPropertyName("round-end-summary-show-friendlyfire")]
		public bool ShowFriendlyFire { get; set; } = false;

		[JsonPropertyName("round-end-summary-show-all-damages")]
		public bool ShowAllDamages { get; set; } = false;

		[JsonPropertyName("round-end-summary-show-all-damages-enemies-only")]
		public bool ShowAllDamagesTeamOnly { get; set; } = true;

		[JsonPropertyName("center-damage-info")]
		public bool CenterDamageInfo { get; set; } = true;

		[JsonPropertyName("console-damage-info")]
		public bool ConsoleDamageInfo { get; set; } = true;

		[JsonPropertyName("ffa-mode")]
		public bool FFAMode { get; set; } = false;

		[JsonPropertyName("norounds-mode")]
		public bool NoRoundsMode { get; set; } = false;

		[JsonPropertyName("center-info-flags")]
		public List<string> CenterInfoFlags { get; set; } = new List<string>
		{
			"@myplugin/can-see-permission",
			"#myplugin/can-see-group",
			"can-see-override",
			"leave-empty-so-let-everyone-see"
		};

		[JsonPropertyName("ConfigVersion")]
		public override int Version { get; set; } = 4;
	}

	[MinimumApiVersion(244)]
	public class DamageInfoPlugin : BasePlugin, IPluginConfig<PluginConfig>
	{
		public override string ModuleName => "Damage Information (Zombie Mode)";
		public override string ModuleVersion => "2.3.3";
		public override string ModuleAuthor => "K4ryuu @ KitsuneLab, Oz-Lin";

		public required PluginConfig Config { get; set; } = new PluginConfig();
		public CCSGameRules? GameRules;
		private bool[] IsDataShown = new bool[65];
		private int[] VictimKiller = new int[65];

		public void OnConfigParsed(PluginConfig config)
		{
			if (config.Version < Config.Version)
				base.Logger.LogWarning("Configuration version mismatch (Expected: {0} | Current: {1})", this.Config.Version, config.Version);

			this.Config = config;
		}

		public override void Load(bool hotReload)
		{
			RegisterEventHandler<EventPlayerHurt>(OnPlayerHurt);
			RegisterEventHandler<EventPlayerDeath>(OnPlayerDeath);
			RegisterEventHandler<EventPlayerSpawn>(OnPlayerSpawn);
			RegisterEventHandler<EventRoundEnd>(OnRoundEnd);
			RegisterEventHandler<EventRoundStart>(OnRoundStart);

			RegisterListener<Listeners.OnMapStart>(OnMapStart);

			OnMapStart();
		}

		private void OnMapStart(string? mapName = null)
		{
			AddTimer(1.0f, () =>
			{
				GameRules = Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules").First().GameRules;
			});
		}

		private HookResult OnPlayerSpawn(EventPlayerSpawn @event, GameEventInfo info)
		{
			CCSPlayerController? player = @event.Userid;
			if (player is null || !player.IsValid || !player.PlayerPawn.IsValid)
				return HookResult.Continue;

			IsDataShown[player.Slot] = false;
			return HookResult.Continue;
		}

		private HookResult OnPlayerDeath(EventPlayerDeath @event, GameEventInfo info)
		{
			if (GameRules is null || GameRules.WarmupPeriod)
				return HookResult.Continue;

			if (!Config.AlowDeathPrint)
				return HookResult.Continue;

			CCSPlayerController? victim = @event.Userid;
			if (victim is null || !victim.IsValid || !victim.PlayerPawn.IsValid || victim.Connected == PlayerConnectedState.PlayerDisconnecting)
				return HookResult.Continue;

			CCSPlayerController? attacker = @event.Attacker;
			VictimKiller[victim.Slot] = attacker?.IsValid == true && attacker.PlayerPawn?.IsValid == true ? attacker.Slot : -1;

			DisplayDamageInfo(victim);

			if (Config.NoRoundsMode)
			{
				if (!playerDamageInfos.ContainsKey(victim.Slot) || playerDamageInfos[victim.Slot] is null)
					return HookResult.Continue;

				playerDamageInfos[victim.Slot].GivenDamage.Clear();
				playerDamageInfos[victim.Slot].TakenDamage.Clear();
			}

			return HookResult.Continue;
		}

		private HookResult OnPlayerHurt(EventPlayerHurt @event, GameEventInfo info)
		{
			CCSPlayerController? victim = @event.Userid;
			if (victim is null || !victim.IsValid || !victim.PlayerPawn.IsValid)
				return HookResult.Continue;

			CCSPlayerController? attacker = @event.Attacker;
			if (attacker is null || !attacker.IsValid || !attacker.PlayerPawn.IsValid)
				return HookResult.Continue;

			if (victim.TeamNum == attacker.TeamNum && !Config.ShowFriendlyFire)
				return HookResult.Continue;

			int damageToHeath = @event.DmgHealth;
			int damageToArmor = @event.DmgArmor;

			string hitgroup = Localizer[$"phrases.hitgroup.{@event.Hitgroup}"];

			if (!attacker.IsBot && (victim.TeamNum != attacker.TeamNum || Config.FFAMode))
			{
				if (Config.ConsoleDamageInfo)
				{
					attacker.PrintToConsole(Localizer["phrases.console.normal", victim.PlayerName, damageToHeath, damageToArmor, hitgroup]);

					if (!victim.IsBot)
						victim.PrintToConsole(Localizer["phrases.console.inverse", attacker.PlayerName, damageToHeath, damageToArmor, hitgroup]);
				}

				if (Config.CenterDamageInfo && PlayerHasPermissions(attacker))
				{

					if (!recentDamages.ContainsKey(attacker.Slot))
					{
						recentDamages[attacker.Slot] = new Dictionary<int, RecentDamage>();
					}

					if (!recentDamages[attacker.Slot].TryGetValue(victim.Slot, out RecentDamage? recentDamage))
					{
						recentDamage = new RecentDamage();
						recentDamages[attacker.Slot][victim.Slot] = recentDamage;
					}

					if (DateTime.Now - recentDamage.LastDamageTime <= TimeSpan.FromSeconds(5))
					{
						recentDamage.TotalDamage += damageToHeath;
					}
					else
					{
						recentDamage.TotalDamage = damageToHeath;
					}

					recentDamage.LastDamageTime = DateTime.Now;

					// This is because of a wierd bug, where if the damage is above 110, the "Armor: x - HitGroup:" gets replaced with ************** ._: idk why
					// If you wanna check it, remove this block, print always the normal and shoot a bot with awp to the head
					string printMessage = recentDamage.TotalDamage > 110
						? Localizer["phrases.center.deadly", recentDamage.TotalDamage, hitgroup]
						: Localizer["phrases.center.normal", recentDamage.TotalDamage, damageToArmor, hitgroup];

					attacker.PrintToCenter(printMessage);
				}
			}

			if (GameRules is null || GameRules.WarmupPeriod)
				return HookResult.Continue;

			if (Config.RoundEndSummary)
			{
				if (!playerDamageInfos.ContainsKey(victim.Slot))
					playerDamageInfos.Add(victim.Slot, new PlayerDamageInfo());

				if (!playerDamageInfos.ContainsKey(attacker.Slot))
					playerDamageInfos.Add(attacker.Slot, new PlayerDamageInfo());

				if (!playerDamageInfos[victim.Slot].TakenDamage.ContainsKey(attacker.Slot))
					playerDamageInfos[victim.Slot].TakenDamage.Add(attacker.Slot, new DamageInfo());

				if (!playerDamageInfos[attacker.Slot].GivenDamage.ContainsKey(victim.Slot))
					playerDamageInfos[attacker.Slot].GivenDamage.Add(victim.Slot, new DamageInfo());

				playerDamageInfos[victim.Slot].TakenDamage[attacker.Slot].TotalDamage += damageToHeath;
				playerDamageInfos[victim.Slot].TakenDamage[attacker.Slot].Hits++;

				playerDamageInfos[attacker.Slot].GivenDamage[victim.Slot].TotalDamage += damageToHeath;
				playerDamageInfos[attacker.Slot].GivenDamage[victim.Slot].Hits++;
			}

			return HookResult.Continue;
		}

		private HookResult OnRoundStart(EventRoundStart @event, GameEventInfo info)
		{
			playerDamageInfos.Clear();
			recentDamages.Clear();

			return HookResult.Continue;
		}

		private HookResult OnRoundEnd(EventRoundEnd @event, GameEventInfo info)
		{
			if (!Config.RoundEndSummary)
				return HookResult.Continue;

			Utilities.GetPlayers()
				.Where(p => p?.IsValid == true && p.PlayerPawn?.IsValid == true && !p.IsBot && !p.IsHLTV && p.Connected == PlayerConnectedState.PlayerConnected && p.TeamNum != 0)
				.ToList()
				.ForEach(DisplayDamageInfo);

			playerDamageInfos.Clear();
			recentDamages.Clear();

			return HookResult.Continue;
		}

        private void DisplayDamageInfo(CCSPlayerController player)
        {
            if (IsDataShown[player.Slot])
                return;

            if (Config.ShowAllDamages)
            {
                Dictionary<int, (DamageInfo given, DamageInfo taken)>? allPlayerSummaries = new Dictionary<int, (DamageInfo given, DamageInfo taken)>();

                IsDataShown[player.Slot] = true;

                foreach (var playerDamage in playerDamageInfos)
                {
                    allPlayerSummaries[playerDamage.Key] = SummarizePlayerDamage(playerDamage.Value);
                }

                if (allPlayerSummaries.Count == 0)
                    return;

                player.PrintToChat($" {Localizer["phrases.summary.startline"]}");

                // Sort and take top 5 damages based on TotalDamage
                var topTakenDamages = allPlayerSummaries
                    .OrderByDescending(summary => summary.Value.taken.TotalDamage)
                    .Take(5);

                var topGivenDamages = allPlayerSummaries
                    .OrderByDescending(summary => summary.Value.given.TotalDamage)
                    .Take(5);

                // Print top 5 damages taken
                foreach (var summary in topTakenDamages)
                {
                    PrintDamageSummary(player, summary);
                }

                // Print top 5 damages given
                foreach (var summary in topGivenDamages)
                {
                    PrintDamageSummary(player, summary);
                }

                player.PrintToChat($" {Localizer["phrases.summary.endline"]}");
            }
            else
            {
                if (!playerDamageInfos.ContainsKey(player.Slot) || playerDamageInfos[player.Slot] is null)
                    return;

                IsDataShown[player.Slot] = true;
                DisplayPlayerDamageInfo(player, playerDamageInfos[player.Slot]);
            }
        }

        private void PrintDamageSummary(CCSPlayerController player, KeyValuePair<int, (DamageInfo given, DamageInfo taken)> summary)
        {
            CCSPlayerController? otherPlayer = Utilities.GetPlayerFromSlot(summary.Key);
            if (Config.ShowAllDamagesTeamOnly && otherPlayer?.TeamNum == player.TeamNum)
                return;

            int otherPlayerHealth = 0;
            string otherPlayerName = "Unknown";

            if (otherPlayer?.IsValid == true)
            {
                otherPlayerName = otherPlayer.PlayerName;
                otherPlayerHealth = otherPlayer.PlayerPawn?.IsValid == true && otherPlayer.Connected == PlayerConnectedState.PlayerConnected ? otherPlayer.PlayerPawn.Value?.Health ?? 0 : 0;
            }

            string otherPlayerHealthString = otherPlayerHealth > 0
                                       ? $"{otherPlayerHealth}HP"
                                       : $"{Localizer["phrases.dead"]}";

            player.PrintToChat($" {Localizer["phrases.summary.dataline", 
                otherPlayerName, otherPlayerHealthString,
                summary.Value.taken.TotalDamage, summary.Value.taken.Hits,
                summary.Value.given.TotalDamage, summary.Value.given.Hits]}");
        }

        private (DamageInfo given, DamageInfo taken) SummarizePlayerDamage(PlayerDamageInfo playerInfo)
		{
			DamageInfo totalGivenDamage = new DamageInfo();
			DamageInfo totalTakenDamage = new DamageInfo();

			foreach (var given in playerInfo.GivenDamage)
			{
				totalGivenDamage.TotalDamage += given.Value.TotalDamage;
				totalGivenDamage.Hits += given.Value.Hits;
			}

			foreach (var taken in playerInfo.TakenDamage)
			{
				totalTakenDamage.TotalDamage += taken.Value.TotalDamage;
				totalTakenDamage.Hits += taken.Value.Hits;
			}

			return (totalGivenDamage, totalTakenDamage);
		}

        private void DisplayPlayerDamageInfo(CCSPlayerController player, PlayerDamageInfo playerInfo)
        {
            bool printed = false;
            List<int> processedPlayers = new List<int>();

            // Sort the TakenDamage dictionary by TotalDamage in descending order and take the top 5 entries
            var sortedTakenDamage = playerInfo.TakenDamage
                .OrderByDescending(entry => entry.Value.TotalDamage)
                .Take(5);

            foreach (var entry in sortedTakenDamage)
            {
                int otherPlayerId = entry.Key;

                if (Config.ShowOnlyKiller && VictimKiller[player.Slot] != otherPlayerId)
                    continue;

                // Skip already processed players from the given damage
                if (processedPlayers.Contains(otherPlayerId))
                    continue;

                if (!printed)
                {
                    player.PrintToChat($" {Localizer["phrases.summary.startline"]}");
                    printed = true;
                }

                DamageInfo takenDamageInfo = entry.Value;
                DamageInfo givenDamageInfo = new DamageInfo();  // No need to fetch given damage again since it's processed already

                string otherPlayerName = "Unknown";
                int otherPlayerHealth = 0;

                CCSPlayerController? otherPlayer = Utilities.GetPlayerFromSlot(otherPlayerId);
                if (otherPlayer?.IsValid == true)
                {
                    otherPlayerName = otherPlayer.PlayerName;
                    otherPlayerHealth = otherPlayer.PlayerPawn?.IsValid == true && otherPlayer.Connected == PlayerConnectedState.PlayerConnected ? otherPlayer.PlayerPawn.Value?.Health ?? 0 : 0;
                }

                string healthStatus = otherPlayerHealth > 0 ? $"{otherPlayerHealth}HP" : $"{Localizer["phrases.dead"]}";

                player.PrintToChat($" {Localizer["phrases.summary.dataline", otherPlayerName, healthStatus, takenDamageInfo.TotalDamage, takenDamageInfo.Hits, givenDamageInfo.TotalDamage, givenDamageInfo.Hits]}");
            }

            // Sort the GivenDamage dictionary by TotalDamage in descending order and take the top 5 entries
            var sortedGivenDamage = playerInfo.GivenDamage
                .OrderByDescending(entry => entry.Value.TotalDamage)
                .Take(5);

            foreach (var entry in sortedGivenDamage)
            {
                int otherPlayerId = entry.Key;

                if (Config.ShowOnlyKiller && VictimKiller[player.Slot] != otherPlayerId)
                    continue;

                if (!printed)
                {
                    player.PrintToChat($" {Localizer["phrases.summary.startline"]}");
                    printed = true;
                }

                DamageInfo givenDamageInfo = entry.Value;
                DamageInfo takenDamageInfo = playerInfo.TakenDamage.ContainsKey(otherPlayerId) ? playerInfo.TakenDamage[otherPlayerId] : new DamageInfo();
                processedPlayers.Add(otherPlayerId);

                string otherPlayerName = "Unknown";
                int otherPlayerHealth = 0;

                CCSPlayerController? otherPlayer = Utilities.GetPlayerFromSlot(otherPlayerId);
                if (otherPlayer?.IsValid == true)
                {
                    otherPlayerName = otherPlayer.PlayerName;
                    otherPlayerHealth = otherPlayer.PlayerPawn?.IsValid == true && otherPlayer.Connected == PlayerConnectedState.PlayerConnected ? otherPlayer.PlayerPawn.Value?.Health ?? 0 : 0;
                }

                string healthStatus = otherPlayerHealth > 0 ? $"{otherPlayerHealth}HP" : $"{Localizer["phrases.dead"]}";

                player.PrintToChat($" {Localizer["phrases.summary.dataline", otherPlayerName, healthStatus, takenDamageInfo.TotalDamage, takenDamageInfo.Hits, givenDamageInfo.TotalDamage, givenDamageInfo.Hits]}");
            }

            if (printed)
                player.PrintToChat($" {Localizer["phrases.summary.endline"]}");
        }




        private Dictionary<int, PlayerDamageInfo> playerDamageInfos = new Dictionary<int, PlayerDamageInfo>();

		private class PlayerDamageInfo
		{
			public Dictionary<int, DamageInfo> GivenDamage = new Dictionary<int, DamageInfo>();
			public Dictionary<int, DamageInfo> TakenDamage = new Dictionary<int, DamageInfo>();
		}

		private class DamageInfo
		{
			public int TotalDamage = 0;
			public int Hits = 0;
		}

		private Dictionary<int, Dictionary<int, RecentDamage>> recentDamages = new Dictionary<int, Dictionary<int, RecentDamage>>();

		private class RecentDamage
		{
			public int TotalDamage;
			public DateTime LastDamageTime;
		}

		public bool PlayerHasPermissions(CCSPlayerController player)
		{
			if (Config.CenterInfoFlags.Count == 0)
				return true;

			bool hasPermission = false;

			foreach (string checkPermission in Config.CenterInfoFlags)
			{
				switch (checkPermission[0])
				{
					case '@':
						if (AdminManager.PlayerHasPermissions(player, checkPermission))
							hasPermission = true;
						break;
					case '#':
						if (AdminManager.PlayerInGroup(player, checkPermission))
							hasPermission = true;
						break;
					default:
						if (AdminManager.PlayerHasCommandOverride(player, checkPermission))
							hasPermission = true;
						break;
				}
			}

			return hasPermission;
		}
	}
}