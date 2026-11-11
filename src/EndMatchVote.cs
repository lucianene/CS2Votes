using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Timers;
using Microsoft.Extensions.Logging;
namespace CS2Votes;

// end-of-match vote with native UI engine shows its map vote panel after scoreboard.
public sealed partial class CS2Votes
{
    private bool _intermissionHandled;
    private bool _voteActive;
    private bool _nativeVote;
    private bool _selectingSeen;
    private bool _finishScheduled;
    private DateTime _voteDeadline;
    private List<MapEntry> _voteOptions = [];
    private readonly Dictionary<ulong, int> _menuVotes = [];
    private readonly Dictionary<ulong, int> _seenNativeVotes = [];
    private CounterStrikeSharp.API.Modules.Timers.Timer? _votePollTimer;
    private CounterStrikeSharp.API.Modules.Timers.Timer? _emptyHomeTimer;

    // First Maps entry. Aim lists aim_redline, casual lists de_dust2.
    private MapEntry HomeMap => Config.Maps[0];

    private void ResetEndMatchVote()
    {
        _intermissionHandled = false;
        _voteActive = false;
        _selectingSeen = false;
        _finishScheduled = false;
        _voteOptions = [];
        _menuVotes.Clear();
        _seenNativeVotes.Clear();
        _votePollTimer?.Kill();
        _votePollTimer = null;
    }

    private HookResult OnIntermission(EventCsIntermission @event, GameEventInfo info)
    {
        if (_intermissionHandled || Config.Maps.Count == 0)
            return HookResult.Continue;
        _intermissionHandled = true;
        CancelPanoramaVote();
        CloseAllHudMenus();
        Logger.LogInformation("Match end. Engine vote winner before the vote: {Winner}", GameRules?.EndMatchMapVoteWinner);

        if (_changing)
            return HookResult.Continue;

        // Gamemode cfg can turn the engine panel back on after map start.
        // Set it again here so only one vote UI is on screen.
        ApplyServerSettings();

        // Nobody online: stay on the first map. Do not roll the mapgroup.
        if (Humans().Count == 0)
        {
            HoldEmptyServer();
            return HookResult.Continue;
        }

        if (_nextMap != null)
        {
            Broadcast("endmatch.nextmap_set", _nextMap.Name);
            ScheduleMapChange(_nextMap, Config.EndMatchVote.ChangeDelaySeconds + 4, "next map was set");
            return HookResult.Continue;
        }
        var options = BuildVoteOptions();
        if (!Config.EndMatchVote.Enabled || options.Count <= 1)
        {
            var map = Config.EndMatchVote.Enabled && options.Count == 1 ? options[0] : HomeMap;
            Broadcast("endmatch.nextmap_set", map.Name);
            ScheduleMapChange(map, Config.EndMatchVote.ChangeDelaySeconds + 4, "rotation (vote disabled)");
            return HookResult.Continue;
        }
        StartEndMatchVote(options);
        return HookResult.Continue;
    }

    // pool without recently played maps. Never fewer than two options if the pool allows it.
    private List<MapEntry> BuildVoteOptions()
    {
        var excluded = _state.History.Take(Config.EndMatchVote.ExcludeRecentMaps).ToHashSet();
        if (_currentMap != null && Config.EndMatchVote.ExcludeRecentMaps > 0)
            excluded.Add(_currentMap.Id);

        var pool = Config.Maps.Where(m => !excluded.Contains(m.Id)).ToList();
        if (pool.Count < 2)
            pool = Config.Maps.Where(m => m.Id != _currentMap?.Id).ToList();
        if (pool.Count == 0)
            pool = [.. Config.Maps];

        // random subset when the pool is larger than the panel
        return pool
            .OrderBy(_ => Random.Shared.Next())
            .Take(Config.EndMatchVote.MaxOptions)
            .OrderBy(m => Config.Maps.IndexOf(m))
            .ToList();
    }

    private void StartEndMatchVote(List<MapEntry> options)
    {
        _voteOptions = options;
        _voteActive = true;
        var seconds = Config.EndMatchVote.VoteSeconds;
        _nativeVote = UseNativeUi && FillNativeOptions(options);
        if (UseNativeUi && !_nativeVote)
        {
            // Options could not be written into the engine panel. Turn that
            // panel off so it cannot cover the center menu and eat the clicks.
            SetConVar("mp_endmatch_votenextmap", "0");
            Logger.LogWarning("Native vote options could not be applied. Using the center menu only.");
        }

        if (_nativeVote)
        {
            CloseAllHudMenus();
            SetConVar("mp_endmatch_votenextmap", "1");
            SetConVar("mp_endmatch_votenextleveltime", seconds.ToString());
            // The panel lasts VoteSeconds. This fallback only waits out the
            // scoreboard that plays first; the selecting event ends the vote.
            _voteDeadline = DateTime.UtcNow.AddSeconds(seconds + 20);
            Broadcast("endmatch.started_native", seconds);
        }
        else
        {
            SetConVar("mp_endmatch_votenextmap", "0");
            _voteDeadline = DateTime.UtcNow.AddSeconds(seconds);
            foreach (var player in Humans())
                ShowEndMatchMenu(player);
            Broadcast("endmatch.started_menu", seconds);
        }

        Logger.LogInformation("End-of-match vote ({Ui}): {Maps}", _nativeVote ? "native" : "menu",
            string.Join(", ", options.Select(m => $"{m.Name}#{MapGroupIndex(m)}")));

        _votePollTimer = AddTimer(1f, PollEndMatchVote, TimerFlags.REPEAT | TimerFlags.STOP_ON_MAPCHANGE);
    }

    private bool FillNativeOptions(List<MapEntry> options)
    {
        var rules = GameRules;
        if (rules == null)
        {
            Logger.LogWarning("No game rules entity; using the menu vote.");
            return false;
        }

        var indices = options.Select(MapGroupIndex).ToList();
        var missing = options.Where((_, i) => indices[i] < 0).Select(m => m.Name).ToList();
        if (missing.Count > 0)
        {
            Logger.LogWarning("Not in mapgroup {Group}: {Maps}. Using the menu vote.", Config.MapGroup, string.Join(", ", missing));
            return false;
        }

        WriteNativeOptions(rules, indices);
        // engine can fill arrays itself at intermission.
        AddTimer(1f, () => ReapplyNativeOptions(indices), TimerFlags.STOP_ON_MAPCHANGE);
        AddTimer(3f, () => ReapplyNativeOptions(indices), TimerFlags.STOP_ON_MAPCHANGE);
        return true;
    }

    private void ReapplyNativeOptions(List<int> indices)
    {
        if (_voteActive && _nativeVote && GameRules is { } rules)
            WriteNativeOptions(rules, indices);
    }

    private void WriteNativeOptions(CCSGameRules rules, List<int> indices)
    {
        var types = rules.EndMatchMapGroupVoteTypes;
        var opts = rules.EndMatchMapGroupVoteOptions;
        for (var slot = 0; slot < opts.Length; slot++)
        {
            var index = slot < indices.Count ? indices[slot] : -1;
            opts[slot] = index;
            if (slot < types.Length)
                types[slot] = index >= 0 ? 0 : -1;
        }
        try
        {
            if (_gameRulesProxy is { IsValid: true })
                Utilities.SetStateChanged(_gameRulesProxy, "CCSGameRulesProxy", "m_pGameRules");
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "SetStateChanged on game rules failed");
        }
    }
    private void ShowEndMatchMenu(CCSPlayerController player)
    {
        var menu = new HudMenu(T(player, "menu.endmatch_title"), _voteDeadline);
        for (var i = 0; i < _voteOptions.Count; i++)
        {
            var slot = i;
            var map = _voteOptions[i];
            menu.Items.Add(new HudMenuItem(map.Name, true, p =>
            {
                if (!_voteActive || _nativeVote)
                    return;
                var changed = !_menuVotes.TryGetValue(p.SteamID, out var old) || old != slot;
                _menuVotes[p.SteamID] = slot;
                // menu stays open until the vote ends
                menu.Marked = slot;
                menu.Dirty = true;
                if (changed)
                    Broadcast("endmatch.player_voted", p.PlayerName, map.Name);
                if (AllHumansVoted())
                    ScheduleFinish(1.5f);
            }));
        }
        OpenHudMenu(player, menu);
    }

    /// <summary>
    /// Panel slot to the map the client showed. The cell in EndMatchMapGroupVoteOptions
    /// is the mapgroup index. A value that is not a live slot is itself a mapgroup index.
    /// </summary>
    private MapEntry? MapFromPanelSlot(int raw)
    {
        if (raw < 0)
            return null;
        var rules = GameRules;
        if (rules != null && raw < rules.EndMatchMapGroupVoteOptions.Length)
        {
            var fromSlot = MapByGroupIndex(rules.EndMatchMapGroupVoteOptions[raw]);
            if (fromSlot != null)
                return fromSlot;
        }
        return MapByGroupIndex(raw);
    }

    private MapEntry? MapByGroupIndex(int groupIndex)
    {
        if (groupIndex < 0)
            return null;
        return Config.Maps.FirstOrDefault(m => MapGroupIndex(m) == groupIndex);
    }

    // Index into _voteOptions, or -1. Native clicks are resolved through the live panel.
    private int VoteSlotOf(CCSPlayerController player)
    {
        if (_nativeVote)
        {
            var map = MapFromPanelSlot(player.EndMatchNextMapVote);
            return map == null ? -1 : _voteOptions.IndexOf(map);
        }
        if (!_menuVotes.TryGetValue(player.SteamID, out var slot))
            return -1;
        return slot >= 0 && slot < _voteOptions.Count ? slot : -1;
    }

    private bool PlayerHasVoted(CCSPlayerController player)
    {
        if (_nativeVote)
            return MapFromPanelSlot(player.EndMatchNextMapVote) != null;
        return VoteSlotOf(player) >= 0;
    }

    private bool AllHumansVoted()
    {
        var humans = Humans();
        return humans.Count > 0 && humans.All(PlayerHasVoted);
    }

    private void PollEndMatchVote()
    {
        if (!_voteActive)
        {
            _votePollTimer?.Kill();
            _votePollTimer = null;
            return;
        }

        if (_nativeVote)
        {
            // tell chat who voted for what, like good old RTV plugins did!
            foreach (var player in Humans())
            {
                var slot = player.EndMatchNextMapVote;
                var map = MapFromPanelSlot(slot);
                if (map == null || (_seenNativeVotes.TryGetValue(player.SteamID, out var old) && old == slot))
                    continue;
                _seenNativeVotes[player.SteamID] = slot;
                Broadcast("endmatch.player_voted", player.PlayerName, map.Name);
            }
        }

        if (AllHumansVoted())
            ScheduleFinish(2f);
        else if (DateTime.UtcNow >= _voteDeadline)
            ScheduleFinish(0.1f);
    }

    private HookResult OnSelectingMap(EventEndmatchMapvoteSelectingMap @event, GameEventInfo info)
    {
        _selectingSeen = true;
        Logger.LogInformation("Engine selecting map: count={Count} slots={Slots} winner={Winner}",
            @event.Count,
            string.Join(",", new[] { @event.Slot1, @event.Slot2, @event.Slot3, @event.Slot4, @event.Slot5,
                @event.Slot6, @event.Slot7, @event.Slot8, @event.Slot9, @event.Slot10 }),
            GameRules?.EndMatchMapVoteWinner);
        if (_voteActive)
            ScheduleFinish(0.2f);
        return HookResult.Continue;
    }

    private void ScheduleFinish(float delay)
    {
        if (_finishScheduled || !_voteActive)
            return;
        _finishScheduled = true;
        AddTimer(delay, () => FinishEndMatchVote(0), TimerFlags.STOP_ON_MAPCHANGE);
    }

    private void FinishEndMatchVote(int attempt)
    {
        if (!_voteActive)
            return;

        var counts = new int[_voteOptions.Count];
        foreach (var player in Humans())
        {
            var slot = VoteSlotOf(player);
            if (slot >= 0)
                counts[slot]++;
        }
        var total = counts.Sum();
        MapEntry? chosen = null;
        var fromEngine = false;
        // The winner field is a slot into the live option array, not into our filtered list.
        // A click on Redline must stay Redline when that list has already dropped it.
        if (_nativeVote && _selectingSeen)
        {
            chosen = MapFromPanelSlot(GameRules?.EndMatchMapVoteWinner ?? -1);
            if (chosen == null && attempt < 12)
            {
                AddTimer(0.5f, () => FinishEndMatchVote(attempt + 1), TimerFlags.STOP_ON_MAPCHANGE);
                return;
            }
            fromEngine = chosen != null;
        }

        var tied = false;
        var chosenVotes = 0;
        if (chosen == null)
        {
            if (total == 0 || counts.Length == 0)
            {
                chosen = HomeMap;
            }
            else
            {
                var top = counts.Max();
                var winners = Enumerable.Range(0, counts.Length)
                    .Where(i => counts[i] == top)
                    .OrderBy(i => Config.Maps.IndexOf(_voteOptions[i]))
                    .ToList();
                tied = winners.Count > 1;
                chosen = _voteOptions[winners[0]];
                chosenVotes = counts[winners[0]];
            }
        }
        else
        {
            var index = _voteOptions.IndexOf(chosen);
            if (index >= 0)
            {
                chosenVotes = counts[index];
                tied = chosenVotes > 0 && counts.Count(c => c == chosenVotes) > 1;
            }
        }

        _voteActive = false;
        _votePollTimer?.Kill();
        _votePollTimer = null;
        CloseAllHudMenus();

        var raw = GameRules?.EndMatchMapVoteWinner ?? -1;
        var cell = -1;
        if (GameRules != null && raw >= 0 && raw < GameRules.EndMatchMapGroupVoteOptions.Length)
            cell = GameRules.EndMatchMapGroupVoteOptions[raw];
        Logger.LogInformation("Vote result: {Result}; engine winner {Engine} option {Cell}; chosen {Map}",
            string.Join(", ", _voteOptions.Select((m, i) => $"{m.Name}={counts[i]}")),
            raw, cell, chosen.Name);

        if (Humans().Count == 0)
        {
            HoldEmptyServer();
            return;
        }

        if (fromEngine)
            Broadcast("endmatch.nextmap_set", chosen.Name);
        else if (total == 0)
            Broadcast("endmatch.no_votes", chosen.Name);
        else if (tied)
            Broadcast("endmatch.tie", chosen.Name, chosenVotes);
        else
            Broadcast("endmatch.result", chosen.Name, chosenVotes, total);
        BroadcastCenter("center.nextmap", chosen.Name);
        ScheduleMapChange(chosen, Config.EndMatchVote.ChangeDelaySeconds, "end-of-match vote");
    }

    // Empty server stays on the first map. A different map is loaded once, then held.
    private void HoldEmptyServer()
    {
        CancelEmptyHome();
        var home = HomeMap;
        SetConVar("mp_match_end_changelevel", "0");
        SetConVar("mp_endmatch_votenextmap", "0");
        if (_currentMap?.Id == home.Id)
        {
            Logger.LogInformation("No players online. Staying on {Map}.", home.Name);
            Server.ExecuteCommand("mp_restartgame 1");
            return;
        }
        Logger.LogInformation("No players online. Next map is {Map}.", home.Name);
        ScheduleMapChange(home, Config.EndMatchVote.ChangeDelaySeconds, "empty server");
    }

    private void WatchEmptyServer()
    {
        if (Humans().Count > 0 || Config.Maps.Count == 0 || _changing || _emptyHomeTimer != null || _voteActive)
            return;
        if (_currentMap?.Id == HomeMap.Id)
            return;
        _emptyHomeTimer = AddTimer(5f, () =>
        {
            _emptyHomeTimer = null;
            if (Humans().Count > 0 || _changing || _voteActive || _intermissionHandled)
                return;
            if (_currentMap?.Id == HomeMap.Id)
                return;
            ScheduleMapChange(HomeMap, 0.1f, "server empty");
        }, TimerFlags.STOP_ON_MAPCHANGE);
    }

    private void CancelEmptyHome()
    {
        _emptyHomeTimer?.Kill();
        _emptyHomeTimer = null;
    }
}
