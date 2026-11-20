using System.Runtime.InteropServices;
using Microsoft.Windows.Storage.Pickers;
using Pane.Localization;
using Pane.Services.Ai;

namespace Pane.Views;

public sealed partial class TranslateView
{
    async Task InstallDictionaryAsync()
    {
        if (!IsDictionary || HistoryVisible || !ActionButton.IsEnabled) return;
        CancelPending();
        using var installation = running = new CancellationTokenSource();
        var token = installation.Token;
        ActionButton.IsEnabled = false;
        try
        {
            var picker = new FileOpenPicker(XamlRoot.ContentIslandEnvironment.AppWindowId);
            picker.FileTypeFilter.Add(".zip");
            if (await picker.PickSingleFileAsync() is not { } package || token.IsCancellationRequested) return;
            var path = package.Path;
            ShowMessage(Strings.Translate_InstallingDictionary, MessageAction.InstallDictionary);
            await Task.Run(() => OfflineDictionary.InstallPackage(path, historySettings.DirectoryPath, token), token);
            if (running != installation) return;
            ShowIdle();
            StateText.Text = Strings.Translate_DictionaryInstalled;
            if (!string.IsNullOrWhiteSpace(Source.Text)) ShowAction(MessageAction.LookupDictionary);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or
            InvalidOperationException or COMException or System.ComponentModel.Win32Exception)
        {
            if (running == installation) ShowMessage(Strings.Translate_DictionaryInstallFailed, MessageAction.InstallDictionary);
        }
        finally
        {
            if (running == installation)
            {
                running = null;
                ActionButton.IsEnabled = true;
            }
        }
    }
}
