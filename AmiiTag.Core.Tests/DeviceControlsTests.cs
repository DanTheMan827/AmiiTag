using System.Reflection;
using System.Text;
using AmiiTag.Tests.Simulation;
using AmiiTag.Bluetooth;
using AmiiTag.Bluetooth.Devices;
using AmiiTag.Core.Components.Devices;
using AmiiTag.Core.Components;
using Microsoft.AspNetCore.Components;
using AmiiTag.Core.Data;
using AmiiTag.Core.Interfaces;
using AmiiTag.Core.Services;
using AmiiTag.Core.Services.Web;
using AmiiTag.Tags;
using Bunit;
using Bunit.TestDoubles;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AmiiTag.Core.Tests;

public class DeviceControlsTests : BunitContext
{
    private readonly Dialogs _dialogs = new();
    private readonly Files _files = new();

    public DeviceControlsTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton<IDialogService>(_dialogs);
        Services.AddSingleton<IFileService>(_files);
        Services.AddSingleton(DispatchProxy.Create<IAppNavigator, EmptyProxy>());
        var database = DispatchProxy.Create<IDatabaseContainer, EmptyProxy>();
        Services.AddSingleton(database);
        Services.AddSingleton(new KeyStore(DispatchProxy.Create<IAppStorage, EmptyProxy>()));
        Services.AddSingleton<AmiiboLookup>();
        Services.AddSingleton<ContextMenus>();
        Services.AddSingleton<IFigureImageProvider, DirectFigureImageProvider>();
        Services.AddSingleton(new AppEnvironment { Platform = AppEnvironment.PlatformType.Windows });
        Services.AddSingleton(new TagActions(null!, null!, _dialogs, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!));
    }

    [Fact]
    public async Task SlotWrite_RefreshesOnlyWrittenSlot_AndUpdatesActiveSlot()
    {
        var device = new Slots();
        var cut = Render<SlotDeviceControls>(p => p.Add(c => c.Device, device));
        Assert.Equal(3, cut.FindAll("li").Count);
        Assert.Equal(1, device.FullRefreshes);
        var writer = cut.FindComponent<TagDeviceWriter>();
        await cut.InvokeAsync(() => writer.Instance.WriteFile(2));
        Assert.Equal(new[] { 2 }, device.SlotRefreshes);
        Assert.Equal(1, device.FullRefreshes);
        Assert.Contains("Slot 3 · Active", cut.Markup);
        Assert.Empty(_dialogs.Errors);
    }

    [Fact]
    public void ParentRerender_DoesNotReloadSlotList()
    {
        var device = new Slots();
        var cut = Render<SlotDeviceControls>(p => p.Add(c => c.Device, device));
        cut.Render(p => p.Add(c => c.Device, device));
        Assert.Equal(1, device.FullRefreshes);
        var replacement = new Slots();
        cut.Render(p => p.Add(c => c.Device, replacement));
        Assert.Equal(1, replacement.FullRefreshes);
    }

    [Fact]
    public async Task UidRefresh_RequestsOneSlot_WithoutChangingSelection()
    {
        var device = new Slots();
        var cut = Render<SlotDeviceControls>(p => p.Add(c => c.Device, device));
        await cut.InvokeAsync(() => cut.Instance.RefreshSlotAsync(1));
        Assert.Equal(new[] { 1 }, device.SlotRefreshes);
        Assert.Equal(1, device.FullRefreshes);
        Assert.Contains("Slot 1 · Active", cut.Markup);
    }

    [Fact]
    public async Task NonSlotWrite_DoesNotRequestAnySlotInformation()
    {
        var device = new SingleDevice();
        var cut = Render<SingleTagDeviceControls>(p => p.Add(c => c.Device, device));
        await cut.InvokeAsync(() => cut.FindComponent<TagDeviceWriter>().Instance.WriteFile(null));
        Assert.Equal(1, device.Writes);
        Assert.Empty(_dialogs.Errors);
    }

    [Fact]
    public async Task CancelledFilePicker_DoesNotWriteOrRefresh()
    {
        _files.Cancel = true;
        var device = new Slots();
        var cut = Render<SlotDeviceControls>(p => p.Add(c => c.Device, device));
        await cut.InvokeAsync(() => cut.FindComponent<TagDeviceWriter>().Instance.WriteFile(1));
        Assert.Equal(0, device.Writes);
        Assert.Empty(device.SlotRefreshes);
        Assert.Equal(1, device.FullRefreshes);
    }

    [Fact]
    public async Task FailedWrite_DoesNotRefreshSlots()
    {
        var device = new Slots { FailWrite = true };
        var cut = Render<SlotDeviceControls>(p => p.Add(c => c.Device, device));
        await cut.InvokeAsync(() => cut.FindComponent<TagDeviceWriter>().Instance.WriteFile(1));
        Assert.Single(_dialogs.Errors);
        Assert.Empty(device.SlotRefreshes);
        Assert.Equal(1, device.FullRefreshes);
    }

    [Fact]
    public void AmiLoop_ShowsItsOwnCommandsAndWriteControls()
    {
        var device = new AmiLoopDevice(DispatchProxy.Create<IBleConnection, EmptyProxy>());
        var cut = Render<AmiLoopDeviceControls>(p => p.Add(c => c.Device, device));
        Assert.Contains("Write Tag File", cut.Markup);
        foreach (var command in device.Commands) Assert.Contains(command.Title, cut.Markup);
        Assert.Empty(cut.FindComponents<SlotDeviceControls>());
        Assert.Empty(cut.FindComponents<PixlDeviceControls>());
        cut.Find("button[aria-label=Settings]").Click();
        Assert.Equal(new[] { "Disconnect" }, _dialogs.Choices);
    }

    [Fact]
    public void AmiiboLink_ShowsItsOwnCommandsAndWriteControls()
    {
        var device = new AmiiboLinkDevice(DispatchProxy.Create<IBleConnection, EmptyProxy>());
        var cut = Render<AmiiboLinkDeviceControls>(p => p.Add(c => c.Device, device));
        Assert.Contains("Write Blank Tag", cut.Markup);
        foreach (var command in device.Commands) Assert.Contains(command.Title, cut.Markup);
        Assert.Empty(cut.FindComponents<SlotDeviceControls>());
        cut.Find("button[aria-label=Settings]").Click();
        Assert.Equal(new[] { "Disconnect" }, _dialogs.Choices);
    }

    [Fact]
    public async Task Pixl_ShowsFileBrowserAndOnlyPixlSettings()
    {
        var sim = new PixlSimulator();
        sim.AddFolder("E:/amiibo");
        var device = new PixlDevice(sim);
        Assert.True(await device.InitializeAsync());
        var cut = Render<PixlDeviceControls>(p => p.Add(c => c.Device, device));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".pixl-drive")));
        Assert.Empty(cut.FindComponents<TagDeviceWriter>());
        Assert.Empty(cut.FindComponents<SlotDeviceControls>());
        cut.Find("button[aria-label=Settings]").Click();
        Assert.Contains("Format drive E:", _dialogs.Choices);
        Assert.Contains("Firmware update mode (DFU)", _dialogs.Choices);
        Assert.DoesNotContain("Change Name", _dialogs.Choices);
        Assert.DoesNotContain("Enable UART", _dialogs.Choices);
        Assert.Empty(_dialogs.Errors);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Puck_HasOnlyPuckSettings_AndUidRefreshReadsOneSlot(bool amiitool)
    {
        var sim = new PuckWire(amiitool);
        var device = new PuckDevice(sim);
        Assert.True(await device.InitializeAsync());
        var cut = Render<PuckDeviceControls>(p => p.Add(c => c.Device, device));
        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll("li").Count));
        Assert.Empty(cut.FindComponents<SingleTagDeviceControls>());
        cut.Find("button[aria-label=Settings]").Click();
        cut.WaitForAssertion(() => Assert.Equal(new[] { "Change Name", "Enable UART", "Disconnect" }, _dialogs.Choices));
        var slots = cut.FindComponent<SlotDeviceControls>();
        var actions = slots.Instance.AdditionalActions!(new SlotInfo(1, new TagDump(new byte[572])));
        if (amiitool)
        {
            sim.Received.Clear();
            await cut.InvokeAsync(() => Assert.Single(actions).Run());
            Assert.Equal(2, sim.Received.Count);
            Assert.Equal(new byte[] { 0xEE, 2, 1 }, sim.Received[0]);
            Assert.Equal(new byte[] { 1, 1, 1 }, sim.Received[1]);
        }
        else Assert.Empty(actions);
        Assert.Empty(_dialogs.Errors);
    }

    private sealed class PuckWire(bool amiitool) : SimulatedUartDevice("Puck.js")
    {
        protected override void OnWrite(byte[] data)
        {
            if (Encoding.ASCII.GetString(data).Contains("fastMode()")) { Notify(Encoding.ASCII.GetBytes("DTM_PUCK_FAST")); return; }
            switch (data[0])
            {
                case 0xFC: Notify(Encoding.ASCII.GetBytes("dtm-2.3.0")); Notify([0]); break;
                case 0xFA: Notify(Encoding.ASCII.GetBytes("Puck.js")); Notify([0]); break;
                case 0xEE:
                    if (!amiitool) Notify(Encoding.ASCII.GetBytes("Bad Command"));
                    else if (data.Length == 1) Notify([0, 1, 2]);
                    else if (data[1] == 0) Notify([0]);
                    else Notify([0xEE, 2, data[2], .. new byte[9]]);
                    break;
                case 1:
                    if (data.Length == 1) Notify([1, 0, 3]);
                    else for (var i = 0; i < data[2]; i++) Notify([1, (byte)(data[1] + i), .. new byte[80]]);
                    break;
                default: throw new Exception("Unexpected test command");
            }
        }
    }

    [Fact]
    public async Task LegacyPuck_ShowsLegacySettingsAndSlotsWithoutAmiitoolActions()
    {
        var device = new LegacyPuckDevice(new LegacyWire());
        Assert.True(await device.InitializeAsync());
        var cut = Render<LegacyPuckDeviceControls>(p => p.Add(c => c.Device, device));
        Assert.Equal(3, cut.FindAll("li").Count);
        Assert.Null(cut.FindComponent<SlotDeviceControls>().Instance.AdditionalActions);
        cut.Find("button[aria-label=Settings]").Click();
        cut.WaitForAssertion(() => Assert.Equal(new[] { "Change Name", "Enable UART", "Disconnect" }, _dialogs.Choices));
        Assert.Empty(_dialogs.Errors);
    }

    private sealed class LegacyWire : IBleConnection, IBleCharacteristic
    {
        public BleDeviceInfo Device { get; } = new("legacy", "Puck.js", [BleUuids.LegacyPuckService]);
        public bool IsConnected => true;
        public Guid Id => BleUuids.LegacyPuckCommand;
        private byte[] _response = [];
#pragma warning disable CS0067
        public event EventHandler? Disconnected;
        public event EventHandler<byte[]>? ValueUpdated;
#pragma warning restore CS0067
        public Task<bool> HasServiceAsync(Guid service, CancellationToken cancellationToken = default) => Task.FromResult(service == BleUuids.LegacyPuckService);
        public Task<IBleCharacteristic?> GetCharacteristicAsync(Guid service, Guid characteristic, CancellationToken cancellationToken = default) =>
            Task.FromResult<IBleCharacteristic?>(service == BleUuids.LegacyPuckService ? this : null);
        public Task<int> RequestMtuAsync(int mtu, CancellationToken cancellationToken = default) => Task.FromResult(247);
        public Task DisconnectAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public Task StartNotificationsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopNotificationsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<byte[]> ReadAsync(CancellationToken cancellationToken = default) => Task.FromResult(_response);
        public Task WriteAsync(byte[] data, bool withResponse = true, CancellationToken cancellationToken = default)
        {
            if (data[0] == 1) _response = data.Length == 1 ? [1, 0, 3] : [1, data[1], .. new byte[80]];
            else throw new Exception("Unexpected legacy command");
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task NativePullRunsDotNetUpdateWithoutRecreatingTheControl()
    {
        var chrome = new PageChromeState(true);
        var device = new Slots();
        var host = Render<CascadingValue<PageChromeState>>(p => p.Add(c => c.Value, chrome)
            .AddChildContent<SlotDeviceControls>(slots => slots.Add(c => c.Device, device)));
        var original = host.FindComponent<SlotDeviceControls>().Instance;
        Assert.Equal(1, device.FullRefreshes);
        await chrome.RefreshAsync();
        Assert.Equal(2, device.FullRefreshes);
        Assert.Same(original, host.FindComponent<SlotDeviceControls>().Instance);
        Assert.Empty(_dialogs.Errors);
    }

    [Fact]
    public async Task NativeSettingsRegistersToolbarCallbackWithoutPageLink()
    {
        var chrome = new PageChromeState();
        var calls = 0;
        var host = Render<CascadingValue<PageChromeState>>(p => p.Add(c => c.Value, chrome)
            .AddChildContent<AppPage>(page => page.Add(c => c.OnSettings, () => { calls++; })));
        Assert.True(chrome.HasSettings);
        Assert.Empty(host.FindAll("button[aria-label=Settings]"));
        await chrome.OpenSettingsAsync();
        Assert.Equal(1, calls);
        host.Render(p => p.Add(c => c.Value, chrome).AddChildContent(""));
        host.WaitForAssertion(() => Assert.False(chrome.HasSettings));
    }

    [Fact]
    public void WebSettingsGearIsInSimulatedNavbar_WhenActionProvided()
    {
        Services.AddSingleton(new AppEnvironment { Platform = AppEnvironment.PlatformType.WebAssembly });
        var calls = 0;
        var cut = Render<AppPage>(p => p.Add(c => c.Title, "AmiiTag")
            .Add(c => c.ShowHeader, false).Add(c => c.OnSettings, () => { calls++; }));
        cut.Find(".page-header .page-header-side.end button[aria-label=Settings]").Click();
        Assert.Equal(1, calls);
        Assert.Empty(cut.FindAll(".page-content button[aria-label=Settings]"));
    }

    [Fact]
    public async Task PixlStartsAtExternalRoot_AndBackReturnsThroughFolderHistory()
    {
        var sim = new PixlSimulator();
        sim.AddFolder("E:/amiibo");
        sim.AddFolder("E:/amiibo/child");
        var device = new PixlDevice(sim);
        Assert.True(await device.InitializeAsync());
        var chrome = new PageChromeState(true);
        var host = Render<CascadingValue<PageChromeState>>(p => p.Add(c => c.Value, chrome)
            .AddChildContent<PixlDeviceControls>(control => control.Add(c => c.Device, device)));
        var browser = host.FindComponent<PixlBrowser>().Instance;
        Assert.Equal("E:/", browser.CurrentFolder);
        Assert.False(chrome.CanGoBack);
        host.FindAll("button.list-row").Single(b => b.TextContent.Contains("amiibo")).Click();
        Assert.Equal("E:/amiibo", browser.CurrentFolder);
        Assert.True(chrome.CanGoBack);
        host.FindAll("button.list-row").Single(b => b.TextContent.Contains("child")).Click();
        Assert.Equal("E:/amiibo/child", browser.CurrentFolder);
        Assert.True(await chrome.TryGoBackAsync());
        Assert.Equal("E:/amiibo", browser.CurrentFolder);
        Assert.True(await chrome.TryGoBackAsync());
        Assert.Equal("E:/", browser.CurrentFolder);
        Assert.False(await chrome.TryGoBackAsync());
        Assert.Same(browser, host.FindComponent<PixlBrowser>().Instance);
        Assert.Empty(_dialogs.Errors);
    }

    [Fact]
    public async Task PixlUpAndBreadcrumbsPopHistory_WithoutReenteringTheFolderLeft()
    {
        var sim = new PixlSimulator();
        sim.AddFolder("E:/amiibo");
        sim.AddFolder("E:/amiibo/child");
        var device = new PixlDevice(sim);
        Assert.True(await device.InitializeAsync());
        var cut = Render<PixlDeviceControls>(p => p.Add(c => c.Device, device));
        var browser = cut.FindComponent<PixlBrowser>().Instance;
        cut.FindAll("button.list-row").Single(b => b.TextContent.Contains("amiibo")).Click();
        cut.FindAll("button.list-row").Single(b => b.TextContent.Contains("child")).Click();
        cut.FindAll("button").Single(b => b.TextContent == "Up").Click();
        Assert.Equal("E:/amiibo", browser.CurrentFolder);
        await cut.InvokeAsync(() => browser.TryGoBackAsync());
        Assert.Equal("E:/", browser.CurrentFolder);
        Assert.False(browser.CanGoBack);
        cut.FindAll("button.list-row").Single(b => b.TextContent.Contains("amiibo")).Click();
        cut.FindAll("button.list-row").Single(b => b.TextContent.Contains("child")).Click();
        cut.FindAll(".pixl-path button").First().Click();
        Assert.Equal("E:/", browser.CurrentFolder);
        Assert.False(browser.CanGoBack);
    }

    [Fact]
    public async Task PixlRefreshDoesNotPushHistory_AndFailedBackDoesNotLeaveDevice()
    {
        var sim = new PixlSimulator();
        sim.AddFolder("E:/amiibo");
        var device = new PixlDevice(sim);
        Assert.True(await device.InitializeAsync());
        var chrome = new PageChromeState(true);
        var host = Render<CascadingValue<PageChromeState>>(p => p.Add(c => c.Value, chrome)
            .AddChildContent<PixlDeviceControls>(control => control.Add(c => c.Device, device)));
        var browser = host.FindComponent<PixlBrowser>().Instance;
        host.FindAll("button.list-row").Single(b => b.TextContent.Contains("amiibo")).Click();
        await chrome.RefreshAsync();
        await chrome.RefreshAsync();
        sim.Unsupported.Add(0x16);
        Assert.True(await chrome.TryGoBackAsync());
        Assert.Equal("E:/amiibo", browser.CurrentFolder);
        Assert.True(chrome.CanGoBack);
        Assert.Single(_dialogs.Errors);
        sim.Unsupported.Remove(0x16);
        Assert.True(await chrome.TryGoBackAsync());
        Assert.Equal("E:/", browser.CurrentFolder);
        Assert.False(chrome.CanGoBack);
    }

    [Fact]
    public async Task WebNavbarBackTraversesPixlFolders()
    {
        Services.AddSingleton(new AppEnvironment { Platform = AppEnvironment.PlatformType.WebAssembly });
        var sim = new PixlSimulator();
        sim.AddFolder("E:/amiibo");
        var device = new PixlDevice(sim);
        Assert.True(await device.InitializeAsync());
        var cut = Render<PixlDeviceControls>(p => p.Add(c => c.Device, device));
        cut.FindAll("button.list-row").Single(b => b.TextContent.Contains("amiibo")).Click();
        cut.Find(".page-header button[aria-label=Back]").Click();
        Assert.Equal("E:/", cut.FindComponent<PixlBrowser>().Instance.CurrentFolder);
        Assert.False(cut.FindComponent<PixlBrowser>().Instance.CanGoBack);
        Assert.Empty(_dialogs.Errors);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BrowserBackStaysOnDevice_AndConsumesOneFolder(bool failRead)
    {
        Services.AddSingleton(new AppEnvironment { Platform = AppEnvironment.PlatformType.WebAssembly });
        Services.AddSingleton<IAppNavigator, BrowserNavigator>();
        var navigation = (BunitNavigationManager)Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/device/pixl");
        var sim = new PixlSimulator();
        sim.AddFolder("E:/amiibo");
        sim.AddFolder("E:/amiibo/child");
        var device = new PixlDevice(sim);
        Assert.True(await device.InitializeAsync());
        var cut = Render<PixlDeviceControls>(p => p.Add(c => c.Device, device));
        var browser = cut.FindComponent<PixlBrowser>().Instance;
        cut.FindAll("button.list-row").Single(b => b.TextContent.Contains("amiibo")).Click();
        cut.FindAll("button.list-row").Single(b => b.TextContent.Contains("child")).Click();
        if (failRead) sim.Unsupported.Add(0x16);
        await cut.InvokeAsync(() => navigation.NavigateTo("/")); // Browser traversal to the prior app route.
        cut.WaitForAssertion(() => Assert.Equal(NavigationState.Prevented, navigation.History.First().State));
        Assert.EndsWith("/device/pixl", navigation.Uri);
        cut.WaitForAssertion(() => Assert.Equal(failRead ? "E:/amiibo/child" : "E:/amiibo", browser.CurrentFolder));
        Assert.Same(browser, cut.FindComponent<PixlBrowser>().Instance);
        Assert.True(device.IsConnected);
        if (failRead) Assert.Single(_dialogs.Errors);
        else
        {
            cut.FindAll("button").Single(b => b.TextContent == "Up").Click();
            Assert.False(browser.CanGoBack);
            await cut.InvokeAsync(() => navigation.NavigateTo("/"));
            cut.WaitForAssertion(() => Assert.Equal(NavigationState.Succeeded, navigation.History.First().State));
        }
    }

    [Fact]
    public async Task SettingsNavigationFromFolderIsNotMistakenForBrowserBack()
    {
        Services.AddSingleton(new AppEnvironment { Platform = AppEnvironment.PlatformType.WebAssembly });
        Services.AddSingleton<IAppNavigator, BrowserNavigator>();
        var navigation = (BunitNavigationManager)Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/device/pixl");
        var sim = new PixlSimulator();
        sim.AddFolder("E:/amiibo");
        var device = new PixlDevice(sim);
        Assert.True(await device.InitializeAsync());
        var cut = Render<PixlDeviceControls>(p => p.Add(c => c.Device, device));
        cut.FindAll("button.list-row").Single(b => b.TextContent.Contains("amiibo")).Click();
        await cut.InvokeAsync(() => Services.GetRequiredService<IAppNavigator>().NavigateAsync(AppRoutes.Settings()));
        cut.WaitForAssertion(() => Assert.Equal(NavigationState.Succeeded, navigation.History.First().State));
        Assert.Equal("E:/amiibo", cut.FindComponent<PixlBrowser>().Instance.CurrentFolder);
        Assert.Empty(_dialogs.Errors);
    }

    [Theory]
    [InlineData(AppEnvironment.PlatformType.Windows, true)]
    [InlineData(AppEnvironment.PlatformType.iOS, false)]
    [InlineData(AppEnvironment.PlatformType.Android, false)]
    [InlineData(AppEnvironment.PlatformType.MacCatalyst, false)]
    [InlineData(AppEnvironment.PlatformType.WebAssembly, false)]
    [InlineData(AppEnvironment.PlatformType.Unknown, false)]
    public void CustomDocumentScrollbarIsWindowsOnly(AppEnvironment.PlatformType platform, bool usesPane)
    {
        Services.AddSingleton(new AppEnvironment { Platform = platform });
        var cut = Render<AppPage>(p => p.Add(c => c.Title, "Test")
            .Add(c => c.HeaderActions, "<button id='header-action'>Header action</button>")
            .AddChildContent("<button id='content-action'>Content action</button>"));
        Assert.Single(cut.FindAll(".app-page > main.page-content"));
        Assert.Single(cut.FindAll("#content-action"));
        Assert.Empty(cut.FindAll(".windows-scroll-layout, .windows-nav-spacer, .windows-page-scroll"));
        if (usesPane)
        {
            var scrollbar = cut.Find(".windows-scrollbar[role=scrollbar]");
            Assert.Equal("vertical", scrollbar.GetAttribute("aria-orientation"));
            Assert.Equal(cut.Find("main.page-content").Id, scrollbar.GetAttribute("aria-controls"));
            Assert.False(string.IsNullOrWhiteSpace(scrollbar.GetAttribute("aria-controls")));
            Assert.Single(cut.FindAll(".app-page > .page-actions #header-action"));
        }
        else Assert.Empty(cut.FindAll(".windows-scrollbar"));
    }

    public class EmptyProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            var type = method!.ReturnType;
            if (type == typeof(void)) return null;
            if (type == typeof(BleDeviceInfo)) return new BleDeviceInfo("test", "Test device", []);
            if (type == typeof(Task)) return Task.CompletedTask;
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Task<>))
            {
                var result = type.GenericTypeArguments[0];
                return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(result)
                    .Invoke(null, [result.IsValueType ? Activator.CreateInstance(result) : null]);
            }
            return type.IsValueType ? Activator.CreateInstance(type) : null;
        }
    }

    private class SingleDevice : ITagDevice
    {
        public string Name => "Test device";
        public TagDeviceKind Kind => TagDeviceKind.AmiLoop;
        public string? Firmware => null;
        public BleDeviceInfo Info => new("test", Name, []);
        public bool IsConnected => true;
        public int Writes { get; private set; }
        public bool FailWrite { get; init; }
#pragma warning disable CS0067
        public event EventHandler? Disconnected;
#pragma warning restore CS0067
        public virtual Task WriteTagAsync(TagDump dump, int? slot = null, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            if (FailWrite) throw new BleException("Test write failed");
            Writes++;
            return Task.CompletedTask;
        }
        public Task DisconnectAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Slots : SingleDevice, ISlotTagDevice
    {
        public int FullRefreshes { get; private set; }
        public List<int> SlotRefreshes { get; } = [];
        private int _active;
        public Task<SlotSummary> GetSlotSummaryAsync(CancellationToken cancellationToken = default) => Task.FromResult(new SlotSummary(_active, 3));
        public Task<IReadOnlyList<SlotInfo>> GetSlotsAsync(IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            FullRefreshes++;
            return Task.FromResult<IReadOnlyList<SlotInfo>>(Enumerable.Range(0, 3).Select(i => new SlotInfo(i, new TagDump(new byte[572]))).ToArray());
        }
        public Task<SlotInfo> GetSlotInformationAsync(int slot, CancellationToken cancellationToken = default)
        {
            SlotRefreshes.Add(slot);
            return Task.FromResult(new SlotInfo(slot, new TagDump(new byte[572])));
        }
        public override async Task WriteTagAsync(TagDump dump, int? slot = null, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            await base.WriteTagAsync(dump, slot, progress, cancellationToken);
            _active = slot ?? _active;
        }
        public Task<TagDump> ReadSlotAsync(int slot, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default) => throw new Exception("A slot summary must not read a full tag");
        public Task SelectSlotAsync(int slot, CancellationToken cancellationToken = default) { _active = slot; return Task.CompletedTask; }
        public Task<byte[]> ClearSlotAsync(int slot, CancellationToken cancellationToken = default) => Task.FromResult(new byte[9]);
    }

    private sealed class Files : IFileService
    {
        public bool Cancel { get; set; }
        public Task<PickedFile?> PickFileAsync(string title, IReadOnlyList<string> extensions) => Task.FromResult(Cancel ? null : new PickedFile("test.bin", TagDump.Blank().ToArray()));
        public Task<IReadOnlyList<PickedFile>> PickFilesAsync(string title, IReadOnlyList<string> extensions) => Task.FromResult<IReadOnlyList<PickedFile>>([]);
        public Task SaveFileAsync(string fileName, byte[] data, string contentType = "application/octet-stream") => Task.CompletedTask;
    }

    private sealed class Dialogs : IDialogService
    {
        public List<string> Errors { get; } = [];
        public IReadOnlyList<string> Choices { get; private set; } = [];
        public Task AlertAsync(string title, string? message = null, string ok = "OK") { Errors.Add(title + ": " + message); return Task.CompletedTask; }
        public Task<bool> ConfirmAsync(string title, string? message = null, string accept = "Yes", string cancel = "No") => Task.FromResult(true);
        public Task<string?> PromptAsync(string title, string? message = null, string? initialValue = null, int maxLength = -1) => Task.FromResult<string?>(null);
        public Task<int> ChooseAsync(string title, IReadOnlyList<string> options, string cancel = "Cancel") { Choices = options; return Task.FromResult(-1); }
        public IProgressDialog ShowProgress(string title, string? message = null, CancellationTokenSource? cancellation = null) => new ProgressDialog();
        private sealed class ProgressDialog : IProgressDialog
        {
            public void Update(string? message, double? fraction = null) { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
