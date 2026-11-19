using AmiiTag.Bluetooth.Devices;
using AmiiTag.Core.Interfaces;
using AmiiTag.Core.Services;
using Microsoft.AspNetCore.Components;

namespace AmiiTag.Core.Components.Devices;

// Shared settings mechanics; each concrete device supplies its own actions.
public abstract class DeviceControlsBase<TDevice> : ComponentBase where TDevice : ITagDevice
{
    [Inject] protected TagActions Actions { get; set; } = default!;
    [Inject] protected IDialogService Dialogs { get; set; } = default!;
    [Inject] protected IAppNavigator Navigator { get; set; } = default!;
    [Parameter, EditorRequired] public TDevice Device { get; set; } = default!;
    [Parameter] public EventCallback OnOpeningChild { get; set; }
    [Parameter] public EventCallback OnDisconnect { get; set; }

    protected async Task ChooseSettings(List<(string Title, Func<Task> Run)> options)
    {
        options.Add(("Disconnect", () => OnDisconnect.InvokeAsync()));
        var choice = await Dialogs.ChooseAsync(Device.Name, options.Select(o => o.Title).ToList());
        if (choice >= 0 && choice < options.Count) await options[choice].Run();
    }

    protected async Task Rename(IRenamableTagDevice device)
    {
        var name = await Dialogs.PromptAsync(device.Name, "Enter a new name. The device will disconnect.", device.Name, device.MaxNameBytes);
        if (string.IsNullOrWhiteSpace(name)) return;
        var ok = await Actions.WithProgressAsync("Changing Name", async (_, ct) => { await device.RenameAsync(name.Trim(), ct); return true; });
        if (ok) await Navigator.GoBackAsync();
    }

    protected async Task EnableUart(IUartTagDevice device)
    {
        await Actions.WithProgressAsync("Enabling UART", async (_, ct) => { await device.EnableUartAsync(ct); return true; });
        await Navigator.GoBackAsync();
    }
}
