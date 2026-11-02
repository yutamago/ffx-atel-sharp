// SPDX-License-Identifier: MIT

namespace Ffx;

/// <summary>
///     The save game as the scripts see it: the known fields of storage 0 (<c>Storage.Save</c>), as
///     storage 0 offsets. Storage 0 starts at <c>g_SaveData + 0x1ec</c> (<c>AtelSetUpCtrlWork</c>,
///     docs/corpus.md "Story progress"), so a field at save offset X is storage 0 offset X - 0x1ec; the
///     save offsets are in each field's summary. A script declares the variable it uses with the
///     constant: <c>[Var(Storage.Save, Save.StoryProgress)] public static ushort StoryProgress;</c>.
/// </summary>
/// <remarks>
///     Names: Fahrenheit's <c>SaveData</c> (<c>core/ffx/savedata.cs</c>, Fahrenheit f7e2258; used with
///     permission from their lead), in C# case, as listed in ffx-rust's <c>docs/formats/save.md</c>. Only
///     fields at or after save offset <c>0x1ec</c> are reachable from a script. Storage 0 offsets the
///     scripts use without a name here are written <c>Save_0123</c> (the storage 0 offset in hex).
/// </remarks>
public static class Save
{
    /// <summary><c>progression_flags_calm_lands_quest</c>, save <c>+0x279</c>.</summary>
    public const int ProgressionFlagsCalmLandsQuest = 0x008d;
    /// <summary><c>progression_flags_belgemine_fight</c>, save <c>+0x287</c>.</summary>
    public const int ProgressionFlagsBelgemineFight = 0x009b;
    /// <summary><c>progression_flags_monster_arena_unlock_quest</c>, save <c>+0x28b</c>.</summary>
    public const int ProgressionFlagsMonsterArenaUnlockQuest = 0x009f;
    /// <summary><c>progression_flags_energy_blast</c>, save <c>+0x2f0</c>.</summary>
    public const int ProgressionFlagsEnergyBlast = 0x0104;
    /// <summary><c>progression_flags_remiem_temple</c>, save <c>+0x300</c>.</summary>
    public const int ProgressionFlagsRemiemTemple = 0x0114;
    /// <summary><c>luca_controllable_character</c>, save <c>+0x337</c>.</summary>
    public const int LucaControllableCharacter = 0x014b;
    /// <summary><c>progression_flags_omega_ruins</c>, save <c>+0x3be</c>.</summary>
    public const int ProgressionFlagsOmegaRuins = 0x01d2;
    /// <summary><c>progression_flags_home</c>, save <c>+0x3c0</c>.</summary>
    public const int ProgressionFlagsHome = 0x01d4;
    /// <summary><c>progression_flags_thunder_plains</c>, save <c>+0x3f1</c>.</summary>
    public const int ProgressionFlagsThunderPlains = 0x0205;
    /// <summary><c>lightning_dodging_total_bolts</c> (<c>u16</c>), save <c>+0x3fc</c>.</summary>
    public const int LightningDodgingTotalBolts = 0x0210;
    /// <summary><c>lightning_dodging_total_dodges</c> (<c>u16</c>), save <c>+0x3fe</c>.</summary>
    public const int LightningDodgingTotalDodges = 0x0212;
    /// <summary><c>lightning_dodging_highest_consecutive_dodges</c> (<c>u16</c>), save <c>+0x400</c>.</summary>
    public const int LightningDodgingHighestConsecutiveDodges = 0x0214;
    /// <summary><c>soundtrack_type</c>, save <c>+0x5ec</c>.</summary>
    public const int SoundtrackType = 0x0400;
    /// <summary>
    ///     <c>story_progress</c> (<c>u16</c>), save <c>+0xbec</c>: <c>g_SaveScenario</c>, read by
    ///     <c>AtelGetScenarioFlag</c>. The values the scripts store are the <see cref="Story"/> constants.
    /// </summary>
    public const int StoryProgress = 0x0a00;
    /// <summary><c>celestial_mirror_obtained</c>, save <c>+0xc38</c>.</summary>
    public const int CelestialMirrorObtained = 0x0a4c;
    /// <summary><c>celestials_obtained</c> (bits), save <c>+0xc39</c>.</summary>
    public const int CelestialsObtained = 0x0a4d;
    /// <summary><c>anima_seals_unlocked</c>, save <c>+0xc5c</c>.</summary>
    public const int AnimaSealsUnlocked = 0x0a70;
    /// <summary><c>current_airship_location</c> (<c>u32</c>), save <c>+0xc60</c>.</summary>
    public const int CurrentAirshipLocation = 0x0a74;
    /// <summary><c>celestials_half_powered</c> (bits), save <c>+0xc6c</c>.</summary>
    public const int CelestialsHalfPowered = 0x0a80;
    /// <summary><c>celestials_full_powered</c> (bits), save <c>+0xc6d</c>.</summary>
    public const int CelestialsFullPowered = 0x0a81;
    /// <summary><c>optional_aeons_unlocked</c> (bits: Anima, Yojimbo, the Magus Sisters), save <c>+0xc7b</c>.</summary>
    public const int OptionalAeonsUnlocked = 0x0a8f;
    /// <summary><c>jecht_spheres.flags_spheres_seen</c> (<c>u16</c> bits), save <c>+0xc7c</c>.</summary>
    public const int JechtSpheresSeen = 0x0a90;
    /// <summary><c>jecht_spheres.collected_amount</c>, save <c>+0xc7f</c>.</summary>
    public const int JechtSpheresCollected = 0x0a93;
    /// <summary><c>unlocked_airship_destinations</c> (<c>u16</c> bits), save <c>+0xc81</c>.</summary>
    public const int UnlockedAirshipDestinations = 0x0a95;
    /// <summary><c>completion_flags_dark_valefor</c>, save <c>+0xc89</c> (<c>g_SaveInterValefor</c>).</summary>
    public const int CompletionFlagsDarkValefor = 0x0a9d;
    /// <summary><c>completion_flags_dark_ifrit</c>, save <c>+0xc8a</c>.</summary>
    public const int CompletionFlagsDarkIfrit = 0x0a9e;
    /// <summary><c>completion_flags_dark_ixion</c>, save <c>+0xc8b</c>.</summary>
    public const int CompletionFlagsDarkIxion = 0x0a9f;
    /// <summary><c>completion_flags_dark_shiva</c>, save <c>+0xc8c</c>.</summary>
    public const int CompletionFlagsDarkShiva = 0x0aa0;
    /// <summary><c>completion_flags_dark_bahamut</c>, save <c>+0xc8d</c>.</summary>
    public const int CompletionFlagsDarkBahamut = 0x0aa1;
    /// <summary><c>completion_flags_dark_yojimbo</c>, save <c>+0xc8e</c>.</summary>
    public const int CompletionFlagsDarkYojimbo = 0x0aa2;
    /// <summary><c>completion_flags_dark_anima</c>, save <c>+0xc8f</c>.</summary>
    public const int CompletionFlagsDarkAnima = 0x0aa3;
    /// <summary><c>completion_flags_dark_magus_sisters</c>, save <c>+0xc90</c>.</summary>
    public const int CompletionFlagsDarkMagusSisters = 0x0aa4;
    /// <summary><c>penance_unlock_state</c>, save <c>+0xc91</c>.</summary>
    public const int PenanceUnlockState = 0x0aa5;
    /// <summary><c>battle_dialog_lines_seen</c> (42 bytes), save <c>+0xcd7</c>.</summary>
    public const int BattleDialogLinesSeen = 0x0aeb;
    /// <summary><c>blitzball_data.player_cost_per_game</c> (60 × <c>u16</c>), save <c>+0x1984</c>.</summary>
    public const int BlitzballPlayerCostPerGame = 0x1798;
    /// <summary><c>blitzball_data.league_prize_index</c> (3 × <c>u16</c>), save <c>+0x19fc</c>.</summary>
    public const int BlitzballLeaguePrizeIndex = 0x1810;
    /// <summary><c>blitzball_data.tournament_prize_index</c> (3 × <c>u16</c>), save <c>+0x1a02</c>.</summary>
    public const int BlitzballTournamentPrizeIndex = 0x1816;
    /// <summary><c>blitzball_data.league_top_scorer_prize_index</c> (<c>u16</c>), save <c>+0x1a08</c>.</summary>
    public const int BlitzballLeagueTopScorerPrizeIndex = 0x181c;
    /// <summary><c>blitzball_data.tournament_top_scorer_prize_index</c> (<c>u16</c>), save <c>+0x1a0a</c>.</summary>
    public const int BlitzballTournamentTopScorerPrizeIndex = 0x181e;
    /// <summary><c>blitzball_data.player_uncovered_techs_page_1</c>, save <c>+0x1a0c</c>.</summary>
    public const int BlitzballPlayerUncoveredTechsPage1 = 0x1820;
    /// <summary><c>config</c> (<c>u32</c> bits), save <c>+0x3d0c</c>: <c>g_SaveMenu</c>.</summary>
    public const int Config = 0x3b20;
    /// <summary><c>unlocked_primers</c> (<c>u32</c> bits), save <c>+0x3d10</c>.</summary>
    public const int UnlockedPrimers = 0x3b24;
    /// <summary><c>battle_count</c> (<c>u32</c>), save <c>+0x3d14</c>: <c>g_SaveBattleCount</c>.</summary>
    public const int BattleCount = 0x3b28;
    /// <summary><c>gil</c> (<c>u32</c>), save <c>+0x3d48</c>: <c>g_SaveGil</c>.</summary>
    public const int Gil = 0x3b5c;
    /// <summary><c>party_frontline</c> (3 bytes), save <c>+0x3d58</c>: <c>g_SaveInParty</c>.</summary>
    public const int PartyFrontline = 0x3b6c;
    /// <summary><c>party_backline</c> (17 bytes), save <c>+0x3d5b</c>: <c>g_SaveOutParty</c>.</summary>
    public const int PartyBackline = 0x3b6f;
    /// <summary><c>ability_map_limit</c>, save <c>+0x3d6c</c>: <c>g_SaveSharedCommands</c>.</summary>
    public const int AbilityMapLimit = 0x3b80;
    /// <summary><c>yojimbo_compatibility</c> (<c>u32</c>), save <c>+0x3da4</c>: <c>g_SaveYoujinbouCom</c>.</summary>
    public const int YojimboCompatibility = 0x3bb8;
    /// <summary><c>yojimbo_type</c>, save <c>+0x3da8</c>.</summary>
    public const int YojimboType = 0x3bbc;
    /// <summary><c>tidus_limit_uses</c> (<c>u32</c>), save <c>+0x3dac</c>.</summary>
    public const int TidusLimitUses = 0x3bc0;
    /// <summary><c>successful_rikku_steals</c> (<c>u32</c>), save <c>+0x3db0</c>.</summary>
    public const int SuccessfulRikkuSteals = 0x3bc4;
    /// <summary><c>bribe_gil_spent</c> (<c>u32</c>), save <c>+0x3db4</c>.</summary>
    public const int BribeGilSpent = 0x3bc8;
    /// <summary><c>event_flags</c> (<c>0x100</c> bytes), save <c>+0x3dcc</c>: <c>g_SaveEventFlags</c>.</summary>
    public const int EventFlags = 0x3be0;
    /// <summary><c>inventory_ids</c>, save <c>+0x3ecc</c>: <c>g_SaveItemIds</c>.</summary>
    public const int InventoryIds = 0x3ce0;
    /// <summary><c>inventory_counts</c>, save <c>+0x40cc</c>: <c>g_SaveItemCounts</c>.</summary>
    public const int InventoryCounts = 0x3ee0;
    /// <summary><c>items_acquired</c> (16 × <c>u16</c>), save <c>+0x41cc</c>: <c>g_SaveItemCheck</c>.</summary>
    public const int ItemsAcquired = 0x3fe0;
    /// <summary><c>items_used</c> (16 × <c>u16</c>), save <c>+0x41ec</c>: <c>g_SaveItemUse</c>.</summary>
    public const int ItemsUsed = 0x4000;
    /// <summary><c>monsters_captured</c> (<c>0x200</c> bytes), save <c>+0x420c</c>: <c>g_SaveCaptures</c>.</summary>
    public const int MonstersCaptured = 0x4020;
    /// <summary><c>monsters_seen</c> (32 × <c>u16</c>), save <c>+0x440c</c>: <c>g_SaveMonsterMeet</c>.</summary>
    public const int MonstersSeen = 0x4220;
    /// <summary><c>monsters_defeated</c> (32 × <c>u16</c>), save <c>+0x444c</c>: <c>g_SaveMonsterDefeat</c>.</summary>
    public const int MonstersDefeated = 0x4260;
    /// <summary><c>key_items</c> (8 × <c>u16</c>), save <c>+0x448c</c>: <c>g_SaveImportant</c>.</summary>
    public const int KeyItems = 0x42a0;
    /// <summary><c>equipment</c> (200 × 22 bytes), save <c>+0x449c</c>: <c>g_Equipment</c>.</summary>
    public const int Equipment = 0x42b0;
    /// <summary><c>ply_saves</c> (18 × <c>0x94</c> bytes), save <c>+0x55cc</c>: <c>g_PlySave</c>.</summary>
    public const int PlySaves = 0x53e0;
    /// <summary><c>commands_used</c> (18 × 22 × <c>u16</c>), save <c>+0x6034</c>: <c>g_SaveCommandUse</c>.</summary>
    public const int CommandsUsed = 0x5e48;
    /// <summary><c>character_names</c> (18 × 20 bytes), save <c>+0x634c</c>: <c>g_SaveChrNames</c>.</summary>
    public const int CharacterNames = 0x6160;
}
