namespace MailServer.Api.Components;

public sealed record AdminSelectOption<TValue>(TValue Value, string Label, bool Disabled = false);
