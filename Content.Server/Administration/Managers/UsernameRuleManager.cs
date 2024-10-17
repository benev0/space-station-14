using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Content.Server.Chat.Managers;
using Content.Server.Database;
using Content.Server.GameTicking;
using Content.Shared.Administration;
using Robust.Server.Player;
using Robust.Shared.Asynchronous;
using Robust.Shared.Configuration;
using Robust.Shared.Network;
using Robust.Shared.Player;
using Robust.Shared.Timing;

namespace Content.Server.Administration.Managers;

public sealed partial class UsernameRuleManager : IUsernameRuleManager, IPostInjectInit
{
    [Dependency] private readonly IServerDbManager _db = default!;
    [Dependency] private readonly IPlayerManager _playerManager = default!;
    [Dependency] private readonly ServerDbEntryManager _entryManager = default!;
    [Dependency] private readonly IChatManager _chat = default!;
    [Dependency] private readonly ILogManager _logManager = default!;
    [Dependency] private readonly IGameTiming _gameTiming = default!;
    [Dependency] private readonly ITaskManager _taskManager = default!;
    [Dependency] private readonly INetManager _net = default!;
    [Dependency] private readonly IAdminManager _admin = default!;
    [Dependency] private readonly IConfigurationManager _cfg = default!;
    [Dependency] private readonly ILocalizationManager _localizationManager = default!;
    [Dependency] private readonly IEntitySystemManager _systems = default!;

    private ISawmill _sawmill = default!;

    public const string SawmillId = "admin.username";

    // this could be changed to sorted to implement deltas + removed bool or negative id
    // id : (regex, regexString, message, ban)
    private readonly Dictionary<int, (Regex?, string, string, bool)> _cachedUsernameRules = new();
    private readonly HashSet<string> _cachedUsernames = new();

    public async void Initialize()
    {
        _db.SubscribeToNotifications(OnDatabaseNotification);

        // needed for deadmin and readmin
        _admin.OnPermsChanged += OnReAdmin;

        _net.RegisterNetMessage<MsgUsernameBan>();
        _net.RegisterNetMessage<MsgRequestUsernameBans>(OnRequestBans);

        _net.RegisterNetMessage<MsgFullUsernameBan>();
        _net.RegisterNetMessage<MsgRequestFullUsernameBan>(OnRequestFullUsernameBan);

        var rules = await _db.GetServerUsernameRulesAsync(false);

        if (rules == null)
        {
            _sawmill.Warning("failed to get rules from database");
            return;
        }

        foreach (var ruleDef in rules)
        {
            if (ruleDef.Id == null)
            {
                _sawmill.Warning("rule had Id of null");
                continue;
            }
            CacheCompiledRegex(ruleDef.Id ?? -1, ruleDef.Regex, ruleDef.Expression, ruleDef.Message, ruleDef.ExtendToBan);
        }
    }

    private void OnRequestBans(MsgRequestUsernameBans msg)
    {
        if (!_playerManager.TryGetSessionById(msg.MsgChannel.UserId, out var player))
        {
            return;
        }

        if (!_admin.HasAdminFlag(player, AdminFlags.Ban))
        {
            return;
        }

        _sawmill.Verbose("Received username ban refresh request");

        SendResetUsernameBan(msg.MsgChannel);
    }

    private void OnRequestFullUsernameBan(MsgRequestFullUsernameBan msg)
    {
        if (!_playerManager.TryGetSessionById(msg.MsgChannel.UserId, out var player))
        {
            return;
        }

        if (!_admin.HasAdminFlag(player, AdminFlags.Ban))
        {
            return;
        }

        _sawmill.Verbose($"Received request for more info on username ban {msg.BanId} from {msg.MsgChannel.UserName}");

        SendFullUsernameBan(msg.MsgChannel, msg.BanId);
    }

    private async void SendFullUsernameBan(INetChannel channel, int banId)
    {
        _sawmill.Debug($"sending full ban for {banId}");

        var banDef = await _db.GetServerUsernameRuleAsync(banId);

        if (banDef is null)
        {
            return;
        }

        MsgFullUsernameBanContent msgContent = new(
            banDef.CreationTime.UtcDateTime,
            banId,

            banDef.Regex,
            banDef.ExtendToBan,
            banDef.Retired,

            banDef.RoundId,
            banDef.RestrictingAdmin,
            banDef.RetiringAdmin,
            banDef.RetireTime?.UtcDateTime,

            banDef.Expression,
            banDef.Message
        );

        MsgFullUsernameBan msg = new MsgFullUsernameBan()
        {
            FullUsernameBan = msgContent
        };

        _net.ServerSendMessage(msg, channel);
    }

    private void OnReAdmin(AdminPermsChangedEventArgs args)
    {
        if (args is null || args?.Flags == null || (args?.Flags & AdminFlags.Ban) == AdminFlags.Ban)
        {
            return;
        }

        if (args is not null)
        {
            SendResetUsernameBan(args.Player.Channel);
        }
    }

    private void CacheCompiledRegex(int id, bool regex, string expression, string message, bool ban)
    {
        _sawmill.Info($"caching rule {id} {expression}");
        if (_cachedUsernameRules.ContainsKey(id))
        {
            _sawmill.Warning($"caching rule {id} already listed in cache");
            return;
        }

        var compiledRegex = regex ? new Regex(expression, RegexOptions.Compiled) : null;

        if (!regex)
        {
            _cachedUsernames.Add(expression);
        }

        _cachedUsernameRules[id] = (compiledRegex, expression, message, ban);
    }

    private void ClearCompiledRegex(int id)
    {
        var expression = _cachedUsernameRules[id].Item2;
        _cachedUsernames.Remove(expression);
        _cachedUsernameRules.Remove(id);
    }

    public async void CreateUsernameRule(bool regex, string expression, string message, NetUserId? restrictingAdmin, bool extendToBan = false)
    {
        if (string.IsNullOrEmpty(expression))
        {
            return;
        }

        var finalMessage = message ?? expression;

        _systems.TryGetEntitySystem<GameTicker>(out var ticker);
        int? roundId = ticker == null || ticker.RoundId == 0 ? null : ticker.RoundId;

        var ruleDef = new ServerUsernameRuleDef(
            null,
            DateTimeOffset.Now,
            roundId,
            regex,
            expression,
            finalMessage,
            restrictingAdmin,
            extendToBan,
            false,
            null,
            null);

        int resultId = await _db.CreateUsernameRuleAsync(ruleDef);

        CacheCompiledRegex(resultId, regex, expression, finalMessage, extendToBan);

        SendAddUsernameBan(resultId, regex, expression, finalMessage, extendToBan);

        var adminName = restrictingAdmin == null
            ? Loc.GetString("system-user")
            : (await _db.GetPlayerRecordByUserId(restrictingAdmin.Value))?.LastSeenUserName ?? Loc.GetString("system-user");

        var logMessage = Loc.GetString(
            "server-username-rule-create",
            ("admin", adminName),
            ("expression", expression),
            ("message", finalMessage));

        _sawmill.Info(logMessage);
        _chat.SendAdminAlert(logMessage);

        KickMatchingConnectedPlayers(resultId, ruleDef, "new username rule");
    }

    public async Task RemoveUsernameRule(int restrictionId, NetUserId? removingAdmin)
    {
        var rule = await _db.GetServerUsernameRuleAsync(restrictionId);

        if (rule == null)
        {
            return;
        }

        ClearCompiledRegex(restrictionId);
        SendRemoveUsernameBan(restrictionId);

        var adminName = removingAdmin == null
            ? Loc.GetString("system-user")
            : (await _db.GetPlayerRecordByUserId(removingAdmin.Value))?.LastSeenUserName ?? Loc.GetString("system-user");

        var logMessage = Loc.GetString(
            "server-username-rule-remove",
            ("admin", adminName),
            ("expression", rule.Expression),
            ("message", rule.Message));

        _sawmill.Info(logMessage);
        _chat.SendAdminAlert(logMessage);

        await _db.RemoveServerUsernameRuleAsync(restrictionId, removingAdmin, DateTimeOffset.Now);
    }

    public List<(int, string, string, bool)> GetUsernameRules()
    {
        return [];
    }

    public async Task<(bool, string, bool)> IsUsernameBannedAsync(string username)
    {
        var whitelist = await _db.CheckUsernameWhitelistAsync(username);
        if (whitelist)
        {
            return (false, "", false);
        }

        foreach ((Regex? rule, _, string message, bool ban) in _cachedUsernameRules.Values)
        {
            if (rule?.IsMatch(username) ?? false)
            {
                return (true, message, ban);
            }
        }

        return (false, "", false);
    }

    public async void Restart()
    {
        var rules = await _db.GetServerUsernameRulesAsync(false);

        if (rules == null)
        {
            _sawmill.Warning("service restart failed");
            return;
        }

        _cachedUsernameRules.Clear();

        foreach (var ruleDef in rules)
        {
            if (ruleDef.Id == null)
            {
                continue;
            }

            CacheCompiledRegex(ruleDef.Id ?? -1, ruleDef.Regex, ruleDef.Expression, ruleDef.Message, ruleDef.ExtendToBan);
            KickMatchingConnectedPlayers(ruleDef.Id ?? -1, ruleDef, "username rule service restart");
        }

        SendResetUsernameBan();
    }

    private void KickMatchingConnectedPlayers(int id, ServerUsernameRuleDef def, string source)
    {
        if (!_cachedUsernameRules.ContainsKey(id))
        {
            return;
        }

        (Regex? compiledRule, string expression, _, _) = _cachedUsernameRules[id];

        if (compiledRule == null)
        {
            _playerManager.TryGetSessionByUsername(expression, out var player);
            if (player != null)
            {
                KickForUsernameRuleDef(player, def);
            }
            return;
        }

        foreach (var player in _playerManager.Sessions)
        {
            if (compiledRule?.IsMatch(player.Name) ?? false)
            {
                KickForUsernameRuleDef(player, def);
                _sawmill.Info($"Kicked player {player.Name} ({player.UserId}) through {source}");
            }
        }
    }

    private void KickForUsernameRuleDef(ICommonSession player, ServerUsernameRuleDef def)
    {
        var message = def.FormatUsernameViolationMessage(_cfg, _localizationManager);
        player.Channel.Disconnect(message);
    }

    private IEnumerable<MsgUsernameBan> CreateResetMessages()
    {
        List<MsgUsernameBanContent> messageContent = new();

        messageContent.EnsureCapacity(_cachedUsernameRules.Count + 1);

        messageContent.Add(new(-1, false, false, false, "empty"));

        foreach (var id in _cachedUsernameRules.Keys)
        {
            (Regex? regex, string expression, string message, bool extendToBan) = _cachedUsernameRules[id];

            _sawmill.Verbose($"sending username ban {id}");

            messageContent.Add(new(id, true, regex != null, extendToBan, expression));
        }

        return messageContent.Select(static mc =>
        {
            return new MsgUsernameBan()
            {
                UsernameBan = mc
            };
        });
    }

    private void SendResetUsernameBan(INetChannel channel)
    {
        _sawmill.Debug($"Sent username bans reset to {channel.UserData.UserName}");
        var resetMessages = CreateResetMessages();
        foreach (var msg in resetMessages)
        {
            _net.ServerSendMessage(msg, channel);
        }
    }

    private void SendResetUsernameBan()
    {
        _sawmill.Debug($"Sent username bans reset to active admins");
        var adminChannels = _admin.ActiveAdmins.Select(a => a.Channel).ToList();
        var resetMessages = CreateResetMessages();
        foreach (var msg in resetMessages)
        {
            _net.ServerSendToMany(msg, adminChannels);
        }
    }

    private void SendAddUsernameBan(int id, bool regex, string expression, string message, bool extendToBan)
    {
        var usernameBanMsg = new MsgUsernameBan()
        {
            UsernameBan = new(id, true, regex, extendToBan, expression),
        };

        _sawmill.Debug($"sent new username ban {id} to active admins");
        _net.ServerSendToMany(usernameBanMsg, _admin.ActiveAdmins.Select(a => a.Channel).ToList());
    }

    private void SendRemoveUsernameBan(int id)
    {
        var usernameBanMsg = new MsgUsernameBan()
        {
            UsernameBan = new(id, false, false, false, "empty"),
        };

        _sawmill.Debug($"sent username ban delete {id} to active admins");
        _net.ServerSendToMany(usernameBanMsg, _admin.ActiveAdmins.Select(a => a.Channel).ToList());
    }

    public void PostInject()
    {
        _sawmill = _logManager.GetSawmill(SawmillId);
    }

    public async Task WhitelistAddUsernameAsync(string username)
    {
        await _db.AddUsernameWhitelistAsync(username);
        _sawmill.Verbose($"sent create username whitelist for {username}");
    }

    public async Task<bool> WhitelistRemoveUsernameAsync(string username)
    {
        bool present = await _db.CheckUsernameWhitelistAsync(username);
        if (present)
        {
            await _db.RemoveUsernameWhitelistAsync(username);
        }
        _sawmill.Verbose($"sent delete username whitelist for {username}");
        return present;
    }
}
