using System.Globalization;
using System.Net;
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

public sealed record MailRuleFacts(
    IReadOnlyList<string> Senders,
    IReadOnlyList<string> SenderDomains,
    IReadOnlyList<string> Recipients,
    IReadOnlyList<string> Cc,
    string Subject,
    string Body,
    IReadOnlyList<string> AttachmentNames,
    long MessageSize,
    DateTimeOffset MessageTime,
    bool HasAttachment);

public sealed record MailRuleMatch(
    Guid RuleId,
    string RuleName,
    string TargetMailboxName,
    IReadOnlyList<MailRuleActionView> Actions);

public sealed partial class MailRuleService(
    IDbContextFactory<MailDbContext> dbFactory,
    ILogger<MailRuleService> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

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
            .Where(x => x.Account.DomainId == domainId && x.Account.IsEnabled)
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
        await db.SaveChangesAsync(cancellationToken);
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
        await db.SaveChangesAsync(cancellationToken);
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        return ToView(rule);
    }

    public async Task DeleteAsync(
        Guid domainId,
        Guid ruleId,
        string actor,
        CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var rule = await db.MailRules.SingleOrDefaultAsync(
            x => x.Id == ruleId && x.DomainId == domainId,
            cancellationToken) ?? throw new KeyNotFoundException("Mail rule not found.");
        var direction = rule.Direction;
        db.MailRules.Remove(rule);
        AddAudit(db, actor, "mail-rule.delete", $"mail-rule:{rule.Id}", rule.Name);
        await db.SaveChangesAsync(cancellationToken);
        await NormalizePrioritiesAsync(db, domainId, direction, cancellationToken);
    }

    public async Task ReorderAsync(
        Guid domainId,
        MailRuleDirection direction,
        IReadOnlyList<Guid> ruleIds,
        string actor,
        CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
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
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<MailRuleMatch?> MatchAsync(
        Guid domainId,
        MailRuleDirection direction,
        MailRuleFacts facts,
        CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var rules = await db.MailRules.AsNoTracking()
            .Include(x => x.Conditions)
            .Include(x => x.Actions)
            .AsSplitQuery()
            .Where(x => x.DomainId == domainId && x.Direction == direction && x.IsEnabled)
            .OrderBy(x => x.Priority)
            .ThenBy(x => x.CreatedAt)
            .ToListAsync(cancellationToken);
        foreach (var rule in rules)
        {
            var results = rule.Conditions.OrderBy(x => x.Position).Select(x => Matches(x, facts)).ToArray();
            var matched = rule.MatchMode == MailRuleMatchMode.All ? results.All(x => x) : results.Any(x => x);
            if (matched)
            {
                logger.LogInformation(
                    "Mail rule {RuleId} matched for domain {DomainId}: direction={Direction}, targetMailbox={TargetMailbox}.",
                    rule.Id,
                    domainId,
                    direction,
                    rule.TargetMailboxName);
                var actions = rule.Actions.OrderBy(x => x.Position).Select(ToActionView).ToArray();
                if (actions.Length == 0)
                {
                    actions = [new MailRuleActionView(Guid.Empty, MailRuleActionKind.MoveToFolder, [rule.TargetMailboxName], 0)];
                }
                return new MailRuleMatch(rule.Id, rule.Name, rule.TargetMailboxName, actions);
            }
        }

        return null;
    }

    public static MailRuleFacts BuildFacts(
        MimeMessage message,
        string envelopeSender,
        IReadOnlyList<string> envelopeRecipients,
        long messageSize,
        DateTimeOffset messageTime)
    {
        var senders = new List<string>();
        AddAddress(senders, envelopeSender);
        foreach (var mailbox in message.From.Mailboxes) AddAddress(senders, mailbox.Address);
        if (message.Sender is not null) AddAddress(senders, message.Sender.Address);
        var recipients = envelopeRecipients.Select(AddressParser.Normalize).ToList();
        foreach (var mailbox in message.To.Mailboxes) AddAddress(recipients, mailbox.Address);
        var cc = message.Cc.Mailboxes.Select(x => AddressParser.Normalize(x.Address)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var domains = senders.Select(address => AddressParser.TrySplit(address, out _, out var domain) ? domain : string.Empty)
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var attachments = message.Attachments
            .Select(x => x.ContentDisposition?.FileName ?? x.ContentType.Name)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var body = string.Join('\n', new[] { message.TextBody, NormalizeHtml(message.HtmlBody) }
            .Where(x => !string.IsNullOrWhiteSpace(x)));
        return new MailRuleFacts(
            senders.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            domains,
            recipients.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            cc,
            message.Subject ?? string.Empty,
            body,
            attachments,
            messageSize,
            messageTime,
            attachments.Length > 0);
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

    private static bool MatchText(IEnumerable<string> candidates, MailRuleOperator operation, IReadOnlyList<string> values)
    {
        var source = candidates.Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();
        return operation switch
        {
            MailRuleOperator.ContainsAny => values.Any(value => source.Any(candidate => candidate.Contains(value, StringComparison.OrdinalIgnoreCase))),
            MailRuleOperator.ContainsAll => values.All(value => source.Any(candidate => candidate.Contains(value, StringComparison.OrdinalIgnoreCase))),
            MailRuleOperator.EqualsAny => values.Any(value => source.Any(candidate => string.Equals(candidate, value, StringComparison.OrdinalIgnoreCase))),
            MailRuleOperator.NotContainsAny => values.All(value => source.All(candidate => !candidate.Contains(value, StringComparison.OrdinalIgnoreCase))),
            _ => false
        };
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
        var supplied = inputActions is { Count: > 0 }
            ? inputActions
            : [new MailRuleActionInput(MailRuleActionKind.MoveToFolder, [legacyTargetMailboxName])];
        if (supplied.Count > 21) throw new FormatException("A rule may contain at most 21 actions.");
        var result = new List<MailRuleActionInput>();
        var moveCount = 0;
        var forwardCount = 0;
        foreach (var action in supplied)
        {
            var values = action.Values.Select(x => x.Trim()).Where(x => x.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            switch (action.Kind)
            {
                case MailRuleActionKind.MoveToFolder:
                    if (++moveCount > 1 || values.Length != 1)
                        throw new FormatException("A rule may contain one move action.");
                    values = [MailboxNames.Normalize(values[0])];
                    break;
                case MailRuleActionKind.ForwardTo:
                    if (direction != MailRuleDirection.Incoming)
                        throw new FormatException("Forward actions are only available for incoming rules.");
                    forwardCount += values.Length;
                    if (values.Length < 1 || forwardCount > 20
                        || values.Any(value => !AddressParser.TrySplit(value, out _, out _)))
                        throw new FormatException("Forward targets must contain 1 to 20 valid email addresses.");
                    values = values.Select(AddressParser.Normalize).ToArray();
                    break;
                default:
                    throw new FormatException("Unknown mail rule action.");
            }
            result.Add(new MailRuleActionInput(action.Kind, values));
        }

        if (result.Count == 0) throw new FormatException("A rule must contain at least one action.");
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
