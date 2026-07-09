namespace MailServer.Api;

public enum AdminToastKind
{
    Success,
    Error,
    Info
}

public sealed record AdminToast(Guid Id, AdminToastKind Kind, string Message);

public sealed class AdminToastService
{
    private readonly List<AdminToast> _items = [];

    public event Action? Changed;

    public IReadOnlyList<AdminToast> Items => _items;

    public void Success(string message) => Show(AdminToastKind.Success, message);

    public void Error(string message) => Show(AdminToastKind.Error, message);

    public void Info(string message) => Show(AdminToastKind.Info, message);

    public void Dismiss(Guid id)
    {
        if (_items.RemoveAll(x => x.Id == id) == 0)
        {
            return;
        }

        Changed?.Invoke();
    }

    private void Show(AdminToastKind kind, string message)
    {
        var toast = new AdminToast(Guid.NewGuid(), kind, message);
        _items.Add(toast);
        Changed?.Invoke();
        _ = DismissLaterAsync(toast.Id);
    }

    private async Task DismissLaterAsync(Guid id)
    {
        await Task.Delay(TimeSpan.FromSeconds(5));
        Dismiss(id);
    }
}
