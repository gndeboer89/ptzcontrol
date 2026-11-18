using System;
using System.Diagnostics;
using System.Reactive;
using System.Reflection;
using ReactiveUI;

namespace PtzJoystickControl.Gui.ViewModels;

public class AboutViewModel : ViewModelBase
{
    public const string ForkAuthor = "Geert de Boer (gndeboer89)";
    public const string ForkUrl = "https://github.com/gndeboer89/ptzcontrol";
    public const string OriginalAuthor = "Aron het Lam (AronHetLam)";
    public const string OriginalUrl = "https://github.com/AronHetLam/PTZJoystickControl";
    public const string LicenseUrl = "https://www.gnu.org/licenses/gpl-3.0.html";

    public AboutViewModel()
    {
        OpenLinkCommand = ReactiveCommand.Create<string>(OpenLink);
    }

    public string AppName => "PTZ Joystick Control";

    public string Version { get; } =
        "Version " + (typeof(AboutViewModel).Assembly.GetName().Version?.ToString(3) ?? "unknown");

    public string ForkText => $"This version is maintained by {ForkAuthor}.";
    public string OriginalText => $"Based on PTZ Joystick Control by {OriginalAuthor}.";
    public string ForkLink => ForkUrl;
    public string OriginalLink => OriginalUrl;
    public string LicenseLink => LicenseUrl;

    public string LicenseText =>
        "Licensed under the GNU General Public License v3.0. This program comes with ABSOLUTELY NO WARRANTY; " +
        "you are welcome to redistribute it under the terms of that license.";

    public string ThirdPartyText =>
        "Built with Avalonia UI, ReactiveUI, SDL2, Octokit and VLC (libVLC, LGPL).";

    public string NdiText => "NDI® is a registered trademark of Vizrt Group. NDI video needs the free NDI Runtime from ndi.video.";

    public ReactiveCommand<string, Unit> OpenLinkCommand { get; }

    private static void OpenLink(string url)
    {
        // Only ever open the links defined above, never arbitrary text.
        if (url is not (ForkUrl or OriginalUrl or LicenseUrl))
            return;
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch (Exception e)
        {
            Debug.WriteLine(e);
        }
    }
}
