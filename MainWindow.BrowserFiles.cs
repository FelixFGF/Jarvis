using System.Windows;
using Jarvis.Services;

namespace Jarvis;

public partial class MainWindow
{
    private async Task<(bool Ok, string Url, string Title, string Error)> NavigateEmbeddedBrowserAsync(string rawUrl, CancellationToken token)
    {
        await Dispatcher.InvokeAsync(() =>
        {
            NavigateTo("Chat");
            SetWorkspaceLayout(WorkspaceLayoutMode.Browser);
        });
        return await IntegratedWorkspace.NavigateBrowserAsync(rawUrl, token);
    }

    private void NavigateToBrowserPage()
    {
        NavigateTo("Chat");
        SetWorkspaceLayout(WorkspaceLayoutMode.Browser);
    }
}
