using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using MailServer.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MimeKit;

namespace MailServer.Infrastructure;

public sealed record MailRuleConditionInput(
    MailRuleField Field,
    MailRuleOperator Operator,
    IReadOnlyList<string> Values);

public sealed record MailRuleActionInput(
    MailRuleActionKind Kind,
    IReadOnlyList<string> Values);

public sealed record MailRuleInput(
    string Name,
    MailRuleDirection Direction,
    MailRuleMatchMode MatchMode,
    string TargetMailboxName,
    bool IsEnabled,
    IReadOnlyList<MailRuleConditionInput> Conditions,
    IReadOnlyList<MailRuleActionInput>? Actions = null);

public sealed record MailRuleConditionView(
    Guid Id,
    MailRuleField Field,
    MailRuleOperator Operator,
    IReadOnlyList<string> Values,
    int Position);

public sealed record MailRuleActionView(
    Guid Id,
    MailRuleActionKind Kind,
    IReadOnlyList<string> Values,
    int Position);

public sealed record MailRuleView(
    Guid Id,
    Guid DomainId,
    string Name,
    MailRuleDirection Direction,
    MailRuleMatchMode MatchMode,
    string TargetMailboxName,
    bool IsEnabled,
    int Priority,
    IReadOnlyList<MailRuleConditionView> Conditions,
    IReadOnlyList<MailRuleActionView> Actions);

public sealed record RuleFolderCoverage(string Name, int AvailableAccounts, int TotalAccounts);

public sealed class MailRuleFacts
{
    private readonly Lazy<string> _body;
    private readonly Lazy<IReadOnlyList<string>> _attachments;
    private bool? _hasAttachmentOverride;
    public IReadOnlyList<string> Senders { get; }
    public IReadOnlyList<string> SenderDomains { get; }
    public IReadOnlyList<string> Recipients { get; }
    public IReadOnlyList<string> Cc { get; }
    public string Subject { get; }
    public string Body => _body.Value;
    public IReadOnlyList<string> AttachmentNames => _attachments.Value;
    public long MessageSize { get; }
    public DateTimeOffset MessageTime { get; }
    public bool HasAttachment => _hasAttachmentOverride ?? _attachments.Value.Count > 0;
    public MailRuleFacts(IReadOnlyList<string> senders, IReadOnlyList<string> senderDomains,
        IReadOnlyList<string> recipients, IReadOnlyList<string> cc, string subject,
        Func<string> body, Func<IReadOnlyList<string>> attachments, long messageSize,
        DateTimeOffset messageTime)
        : this(senders, senderDomains, recipients, cc, subject, new Lazy<string>(body),
            new Lazy<IReadOnlyList<string>>(attachments), messageSize, messageTime) { }
    internal MailRuleFacts(IReadOnlyList<string> senders, IReadOnlyList<string> senderDomains,
        IReadOnlyList<string> recipients, IReadOnlyList<string> cc, string subject,
        Lazy<string> body, Lazy<IReadOnlyList<string>> attachments, long messageSize,
        DateTimeOffset messageTime)
    {
        Senders = senders; SenderDomains = senderDomains; Recipients = recipients; Cc = cc;
        Subject = subject; MessageSize = messageSize; MessageTime = messageTime;
        _body = body; _attachments = attachments;
    }
    public MailRuleFacts(IReadOnlyList<string> senders, IReadOnlyList<string> senderDomains,
        IReadOnlyList<string> recipients, IReadOnlyList<string> cc, string subject,
        string body, IReadOnlyList<string> attachments, long messageSize,
        DateTimeOffset messageTime, bool hasAttachment)
        : this(senders, senderDomains, recipients, cc, subject, () => body,
            () => attachments, messageSize, messageTime) { _hasAttachmentOverride = hasAttachment; }
}

public sealed record MailRuleMatch(
    Guid RuleId,
    string RuleName,
    string TargetMailboxName,
    IReadOnlyList<MailRuleActionView> Actions);

/// <summary>
/// 发信规则命中「拒绝发信」后的结果，Reason 可回传给发信客户端。
/// </summary>
public sealed record MailRuleRejection(Guid RuleId, string RuleName, string? Reason);

public sealed partial class MailRuleService(
    IDbContextFactory<MailDbContext> dbFactory,
    ILogger<MailRuleService> logger,
    ConfigurationSnapshotCache? cache = null,
    ConfigurationVersionService? versions = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly AsyncLocal<EvaluationScope?> CurrentScope = new();
    private const int ConditionResultLimit = 512;
    private const int ConditionResultBudgetBytes = 64 * 1024;
    private const int EstimatedConditionResultBytes = 128;
    private sealed class EvaluationScope
    {
        public readonly Dictionary<(Guid DomainId, MailRuleDirection Direction), RuleSnapshot> Snapshots = new();
        public readonly Dictionary<(Guid DomainId, MailRuleDirection Direction, MailRuleFacts Facts), MailRuleMatch?> Matches = new();
        public readonly Dictionary<(Guid AccountId, MailRuleFacts Facts), MailRuleRejection?> Rejections = new();
        public readonly Dictionary<MimeMessage, (Lazy<string> Body, Lazy<IReadOnlyList<string>> Attachments)> MimeContent = new();
        public readonly Dictionary<(MimeMessage Message, string Sender, string Recipients),
            (IReadOnlyList<string> Senders, IReadOnlyList<string> Domains,
             IReadOnlyList<string> Recipients, IReadOnlyList<string> Cc)> Addresses = new();
        public readonly Dictionary<(MimeMessage Message, string Sender, string Recipients, long Size, DateTimeOffset Time), MailRuleFacts> Facts = new();
        public Stream? ParsedStream;
        public MimeMessage? ParsedMessage;
        public readonly Dictionary<ConditionResultKey, bool> ConditionResults = new(new ConditionResultKeyComparer());
        public long? Version;
        public void CheckVersion(long version)
        {
            if (Version is { } previous && previous != version)
                throw new InvalidOperationException("Mail rules changed during message evaluation; retry later.");
            Version = version;
        }
    }
    private sealed class ScopeLease(EvaluationScope? previous, bool created) : IDisposable
    {
        public void Dispose() { if (created) CurrentScope.Value = previous; }
    }
    public static IDisposable BeginEvaluationScope()
    {
        var previous = CurrentScope.Value;
        if (previous is null) CurrentScope.Value = new EvaluationScope();
        return new ScopeLease(previous, previous is null);
    }
    internal static void RememberParsedMessage(Stream content, MimeMessage message)
    {
        if (CurrentScope.Value is { } scope)
        {
            scope.ParsedStream = content;
            scope.ParsedMessage = message;
        }
    }
    internal static MimeMessage? GetParsedMessage(Stream content) =>
        CurrentScope.Value is { } scope && ReferenceEquals(scope.ParsedStream, content)
            ? scope.ParsedMessage : null;

    internal static void InvalidateParsedMessage(Stream content)
    {
        if (CurrentScope.Value is not { } scope || !ReferenceEquals(scope.ParsedStream, content)) return;
        scope.ParsedStream = null;
        scope.ParsedMessage = null;
        scope.MimeContent.Clear();
        scope.Addresses.Clear();
        scope.Facts.Clear();
        scope.Matches.Clear();
        scope.Rejections.Clear();
        scope.ConditionResults.Clear();
    }
    private sealed record CompiledPattern(string Text, Regex? Wildcard);
    private sealed record CompiledCondition(MailRuleField Field, MailRuleOperator Operator,
        IReadOnlyList<CompiledPattern> Values, long? Number, DateTimeOffset? FirstTime,
        DateTimeOffset? SecondTime, bool? Boolean);
    private sealed record CompiledRule(
        Guid Id,
        string Name,
        MailRuleMatchMode MatchMode,
        string TargetMailboxName,
        IReadOnlyList<CompiledCondition> Conditions,
        IReadOnlyList<MailRuleActionView> Actions);
    private sealed record RuleSnapshot(IReadOnlyList<CompiledRule> Rules, bool Oversized, CandidateIndex? Index = null);
    private sealed record CandidateIndex(Dictionary<string, int[]> Senders,
        Dictionary<string, int[]> SenderDomains, Dictionary<string, int[]> Recipients,
        Dictionary<string, int[]> Cc, int[] Fallback);
    private readonly record struct ConditionResultKey(CompiledCondition Condition, object? Dependency,
        object? AdditionalDependency = null,
        long Number = 0, DateTimeOffset Time = default);
    private sealed class ConditionResultKeyComparer : IEqualityComparer<ConditionResultKey>
    {
        public bool Equals(ConditionResultKey x, ConditionResultKey y) =>
            ReferenceEquals(x.Condition, y.Condition) && x.Number == y.Number && x.Time == y.Time
            && ReferenceEquals(x.Dependency, y.Dependency)
            && ReferenceEquals(x.AdditionalDependency, y.AdditionalDependency);
        public int GetHashCode(ConditionResultKey key) => HashCode.Combine(
            RuntimeHelpers.GetHashCode(key.Condition), key.Number, key.Time,
            key.Dependency is null ? 0 : RuntimeHelpers.GetHashCode(key.Dependency),
            key.AdditionalDependency is null ? 0 : RuntimeHelpers.GetHashCode(key.AdditionalDependency));
    }

    public async Task<IReadOnlyList<MailRuleView>> GetRulesAsync(
        Guid domainId,
        MailRuleDirection direction,
        CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        if (!await db.Domains.AnyAsync(x => x.Id == domainId, cancellationToken))
            throw new KeyNotFoundException("Domain not found.");

        var rules = await db.MailRules.AsNoTracking()
            .Include(x => x.Conditions)
            .Include(x => x.Actions)
            .AsSplitQuery()
            .Where(x => x.DomainId == domainId && x.Direction == direction)
            .OrderBy(x => x.Priority)
            .ThenBy(x => x.CreatedAt)
            .ToListAsync(cancellationToken);
        return rules.Select(ToView).ToArray();
    }

    public async Task<IReadOnlyList<RuleFolderCoverage>> GetFolderCoverageAsync(
        Guid domainId,
        CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var totalAccounts = await db.Accounts.CountAsync(x => x.DomainId == domainId && x.IsEnabled, cancellationToken);
        var mailboxRows = await db.Mailboxes.AsNoTracking()
            .Where(x => x.Account.DomainId == domainId && x.Account.IsEnabled && x.PendingDeletionJobId == null)
            .Select(x => new { x.Name, x.AccountId })
            .ToListAsync(cancellationToken);
        var rows = mailboxRows
            .GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => new RuleFolderCoverage(group.Key, group.Select(x => x.AccountId).Distinct().Count(), totalAccounts))
            .OrderBy(x => x.Name == "INBOX" ? 0 : x.Name == "Sent" ? 1 : 2)
            .ThenBy(x => x.Name)
            .ToArray();
        return rows;
    }

    public async Task<MailRuleView> CreateAsync(
        Guid domainId,
        MailRuleInput input,
        string actor,
        CancellationToken cancellationToken)
    {
        var normalized = Validate(input);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(cancellationToken) : null;
        if (!await db.Domains.AnyAsync(x => x.Id == domainId, cancellationToken))
            throw new KeyNotFoundException("Domain not found.");
        var priority = await db.MailRules
            .Where(x => x.DomainId == domainId && x.Direction == input.Direction)
            .Select(x => (int?)x.Priority)
            .MaxAsync(cancellationToken) ?? -1;
        var rule = new MailRule
        {
            DomainId = domainId,
            Name = normalized.Name,
            Direction = input.Direction,
            MatchMode = input.MatchMode,
            TargetMailboxName = normalized.TargetMailboxName,
            IsEnabled = input.IsEnabled,
            Priority = priority + 1,
            Conditions = normalized.Conditions.Select((condition, index) => new MailRuleCondition
            {
                Field = condition.Field,
                Operator = condition.Operator,
                ValuesJson = JsonSerializer.Serialize(condition.Values, JsonOptions),
                Position = index
            }).ToList(),
            Actions = normalized.Actions!.Select((action, index) => new MailRuleAction
            {
                Kind = action.Kind,
                ValuesJson = JsonSerializer.Serialize(action.Values, JsonOptions),
                Position = index
            }).ToList()
        };
        db.MailRules.Add(rule);
        AddAudit(db, actor, "mail-rule.create", $"mail-rule:{rule.Id}", $"{input.Direction}:{rule.Name}");
        var version = await ConfigurationVersionService.BumpAsync(db, ConfigurationScopes.Rules, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        cache?.ObserveCommittedVersion(ConfigurationScopes.Rules, version);
        return ToView(rule);
    }

    public async Task<MailRuleView> UpdateAsync(
        Guid domainId,
        Guid ruleId,
        MailRuleInput input,
        string actor,
        CancellationToken cancellationToken)
    {
        var normalized = Validate(input);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(cancellationToken)
            : null;
        var rule = await db.MailRules.SingleOrDefaultAsync(
            x => x.Id == ruleId && x.DomainId == domainId,
            cancellationToken)
            ?? throw new KeyNotFoundException("Mail rule not found.");
        if (rule.Direction != input.Direction)
            throw new InvalidOperationException("Rule direction cannot be changed.");

        if (db.Database.IsRelational())
        {
            await db.MailRuleConditions.Where(x => x.RuleId == rule.Id).ExecuteDeleteAsync(cancellationToken);
            await db.MailRuleActions.Where(x => x.RuleId == rule.Id).ExecuteDeleteAsync(cancellationToken);
        }
        else
        {
            db.MailRuleConditions.RemoveRange(await db.MailRuleConditions
                .Where(x => x.RuleId == rule.Id)
                .ToListAsync(cancellationToken));
            db.MailRuleActions.RemoveRange(await db.MailRuleActions
                .Where(x => x.RuleId == rule.Id)
                .ToListAsync(cancellationToken));
        }

        rule.Name = normalized.Name;
        rule.MatchMode = input.MatchMode;
        rule.TargetMailboxName = normalized.TargetMailboxName;
        rule.IsEnabled = input.IsEnabled;
        rule.UpdatedAt = DateTimeOffset.UtcNow;
        rule.Conditions = normalized.Conditions.Select((condition, index) => new MailRuleCondition
        {
            Field = condition.Field,
            Operator = condition.Operator,
            ValuesJson = JsonSerializer.Serialize(condition.Values, JsonOptions),
            Position = index
        }).ToList();
        rule.Actions = normalized.Actions!.Select((action, index) => new MailRuleAction
        {
            Kind = action.Kind,
            ValuesJson = JsonSerializer.Serialize(action.Values, JsonOptions),
            Position = index
        }).ToList();
        db.MailRuleConditions.AddRange(rule.Conditions);
        db.MailRuleActions.AddRange(rule.Actions);
        AddAudit(db, actor, "mail-rule.update", $"mail-rule:{rule.Id}", rule.Name);
        var version = await ConfigurationVersionService.BumpAsync(db, ConfigurationScopes.Rules, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        cache?.ObserveCommittedVersion(ConfigurationScopes.Rules, version);
        return ToView(rule);
    }

    public async Task DeleteAsync(
        Guid domainId,
        Guid ruleId,
        string actor,
        CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(cancellationToken) : null;
        var rule = await db.MailRules.SingleOrDefaultAsync(
            x => x.Id == ruleId && x.DomainId == domainId,
            cancellationToken) ?? throw new KeyNotFoundException("Mail rule not found.");
        var direction = rule.Direction;
        db.MailRules.Remove(rule);
        AddAudit(db, actor, "mail-rule.delete", $"mail-rule:{rule.Id}", rule.Name);
        await db.SaveChangesAsync(cancellationToken);
        await NormalizePrioritiesAsync(db, domainId, direction, cancellationToken);
        var version = await ConfigurationVersionService.BumpAsync(db, ConfigurationScopes.Rules, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        cache?.ObserveCommittedVersion(ConfigurationScopes.Rules, version);
    }

    public async Task ReorderAsync(
        Guid domainId,
        MailRuleDirection direction,
        IReadOnlyList<Guid> ruleIds,
        string actor,
        CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(cancellationToken) : null;
        var rules = await db.MailRules
            .Where(x => x.DomainId == domainId && x.Direction == direction)
            .ToListAsync(cancellationToken);
        if (ruleIds.Count != rules.Count || ruleIds.Distinct().Count() != rules.Count
            || rules.Any(rule => !ruleIds.Contains(rule.Id)))
            throw new InvalidOperationException("Rule order must contain every rule exactly once.");
        var byId = rules.ToDictionary(x => x.Id);
        for (var index = 0; index < ruleIds.Count; index++)
        {
            byId[ruleIds[index]].Priority = index;
            byId[ruleIds[index]].UpdatedAt = DateTimeOffset.UtcNow;
        }

        AddAudit(db, actor, "mail-rule.reorder", $"domain:{domainId}", direction.ToString());
        var version = await ConfigurationVersionService.BumpAsync(db, ConfigurationScopes.Rules, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        cache?.ObserveCommittedVersion(ConfigurationScopes.Rules, version);
    }

    public async Task<MailRuleMatch?> MatchAsync(
        Guid domainId,
        MailRuleDirection direction,
        MailRuleFacts facts,
        CancellationToken cancellationToken)
    {
        var evaluation = CurrentScope.Value;
        using var performance = PerformanceTelemetry.Stage("rules.match");
        var resultKey = (domainId, direction, facts);
        if (evaluation is not null && evaluation.Matches.TryGetValue(resultKey, out var prior)) return prior;
        var snapshot = await GetSnapshotAsync(domainId, direction, cancellationToken);
        if (snapshot.Oversized)
        {
            var batched = await MatchBatchedAsync(domainId, direction, facts, rejectionOnly: false, cancellationToken) as MailRuleMatch;
            if (evaluation is not null) evaluation.Matches[resultKey] = batched;
            return batched;
        }
        foreach (var rule in Candidates(snapshot, facts))
        {
            var matched = RuleMatches(rule, facts, evaluation);
            if (matched)
            {
                logger.LogInformation(
                    "Mail rule {RuleId} matched for domain {DomainId}: direction={Direction}, targetMailbox={TargetMailbox}.",
                    rule.Id,
                    domainId,
                    direction,
                    rule.TargetMailboxName);
                var actions = rule.Actions;
                if (actions.Count == 0)
                {
                    actions = [new MailRuleActionView(Guid.Empty, MailRuleActionKind.MoveToFolder, [rule.TargetMailboxName], 0)];
                }
                var match = new MailRuleMatch(rule.Id, rule.Name, rule.TargetMailboxName, actions);
                if (evaluation is not null) evaluation.Matches[resultKey] = match;
                return match;
            }
        }

        if (evaluation is not null) evaluation.Matches[resultKey] = null;
        return null;
    }

    /// <summary>
    /// 发信规则里的「拒绝发信」动作在 SMTP DATA 阶段生效。返回首个命中规则，
    /// 调用方据此在落库前用 550 拒绝整封信。
    /// </summary>
    public async Task<MailRuleRejection?> EvaluateOutgoingRejectionAsync(
        Guid accountId,
        MailRuleFacts facts,
        CancellationToken cancellationToken)
    {
        var evaluation = CurrentScope.Value;
        var resultKey = (accountId, facts);
        if (evaluation is not null && evaluation.Rejections.TryGetValue(resultKey, out var prior)) return prior;
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var domainId = await db.Accounts.Where(x => x.Id == accountId)
            .Select(x => (Guid?)x.DomainId)
            .SingleOrDefaultAsync(cancellationToken);
        if (domainId is null) return null;
        var snapshot = await GetSnapshotAsync(domainId.Value, MailRuleDirection.Outgoing, cancellationToken);
        if (snapshot.Oversized)
        {
            var batched = await MatchBatchedAsync(domainId.Value, MailRuleDirection.Outgoing, facts, rejectionOnly: true, cancellationToken) as MailRuleRejection;
            if (evaluation is not null) evaluation.Rejections[resultKey] = batched;
            return batched;
        }
        foreach (var rule in Candidates(snapshot, facts))
        {
            var matched = RuleMatches(rule, facts, evaluation);
            if (!matched) continue;
            var reject = rule.Actions.FirstOrDefault(x => x.Kind == MailRuleActionKind.Reject);
            if (reject is null) continue;
            var reason = reject.Values.FirstOrDefault();
            logger.LogInformation(
                "Outgoing mail rule {RuleId} rejected a message for domain {DomainId}: {Reason}",
                rule.Id,
                domainId,
                reason ?? string.Empty);
            var rejection = new MailRuleRejection(rule.Id, rule.Name, reason);
            if (evaluation is not null) evaluation.Rejections[resultKey] = rejection;
            return rejection;
        }

        if (evaluation is not null) evaluation.Rejections[resultKey] = null;
        return null;
    }

    private async Task<RuleSnapshot> GetSnapshotAsync(
        Guid domainId,
        MailRuleDirection direction,
        CancellationToken cancellationToken)
    {
        var evaluation = CurrentScope.Value;
        var key = (domainId, direction);
        if (evaluation is not null && evaluation.Snapshots.TryGetValue(key, out var pinned)) return pinned;
        if (cache is not null) evaluation?.CheckVersion(cache.Versions.Rules);
        var snapshot = cache is null ? (await LoadSnapshotAsync(domainId, direction, cancellationToken)).Value : await cache.GetAsync(
            $"rules:{domainId:N}:{direction}",
            ConfigurationScopes.Rules,
            token => LoadSnapshotAsync(domainId, direction, token),
            cancellationToken);
        if (evaluation is not null) evaluation.Snapshots[key] = snapshot;
        return snapshot;
    }

    private async Task<(RuleSnapshot Value, long Size)> LoadSnapshotAsync(
        Guid domainId,
        MailRuleDirection direction,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var before = versions is null ? null : await versions.ReadAsync(cancellationToken);
            var result = await LoadSnapshotOnceAsync(domainId, direction, cancellationToken);
            var after = versions is null ? before : await versions.ReadAsync(cancellationToken);
            if (before?.Rules != after?.Rules) continue;
            if (after is not null) CurrentScope.Value?.CheckVersion(after.Rules);
            return result;
        }
        throw new InvalidOperationException("Mail rules changed repeatedly during snapshot loading.");
    }

    private async Task<(RuleSnapshot Value, long Size)> LoadSnapshotOnceAsync(
        Guid domainId,
        MailRuleDirection direction,
        CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var candidates = db.MailRules.AsNoTracking()
            .Where(x => x.DomainId == domainId && x.Direction == direction && x.IsEnabled);
        var count = await candidates.CountAsync(cancellationToken);
        if (count == 0) return (new RuleSnapshot([], false), 128);
        var estimate = 512L + await candidates.Select(x =>
            512L + x.Name.Length * 2L + x.TargetMailboxName.Length * 2L
            + x.Conditions.Sum(condition => 128L + condition.ValuesJson.Length * 2L)
            + x.Actions.Sum(action => 96L + action.ValuesJson.Length * 2L))
            .SumAsync(cancellationToken);
        if (cache is not null && estimate > cache.Options.MaximumEntryBytes)
            return (new RuleSnapshot([], true), Math.Max(1, Math.Min(128, cache.Options.MaximumEntryBytes)));
        var rows = await LoadRuleBatchAsync(db, domainId, direction, 0, Math.Max(1, count), cancellationToken);
        var sharedConditions = new Dictionary<(MailRuleField, MailRuleOperator, string), CompiledCondition>();
        var rules = rows.Select(row => Compile(row, sharedConditions)).ToArray();
        estimate = 512L + rows.Sum(EstimateRuleSize);
        var oversized = estimate > (cache?.Options.MaximumEntryBytes ?? long.MaxValue);
        if (oversized) return (new RuleSnapshot([], true), Math.Max(1, Math.Min(128, cache!.Options.MaximumEntryBytes)));
        var indexBudget = cache is null ? long.MaxValue
            : Math.Min(cache.Options.MaximumEntryBytes / 4, Math.Max(0, cache.Options.MaximumEntryBytes - estimate));
        var (index, indexSize) = BuildIndex(rules, indexBudget);
        return (new RuleSnapshot(rules, false, index), estimate + indexSize);
    }

    private async Task<object?> MatchBatchedAsync(
        Guid domainId,
        MailRuleDirection direction,
        MailRuleFacts facts,
        bool rejectionOnly,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var before = versions is null ? null : await versions.ReadAsync(cancellationToken);
            if (before is not null) CurrentScope.Value?.CheckVersion(before.Rules);
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            var batchSize = Math.Clamp(cache?.Options.RuleFallbackBatchSize ?? 64, 1, 512);
            object? candidate = null;
            for (var offset = 0; ; offset += batchSize)
            {
                var rows = await LoadRuleBatchAsync(db, domainId, direction, offset, batchSize, cancellationToken);
                foreach (var rule in rows.Select(row => Compile(row)))
                {
                    var matched = RuleMatches(rule, facts, CurrentScope.Value);
                    if (!matched) continue;
                    if (rejectionOnly)
                    {
                        var reject = rule.Actions.FirstOrDefault(x => x.Kind == MailRuleActionKind.Reject);
                        if (reject is not null)
                        {
                            candidate = new MailRuleRejection(rule.Id, rule.Name, reject.Values.FirstOrDefault());
                            break;
                        }
                        continue;
                    }
                    IReadOnlyList<MailRuleActionView> actions = rule.Actions.Count == 0
                        ? [new MailRuleActionView(Guid.Empty, MailRuleActionKind.MoveToFolder, [rule.TargetMailboxName], 0)]
                        : rule.Actions;
                    candidate = new MailRuleMatch(rule.Id, rule.Name, rule.TargetMailboxName, actions);
                    break;
                }
                if (candidate is not null || rows.Count < batchSize) break;
            }
            var after = versions is null ? before : await versions.ReadAsync(cancellationToken);
            if (before?.Rules == after?.Rules) return candidate;
            var scope = CurrentScope.Value;
            if (scope is not null && scope.Snapshots.Values.All(x => x.Oversized)
                && scope.Matches.Count == 0 && scope.Rejections.Count == 0)
                scope.Version = null;
        }
        throw new InvalidOperationException("Mail rules changed repeatedly during bounded evaluation.");
    }

    private static Task<List<MailRule>> LoadRuleBatchAsync(
        MailDbContext db,
        Guid domainId,
        MailRuleDirection direction,
        int offset,
        int count,
        CancellationToken cancellationToken) =>
        db.MailRules.AsNoTracking()
            .Include(x => x.Conditions)
            .Include(x => x.Actions)
            .AsSplitQuery()
            .Where(x => x.DomainId == domainId && x.Direction == direction && x.IsEnabled)
            .OrderBy(x => x.Priority).ThenBy(x => x.CreatedAt).ThenBy(x => x.Id)
            .Skip(offset).Take(count).ToListAsync(cancellationToken);

    private static long EstimateRuleSize(MailRule rule) =>
        512L + rule.Name.Length * 2L + rule.TargetMailboxName.Length * 2L
        + rule.Conditions.Sum(x => 128L + x.ValuesJson.Length * 2L)
        + rule.Actions.Sum(x => 96L + x.ValuesJson.Length * 2L);

    private static CompiledRule Compile(MailRule rule,
        Dictionary<(MailRuleField, MailRuleOperator, string), CompiledCondition>? shared = null) => new(
        rule.Id,
        rule.Name,
        rule.MatchMode,
        rule.TargetMailboxName,
        rule.Conditions.OrderBy(x => x.Position).Select(x => InternCondition(x, shared)).ToArray(),
        rule.Actions.OrderBy(x => x.Position).Select(ToActionView).ToArray());

    private static CompiledCondition InternCondition(MailRuleCondition condition,
        Dictionary<(MailRuleField, MailRuleOperator, string), CompiledCondition>? shared)
    {
        if (shared is null) return CompileCondition(condition);
        var key = (condition.Field, condition.Operator, condition.ValuesJson);
        if (shared.TryGetValue(key, out var compiled)) return compiled;
        compiled = CompileCondition(condition);
        shared[key] = compiled;
        return compiled;
    }

    private static (CandidateIndex? Index, long Size) BuildIndex(CompiledRule[] rules, long budget)
    {
        if (rules.Length < 64 || budget < 512) return (null, 0);
        var maps = new Dictionary<string, List<int>>[4];
        for (var i = 0; i < maps.Length; i++) maps[i] = new(StringComparer.OrdinalIgnoreCase);
        var fallback = new List<int>();
        long size = 512;
        for (var i = 0; i < rules.Length; i++)
        {
            var rule = rules[i];
            var first = rule.Conditions.FirstOrDefault();
            var slot = first?.Field switch
            {
                MailRuleField.Sender => 0,
                MailRuleField.SenderDomain => 1,
                MailRuleField.Recipient => 2,
                MailRuleField.Cc => 3,
                _ => -1
            };
            if (rule.MatchMode != MailRuleMatchMode.All || first?.Operator != MailRuleOperator.EqualsAny
                || slot < 0 || first.Values.Count == 0 || first.Values.Any(x => x.Wildcard is not null))
            {
                fallback.Add(i);
                size += 16;
            }
            else
            {
                foreach (var value in first.Values.Select(x => x.Text).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (!maps[slot].TryGetValue(value, out var entries))
                    {
                        entries = [];
                        maps[slot][value] = entries;
                        size += 192 + value.Length * 2L;
                    }
                    entries.Add(i);
                    size += 16;
                }
            }
            if (size > budget) return (null, 0);
        }
        if (fallback.Count > rules.Length / 2) return (null, 0);
        static Dictionary<string, int[]> Freeze(Dictionary<string, List<int>> map) =>
            map.ToDictionary(x => x.Key, x => x.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
        return (new CandidateIndex(Freeze(maps[0]), Freeze(maps[1]), Freeze(maps[2]), Freeze(maps[3]),
            fallback.ToArray()), size);
    }

    private static IEnumerable<CompiledRule> Candidates(RuleSnapshot snapshot, MailRuleFacts facts)
    {
        if (snapshot.Index is not { } index) return snapshot.Rules;
        var selected = new HashSet<int>(index.Fallback);
        static void Add(Dictionary<string, int[]> map, IReadOnlyList<string> values, HashSet<int> selected)
        {
            foreach (var value in values)
                if (map.TryGetValue(value, out var positions))
                    selected.UnionWith(positions);
        }
        Add(index.Senders, facts.Senders, selected);
        Add(index.SenderDomains, facts.SenderDomains, selected);
        Add(index.Recipients, facts.Recipients, selected);
        Add(index.Cc, facts.Cc, selected);
        return selected.Order().Select(position => snapshot.Rules[position]);
    }

    private static CompiledCondition CompileCondition(MailRuleCondition condition)
    {
        var values = DeserializeValues(condition.ValuesJson);
        long? number = long.TryParse(values.FirstOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedNumber)
            ? parsedNumber : null;
        DateTimeOffset? first = DateTimeOffset.TryParse(values.FirstOrDefault(), CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind, out var parsedFirst) ? parsedFirst : null;
        DateTimeOffset? second = values.Length > 1 && DateTimeOffset.TryParse(values[1], CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind, out var parsedSecond) ? parsedSecond : null;
        bool? boolean = bool.TryParse(values.FirstOrDefault(), out var parsedBoolean) ? parsedBoolean : null;
        CompiledPattern[] patterns = condition.Field is MailRuleField.MessageSize or MailRuleField.MessageTime or MailRuleField.HasAttachment
            ? [] : values.Select(value => CompilePattern(condition.Field == MailRuleField.SenderDomain ? NormalizeDomain(value) : value)).ToArray();
        return new(condition.Field, condition.Operator, patterns, number, first, second, boolean);
    }

    private static CompiledPattern CompilePattern(string value)
    {
        if (!value.Contains('*') && !value.Contains('?')) return new CompiledPattern(value, null);
        var pattern = "^" + Regex.Escape(value)
            .Replace("\\*", ".*", StringComparison.Ordinal)
            .Replace("\\?", ".", StringComparison.Ordinal) + "$";
        return new CompiledPattern(value, new Regex(
            pattern,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
            TimeSpan.FromMilliseconds(200)));
    }

    public static MailRuleFacts BuildFacts(
        MimeMessage message,
        string envelopeSender,
        IReadOnlyList<string> envelopeRecipients,
        long messageSize,
        DateTimeOffset messageTime)
    {
        var evaluation = CurrentScope.Value;
        var recipientsKey = string.Join('\0', envelopeRecipients);
        var factsKey = (message, envelopeSender, recipientsKey, messageSize, messageTime);
        if (evaluation is not null && evaluation.Facts.TryGetValue(factsKey, out var previous)) return previous;
        var addressKey = (message, envelopeSender, recipientsKey);
        if (evaluation is null || !evaluation.Addresses.TryGetValue(addressKey, out var addresses))
        {
            var senders = new List<string>();
            AddAddress(senders, envelopeSender);
            foreach (var mailbox in message.From.Mailboxes) AddAddress(senders, mailbox.Address);
            if (message.Sender is not null) AddAddress(senders, message.Sender.Address);
            var recipients = envelopeRecipients.Select(AddressParser.Normalize).ToList();
            foreach (var mailbox in message.To.Mailboxes) AddAddress(recipients, mailbox.Address);
            var cc = message.Cc.Mailboxes.Select(x => AddressParser.Normalize(x.Address))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var domains = senders.Select(address => AddressParser.TrySplit(address, out _, out var domain) ? domain : string.Empty)
                .Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            addresses = (senders.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), domains,
                recipients.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), cc);
            if (evaluation is not null) evaluation.Addresses[addressKey] = addresses;
        }
        if (evaluation is null || !evaluation.MimeContent.TryGetValue(message, out var content))
        {
            content = (new Lazy<string>(() => string.Join('\n', new[] { message.TextBody, NormalizeHtml(message.HtmlBody) }
                .Where(x => !string.IsNullOrWhiteSpace(x)))),
                new Lazy<IReadOnlyList<string>>(() => message.Attachments
                    .Select(x => x.ContentDisposition?.FileName ?? x.ContentType.Name)
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Select(x => x!)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray()));
            if (evaluation is not null) evaluation.MimeContent[message] = content;
        }
        var facts = new MailRuleFacts(
            addresses.Senders,
            addresses.Domains,
            addresses.Recipients,
            addresses.Cc,
            message.Subject ?? string.Empty,
            content.Body,
            content.Attachments,
            messageSize,
            messageTime);
        if (evaluation is not null) evaluation.Facts[factsKey] = facts;
        return facts;
    }

    private static bool Matches(MailRuleCondition condition, MailRuleFacts facts)
    {
        var values = DeserializeValues(condition.ValuesJson);
        return condition.Field switch
        {
            MailRuleField.Sender => MatchText(facts.Senders, condition.Operator, values),
            MailRuleField.SenderDomain => MatchText(facts.SenderDomains, condition.Operator, values.Select(NormalizeDomain).ToArray()),
            MailRuleField.Recipient => MatchText(facts.Recipients, condition.Operator, values),
            MailRuleField.Cc => MatchText(facts.Cc, condition.Operator, values),
            MailRuleField.RecipientOrCc => MatchText(facts.Recipients.Concat(facts.Cc), condition.Operator, values),
            MailRuleField.Subject => MatchText([facts.Subject], condition.Operator, values),
            MailRuleField.Body => MatchText([facts.Body], condition.Operator, values),
            MailRuleField.AttachmentName => MatchText(facts.AttachmentNames, condition.Operator, values),
            MailRuleField.MessageSize => MatchNumber(facts.MessageSize, condition.Operator, values),
            MailRuleField.MessageTime => MatchTime(facts.MessageTime, condition.Operator, values),
            MailRuleField.HasAttachment => condition.Operator == MailRuleOperator.Is
                                            && bool.TryParse(values[0], out var expected)
                                            && expected == facts.HasAttachment,
            _ => false
        };
    }

    private static bool Matches(CompiledCondition condition, MailRuleFacts facts)
    {
        return condition.Field switch
        {
            MailRuleField.Sender => MatchText(facts.Senders, condition.Operator, condition.Values),
            MailRuleField.SenderDomain => MatchText(facts.SenderDomains, condition.Operator, condition.Values),
            MailRuleField.Recipient => MatchText(facts.Recipients, condition.Operator, condition.Values),
            MailRuleField.Cc => MatchText(facts.Cc, condition.Operator, condition.Values),
            MailRuleField.RecipientOrCc => MatchText(facts.Recipients.Concat(facts.Cc), condition.Operator, condition.Values),
            MailRuleField.Subject => MatchText([facts.Subject], condition.Operator, condition.Values),
            MailRuleField.Body => MatchText([facts.Body], condition.Operator, condition.Values),
            MailRuleField.AttachmentName => MatchText(facts.AttachmentNames, condition.Operator, condition.Values),
            MailRuleField.MessageSize => condition.Number is { } number && MatchNumber(facts.MessageSize, condition.Operator, number),
            MailRuleField.MessageTime => condition.FirstTime is { } first
                && MatchTime(facts.MessageTime, condition.Operator, first, condition.SecondTime),
            MailRuleField.HasAttachment => condition.Operator == MailRuleOperator.Is
                                            && condition.Boolean is { } expected
                                            && expected == facts.HasAttachment,
            _ => false
        };
    }

    private static bool MatchesMemoized(CompiledCondition condition, MailRuleFacts facts, EvaluationScope? scope)
    {
        if (scope is null) return Matches(condition, facts);
        object? dependency = condition.Field switch
        {
            MailRuleField.Sender => facts.Senders,
            MailRuleField.SenderDomain => facts.SenderDomains,
            MailRuleField.Recipient => facts.Recipients,
            MailRuleField.Cc => facts.Cc,
            MailRuleField.RecipientOrCc => facts.Recipients,
            MailRuleField.Subject => facts.Subject,
            MailRuleField.Body => facts.Body,
            MailRuleField.AttachmentName => facts.AttachmentNames,
            MailRuleField.MessageSize or MailRuleField.MessageTime or MailRuleField.HasAttachment => null,
            _ => facts
        };
        var key = new ConditionResultKey(condition, dependency,
            condition.Field == MailRuleField.RecipientOrCc ? facts.Cc : null,
            condition.Field switch
            {
                MailRuleField.MessageSize => facts.MessageSize,
                MailRuleField.HasAttachment => facts.HasAttachment ? 1 : 0,
                _ => 0
            },
            condition.Field == MailRuleField.MessageTime ? facts.MessageTime : default);
        if (scope.ConditionResults.TryGetValue(key, out var result)) return result;
        result = Matches(condition, facts);
        if (scope.ConditionResults.Count < ConditionResultLimit
            && (scope.ConditionResults.Count + 1) * EstimatedConditionResultBytes <= ConditionResultBudgetBytes)
            scope.ConditionResults[key] = result;
        return result;
    }

    private static bool RuleMatches(CompiledRule rule, MailRuleFacts facts, EvaluationScope? scope)
    {
        if (rule.MatchMode == MailRuleMatchMode.All)
        {
            foreach (var condition in rule.Conditions)
                if (!MatchesMemoized(condition, facts, scope)) return false;
            return true;
        }
        foreach (var condition in rule.Conditions)
            if (MatchesMemoized(condition, facts, scope)) return true;
        return false;
    }

    private static bool MatchNumber(long candidate, MailRuleOperator operation, long expected) => operation switch
    {
        MailRuleOperator.GreaterThan => candidate > expected,
        MailRuleOperator.GreaterThanOrEqual => candidate >= expected,
        MailRuleOperator.LessThan => candidate < expected,
        MailRuleOperator.LessThanOrEqual => candidate <= expected,
        _ => false
    };

    private static bool MatchTime(DateTimeOffset candidate, MailRuleOperator operation,
        DateTimeOffset first, DateTimeOffset? second) => operation switch
    {
        MailRuleOperator.Between => second is { } end && candidate >= first && candidate <= end,
        MailRuleOperator.Before => candidate < first,
        MailRuleOperator.After => candidate > first,
        _ => false
    };

    private static bool MatchText(
        IEnumerable<string> candidates,
        MailRuleOperator operation,
        IReadOnlyList<CompiledPattern> values)
    {
        static bool Match(string candidate, CompiledPattern pattern, bool contains) => pattern.Wildcard is not null
            ? pattern.Wildcard.IsMatch(candidate)
            : contains
                ? candidate.Contains(pattern.Text, StringComparison.OrdinalIgnoreCase)
                : string.Equals(candidate, pattern.Text, StringComparison.OrdinalIgnoreCase);
        bool Any(CompiledPattern value, bool contains)
        {
            foreach (var candidate in candidates)
                if (!string.IsNullOrWhiteSpace(candidate) && Match(candidate, value, contains)) return true;
            return false;
        }
        return operation switch
        {
            MailRuleOperator.ContainsAny => values.Any(value => Any(value, true)),
            MailRuleOperator.ContainsAll => values.All(value => Any(value, true)),
            MailRuleOperator.EqualsAny => values.Any(value => Any(value, false)),
            MailRuleOperator.NotContainsAny => values.All(value => !Any(value, true)),
            _ => false
        };
    }

    private static bool MatchText(IEnumerable<string> candidates, MailRuleOperator operation, IReadOnlyList<string> values)
    {
        var source = candidates.Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();
        return operation switch
        {
            MailRuleOperator.ContainsAny => values.Any(value => source.Any(candidate => MatchValue(candidate, value, contains: true))),
            MailRuleOperator.ContainsAll => values.All(value => source.Any(candidate => MatchValue(candidate, value, contains: true))),
            MailRuleOperator.EqualsAny => values.Any(value => source.Any(candidate => MatchValue(candidate, value, contains: false))),
            MailRuleOperator.NotContainsAny => values.All(value => source.All(candidate => !MatchValue(candidate, value, contains: true))),
            _ => false
        };
    }

    /// <summary>
    /// 匹配模式支持 * 与 ? 通配符；没有通配符时 Contains 走子串匹配，
    /// Equals 走整串比较，保持与历史规则兼容。
    /// </summary>
    private static bool MatchValue(string candidate, string pattern, bool contains)
    {
        if (!pattern.Contains('*') && !pattern.Contains('?'))
        {
            return contains
                ? candidate.Contains(pattern, StringComparison.OrdinalIgnoreCase)
                : string.Equals(candidate, pattern, StringComparison.OrdinalIgnoreCase);
        }

        var regex = "^" + Regex.Escape(pattern)
            .Replace("\\*", ".*", StringComparison.Ordinal)
            .Replace("\\?", ".", StringComparison.Ordinal) + "$";
        return Regex.IsMatch(
            candidate,
            regex,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(200));
    }

    private static bool MatchNumber(long candidate, MailRuleOperator operation, IReadOnlyList<string> values)
    {
        if (!long.TryParse(values[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var expected)) return false;
        return operation switch
        {
            MailRuleOperator.GreaterThan => candidate > expected,
            MailRuleOperator.GreaterThanOrEqual => candidate >= expected,
            MailRuleOperator.LessThan => candidate < expected,
            MailRuleOperator.LessThanOrEqual => candidate <= expected,
            _ => false
        };
    }

    private static bool MatchTime(DateTimeOffset candidate, MailRuleOperator operation, IReadOnlyList<string> values)
    {
        if (!DateTimeOffset.TryParse(values[0], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var first)) return false;
        if (operation == MailRuleOperator.Between)
        {
            return values.Count > 1
                   && DateTimeOffset.TryParse(values[1], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var second)
                   && candidate >= first && candidate <= second;
        }

        return operation switch
        {
            MailRuleOperator.Before => candidate < first,
            MailRuleOperator.After => candidate > first,
            _ => false
        };
    }

    private static MailRuleInput Validate(MailRuleInput input)
    {
        var name = input.Name.Trim();
        if (name.Length is < 1 or > 128) throw new FormatException("Rule name must contain 1 to 128 characters.");
        if (input.Conditions.Count is < 1 or > 20) throw new FormatException("A rule must contain 1 to 20 conditions.");
        var actions = ValidateActions(input.Direction, input.Actions, input.TargetMailboxName);
        // 目标文件夹不再是独立表单字段：取移动动作的值（收信=投递文件夹，发信=Sent 副本落盘文件夹），
        // 没有移动动作时回落到默认值。
        var target = actions.FirstOrDefault(x => x.Kind == MailRuleActionKind.MoveToFolder)?.Values[0]
                     ?? (input.Direction == MailRuleDirection.Incoming ? "INBOX" : "Sent");
        var conditions = input.Conditions.Select(ValidateCondition).ToArray();
        return input with { Name = name, TargetMailboxName = target, Conditions = conditions, Actions = actions };
    }

    private static IReadOnlyList<MailRuleActionInput> ValidateActions(
        MailRuleDirection direction,
        IReadOnlyList<MailRuleActionInput>? inputActions,
        string legacyTargetMailboxName)
    {
        // 旧客户端只提交 targetMailboxName 而不带 actions：仅这种情况补默认移动动作，保持兼容。
        // 显式提交空 actions 视为错误，避免“保存后静默多出一个动作”。
        IReadOnlyList<MailRuleActionInput> supplied = inputActions is null
            ? [new MailRuleActionInput(MailRuleActionKind.MoveToFolder, [legacyTargetMailboxName])]
            : inputActions;
        if (supplied.Count == 0) throw new FormatException("A rule must contain at least one action.");
        if (supplied.Count > 21) throw new FormatException("A rule may contain at most 21 actions.");
        var result = new List<MailRuleActionInput>();
        var moveCount = 0;
        var forwardCount = 0;
        var rejectCount = 0;
        foreach (var action in supplied)
        {
            var values = action.Values.Select(x => x.Trim()).Where(x => x.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            switch (action.Kind)
            {
                case MailRuleActionKind.MoveToFolder:
                    // 收信规则决定投递文件夹；发信规则决定 Sent 副本的落盘文件夹。
                    if (++moveCount > 1 || values.Length != 1)
                        throw new FormatException("A rule may contain one move action.");
                    values = [MailboxNames.Normalize(values[0])];
                    break;
                case MailRuleActionKind.ForwardTo:
                    // 收信规则转发入站邮件；发信规则把外发副本额外转发到目标地址。
                    forwardCount += values.Length;
                    if (values.Length < 1 || forwardCount > 20
                        || values.Any(value => !AddressParser.TrySplit(value, out _, out _)))
                        throw new FormatException("Forward targets must contain 1 to 20 valid email addresses.");
                    values = values.Select(AddressParser.Normalize).ToArray();
                    break;
                case MailRuleActionKind.Reject:
                    if (direction != MailRuleDirection.Outgoing)
                        throw new FormatException("Reject actions are only available for outgoing rules.");
                    if (++rejectCount > 1 || values.Length > 1)
                        throw new FormatException("A rule may contain one reject action with an optional reason.");
                    if (values.Length == 1 && values[0].Length > 255)
                        throw new FormatException("Reject reason must be at most 255 characters.");
                    break;
                default:
                    throw new FormatException("Unknown mail rule action.");
            }
            result.Add(new MailRuleActionInput(action.Kind, values));
        }

        if (rejectCount > 0 && forwardCount > 0)
            throw new FormatException("A rule cannot combine reject and forward actions.");
        return result;
    }

    private static MailRuleConditionInput ValidateCondition(MailRuleConditionInput input)
    {
        var values = input.Values.Select(x => x.Trim()).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var required = input.Operator == MailRuleOperator.Between ? 2 : 1;
        if (values.Length < required || values.Length > 50) throw new FormatException("Rule condition values are invalid.");
        var valid = input.Field switch
        {
            MailRuleField.MessageSize => input.Operator is MailRuleOperator.GreaterThan or MailRuleOperator.GreaterThanOrEqual or MailRuleOperator.LessThan or MailRuleOperator.LessThanOrEqual
                                         && long.TryParse(values[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var size) && size >= 0,
            MailRuleField.MessageTime => input.Operator is MailRuleOperator.Before or MailRuleOperator.After or MailRuleOperator.Between
                                         && values.Take(required).All(x => DateTimeOffset.TryParse(x, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _)),
            MailRuleField.HasAttachment => input.Operator == MailRuleOperator.Is && bool.TryParse(values[0], out _),
            _ => input.Operator is MailRuleOperator.ContainsAny or MailRuleOperator.ContainsAll or MailRuleOperator.EqualsAny or MailRuleOperator.NotContainsAny
        };
        if (!valid) throw new FormatException("Rule field and operator are incompatible.");
        return input with { Values = values };
    }

    private static MailRuleView ToView(MailRule rule) => new(
        rule.Id,
        rule.DomainId,
        rule.Name,
        rule.Direction,
        rule.MatchMode,
        rule.TargetMailboxName,
        rule.IsEnabled,
        rule.Priority,
        rule.Conditions.OrderBy(x => x.Position).Select(condition => new MailRuleConditionView(
            condition.Id,
            condition.Field,
            condition.Operator,
            DeserializeValues(condition.ValuesJson),
            condition.Position)).ToArray(),
        rule.Actions.OrderBy(x => x.Position).Select(ToActionView).ToArray());

    private static MailRuleActionView ToActionView(MailRuleAction action) => new(
        action.Id,
        action.Kind,
        DeserializeValues(action.ValuesJson),
        action.Position);

    private static string[] DeserializeValues(string json) =>
        JsonSerializer.Deserialize<string[]>(json, JsonOptions) ?? [];

    private static async Task NormalizePrioritiesAsync(
        MailDbContext db,
        Guid domainId,
        MailRuleDirection direction,
        CancellationToken cancellationToken)
    {
        var rules = await db.MailRules.Where(x => x.DomainId == domainId && x.Direction == direction)
            .OrderBy(x => x.Priority).ThenBy(x => x.CreatedAt).ToListAsync(cancellationToken);
        for (var index = 0; index < rules.Count; index++) rules[index].Priority = index;
        await db.SaveChangesAsync(cancellationToken);
    }

    private static void AddAddress(ICollection<string> addresses, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) addresses.Add(AddressParser.Normalize(value));
    }

    private static string NormalizeDomain(string value) => value.Trim().TrimStart('@').TrimEnd('.').ToLowerInvariant();

    private static string NormalizeHtml(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return string.Empty;
        return WebUtility.HtmlDecode(HtmlTagRegex().Replace(html, " "));
    }

    private static void AddAudit(MailDbContext db, string actor, string action, string resource, string? detail) =>
        db.AuditRecords.Add(new AuditRecord { Actor = actor, Action = action, Resource = resource, Detail = detail });

    [GeneratedRegex("<[^>]+>", RegexOptions.CultureInvariant)]
    private static partial Regex HtmlTagRegex();
}
