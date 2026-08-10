using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Dalamud.Game.Chat;
using Dalamud.Game.Text;
using Cactjack.Windows;

namespace Cactjack;

public sealed class Plugin : IDalamudPlugin
{
    private const string CommandName = "/cactjack";

    [PluginService]
    internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;

    [PluginService]
    internal static ICommandManager CommandManager { get; private set; } = null!;

    [PluginService]
    internal static IChatGui ChatGui { get; private set; } = null!;

    private readonly WindowSystem windowSystem = new("Cactjack");
    private readonly MainWindow mainWindow;

    public Plugin()
    {
        mainWindow = new MainWindow();
        windowSystem.AddWindow(mainWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open the Cactjack window."
        });

        PluginInterface.UiBuilder.Draw += windowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleMainUi;
        ChatGui.ChatMessageUnhandled += OnChatMessage;
    }

    public void Dispose()
    {
        PluginInterface.UiBuilder.Draw -= windowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleMainUi;
        ChatGui.ChatMessageUnhandled -= OnChatMessage;

        CommandManager.RemoveHandler(CommandName);
        windowSystem.RemoveAllWindows();
        mainWindow.Dispose();
    }

    private void OnCommand(string command, string arguments) => ToggleMainUi();

    private void OnChatMessage(IChatMessage message)
    {
        if (message.LogKind == XivChatType.Party)
            mainWindow.HandlePartyChat(message);
    }

    private void ToggleMainUi() => mainWindow.Toggle();
}
